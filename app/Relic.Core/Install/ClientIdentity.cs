using System.Text.Json;
using Relic.Core.Util;

namespace Relic.Core.Install;

/// <summary>What <see cref="ClientIdentity.Check"/> can say about a game folder.</summary>
public enum ClientIdentityKind
{
    /// <summary>Nothing to judge by: no pkg_version, no line for the identity file in it, or no
    /// table that covers it.</summary>
    Unknown,
    /// <summary>pkg_version names a build the table lists for the asked version.</summary>
    Match,
    /// <summary>pkg_version names a build the table lists for ANOTHER version.</summary>
    OtherVersion,
    /// <summary>The table has rows for the asked version and pkg_version lists their file, but with a
    /// content no row of any version names.</summary>
    UnknownBuild,
}

/// <summary>
/// The answer of <see cref="ClientIdentity.Check"/>. <paramref name="Found"/> is the version the folder
/// holds (OtherVersion only) and <paramref name="Build"/> the name of the build that was recognised
/// (Match, OtherVersion). <paramref name="Known"/> lists the builds of the ASKED version the table
/// names, for a message, and <paramref name="Exact"/> says that every one of that version's rows is
/// marked "exact": only those builds work, so an unknown one is refused instead of tolerated.
/// </summary>
public readonly record struct ClientIdentityResult(
    ClientIdentityKind Kind, string Found = "", string Build = "", string Known = "", bool Exact = false);

/// <summary>
/// The answer of <see cref="ClientIdentity.Identify"/>: what a folder's own file list says it holds when no
/// version is asked. <see cref="ClientIdentityKind.Match"/>: a row of the table names the line —
/// <paramref name="Version"/>, <paramref name="Build"/> and <paramref name="Exact"/> (every row of that version is
/// "exact") are that row's. <see cref="ClientIdentityKind.UnknownBuild"/>: the line of a row's file is there, with a
/// content no row names — the version cannot be told. <see cref="ClientIdentityKind.Unknown"/>: no list, no such
/// line, no table. <paramref name="File"/> is the identity file whose line was read, <paramref name="ListedSize"/> the
/// size that line gives it (-1: none) and <paramref name="OnDiskSize"/> the size of that file in the folder (-1: not
/// there). <see cref="ClientIdentityKind.OtherVersion"/> is never answered: there is no asked version.
/// </summary>
public readonly record struct ClientIdentification(
    ClientIdentityKind Kind, string Version = "", string Build = "", bool Exact = false,
    string File = "", long ListedSize = -1, long OnDiskSize = -1)
{
    /// <summary>The list names a build of the table and the folder's identity file has the size the list gives it —
    /// one stat, no hash: what a found folder is proven by.</summary>
    public bool SizeProven => Kind == ClientIdentityKind.Match && ListedSize >= 0 && OnDiskSize == ListedSize;
}

/// <summary>
/// Which game build a folder holds, read from the folder's own file list. Every client ships
/// <c>pkg_version</c> at its root — one JSON object per line with <c>remoteName</c>, <c>md5</c> and
/// <c>fileSize</c> — and the "clients" table of payload/manifest.json names, per version, the line of
/// one file that differs in every build (<c>UserAssembly.dll</c>). A version may list several builds:
/// hosts serve different builds of one version, and each of them is that version.
///
/// The list says what the build IS, not what the files on disk hold at this moment, so the answer
/// costs one small read and suits a refusal BEFORE anything is written: at import, and before
/// <see cref="GamePatcher"/> puts a version's own files into a folder. Whether a file is still the
/// unmodified one is the business of whoever writes into it.
/// </summary>
public static class ClientIdentity
{
    /// <summary>The file list at the root of every client.</summary>
    public const string ListFile = "pkg_version";

    // The real list is about a megabyte; a file far beyond that is not one, and is not read line by line.
    private const long MaxListBytes = 64L << 20;

    private sealed record Row(string Version, string Build, string File, string Md5, long Size, bool Exact);

    /// <summary>
    /// Compares <paramref name="gameDir"/>'s pkg_version with the table of
    /// <paramref name="payloadRoot"/> (the shipped payload when null). Never throws: whatever cannot
    /// be read answers <see cref="ClientIdentityKind.Unknown"/>.
    /// </summary>
    public static ClientIdentityResult Check(string versionId, string gameDir, string? payloadRoot = null)
    {
        try
        {
            var rows = LoadRows(payloadRoot ?? GamePatcher.PayloadRoot());
            if (rows.Count == 0) return new(ClientIdentityKind.Unknown);
            var own = rows.Where(r => SameVersion(r.Version, versionId)).ToList();
            string known = string.Join(", ", own.Select(r => r.Build).Where(b => b.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            bool exact = own.Count > 0 && own.All(r => r.Exact);

            var listed = ReadListed(Path.Combine(gameDir, ListFile),
                rows.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

            bool Names(Row r) => listed.TryGetValue(r.File, out var line)
                && line.Size == r.Size && string.Equals(line.Md5, r.Md5, StringComparison.OrdinalIgnoreCase);

            if (own.FirstOrDefault(Names) is { } mine)
                return new(ClientIdentityKind.Match, Build: mine.Build, Known: known, Exact: exact);
            if (rows.FirstOrDefault(r => !SameVersion(r.Version, versionId) && Names(r)) is { } other)
                return new(ClientIdentityKind.OtherVersion, other.Version, other.Build, known, exact);
            return new(own.Any(r => listed.ContainsKey(r.File)) ? ClientIdentityKind.UnknownBuild : ClientIdentityKind.Unknown,
                Known: known, Exact: exact);
        }
        catch (Exception ex)
        {
            Util.Log.Error($"client identity: {versionId} in \"{gameDir}\" could not be checked", ex);
            return new(ClientIdentityKind.Unknown);
        }
    }

    /// <summary>
    /// What <paramref name="gameDir"/>'s pkg_version says the folder holds, asked without a version: the first row of
    /// the table (in its order) whose file the list names with that row's md5 and size, else the first row file the
    /// list names at all. Reads the list up to the identity file's line and stats that file — never a hash, and never
    /// throws: whatever cannot be read answers <see cref="ClientIdentityKind.Unknown"/>.
    /// </summary>
    public static ClientIdentification Identify(string gameDir, string? payloadRoot = null)
    {
        try
        {
            var rows = LoadRows(payloadRoot ?? GamePatcher.PayloadRoot());
            if (rows.Count == 0) return new(ClientIdentityKind.Unknown);
            var files = rows.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var listed = ReadListed(Path.Combine(gameDir, ListFile), files);

            foreach (var row in rows)
            {
                if (!listed.TryGetValue(row.File, out var line) || line.Size != row.Size
                    || !string.Equals(line.Md5, row.Md5, StringComparison.OrdinalIgnoreCase))
                    continue;
                bool exact = rows.Where(r => SameVersion(r.Version, row.Version)).All(r => r.Exact);
                return new(ClientIdentityKind.Match, row.Version, row.Build, exact, row.File, line.Size, SizeOnDisk(gameDir, row.File));
            }
            string? named = files.FirstOrDefault(listed.ContainsKey);
            return named is null
                ? new(ClientIdentityKind.Unknown)
                : new(ClientIdentityKind.UnknownBuild, File: named, ListedSize: listed[named].Size, OnDiskSize: SizeOnDisk(gameDir, named));
        }
        catch (Exception ex)
        {
            Util.Log.Error($"client identity: \"{gameDir}\" could not be identified", ex);
            return new(ClientIdentityKind.Unknown);
        }
    }

    /// <summary>The size of the folder's copy of a file the table names, or -1 when it is not there.</summary>
    private static long SizeOnDisk(string gameDir, string file)
    {
        var info = new FileInfo(Path.Combine(gameDir, file.Replace('/', Path.DirectorySeparatorChar)));
        return info.Exists ? info.Length : -1;
    }

    /// <summary>
    /// Why a folder with this <paramref name="identity"/> cannot be registered as
    /// <paramref name="versionId"/> — the text for the user — or null when it can. A build of another
    /// version is always refused. A build no row names is refused only for a version whose rows are
    /// all "exact"; everywhere else it is that version as far as anyone can tell, and so is a folder
    /// that could not be judged at all.
    /// </summary>
    public static string? ImportRefusal(string versionId, ClientIdentityResult identity) => identity.Kind switch
    {
        ClientIdentityKind.OtherVersion =>
            L.T("core.install.import.versionMismatch", new { found = identity.Found, version = versionId }),
        ClientIdentityKind.UnknownBuild when identity.Exact =>
            L.T("core.install.import.wrongBuild", new { version = versionId, build = identity.Known }),
        _ => null,
    };

    private static bool SameVersion(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The usable rows of the manifest's "clients" list. A row that names no version, file,
    /// md5 or size identifies nothing and is left out — build/publish.ps1 refuses to ship one.</summary>
    private static List<Row> LoadRows(string? payloadRoot)
    {
        var rows = new List<Row>();
        if (payloadRoot is null) return rows;
        string manifest = Path.Combine(payloadRoot, "manifest.json");
        if (!File.Exists(manifest)) return rows;

        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("clients", out var clients)
            || clients.ValueKind != JsonValueKind.Array)
            return rows;
        foreach (var c in clients.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            string version = Str(c, "version"), file = Str(c, "file").Replace('\\', '/'), md5 = Str(c, "md5");
            long size = c.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                && s.TryGetInt64(out long n) ? n : 0;
            if (version.Length == 0 || file.Length == 0 || md5.Length == 0 || size <= 0) continue;
            bool exact = c.TryGetProperty("exact", out var x) && x.ValueKind == JsonValueKind.True;
            rows.Add(new Row(version, Str(c, "build"), file, md5, size, exact));
        }
        return rows;
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// The md5 and size pkg_version lists for each of <paramref name="files"/>, read line by line up
    /// to the last of them. The list has thousands of lines, so only a line that can be about one of
    /// the files is parsed; one that is not JSON names no file.
    /// </summary>
    private static Dictionary<string, (string Md5, long Size)> ReadListed(string listPath, IReadOnlyList<string> files)
    {
        var found = new Dictionary<string, (string Md5, long Size)>(StringComparer.OrdinalIgnoreCase);
        var info = new FileInfo(listPath);
        if (!info.Exists || info.Length > MaxListBytes) return found;
        var leaves = files.Select(f => f[(f.LastIndexOf('/') + 1)..]).ToList();

        using var reader = new StreamReader(
            new FileStream(listPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (found.Count < files.Count && reader.ReadLine() is { } line)
        {
            if (!leaves.Any(leaf => line.Contains(leaf, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                string name = Str(doc.RootElement, "remoteName").Replace('\\', '/');
                string md5 = Str(doc.RootElement, "md5");
                if (md5.Length == 0 || !files.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                long size = doc.RootElement.TryGetProperty("fileSize", out var s)
                    && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long n) ? n : -1;
                found.TryAdd(name, (md5, size));
            }
            catch (JsonException) { }
        }
        return found;
    }
}
