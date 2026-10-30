using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relic.Core.Install;

namespace Relic.DevTest;

/// <summary>The enhancements entry of a version in a payload/manifest.json: the one <c>inject</c> entry whose
/// destination is the mod's DLL in the game folder. <paramref name="Synthetic"/>: the payload ships no such entry for the
/// version, and this one exists in the stage only — the release state of the game folder is then "no DLL".</summary>
internal sealed record InjectEntry(string Src, string Dst, string Sha256, bool Synthetic = false);

/// <summary>A payload folder as the runner reads it: its manifest and the files of one version's plan.</summary>
internal static class Payload
{
    public const string ModDllDst = "ayy/anime/ee/CLibrary.dll";

    /// <summary>The entry of <paramref name="version"/> that puts the enhancements DLL in place — exactly one, or the
    /// reason why there is none to stage.</summary>
    public static InjectEntry FindInjectEntry(string payloadRoot, string version)
    {
        if (!TryFindInjectEntry(payloadRoot, version, out var entry))
            throw new InvalidDataException($"{Path.Combine(payloadRoot, "manifest.json")} has no enhancements entry for version {version}");
        return entry!;
    }

    /// <summary>The version's enhancements entry, or false when the payload ships none for it (the version is not listed,
    /// or its list holds no such entry). A manifest that cannot be read, two such entries, or one without a usable src or
    /// sha256 are refused.</summary>
    public static bool TryFindInjectEntry(string payloadRoot, string version, out InjectEntry? entry)
    {
        entry = null;
        string manifest = Path.Combine(payloadRoot, "manifest.json");
        if (!File.Exists(manifest)) throw new InvalidDataException($"{manifest} is not there");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
        if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{manifest} has no 'versions'");
        JsonElement? list = null;
        foreach (var v in versions.EnumerateObject())
            if (string.Equals(v.Name, version, StringComparison.OrdinalIgnoreCase)) list = v.Value;
        if (list is null) return false;
        if (list is not { ValueKind: JsonValueKind.Array } entries)
            throw new InvalidDataException($"{manifest}: the files of version {version} are not a list");
        var found = new List<InjectEntry>();
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            bool inject = e.TryGetProperty("inject", out var i) && i.ValueKind == JsonValueKind.True;
            string dst = Str(e, "dst").Replace('\\', '/');
            if (!inject || !string.Equals(dst, ModDllDst, StringComparison.OrdinalIgnoreCase)) continue;
            found.Add(new InjectEntry(Str(e, "src").Replace('\\', '/'), dst, Str(e, "sha256").ToLowerInvariant()));
        }
        if (found.Count == 0) return false;
        if (found.Count != 1)
            throw new InvalidDataException($"{manifest} has {found.Count} enhancements entries for version {version} (one expected)");
        entry = Checked(manifest, version, found[0]);
        return true;
    }

    /// <summary>The entry the stage gets for a version whose payload ships none: the DLL at the place a shipped one
    /// would have, its sha256 filled in when it is staged.</summary>
    public static InjectEntry SyntheticEntry(string version)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9]+\.[0-9]+$"))
            throw new InvalidDataException($"'{version}' is not a game version id (<digits>.<digits>)");
        return new($"{version}/ee/CLibrary.dll.bin", ModDllDst, "", Synthetic: true);
    }

    private static InjectEntry Checked(string manifest, string version, InjectEntry entry)
    {
        if (entry.Src.Length == 0 || entry.Src.Contains("..") || Path.IsPathRooted(entry.Src))
            throw new InvalidDataException($"{manifest}: the enhancements entry of {version} has the src '{entry.Src}'");
        if (entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException($"{manifest}: the enhancements entry of {version} has no sha256");
        return entry;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static string SourcePath(string payloadRoot, string src) =>
        Path.Combine(payloadRoot, src.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The file names and expected hashes of a version's plan in a payload folder (the common entries plus
    /// the version's own): a plain entry's source must hash to <c>sha256</c>, a byte patch's span file to
    /// <c>spanSha256</c>.</summary>
    public static List<(string Src, string Sha256)> PlanHashes(string payloadRoot, string version)
    {
        var plan = new List<(string, string)>();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(payloadRoot, "manifest.json")));
        var root = doc.RootElement;
        IEnumerable<JsonElement> entries = Enumerable.Empty<JsonElement>();
        if (root.TryGetProperty("common", out var common) && common.ValueKind == JsonValueKind.Array)
            entries = entries.Concat(common.EnumerateArray().ToList());
        if (root.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Object)
            foreach (var v in versions.EnumerateObject())
                if (string.Equals(v.Name, version, StringComparison.OrdinalIgnoreCase) && v.Value.ValueKind == JsonValueKind.Array)
                    entries = entries.Concat(v.Value.EnumerateArray().ToList());
        foreach (var e in entries)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            bool bytePatch = Str(e, "kind") == GamePatcher.BytePatchKind;
            string sha = bytePatch ? Str(e, "spanSha256") : Str(e, "sha256");
            plan.Add((Str(e, "src"), sha.ToLowerInvariant()));
        }
        return plan;
    }

    /// <summary>The copy's manifest text: the old hash of the entry replaced by the new one, its one occurrence and
    /// nothing else — then proven by reading both documents: equal but for that entry's sha256.</summary>
    public static byte[] RewriteSha(byte[] manifestBytes, string version, InjectEntry entry, string newSha)
    {
        bool bom = manifestBytes.Length >= 3 && manifestBytes[0] == 0xEF && manifestBytes[1] == 0xBB && manifestBytes[2] == 0xBF;
        string text = new UTF8Encoding(false).GetString(manifestBytes, bom ? 3 : 0, manifestBytes.Length - (bom ? 3 : 0));
        int first = text.IndexOf(entry.Sha256, StringComparison.OrdinalIgnoreCase);
        if (first < 0 || text.IndexOf(entry.Sha256, first + 1, StringComparison.OrdinalIgnoreCase) >= 0)
            throw new InvalidDataException("the enhancements entry's sha256 does not occur exactly once in the manifest");
        string updated = string.Concat(text.AsSpan(0, first), newSha.ToLowerInvariant().AsSpan(), text.AsSpan(first + entry.Sha256.Length));

        var expected = JsonNode.Parse(text) ?? throw new InvalidDataException("the manifest is empty");
        JsonObject? target = null;
        if (expected["versions"] is JsonObject vs)
            foreach (var (name, node) in vs)
                if (string.Equals(name, version, StringComparison.OrdinalIgnoreCase) && node is JsonArray arr)
                    foreach (var item in arr)
                        if (item is JsonObject o && o["inject"]?.GetValueKind() == JsonValueKind.True
                            && string.Equals((o["dst"]?.GetValue<string>() ?? "").Replace('\\', '/'), entry.Dst, StringComparison.OrdinalIgnoreCase))
                            target = o;
        if (target is null) throw new InvalidDataException("the enhancements entry is not in the manifest");
        target["sha256"] = newSha.ToLowerInvariant();
        var staged = JsonNode.Parse(updated);
        if (!JsonNode.DeepEquals(expected, staged))
            throw new InvalidDataException("the rewritten manifest differs from the original in more than the entry's sha256");

        byte[] body = new UTF8Encoding(false).GetBytes(updated);
        return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }

    /// <summary>The copy's manifest with a synthetic entry added to the version's list (the list made when the version
    /// is not listed) — then proven by reading both documents: the original plus that one entry, nothing else.</summary>
    public static byte[] AddEntry(byte[] manifestBytes, string version, InjectEntry entry, string sha)
    {
        if (!entry.Synthetic) throw new InvalidOperationException("only a synthetic entry is added to a staged manifest");
        bool bom = manifestBytes.Length >= 3 && manifestBytes[0] == 0xEF && manifestBytes[1] == 0xBB && manifestBytes[2] == 0xBF;
        string text = new UTF8Encoding(false).GetString(manifestBytes, bom ? 3 : 0, manifestBytes.Length - (bom ? 3 : 0));
        var original = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("the manifest is not an object");
        var staged = (JsonObject)original.DeepClone();
        if (staged["versions"] is not JsonObject versions) throw new InvalidDataException("the manifest has no 'versions'");
        string? key = versions.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, version, StringComparison.OrdinalIgnoreCase));
        bool created = key is null;
        if (created) versions[key = version] = new JsonArray();
        if (versions[key!] is not JsonArray list) throw new InvalidDataException($"the files of version {version} are not a list");
        list.Add(new JsonObject
        {
            ["src"] = entry.Src,
            ["dst"] = entry.Dst,
            ["sha256"] = sha.ToLowerInvariant(),
            ["inject"] = true,
            ["reason"] = "DevTest stage only: the payload ships no enhancements DLL for this version.",
        });

        var proof = (JsonObject)staged.DeepClone();
        var proofVersions = (JsonObject)proof["versions"]!;
        if (created) proofVersions.Remove(key!);
        else ((JsonArray)proofVersions[key!]!).RemoveAt(((JsonArray)proofVersions[key!]!).Count - 1);
        if (!JsonNode.DeepEquals(original, proof))
            throw new InvalidDataException("the staged manifest differs from the original in more than the added entry");

        byte[] body = new UTF8Encoding(false).GetBytes(staged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }
}

/// <summary>
/// The staged payload: a copy of the repository's payload folder in which the version's enhancements DLL is the
/// DevTest build and that one entry's sha256 is the build's — in the copy only. Relic's PlaySession takes its payload
/// from the folder <c>payload</c> next to the running program (<see cref="GamePatcher.PayloadRoot"/>), so the runner
/// makes the stage folder its base directory before the session: the staged payload is what the session applies,
/// and neither the repository's payload nor the installed launcher's is ever written.
/// </summary>
internal static class Stage
{
    /// <summary>The file that marks <c>&lt;stage&gt;\payload</c> as the runner's: only a payload folder that carries it
    /// is deleted and rebuilt. Everything else in the stage folder — build_ee.ps1 -StageDev puts its
    /// <c>&lt;version&gt;\CLibrary.dll</c> there — is left alone.</summary>
    public const string MarkerFile = ".devtest-stage";

    public static string PayloadDir(string stageDir) => Path.Combine(Path.GetFullPath(stageDir), "payload");

    /// <summary>Why <paramref name="stageDir"/> cannot be the stage folder, or null.</summary>
    public static string? Problem(string stageDir, string sourcePayload, IEnumerable<string> forbiddenRoots)
    {
        string full;
        try { full = Path.GetFullPath(stageDir); }
        catch (Exception ex) { return $"--stage '{stageDir}' is not a usable path ({ex.Message})"; }
        if (Path.GetPathRoot(full) is { } root && Files.SamePath(full, root))
            return $"--stage '{full}' is the root of a drive -- name a folder of its own";
        if (Files.IsInside(full, sourcePayload) || Files.IsInside(sourcePayload, full))
            return $"--stage '{full}' overlaps the repository's payload folder -- the stage must never be under payload/";
        foreach (string forbidden in forbiddenRoots)
            if (Files.IsInside(full, forbidden) || Files.IsInside(forbidden, full))
                return $"--stage '{full}' overlaps {forbidden} -- name a folder of its own";
        if (File.Exists(full))
            return $"--stage '{full}' is a file";
        string payload = PayloadDir(full);
        if (File.Exists(payload))
            return $"{payload} is a file";
        if (Directory.Exists(payload) && !File.Exists(Path.Combine(payload, MarkerFile)))
            return $"{payload} is a folder the runner did not make (no {MarkerFile} in it) -- name another --stage folder";
        return null;
    }

    /// <summary>Rebuilds <c>&lt;stage&gt;\payload</c>: deleted (only when it carries the marker), copied from the source
    /// payload, the DevTest DLL put in the entry's place and that entry's sha256 rewritten. Returns the sha256 of the
    /// staged DLL, proven equal to <paramref name="expectedSha"/>.</summary>
    public static string Build(string stageDir, string sourcePayload, string version, InjectEntry entry,
        string devDll, string expectedSha, RunLog log)
    {
        string payload = PayloadDir(stageDir);
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        if (Directory.Exists(payload))
        {
            if (!File.Exists(Path.Combine(payload, MarkerFile)))
                throw new Refusal($"{payload} is a folder the runner did not make (no {MarkerFile} in it)");
            Directory.Delete(payload, recursive: true);
        }
        // The marker first: a copy cut halfway leaves a folder the next run still knows as its own.
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, MarkerFile),
            "The DevTest runner's staged payload: rebuilt from the repository's payload before every session.\r\n");
        int copied = Files.CopyTree(sourcePayload, payload);

        string dllDst = Payload.SourcePath(payload, entry.Src);
        Directory.CreateDirectory(Path.GetDirectoryName(dllDst)!);
        File.Copy(devDll, dllDst, overwrite: true);
        string staged = Files.Sha256(dllDst);
        if (!Files.SameHash(staged, expectedSha))
            throw new Refusal($"{devDll} changed while it was staged (sha256 {staged}, the preflight read {expectedSha}) -- is a build still writing it?");
        if (!DevBuild.CarriesMarker(dllDst))
            throw new Refusal($"the staged copy of {devDll} carries no DevTest marker");

        string manifest = Path.Combine(payload, "manifest.json");
        Files.WriteAtomic(manifest, entry.Synthetic
            ? Payload.AddEntry(File.ReadAllBytes(manifest), version, entry, staged)
            : Payload.RewriteSha(File.ReadAllBytes(manifest), version, entry, staged));

        // Every file of the version's plan in the copy is what the staged manifest says it is.
        foreach (var (src, sha) in Payload.PlanHashes(payload, version))
        {
            if (sha.Length == 0) continue;
            string file = Payload.SourcePath(payload, src);
            if (!File.Exists(file)) throw new Refusal($"the staged payload lacks {src}");
            string got = Files.Sha256(file);
            if (!Files.SameHash(got, sha)) throw new Refusal($"the staged {src} hashes to {got}, the staged manifest says {sha}");
        }
        log.Line(entry.Synthetic
            ? $"stage: {copied} payload files copied to {payload}; {entry.Src} is the DevTest build (sha256 {staged}), in an entry of the stage's own -- the payload ships none for {version}"
            : $"stage: {copied} payload files copied to {payload}; {entry.Src} is the DevTest build (sha256 {staged})");
        return staged;
    }

    /// <summary>Makes the stage folder the base directory of this process, so that <see cref="GamePatcher.PayloadRoot"/>
    /// names the staged payload, and proves it does.</summary>
    public static void Activate(string stageDir)
    {
        string baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stageDir)) + Path.DirectorySeparatorChar;
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", baseDir);
        string expected = Path.Combine(baseDir, "payload");
        string? root = GamePatcher.PayloadRoot();
        if (root is null || !Files.SamePath(root, expected))
            throw new Refusal($"the staged payload is not the one Relic's session would apply (payload root '{root ?? "none"}', expected '{expected}')");
    }
}
