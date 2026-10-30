using System.Text.Json;
using Relic.Core.Install;
using Relic.Core.Util;

namespace Relic.Core.Server;

/// <summary>What the shipped catalogue <c>agent/payloads/&lt;version&gt;/stack.json</c> says about a version's
/// ready-made server package. <paramref name="Published"/> is false when the build ships no usable
/// catalogue for the version: there is then nothing the agent could download, and the other members
/// describe no archive (<see cref="None"/>). <paramref name="Size"/> and <paramref name="Archive"/> describe the
/// archive the catalogue names under <c>url</c> — its bytes, and its format as the address's extension
/// ("7z" or "zip"). <paramref name="NeedsExtractor"/>: unpacking takes a 7z tool on the box, which is the
/// case unless the catalogue names a <c>.zip</c> — under <c>url</c> or in an <c>alt</c> entry, the same
/// tree in the other format — since the agent reads a .zip by itself. <paramref name="Label"/> is the
/// catalogue's name for where the package is hosted.</summary>
public sealed record StackPackage(bool Published, long Size, string Archive, bool NeedsExtractor, string Label)
{
    /// <summary>No package: a version without a catalogue, or one whose catalogue cannot be used. It names
    /// no .zip, so by the rule above it reads as needing an extractor — which nothing asks of a version
    /// that cannot be downloaded.</summary>
    public static StackPackage None { get; } = new(false, 0, "", true, "");

    /// <summary>The bytes of the unpacked tree, as the catalogue's <c>extractedSize</c> names them; 0 when it
    /// names none (or no JSON integer ≥ 0) — the agent then counts three times the archive for the disk
    /// check.</summary>
    public long ExtractedSize { get; init; }

    /// <summary>The bytes of the package as a <c>.zip</c> — what a box without a 7z tool downloads: <see cref="Size"/>
    /// when <c>url</c> names a .zip, else the size of the first <c>alt</c> entry that names a .zip with a
    /// positive size; 0 when the catalogue names no such archive.</summary>
    public long ZipSize { get; init; }
}

/// <summary>
/// The server versions the launcher can set an agent up for — ONE list. The install plan, the config of
/// the agent on this PC, the install form and its payload all take their versions from it and derive
/// every per-version name from the id (the agent's config key, the installer's choice key and download
/// mark, the form's field names), so on the launcher's side of an agent install a version is one id here. The agent's own
/// table (<c>VERSIONS</c> in gio_agent.py) and the two installers' lists hold the same ids in the same
/// order; the <c>server</c> spike keeps the four equal.
/// </summary>
public static class ServerVersions
{
    /// <summary>The version ids, in the order they are shown and written.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { "1.6", "2.8", "3.2" };

    /// <summary>The id without its dot ("2.8" → "28"): what every per-version name ends in.</summary>
    public static string Suffix(string version) => (version ?? "").Replace(".", "");

    /// <summary>The agent config key that holds the version's stack folder — the agent's
    /// <c>dir_setting_key</c> ("2.8" → <c>GIO_DIR_28</c>).</summary>
    public static string DirKey(string version) => "GIO_DIR_" + Suffix(version);

    /// <summary>The installer-only mark "download this version's server package" (<c>GIO_FETCH_28</c>) — the
    /// hand-run key; the launcher sends <see cref="StackKey"/> instead.</summary>
    public static string FetchKey(string version) => "GIO_FETCH_" + Suffix(version);

    /// <summary>The installer-only choice of what an install does with the version (<c>GIO_STACK_28</c>:
    /// download | have | later | keep, <see cref="StackChoices.Token"/>) — never a key of the agent config.</summary>
    public static string StackKey(string version) => "GIO_STACK_" + Suffix(version);

    /// <summary>The install form's field for the version's stack folder (<c>dir28</c>).</summary>
    public static string FormDir(string version) => "dir" + Suffix(version);

    /// <summary>The install payload's choice of the version (<c>stack28</c>) — a <see cref="StackChoices.Token"/>.</summary>
    public static string FormStack(string version) => "stack" + Suffix(version);

    /// <summary>The folder name a stack of the version conventionally has ("2.8_live") — for prompts and
    /// placeholders only. Never the catalogue's <c>topDir</c>: that is the name of the folder inside the
    /// archive, which the agent renames onto whatever folder is configured.</summary>
    public static string ConventionalFolder(string version) => version + "_live";

    /// <summary>The server package of a version as the shipped catalogue describes it
    /// (<c>&lt;payloadsRoot&gt;/&lt;version&gt;/stack.json</c>; the root defaults to the payloads next to the
    /// exe). <see cref="StackPackage.None"/> for an id that is no plain folder name, a version without a
    /// catalogue, and a catalogue that does not hold what is read here: a JSON object with an
    /// <c>https</c> <c>url</c> ending in <c>.7z</c> or <c>.zip</c>, a positive integer <c>size</c> and,
    /// when it has an <c>alt</c>, a list of objects that each name a <c>url</c>. The file is read as the
    /// agent reads it — UTF-8 bytes, where a byte order mark in front of the object is not JSON — so a
    /// catalogue the agent could not open is not offered as a download. <see cref="StackPackage.ExtractedSize"/>
    /// and <see cref="StackPackage.ZipSize"/> come from the same file; a value that cannot be read there is
    /// 0, never a reason to refuse the catalogue. Read from the disk at every call; never throws.</summary>
    public static StackPackage Package(string version, string? payloadsRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(payloadsRoot) ? GameAccounts.DefaultPayloadsRoot : payloadsRoot!;
        string key = (version ?? "").Trim();
        // The id goes into a path: one with separators or ".." opens nothing, not another folder.
        if (key.Length == 0 || key.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || key.Contains("..")) return StackPackage.None;
        string path = Path.Combine(root, key, "stack.json");
        try
        {
            if (!File.Exists(path)) return StackPackage.None;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var cat = doc.RootElement;
            if (cat.ValueKind != JsonValueKind.Object) return Unusable(path, "not a JSON object");
            string url = Str(cat, "url");
            string archive = ArchiveOf(url);
            if (archive.Length == 0) return Unusable(path, "url is not an https address ending in .7z or .zip");
            if (!cat.TryGetProperty("size", out var sizeEl) || sizeEl.ValueKind != JsonValueKind.Number
                || !sizeEl.TryGetInt64(out long size) || size <= 0)
                return Unusable(path, "size is not a positive integer");
            bool zipped = archive == "zip";
            long zipSize = zipped ? size : 0;
            if (cat.TryGetProperty("alt", out var alts))
            {
                if (alts.ValueKind != JsonValueKind.Array) return Unusable(path, "alt is not a list");
                foreach (var alt in alts.EnumerateArray())
                {
                    if (alt.ValueKind != JsonValueKind.Object || Str(alt, "url").Length == 0)
                        return Unusable(path, "an alt entry names no url");
                    bool altZip = ArchiveOf(Str(alt, "url")) == "zip";
                    zipped |= altZip;
                    if (altZip && zipSize == 0 && Integer(alt, "size") is long altSize && altSize > 0) zipSize = altSize;
                }
            }
            return new StackPackage(true, size, archive, !zipped, Str(cat, "label"))
            {
                ExtractedSize = Integer(cat, "extractedSize") is long unpacked && unpacked >= 0 ? unpacked : 0,
                ZipSize = zipSize,
            };
        }
        catch (Exception ex)
        {
            // An unreadable or broken catalogue must not break the page that asks: only the download is lost.
            return Unusable(path, ex.Message);
        }
    }

    /// <summary>"7z" or "zip" for an https address whose path ends in that extension (any letter case);
    /// "" for anything else.</summary>
    private static string ArchiveOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return "";
        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch { ".7z" => "7z", ".zip" => "zip", _ => "" };
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "").Trim() : "";

    /// <summary>The value of <paramref name="name"/> when it is a JSON integer (no fraction, no exponent, in the
    /// range of a long); null for anything else — a string of digits included.</summary>
    private static long? Integer(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long n) ? n : null;

    private static readonly HashSet<string> Noted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A catalogue that is there and cannot be used reads as no package; the log says why, once
    /// per file and reason — the init state asks for every version at every reply.</summary>
    private static StackPackage Unusable(string path, string reason)
    {
        bool first;
        lock (Noted) first = Noted.Add(path + "|" + reason);
        if (first) Log.Info($"server package catalogue {path} is not usable ({reason}) — the version reads as having no package to download");
        return StackPackage.None;
    }
}
