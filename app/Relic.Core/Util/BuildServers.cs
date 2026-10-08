using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Relic.Core.Util;

/// <summary>
/// Default servers and start mode baked into a ship build by build/publish.ps1
/// (<c>--default-server</c>, <c>--default-servers="Label=host[:port];…"</c>, <c>--default-mode</c>).
/// The list travels as base64(JSON) in the <c>Relic.DefaultServersB64</c> AssemblyMetadata attribute
/// because MSBuild splits <c>-p:</c> values on ';'. A build without them simply offers no quick picks —
/// the start screen asks for an address.
/// </summary>
public static class BuildServers
{
    public sealed record Entry(string Label, string Host);

    /// <summary>Quick-pick servers (label + host[:port]); empty when nothing was baked.</summary>
    public static IReadOnlyList<Entry> Servers { get; } = Load();

    /// <summary>"player" | "admin" | "" (no preselected mode).</summary>
    public static string DefaultMode { get; } = NormalizeMode(Read("Relic.DefaultMode"));

    /// <summary>Decode the base64(JSON [{label,host}]) list; invalid input → empty list.</summary>
    public static IReadOnlyList<Entry> Decode(string? b64)
    {
        var list = new List<Entry>();
        if (string.IsNullOrWhiteSpace(b64)) return list;
        try
        {
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim()));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string host = e.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? (h.GetString() ?? "").Trim() : "";
                string label = e.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? (l.GetString() ?? "").Trim() : "";
                if (host.Length == 0) continue;
                list.Add(new Entry(label.Length > 0 ? label : host, host));
            }
        }
        catch { list.Clear(); }
        return list;
    }

    private static IReadOnlyList<Entry> Load()
    {
        var fromList = Decode(Read("Relic.DefaultServersB64"));
        if (fromList.Count > 0) return fromList;
        string single = (Read("Relic.DefaultServer") ?? "").Trim();
        return single.Length > 0 ? new[] { new Entry(single, single) } : Array.Empty<Entry>();
    }

    private static string NormalizeMode(string? m)
    {
        m = (m ?? "").Trim().ToLowerInvariant();
        return m is "player" or "admin" ? m : "";
    }

    private static string? Read(string key) => typeof(BuildServers).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key && !string.IsNullOrWhiteSpace(a.Value))?.Value;
}
