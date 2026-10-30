using Relic.Core.Util;

namespace Relic.Core.State;

/// <summary>Manages the "run Relic when Windows starts" HKCU Run entry (per-user, no elevation), the
/// one-shot RunOnce logon-recovery hook that exists only while a private profile is loaded, and the RunOnce
/// value that resumes a Quick Start run after a restart.</summary>
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

    /// <summary>Spikes only: point the RunOnce values at a synthetic key so a test never touches the real RunOnce.</summary>
    public static string? RunOnceKeyOverride { get; set; }

    /// <summary>The RunOnce key both values live in: the real one, or the spikes' synthetic key.</summary>
    private static string RunOnceKeyPath => RunOnceKeyOverride ?? RunOnceKey;

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
        var r = Proc.Run("reg", "add", RunOnceKeyPath, "/v", RecoverValueName, "/t", "REG_SZ", "/d", value, "/f");
        if (!r.Ok) throw new InvalidOperationException($"reg add {RunOnceKeyPath}\\{RecoverValueName} failed ({r.Code}): {r.Err.Trim()}");
    }

    public static void DisarmLogonRecovery()
    {
        Proc.Run("reg", "delete", RunOnceKeyPath, "/v", RecoverValueName, "/f"); // ok if it didn't exist
    }

    public static bool LogonRecoveryArmed()
    {
        var r = Proc.Run("reg", "query", RunOnceKeyPath, "/v", RecoverValueName);
        return r.Ok && r.Out.Contains(RecoverValueName, StringComparison.OrdinalIgnoreCase);
    }

    // ── Quick Start's resume (RunOnce) ──
    //
    // While a Quick Start run is not ended — it runs, or waits for the restart that finishes turning on WSL 2 —
    // RelicQuickStart = "<Relic.exe>" --quickstart brings the window back at the next logon, so the run goes on
    // after a restart of Windows or a power cut. A start without --tray or --play shows the window, and a second
    // instance hands over to the first, so the autostart's --tray start and this one may come in either order.
    // Windows deletes a RunOnce value before it runs it: every resume arms it again. A paused, ended or discarded
    // run disarms it, and so does the uninstaller. It sits in the same key as RelicRecover and never touches it.

    private const string QuickStartValueName = "RelicQuickStart";

    /// <summary>Arm (a run is going on) or disarm (it is paused, ended or discarded) the resume value, with the exe
    /// path of THIS process; a process without a path does not arm.</summary>
    public static void SetQuickStartResume(bool armed)
    {
        if (armed) ArmQuickStartResume(Environment.ProcessPath ?? "");
        else DisarmQuickStartResume();
    }

    /// <summary>Writes <c>RelicQuickStart = "&lt;exePath&gt;" --quickstart</c> under RunOnce. Nothing for an empty
    /// path; a failed write throws.</summary>
    public static void ArmQuickStartResume(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;
        string value = $"\"{exePath}\" --quickstart";
        var r = Proc.Run("reg", "add", RunOnceKeyPath, "/v", QuickStartValueName, "/t", "REG_SZ", "/d", value, "/f");
        if (!r.Ok) throw new InvalidOperationException($"reg add {RunOnceKeyPath}\\{QuickStartValueName} failed ({r.Code}): {r.Err.Trim()}");
    }

    /// <summary>Removes the resume value (fine when it is not there); <c>RelicRecover</c> stays as it is.</summary>
    public static void DisarmQuickStartResume()
    {
        Proc.Run("reg", "delete", RunOnceKeyPath, "/v", QuickStartValueName, "/f"); // ok if it didn't exist
    }

    public static bool QuickStartResumeArmed()
    {
        var r = Proc.Run("reg", "query", RunOnceKeyPath, "/v", QuickStartValueName);
        return r.Ok && r.Out.Contains(QuickStartValueName, StringComparison.OrdinalIgnoreCase);
    }
}
