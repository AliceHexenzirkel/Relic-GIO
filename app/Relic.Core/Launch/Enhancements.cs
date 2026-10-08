using Relic.Core.Install;
using Relic.Core.Util;

namespace Relic.Core.Launch;

/// <summary>
/// The optional in-game enhancements (the F1 menu): which extra DLLs, if any, launcher.exe has to
/// inject after mhynot2.dll for a given version. A pure lookup over the payload manifest's
/// <see cref="PatchEntry.Inject"/> entries and the files on disk — nothing here copies or deletes
/// anything (that is <see cref="GamePatcher"/>'s job) and nothing is persisted: the list is computed
/// at every launch, so a DLL the antivirus removed between two launches is simply not handed to the
/// launcher (launcher.exe refuses the WHOLE launch over a missing argv DLL, see launcher.cpp).
/// </summary>
public static class Enhancements
{
    /// <param name="Enabled">The user's toggle (<c>RelicSettings.Enhancements</c>).</param>
    /// <param name="Shipped">This build carries at least one inject entry for the version —
    /// answered even when disabled, so the log can tell "off" from "nothing to turn on".</param>
    /// <param name="Dlls">Absolute paths for launcher.exe, manifest order: shipped AND present in the
    /// game dir right now. Empty when disabled.</param>
    /// <param name="Missing">Shipped but absent from the game dir (antivirus, IO). Empty when disabled.</param>
    public sealed record Resolution(
        bool Enabled, bool Shipped, IReadOnlyList<string> Dlls, IReadOnlyList<string> Missing);

    /// <summary>What to inject for <paramref name="versionId"/> installed at <paramref name="gameDir"/>.
    /// Never throws — any failure resolves to "launch without enhancements".</summary>
    public static Resolution Resolve(string versionId, string gameDir, bool enabled, string? payloadRoot = null)
    {
        try
        {
            var injectables = GamePatcher.Injectables(versionId, gameDir, payloadRoot);
            bool shipped = injectables.Any(i => i.Shipped);
            if (!enabled)
                return new Resolution(false, shipped, Array.Empty<string>(), Array.Empty<string>());

            var dlls = new List<string>();
            var missing = new List<string>();
            foreach (var i in injectables)
            {
                if (!i.Shipped) continue; // a dev build without the payload: nothing to miss
                if (i.DstPath is not null && File.Exists(i.DstPath)) dlls.Add(i.DstPath);
                else missing.Add(i.Dst);
            }
            return new Resolution(true, shipped, dlls, missing);
        }
        catch (Exception ex)
        {
            Log.Error($"enhancements: resolving {versionId} failed — launching without them", ex);
            return new Resolution(enabled, false, Array.Empty<string>(), Array.Empty<string>());
        }
    }

    /// <summary>Does this build ship an enhancements DLL for <paramref name="versionId"/>? Drives the
    /// Settings section and the library badges; independent of the toggle and of any install.</summary>
    public static bool ShippedFor(string versionId, string? payloadRoot = null)
    {
        try { return GamePatcher.Injectables(versionId, null, payloadRoot).Any(i => i.Shipped); }
        catch (Exception ex)
        {
            Log.Error($"enhancements: ShippedFor({versionId}) failed", ex);
            return false;
        }
    }
}
