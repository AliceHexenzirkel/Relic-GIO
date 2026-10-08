using System.Security.Cryptography;
using System.Text.Json;
using Relic.Core.Util;

namespace Relic.Core.Server;

/// <summary>Agent connection details of a server. Persisted only as a DPAPI-protected file in the user's
/// Relic secrets folder — never in the repository. Two modes:
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
}

public static class ServerConfigStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Relic", "secrets", "server.json");

    /// <summary>
    /// The file holds the ROOT SSH password + agent bearer token, so it is protected with per-user
    /// DPAPI (no elevation needed, unreadable to other accounts/backup readers). Files written by
    /// older builds were plaintext JSON — those are accepted once and immediately re-saved protected.
    /// </summary>
    public static ServerConfig? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            byte[] raw = File.ReadAllBytes(path);
            try
            {
                byte[] plain = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<ServerConfig>(plain);
            }
            catch (CryptographicException)
            {
                var legacy = JsonSerializer.Deserialize<ServerConfig>(raw);
                if (legacy is not null)
                {
                    // Best-effort upgrade: a read-only/locked file must not prevent USING the
                    // config that was just parsed fine — the next successful Save protects it.
                    try { Save(legacy, path); }
                    catch (Exception ex) { Log.Error("protected re-save of server.json failed (using the config as read)", ex); }
                }
                return legacy;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Callers (Backend's constructor, at app startup) treat null as "not configured";
            // an unreadable/corrupt file must never kill the launcher.
            Log.Error($"server.json unreadable/corrupt ({path}) — starting without the server configuration", ex);
            return null;
        }
    }

    public static void Save(ServerConfig cfg, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] blob = ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(cfg), null, DataProtectionScope.CurrentUser);
        // tmp + rename: a crash mid-write must not truncate the ONLY copy of the credentials
        // (the AgentToken has to keep matching the one already deployed inside the Linux agent).
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A fresh random bearer token for the agent.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>Drop the saved override so the app falls back to the build's built-in default.</summary>
    public static void Delete(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path)) File.Delete(path);
    }
}
