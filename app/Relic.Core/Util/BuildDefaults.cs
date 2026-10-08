using System.Reflection;

namespace Relic.Core.Util;

/// <summary>
/// Defaults baked into the ship build by build/publish.ps1 (--default-server= / --token=), carried
/// as AssemblyMetadata attributes on Relic.Core. A plain dev build (dotnet run / build without the
/// properties) has no token — the app then reports "server not configured" until the advanced
/// settings are filled in. The single ServerHost is used BOTH for the game redirect (Fiddler rules) and as the
/// agent's host, so one hostname/IP drives the whole app.
/// </summary>
public static class BuildDefaults
{
    /// <summary>The default server host baked by publish.ps1 (ex: game.example.com). Empty in a build
    /// without a baked server — the start screen then asks the user for an address.</summary>
    public static string ServerHost { get; } = Read("Relic.DefaultServer") ?? "";

    /// <summary>Agent bearer token baked at publish time; "" in dev builds.</summary>
    public static string AgentToken { get; } = Read("Relic.AgentToken") ?? "";

    private static string? Read(string key) => typeof(BuildDefaults).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key && !string.IsNullOrWhiteSpace(a.Value))?.Value;
}
