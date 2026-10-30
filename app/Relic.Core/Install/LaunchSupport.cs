using System.Text.Json;
using Relic.Core.Util;

namespace Relic.Core.Install;

/// <summary>
/// What payload/manifest.json says of how a version's client starts through Relic's launch path on an
/// operating system — its "launch" table, one object per version:
/// <c>"launch": {"&lt;version&gt;": {"windows11": "unsupported"}}</c>. A version whose client is not known to
/// start on Windows 11 that way is not refused there: PLAY asks first, and "Play anyway" goes on with the
/// normal launch for that OS (<see cref="AskOnWindows11"/>). Read like the manifest's other tables
/// (<see cref="ClientIdentity"/>): whatever is absent or cannot be read asks nothing.
/// </summary>
public static class LaunchSupport
{
    /// <summary>The "windows11" value of a version whose client is not known to start on Windows 11
    /// through Relic's launch path. Any other value says nothing.</summary>
    public const string Unsupported = "unsupported";

    private static readonly HashSet<string> Noted = new(StringComparer.Ordinal);

    /// <summary>
    /// Does the manifest of <paramref name="payloadRoot"/> (the shipped payload when null) say that the
    /// client of <paramref name="versionId"/> is not known to start on Windows 11 through Relic's launch
    /// path? The version is matched as the manifest's "versions" are, without regard to letter case. False
    /// for a build without a payload, a manifest that is missing or is no JSON object, a "launch" that is
    /// not an object, a version it does not name, an entry that is not an object and any "windows11" value
    /// but <see cref="Unsupported"/>. Never throws.
    /// </summary>
    public static bool Windows11Unsupported(string versionId, string? payloadRoot = null)
    {
        string? root = payloadRoot ?? GamePatcher.PayloadRoot();
        if (root is null || string.IsNullOrWhiteSpace(versionId)) return false;
        string manifest = Path.Combine(root, "manifest.json");
        try
        {
            if (!File.Exists(manifest)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("launch", out var launch) || launch.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var entry in launch.EnumerateObject())
            {
                if (!string.Equals(entry.Name, versionId.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                return entry.Value.ValueKind == JsonValueKind.Object
                    && entry.Value.TryGetProperty("windows11", out var w11) && w11.ValueKind == JsonValueKind.String
                    && w11.GetString() == Unsupported;
            }
            return false;
        }
        catch (Exception ex)
        {
            // The init state asks for every version at every reply: the reason is logged once per file.
            bool first;
            lock (Noted) first = Noted.Add(manifest + "|" + ex.Message);
            if (first) Log.Info($"launch: {manifest} could not be read ({ex.Message}) — no version asks before PLAY");
            return false;
        }
    }

    /// <summary>Whether PLAY asks before it launches: the version's client is not known to start on
    /// Windows 11 through Relic's launch path (<see cref="Windows11Unsupported"/>) and this PC runs
    /// Windows 11 (<c>WindowsInfo.Detect().IsWindows11</c>). Pure.</summary>
    public static bool AskOnWindows11(bool windows11Unsupported, bool isWindows11) => windows11Unsupported && isWindows11;
}
