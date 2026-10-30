using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relic.Core.Util;

namespace Relic.Core.Server;

/// <summary>Agent connection details of a server — one record per host. Persisted only as a DPAPI-protected file in
/// the user's Relic secrets folder (<see cref="ServerConfigStore"/>) — never in the repository. Two modes:
/// "direct" — plain HTTP to Host:AgentPort with the bearer token (normal users; SSH fields unused);
/// "ssh"    — SSH local-forward to the agent's loopback bind (advanced/legacy setups).
/// <para><c>OpenKey</c>: the bearer is the key the agent hands out to every player while its admin lets
/// everyone administer the server (the Player page's "Enter admin mode"), not the agent token. It is
/// saved like a token but is never offered as one on the start screen, and a 401 on it means the admin
/// closed the option — the launcher then forgets it and returns to Player mode.</para></summary>
public sealed record ServerConfig(
    string Host,
    int Port,
    string User,
    string Password,
    string AgentToken,
    int AgentPort = 18080,
    string Mode = "ssh", // older DPAPI files have no Mode — they were all tunnel setups
    bool OpenKey = false) // a file without the field holds a token the admin typed or an install saved
{
    public bool IsDirect => string.Equals(Mode, "direct", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the record prints (an interpolation, a debugger): where it connects and how — never the SSH
    /// password or the agent token.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Host = ").Append(Host).Append(", Port = ").Append(Port).Append(", User = ").Append(User)
            .Append(", AgentPort = ").Append(AgentPort).Append(", Mode = ").Append(Mode)
            .Append(", OpenKey = ").Append(OpenKey);
        return true;
    }

    /// <summary>The zero-setup config every ship build carries: direct mode against the baked-in
    /// host + token. Null in dev builds (no token baked), meaning "not configured".</summary>
    public static ServerConfig? BuiltInDefault() => string.IsNullOrWhiteSpace(BuildDefaults.AgentToken)
        ? null
        : new ServerConfig(BuildDefaults.ServerHost, 22, "root", "", BuildDefaults.AgentToken, 18080, "direct");

    /// <summary>Same server? Hosts are compared case-insensitively (DNS names) after trimming; an
    /// empty host is "no server" and never the same as anything.</summary>
    public static bool SameHost(string? a, string? b)
    {
        string x = (a ?? "").Trim().TrimEnd('.'), y = (b ?? "").Trim().TrimEnd('.');
        return x.Length > 0 && y.Length > 0 && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The connection the app ACTUALLY uses for admin calls. The host and the agent
    /// port are the ones the user configured — <c>RelicSettings.ServerHost/AgentPort</c>, written by the
    /// start screen and the agent installs — never the host remembered inside server.json: that file
    /// only contributes the bearer token (and SSH details for a tunnel override), and only when it was
    /// saved for THIS host. A stored token for another server is not a token for this one; the build's
    /// baked token (if any) applies to any host. Null when no token applies. The open-key flag travels
    /// with the token it describes: only the stored bearer can be the key the agent handed out, so the
    /// baked token that applies when the file lends none is never flagged.
    /// </summary>
    public static ServerConfig? Effective(ServerConfig? stored, string? host, int agentPort)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        host = host.Trim();
        bool same = stored is not null && SameHost(stored.Host, host);
        string token = same ? stored!.AgentToken ?? "" : "";
        bool openKey = same && token.Length > 0 && stored!.OpenKey;
        if (token.Length == 0) token = BuildDefaults.AgentToken;
        if (token.Length == 0) return null;
        // An SSH tunnel override keeps its own remote agent port (the agent's loopback bind on the box).
        if (same && !stored!.IsDirect) return stored with { Host = host, AgentToken = token };
        return new ServerConfig(host, same ? stored!.Port : 22, same ? stored!.User : "root",
            same ? stored!.Password : "", token, agentPort > 0 ? agentPort : 18080, "direct", openKey);
    }

    /// <summary>Which server the stored/baked token applies to: "any" (a baked token exists), the
    /// stored file's host, or "" (no token at all). The start screen uses it to say whether the token
    /// field may stay empty for the address being typed. An open key is no saved token: the start
    /// screen must ask for the agent token as if nothing were stored.</summary>
    public static string TokenScope(ServerConfig? stored) =>
        !string.IsNullOrWhiteSpace(BuildDefaults.AgentToken) ? "any"
        : stored is not null && !string.IsNullOrWhiteSpace(stored.AgentToken) && !stored.OpenKey ? stored.Host : "";

    /// <summary>The hosts of <paramref name="records"/> that hold a saved agent token — as saved, trimmed, once per
    /// host (<see cref="SameHost"/>; the first record of a host gives its spelling), in the records' order. A record
    /// whose bearer is an open-administration key (<see cref="OpenKey"/>) or whose token is empty names none: the
    /// start screen asks for the agent token there as if nothing were saved. Host names alone — never a token.</summary>
    public static IReadOnlyList<string> TokenHosts(IEnumerable<ServerConfig?>? records)
    {
        var hosts = new List<string>();
        foreach (var r in records ?? Array.Empty<ServerConfig?>())
        {
            if (r is null || r.OpenKey || string.IsNullOrWhiteSpace(r.AgentToken)) continue;
            string host = (r.Host ?? "").Trim();
            if (host.Length == 0 || hosts.Any(h => SameHost(h, host))) continue;
            hosts.Add(host);
        }
        return hosts;
    }
}

/// <summary>
/// <c>server.json</c>: the saved <see cref="ServerConfig"/> records, at most one per host (<see cref="ServerConfig.SameHost"/>),
/// the most recently saved first. The file holds agent bearer tokens (and the SSH details of a tunnel override), so it is
/// protected with per-user DPAPI — no elevation needed, unreadable to other accounts and backup readers. Its content is
/// the UTF-8 JSON object <c>{"servers": [&lt;record&gt;, …]}</c>; a document whose root is a single record reads as a list
/// of one — the two forms are told apart by their shape, an object with a <c>servers</c> array being the list. A plaintext
/// file of either form is read and written again protected.
/// <para>Every read, write and delete of the file runs under <see cref="Gate"/>, so an upsert never writes back a list
/// another thread changed meanwhile.</para>
/// </summary>
public static class ServerConfigStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Relic", "secrets", "server.json");

    /// <summary>The one lock on the file in this process — also the lock under which a caller replaces the copy of the
    /// records it holds. Nothing done under it touches the launcher's state, so <c>RelicState.Gate</c> may be held when
    /// this one is taken, never the other way round: never call into <c>RelicState</c> (its <c>Save</c> included) while
    /// holding this lock.</summary>
    public static readonly object Gate = new();

    /// <summary>Every saved record, the most recently saved first. Never throws: a file that cannot be read, decrypted
    /// or parsed reads as none (one log line). A plaintext file is written again protected (best effort: a file that
    /// cannot be written is still used as read).</summary>
    public static IReadOnlyList<ServerConfig> LoadAll(string? path = null)
    {
        path ??= DefaultPath;
        lock (Gate)
        {
            Records read;
            try { read = ReadRecords(path); }
            catch (Exception ex)
            {
                // Callers (Backend's constructor, at app startup) treat no record as "not configured";
                // an unreadable file must never kill the launcher.
                Log.Error($"server.json unreadable/corrupt ({path}) — starting without the server configuration", ex);
                return Array.Empty<ServerConfig>();
            }
            if (read.Unusable is { } why)
            {
                Log.Error($"server.json unreadable/corrupt ({path}) — starting without the server configuration", why);
                return Array.Empty<ServerConfig>();
            }
            if (read.Plaintext && read.Found.Count > 0)
            {
                try
                {
                    WriteRecords(path, read.Found);
                    LogDropped(read, path);
                }
                catch (Exception ex) { Log.Error("protected re-save of server.json failed (using the config as read)", ex); }
            }
            return read.Found;
        }
    }

    /// <summary>The most recently saved record — the record a file of one held — or null when there is none.
    /// Never throws (<see cref="LoadAll"/>). A caller that needs the token of a given host reads <see cref="LoadFor"/>:
    /// with several hosts saved, the newest record is any host's.</summary>
    public static ServerConfig? Load(string? path = null) => LoadAll(path).FirstOrDefault();

    /// <summary>The record saved for <paramref name="host"/> (<see cref="ServerConfig.SameHost"/>), whichever host was
    /// saved last — or null when that host has none (and for no host). Never throws (<see cref="LoadAll"/>).</summary>
    public static ServerConfig? LoadFor(string? host, string? path = null) =>
        string.IsNullOrWhiteSpace(host) ? null : LoadAll(path).FirstOrDefault(r => ServerConfig.SameHost(r.Host, host));

    /// <summary>How many copies of files it replaced <see cref="Save"/> keeps beside the file at most: a newer copy that
    /// would make one more takes the place of the oldest.</summary>
    public const int UnreadableCopiesKept = 3;

    /// <summary>What a kept copy's name adds to the file's own name, before the time the copy was made.</summary>
    private const string UnreadableMark = ".unreadable-";

    /// <summary>The time in a kept copy's name: UTC to the millisecond, so the names sort as the copies were made.</summary>
    private const string UnreadableStamp = "yyyyMMddHHmmssfff";

    /// <summary>The first bytes of every DPAPI blob: its version (1) and its provider's GUID.</summary>
    private static readonly byte[] DpapiHeader =
        { 0x01, 0x00, 0x00, 0x00, 0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11, 0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB };

    /// <summary>Whether <paramref name="raw"/> is a DPAPI blob — it starts as every one does —, protected for this user
    /// or for another, whole or damaged. A file written in the clear never starts that way.</summary>
    public static bool IsProtectedBlob(ReadOnlySpan<byte> raw) =>
        raw.Length > DpapiHeader.Length && raw[..DpapiHeader.Length].SequenceEqual(DpapiHeader);

    /// <summary>The copies <see cref="Save"/> kept of files it replaced because they could not be decrypted or parsed,
    /// the oldest first: beside the file, each named <c>server.json.unreadable-&lt;yyyyMMddHHmmssfff&gt;</c> (UTC) and
    /// holding the replaced file as it was. Only a protected file is kept, so every copy is a DPAPI blob — nothing is
    /// decrypted or written in the clear —; a copy is never written over, and at most <see cref="UnreadableCopiesKept"/>
    /// stay. Never throws: a folder that cannot be listed holds none (one log line).</summary>
    public static IReadOnlyList<string> UnreadableCopies(string? path = null)
    {
        path = Path.GetFullPath(path ?? DefaultPath);
        string prefix = Path.GetFileName(path) + UnreadableMark;
        lock (Gate)
        {
            try
            {
                string dir = Path.GetDirectoryName(path)!;
                if (!Directory.Exists(dir)) return Array.Empty<string>();
                return Directory.EnumerateFiles(dir, prefix + "*")
                    .Where(f => Path.GetFileName(f) is var name && name.Length == prefix.Length + UnreadableStamp.Length
                        && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name[prefix.Length..].All(char.IsAsciiDigit))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"server.json: the copies kept beside {path} cannot be listed", ex);
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>
    /// Saves <paramref name="cfg"/> as the record of its host: every earlier record of that host goes, the records of
    /// the other hosts stay, and <paramref name="cfg"/> comes first. A file that exists but cannot be read for an I/O
    /// reason (another program holds it) throws, and the file stays as it was — it is never written over with a list
    /// that lost the other hosts' records. A file that cannot be decrypted or parsed is replaced (one log line); a
    /// protected one is kept beside it first, as a copy of its own (<see cref="UnreadableCopies"/>): the tokens of every
    /// host it held may be readable again later (a DPAPI key that comes back), and no copy of them exists anywhere else.
    /// A file written in the clear is replaced without a copy: a copy would carry its tokens readable outside DPAPI, and
    /// outlive it.
    /// Written to a temporary file and renamed over the old one: a crash mid-write never truncates the only copy of
    /// the credentials (a token has to keep matching the one deployed inside the agent).
    /// </summary>
    public static void Save(ServerConfig cfg, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        path ??= DefaultPath;
        lock (Gate)
        {
            var read = ReadRecords(path);
            if (read.Unusable is { } why)
            {
                Log.Error($"server.json could not be decrypted or parsed ({path}) — replaced by the record being saved", why);
                KeepUnreadable(path, read.Raw ?? Array.Empty<byte>());
            }
            var list = new List<ServerConfig>(read.Found.Count + 1) { cfg };
            list.AddRange(read.Found.Where(r => !ServerConfig.SameHost(r.Host, cfg.Host)));
            WriteRecords(path, list);
            LogDropped(read, path);
        }
    }

    /// <summary>Removes the records of <paramref name="host"/> (<see cref="ServerConfig.SameHost"/>); the file is
    /// deleted with the last record. True when something was removed. A file that cannot be read for an I/O reason
    /// throws; one that cannot be decrypted or parsed holds nothing to remove and stays as it is.</summary>
    public static bool Forget(string host, string? path = null) => ForgetWhere(host, _ => true, path);

    /// <summary>Removes the records of <paramref name="host"/> that <paramref name="which"/> picks, by the rules of
    /// <see cref="Forget(string, string?)"/> — <c>r =&gt; r.OpenKey</c> forgets the host's open-administration key and
    /// leaves a real token saved for the host meanwhile where it is. True when something was removed.</summary>
    public static bool ForgetWhere(string host, Func<ServerConfig, bool> which, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(which);
        path ??= DefaultPath;
        if (string.IsNullOrWhiteSpace(host)) return false;
        lock (Gate)
        {
            var read = ReadRecords(path);
            if (read.Unusable is { } why)
            {
                Log.Error($"server.json could not be decrypted or parsed ({path}) — it holds no record to remove", why);
                return false;
            }
            var rest = read.Found.Where(r => !(ServerConfig.SameHost(r.Host, host) && which(r))).ToList();
            if (rest.Count == read.Found.Count) return false;
            if (rest.Count == 0) File.Delete(path);
            else WriteRecords(path, rest);
            LogDropped(read, path);
            return true;
        }
    }

    /// <summary>A fresh random bearer token for the agent.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>Deletes the whole file — every host's record — so the app falls back to the build's built-in default; the
    /// copies of unreadable files kept beside it (<see cref="UnreadableCopies"/>) go with it, and so do the temporary files
    /// a cut write can leave there: a copy's own (<c>server.json.unreadable.tmp</c>) and <see cref="WriteRecords"/>'s
    /// (<c>server.json.tmp</c>, which holds the records it was about to rename over the file) — best effort: a file that
    /// cannot be deleted is one log line, never a failed delete.</summary>
    public static void Delete(string? path = null)
    {
        path ??= DefaultPath;
        lock (Gate)
        {
            if (File.Exists(path)) File.Delete(path);
            foreach (string copy in UnreadableCopies(path).Append(UnreadableTmp(path)).Append(path + ".tmp"))
            {
                try { if (File.Exists(copy)) File.Delete(copy); }
                catch (Exception ex) { Log.Error($"server.json: deleting the kept copy {copy} failed", ex); }
            }
        }
    }

    /// <summary>What a read of the file found: the usable records (most recently saved first), whether the file was
    /// plaintext, for a file that could not be decrypted or parsed why (the records are then none) and its bytes as read,
    /// and how many elements of its list were passed over (not an object, not a record, no host).</summary>
    private sealed record Records(List<ServerConfig> Found, bool Plaintext, Exception? Unusable, int Skipped = 0, byte[]? Raw = null);

    /// <summary>One log line when a write dropped elements of the list that could not be read as records — their count,
    /// nothing of their content.</summary>
    private static void LogDropped(Records read, string path)
    {
        if (read.Skipped > 0)
            Log.Info($"server.json: {read.Skipped} element(s) of its list that could not be read as a record were dropped by this write ({path})");
    }

    /// <summary>Where a copy is written before it is renamed to its name — never a kept copy's name.</summary>
    private static string UnreadableTmp(string path) => path + ".unreadable.tmp";

    /// <summary>A kept copy's name for the time <paramref name="utc"/>.</summary>
    private static string UnreadableCopyName(string path, DateTime utc) =>
        path + UnreadableMark + utc.ToString(UnreadableStamp, CultureInfo.InvariantCulture);

    /// <summary>The time in a kept copy's name, or null.</summary>
    private static DateTime? UnreadableCopyTime(string copy) =>
        DateTime.TryParseExact(Path.GetFileName(copy)[^UnreadableStamp.Length..], UnreadableStamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    /// <summary>
    /// Keeps the file <see cref="Save"/> is about to replace because it could not be decrypted or parsed — its bytes as
    /// read, <paramref name="raw"/> — as a copy of its own (<see cref="UnreadableCopies"/>), when it is a protected file:
    /// written beside the copy's place and renamed to a name no copy has (later than every kept one, whatever the clock
    /// did meanwhile), so a cut copy is never taken for a kept one and no copy is ever written over. Bytes a copy already
    /// holds (the same file met again, a save that failed after keeping it) are not kept twice; past
    /// <see cref="UnreadableCopiesKept"/> copies the oldest go. A file written in the clear is not kept. Best effort, one
    /// log line either way and never the file's content: the save goes on without the copy.
    /// </summary>
    private static void KeepUnreadable(string path, byte[] raw)
    {
        if (!IsProtectedBlob(raw))
        {
            Log.Info($"server.json: the file that could not be read is no protected file — it is replaced without a copy ({path})");
            return;
        }
        string tmp = UnreadableTmp(path);
        try
        {
            var kept = UnreadableCopies(path);
            if (kept.FirstOrDefault(k => SameBytes(k, raw)) is { } same)
            {
                Log.Info($"server.json: the file that could not be read is already kept as {same}");
                return;
            }
            DateTime at = DateTime.UtcNow;
            if (kept.Count > 0 && UnreadableCopyTime(kept[^1]) is { } newest && newest >= at) at = newest.AddMilliseconds(1);
            string copy = UnreadableCopyName(path, at);
            while (File.Exists(copy)) copy = UnreadableCopyName(path, at = at.AddMilliseconds(1));
            File.WriteAllBytes(tmp, raw);
            File.Move(tmp, copy, overwrite: false);
            Log.Info($"server.json: the file that could not be read is kept as {copy}");
            var all = UnreadableCopies(path);
            foreach (string old in all.Take(all.Count - UnreadableCopiesKept))
            {
                try
                {
                    File.Delete(old);
                    Log.Info($"server.json: the oldest kept copy {old} went (at most {UnreadableCopiesKept} are kept)");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Error($"server.json: the oldest kept copy {old} could not be deleted", ex);
                }
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            Log.Error($"server.json: keeping the file that could not be read beside {path} failed", ex);
        }
    }

    /// <summary>Whether the file at <paramref name="file"/> holds exactly <paramref name="raw"/> (false when it cannot be
    /// read).</summary>
    private static bool SameBytes(string file, byte[] raw)
    {
        try { return new FileInfo(file).Length == raw.Length && File.ReadAllBytes(file).AsSpan().SequenceEqual(raw); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The document of the file: <c>{"servers": [...]}</c>.</summary>
    private sealed class Document
    {
        [JsonPropertyName("servers")]
        public List<ServerConfig> Servers { get; set; } = new();
    }

    /// <summary>Parses the file, and never writes it. A missing file holds no record; an I/O failure throws; a file that
    /// cannot be decrypted or parsed holds no record and says why; an element of <c>servers</c> that is not an object,
    /// cannot be read as a record or names no host is passed over (and counted).</summary>
    private static Records ReadRecords(string path)
    {
        byte[] raw;
        try
        {
            if (!File.Exists(path)) return new Records(new List<ServerConfig>(), false, null);
            raw = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Records(new List<ServerConfig>(), false, null);
        }

        byte[] json;
        bool plaintext = false;
        Exception? notProtected = null;
        try
        {
            json = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            // No DPAPI blob of this user: a plaintext file, or nothing this user can read.
            json = raw;
            plaintext = true;
            notProtected = ex;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("server.json: the document is not a JSON object");
            var list = new List<ServerConfig>();
            int skipped = 0;
            if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in servers.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) { skipped++; continue; }
                    ServerConfig? r;
                    try { r = element.Deserialize<ServerConfig>(); }
                    catch (JsonException) { skipped++; continue; }
                    if (r is not null && !string.IsNullOrWhiteSpace(r.Host)) list.Add(r);
                    else skipped++;
                }
            }
            else if (root.Deserialize<ServerConfig>() is { } single && !string.IsNullOrWhiteSpace(single.Host))
            {
                list.Add(single);
            }
            return new Records(list, plaintext, null, skipped);
        }
        catch (JsonException ex)
        {
            return new Records(new List<ServerConfig>(), false, notProtected ?? ex, Raw: raw);
        }
    }

    /// <summary>Writes <paramref name="records"/> protected, to a temporary file renamed over the old one.</summary>
    private static void WriteRecords(string path, IReadOnlyList<ServerConfig> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] blob = ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(new Document { Servers = records.ToList() }), null, DataProtectionScope.CurrentUser);
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort: the file itself is untouched */ }
            throw;
        }
    }
}
