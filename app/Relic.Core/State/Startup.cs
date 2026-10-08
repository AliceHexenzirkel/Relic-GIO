using Relic.Core.Util;

namespace Relic.Core.State;

/// <summary>Manages the "run Relic when Windows starts" HKCU Run entry (per-user, no elevation), and
/// the one-shot RunOnce logon-recovery hook that exists only while a private profile is loaded.</summary>
public static class Startup
{
    private const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Relic";

    public static bool IsEnabled()
    {
        var r = Proc.Run("reg", "query", RunKey, "/v", ValueName);
        return r.Ok && r.Out.Contains(ValueName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Enable/disable auto-start. When enabled, Relic launches minimized to the tray (--tray).</summary>
    public static void Set(bool enabled, string exePath)
    {
        if (enabled)
        {
            // Value: "C:\...\Relic.exe" --tray
            string value = $"\"{exePath}\" --tray";
            var r = Proc.Run("reg", "add", RunKey, "/v", ValueName, "/t", "REG_SZ", "/d", value, "/f");
            if (!r.Ok) throw new InvalidOperationException(L.T("core.state.autostartFailed", new { error = r.Err.Trim() }));
        }
        else
        {
            Proc.Run("reg", "delete", RunKey, "/v", ValueName, "/f"); // ok if it didn't exist
        }
    }

    // ── logon recovery hook (RunOnce) ──
    //
    // A BSOD, a power cut or a Windows shutdown with the game open ends Relic before the session's
    // own restore can run, and leaves the private profile loaded in the registry + LocalLow. Autostart
    // is opt-in, so until the user happened to start Relic the official client would run on that
    // profile (its login then landing in the private slot). RunOnce closes that window: Windows runs
    // the value once at the next logon and deletes it. "--tray" starts Relic in the background, whose
    // Backend constructor performs the same recovery pass as any start. The value exists ONLY while a
    // non-live profile is loaded — armed after LoadProfile(id != live) completes, removed after
    // LoadProfile(live) completes (ProfileStore.ForGenshin wires that) — so a machine that never
    // crashes never sees it, and the uninstaller removes a leftover one.

    private const string RunOnceKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RecoverValueName = "RelicRecover";

    /// <summary>Spikes only: point the hook at a synthetic key so a test never touches the real RunOnce.</summary>
    public static string? RunOnceKeyOverride { get; set; }

    private static string RecoverKey => RunOnceKeyOverride ?? RunOnceKey;

    /// <summary>Arm (a private profile was just loaded) or disarm (live is back) the logon recovery.
    /// Arming needs the exe path of THIS process — the launcher, or the standalone "--play" process,
    /// which is the same Relic.exe; a process without a path (never the case for a published exe)
    /// simply does not arm.</summary>
    public static void SetLogonRecovery(bool armed)
    {
        if (armed) ArmLogonRecovery(Environment.ProcessPath ?? "");
        else DisarmLogonRecovery();
    }

    public static void ArmLogonRecovery(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;
        string value = $"\"{exePath}\" --tray";
        var r = Proc.Run("reg", "add", RecoverKey, "/v", RecoverValueName, "/t", "REG_SZ", "/d", value, "/f");
        if (!r.Ok) throw new InvalidOperationException($"reg add {RecoverKey}\\{RecoverValueName} failed ({r.Code}): {r.Err.Trim()}");
    }

    public static void DisarmLogonRecovery()
    {
        Proc.Run("reg", "delete", RecoverKey, "/v", RecoverValueName, "/f"); // ok if it didn't exist
    }

    public static bool LogonRecoveryArmed()
    {
        var r = Proc.Run("reg", "query", RecoverKey, "/v", RecoverValueName);
        return r.Ok && r.Out.Contains(RecoverValueName, StringComparison.OrdinalIgnoreCase);
    }
}
