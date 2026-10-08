using System.Security.Cryptography;
using System.Text;
using Relic.Core.Util;

namespace Relic.Core.Server;

/// <summary>
/// The build identity of an agent: one sha256 over the agent script and the payload files that came with
/// it — the twin of <c>build_identity</c> in gio_agent.py, which reports the same value for the agent that
/// answers (<c>GET /status</c>, key <c>build</c>). The launcher compares two identities as they are:
/// whether a server runs the agent this launcher would install is a question of content, never of a
/// version number. Left out is what an agent or its admin adds to the payloads folder afterwards
/// (<see cref="Skips"/>), so a box that adopted a navmesh bundle still reads as the agent it was given.
/// </summary>
public static class AgentBuild
{
    public const string ScriptName = "gio_agent.py";
    public const string PayloadsName = "payloads";

    /// <summary>Lower case for A–Z only: the same answer as the agent's <c>_ascii_lower</c>, whatever
    /// other letters a file name holds.</summary>
    private static string AsciiLower(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
        return sb.ToString();
    }

    /// <summary>Whether the payload file <paramref name="rel"/> (its path inside the payloads folder, "/"
    /// between the segments) is left out of the identity — the agent's <c>identity_skips</c>: a segment
    /// that starts with "." or is "__pycache__", anywhere in the path; everything of a version folder
    /// whose own name there starts with "navmesh" (the bundle, its archive, a folder an adoption renamed
    /// aside); a *.zip or *.part file of the payloads folder itself or directly in a version folder. The
    /// letter case of A–Z does not matter. Pure.</summary>
    public static bool Skips(string rel)
    {
        var parts = (rel ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Select(AsciiLower).ToArray();
        if (parts.Length == 0) return true;
        foreach (var p in parts)
            if (p.StartsWith('.') || p == "__pycache__") return true;
        if (parts.Length >= 2 && parts[1].StartsWith("navmesh", StringComparison.Ordinal)) return true;
        string last = parts[^1];
        return parts.Length <= 2
            && (last.EndsWith(".zip", StringComparison.Ordinal) || last.EndsWith(".part", StringComparison.Ordinal));
    }

    /// <summary>Order by the UTF-8 bytes of the path — the agent sorts the same way, and the order of a
    /// string's UTF-16 units is another one for the characters outside the basic plane.</summary>
    private sealed class Utf8Order : IComparer<string>
    {
        public static readonly Utf8Order Instance = new();
        public int Compare(string? a, string? b) =>
            Encoding.UTF8.GetBytes(a ?? "").AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b ?? ""));
    }

    /// <summary>The identity of a script with this sha256 and these payload files (in any order): sha256
    /// over one line "&lt;sha256&gt;  &lt;path&gt;" per file — "gio_agent.py", then "payloads/&lt;rel&gt;" —
    /// sorted by the path's UTF-8 bytes, each line ended by "\n". Lower-case hex. Pure.</summary>
    public static string Digest(string scriptSha256, IEnumerable<(string Rel, string Sha256)> payloadHashes)
    {
        var lines = new List<(string Name, string Sha)> { (ScriptName, scriptSha256) };
        foreach (var (rel, sha) in payloadHashes) lines.Add((PayloadsName + "/" + rel, sha));
        lines.Sort((x, y) => Utf8Order.Instance.Compare(x.Name, y.Name));
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, sha) in lines)
            h.AppendData(Encoding.UTF8.GetBytes(sha + "  " + name + "\n"));
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    private static string FileSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>(rel, path) of the payload files the identity covers under <paramref name="payloadDir"/>,
    /// rel with "/" between the segments. A folder that does not exist holds none; a folder behind a link
    /// (a junction, a symbolic link) is not entered.</summary>
    public static List<(string Rel, string Path)> Files(string payloadDir)
    {
        var found = new List<(string Rel, string Path)>();
        if (!Directory.Exists(payloadDir)) return found;
        void Walk(string dir, string prefix)
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                string rel = prefix + Path.GetFileName(f);
                if (!Skips(rel)) found.Add((rel, f));
            }
            foreach (var d in Directory.GetDirectories(dir))
            {
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                Walk(d, prefix + Path.GetFileName(d) + "/");
            }
        }
        Walk(payloadDir, "");
        return found;
    }

    /// <summary>The identity of the agent the folder <paramref name="agentDir"/> holds
    /// (<c>gio_agent.py</c> and <c>payloads\</c> in it), everything read now. Null when there is no script
    /// in it; an unreadable file throws.</summary>
    public static string? Identity(string agentDir)
    {
        string script = Path.Combine(agentDir, ScriptName);
        if (!File.Exists(script)) return null;
        var hashes = Files(Path.Combine(agentDir, PayloadsName)).Select(f => (f.Rel, FileSha256(f.Path)));
        return Digest(FileSha256(script), hashes);
    }

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (string Stamp, string? Id)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="Identity"/>, hashed again only when a covered file's size or time changed (or a
    /// file came or went) since the last call for that folder. Null when the folder holds no script or a
    /// file in it cannot be read (logged, never thrown: the start screen asks).</summary>
    public static string? IdentityCached(string agentDir)
    {
        try
        {
            string script = Path.Combine(agentDir, ScriptName);
            if (!File.Exists(script)) return null;
            var sb = new StringBuilder();
            void Stamp(string rel, string path)
            {
                var fi = new FileInfo(path);
                sb.Append(rel).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
            }
            Stamp(ScriptName, script);
            foreach (var (rel, path) in Files(Path.Combine(agentDir, PayloadsName))) Stamp(PayloadsName + "/" + rel, path);
            string stamp = sb.ToString();
            lock (CacheLock)
                if (Cache.TryGetValue(agentDir, out var hit) && hit.Stamp == stamp) return hit.Id;
            string? id = Identity(agentDir);
            lock (CacheLock) Cache[agentDir] = (stamp, id);
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"agent build identity of {agentDir}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The identity of the agent this launcher ships; null for a build without one.</summary>
    public static string? Shipped => IdentityCached(LocalAgent.ShippedAgentDir);

    /// <summary>The identity of the agent files installed on this PC; null when none is installed.</summary>
    public static string? Installed => LocalAgent.IsInstalled ? IdentityCached(LocalAgent.Root) : null;

    /// <summary>Does an agent that reports <paramref name="reported"/> differ from the one this launcher
    /// ships (<paramref name="shipped"/>)? An agent that reports none — one without the key, or one that
    /// could not read its own files — is not the shipped one. Without a shipped agent there is nothing to
    /// compare with and nothing to offer. Pure.</summary>
    public static bool Differs(string? shipped, string? reported) =>
        !string.IsNullOrEmpty(shipped) && !string.Equals(shipped, reported ?? "", StringComparison.Ordinal);
}
