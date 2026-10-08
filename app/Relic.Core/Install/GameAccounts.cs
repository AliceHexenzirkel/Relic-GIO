using System.Text.Json;
using Relic.Core.Util;

namespace Relic.Core.Install;

/// <summary>
/// The account the player logs in with IN THE GAME (the client's login screen) — unrelated to any
/// Relic account or to the Windows account. Each version ships with a pre-made save in
/// <c>agent/payloads/&lt;version&gt;/</c>, and that save's account is written in its manifest
/// ("aether" on 1.6, "Aetherr" on 2.8). The password is NOT verified by the server (see
/// docs/SERVER-DEPLOY.md), so it is only ever shown as an EXAMPLE, never as a required value.
///
/// The manifest takes precedence over the catalogue: it is the very file that describes the save
/// imported on the server, so it cannot lag behind when the payload changes. The <c>account</c> field
/// in versions.json remains the safety net for a build shipped without the agent payloads —
/// publish.ps1 only warns in that case, it does not stop the build.
/// </summary>
public static class GameAccounts
{
    /// <summary>The password shown as an example when the manifest does not give a concrete one.</summary>
    public const string DefaultPasswordExample = "123";

    // "orice"/"any" mean "type anything", not a password — they do not belong in an "e.g.: ..." hint.
    private static readonly string[] GenericPasswords = { "orice", "oricare", "any", "whatever", "n/a", "-" };

    private static readonly Dictionary<string, (string Account, string Password)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the agent payloads live next to the exe. Parameterizable only so the spike can
    /// check it, exactly like <see cref="GamePatcher"/>'s payload root.</summary>
    public static string DefaultPayloadsRoot => Path.Combine(AppContext.BaseDirectory, "agent", "payloads");

    /// <summary>The account + an example password for a catalogue entry. An empty account means
    /// unknown, and the UI must stay silent in that case rather than invent a name.</summary>
    public static (string Account, string PasswordExample) For(GameVersionInfo v, string? payloadsRoot = null)
    {
        var (account, password) = FromPayload(string.IsNullOrWhiteSpace(v.Server) ? v.Id : v.Server, payloadsRoot);
        if (account.Length == 0) account = (v.Account ?? "").Trim();
        return (account, Example(password));
    }

    /// <summary>The same information starting from the version id alone (the shortcut's launch
    /// window, which does not have the catalogue loaded yet).</summary>
    public static (string Account, string PasswordExample) ForVersion(string versionId, string? payloadsRoot = null)
    {
        var v = VersionCatalog.Load().FirstOrDefault(x => string.Equals(x.Id, versionId, StringComparison.OrdinalIgnoreCase));
        if (v is not null) return For(v, payloadsRoot);
        var p = FromPayload(versionId, payloadsRoot);
        return (p.Account, Example(p.Password));
    }

    /// <summary>The account for the CONFIGURED server, the server's word winning: an account the player
    /// created through Relic on it → the answer the server gave last time (<see cref="RelicState.ServerAccounts"/>;
    /// "" = it said there is none — a stack prepared without the default save — and the
    /// caller must show nothing) → the manifest/catalogue fallback, which applies only BEFORE this server
    /// ever answered. The shortcut splash reads this: it has no server status of its own.</summary>
    public static (string Account, string PasswordExample) ForVersion(string versionId, State.RelicState state, string? payloadsRoot = null)
    {
        var fallback = ForVersion(versionId, payloadsRoot);
        if (state is null) return fallback;
        string host = state.Settings.ServerHost ?? "";
        int port = state.Settings.ServerPort;
        if (host.Length == 0) return fallback;
        if (state.AccountFor(host, port, versionId) is { Name.Length: > 0 } mine) return (mine.Name, fallback.PasswordExample);
        if (state.ServerAccountFor(host, port, versionId) is { } said) return (said.Trim(), fallback.PasswordExample);
        return fallback;
    }

    private static (string Account, string Password) FromPayload(string key, string? payloadsRoot)
    {
        string root = string.IsNullOrWhiteSpace(payloadsRoot) ? DefaultPayloadsRoot : payloadsRoot!;
        key = (key ?? "").Trim();
        // The key goes into a path: an id with separators or ".." opens nothing, not another folder.
        if (key.Length == 0 || key.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || key.Contains("..")) return ("", "");
        lock (Cache)
        {
            // The cache key carries the root too: otherwise a spike reading from a synthetic folder
            // would poison the answer for the real payload (and vice versa).
            string cacheKey = root + "|" + key;
            if (Cache.TryGetValue(cacheKey, out var hit)) return hit;
            var result = ("", "");
            try
            {
                string path = Path.Combine(root, key, "manifest.json");
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        result = (Str(doc.RootElement, "account"), Str(doc.RootElement, "password"));
                }
            }
            catch (Exception ex)
            {
                // A broken manifest must not break app startup — only the account display is lost.
                Log.Error($"reading the account from the payload ({key})", ex);
            }
            Cache[cacheKey] = result;
            return result;
        }
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "").Trim() : "";

    private static string Example(string password) =>
        password.Length == 0 || GenericPasswords.Contains(password, StringComparer.OrdinalIgnoreCase)
            ? DefaultPasswordExample : password;
}
