using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Relic.Core.Download;
using Relic.Core.Util;

namespace Relic.Core.Fiddler;

/// <summary>
/// Automates Fiddler Classic setup so the game's HTTPS traffic can be decrypted and redirected:
/// silent install, HTTPS-decrypt prefs, custom rules, and starting/stopping Fiddler around a play
/// session. The install prefers an elevation-free launch and only falls back to the consented UAC
/// path if that verifiably produced nothing. The root-certificate trust is deliberately CONSENTED —
/// we use the standard Windows trust path that shows the one-time "Security Warning" dialog, rather
/// than silently planting the cert, because a trusted root can intercept ALL HTTPS on the machine
/// and deserves a conscious approval.
/// </summary>
public static class FiddlerAutomation
{
    public const string InstallerUrl = "https://telerik-fiddler.s3.amazonaws.com/fiddler/FiddlerSetup.exe";
    public const string CertSubjectMarker = "DO_NOT_TRUST_FiddlerRoot";
    private const string PrefsKey = @"HKCU\Software\Microsoft\Fiddler2";

    // Split verification budget: the two install attempts share one ~60s wait between them,
    // so trying without UAC first costs no extra wall-clock time.
    private const int NoUacVerifySeconds = 20;
    private const int UacVerifySeconds = 40;

    // An elevated (UAC) NSIS install may land per-user OR per-machine, so we look in both. Per-user
    // (LocalAppData) is the installer's default and stays first, so that's where a fresh install
    // goes — an elevation-free install ALWAYS lands there; the per-machine dirs only ever match an
    // install done elevated or by hand.
    private static readonly string[] CandidateDirs =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Fiddler"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fiddler"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Fiddler"),
    };

    public static string DefaultInstallDir => CandidateDirs[0];

    /// <summary>Fiddler.exe of an existing install (per-user first, then per-machine); null if not found.</summary>
    public static string? InstalledFiddlerExe
    {
        get
        {
            foreach (var d in CandidateDirs)
            {
                string exe = Path.Combine(d, "Fiddler.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }
    }

    public static string FiddlerExe => InstalledFiddlerExe ?? Path.Combine(DefaultInstallDir, "Fiddler.exe");

    public static bool IsInstalled => InstalledFiddlerExe is not null;

    /// <summary>Where Fiddler auto-loads user rules from.</summary>
    public static string CustomRulesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fiddler2", "Scripts", "CustomRules.js");

    // ── install ──

    public static async Task<string> DownloadInstallerAsync(
        string destDir, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        string dest = Path.Combine(destDir, "FiddlerSetup.exe");
        await Downloader.DownloadResumableAsync(InstallerUrl, dest, progress, ct);
        return dest;
    }

    // Why we try WITHOUT the Windows prompt (UAC) first: FiddlerSetup.exe's manifest requests
    // "highestAvailable", NOT "requireAdministrator" — i.e. it uses administrator rights if they are
    // at hand, but does not need them: everything it writes lives in the user profile
    // (%LOCALAPPDATA%\Programs\Fiddler, HKCU, the current user's Start menu). The prompt only ever
    // appears because on an administrator account Windows auto-elevates a "highestAvailable"
    // request. __COMPAT_LAYER=RunAsInvoker tells the compatibility engine to answer that request
    // with the caller's token, so the install proceeds identically, just without any confirmation.
    // On a standard account the gain is even bigger: "runas" asks for an administrator's password
    // there, and with ANOTHER account's credentials the install lands in that account's profile,
    // where we could never find it again.
    /// <summary>
    /// Elevation-free install attempt: the NSIS installer run silently (/S) with the AppCompat
    /// RunAsInvoker layer, so its manifest's elevation request is answered with our own token and no
    /// consent dialog appears. Returns only whether the attempt RAN to completion — never whether it
    /// installed anything (the caller verifies that by polling IsInstalled). It never throws on a
    /// failed launch, so the consented fallback always gets its turn; cancellation still propagates.
    /// </summary>
    public static async Task<bool> TryInstallSilentWithoutUacAsync(string installerPath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,  // required: the environment block below is only honoured without ShellExecute
            CreateNoWindow = true,
            Arguments = "/S",
        };
        // Scoped to THIS child (and the helpers NSIS extracts from it) on purpose — setting it on our
        // own process would leak RunAsInvoker into every later child: game, launcher.exe, Fiddler,
        // reg.exe, robocopy.
        psi.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                Log.Error("Fiddler no-UAC install: Process.Start returned no process");
                return false;
            }
            await p.WaitForExitAsync(ct);
            Log.Info($"Fiddler no-UAC install attempt ran (exit {p.ExitCode})");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 740 (AppCompat engine disabled by policy), 5 (AV/EDR block), 1260 (AppLocker/SRP) — all
            // mean the same thing to us: fall back to the consented prompt instead of failing.
            Log.Error("Fiddler no-UAC install attempt could not run", ex);
            return false;
        }
    }

    /// <summary>
    /// Run the NSIS installer silently (/S) via a one-time UAC elevation — the automatic fallback
    /// when <see cref="TryInstallSilentWithoutUacAsync"/> produced no install. A non-elevated
    /// CreateProcess (UseShellExecute=false) can't show the UAC prompt on an admin account — it fails
    /// with "requires elevation". So we launch it with ShellExecute + the "runas" verb, which shows
    /// the consent dialog once. Success is confirmed by the caller polling IsInstalled (NSIS may
    /// relaunch/detach), so we don't treat a nonzero/early exit as failure here.
    /// </summary>
    public static void InstallSilent(string installerPath)
    {
        var psi = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,   // required so Windows can raise the UAC elevation prompt
            Verb = "runas",           // the one-time consented elevation for the installer
            Arguments = "/S",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException(L.T("core.fiddler.installStartFailed"));
            p.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            throw new InvalidOperationException(L.T("core.fiddler.installCancelled"));
        }
    }

    // ── prefs (Fiddler MUST be closed while writing) ──

    /// <summary>
    /// Turn on "Decrypt HTTPS traffic" + capture settings via Fiddler's prefs registry key, and limit
    /// the decryption to non-browser processes — the game is not a browser, so the redirect is
    /// unaffected, while browsers stop being asked to accept a Fiddler certificate for every site.
    ///
    /// This is the SECOND of the two guards against "Fiddler is running, so Google/YouTube are dead";
    /// the first (and the load-bearing one) is the CONNECT gate in <see cref="FiddlerRules"/>, which
    /// covers every non-browser app too. Prefs are only best-effort: nothing verifies them before a
    /// session (<see cref="CheckReady"/> looks at install/cert/rules), and Fiddler rewrites its whole
    /// prefs block from memory on exit — whereas a drifted CustomRules.js IS noticed and rewritten.
    /// </summary>
    public static void EnableHttpsDecrypt(int listenPort = 8888)
    {
        void Sz(string name, string val) => Reg("add", PrefsKey, "/v", name, "/t", "REG_SZ", "/d", val, "/f");
        Sz("CaptureHTTPS", "True");
        Sz("CaptureCONNECT", "True");
        Sz("HookAllConnections", "True");
        Sz("IgnoreServerCertErrors", "True");
        Sz("CheckForUpdates", "False");
        // Without socket→process mapping the process filter below has nothing to classify with. It is
        // Fiddler's own default, but we depend on it, so it is written explicitly.
        Sz("MapSocketToProcess", "True");
        Reg("add", PrefsKey, "/v", "ListenPort", "/t", "REG_DWORD", "/d", listenPort.ToString(), "/f");
        Reg("add", PrefsKey, "/v", "HTTPSProcessFilter", "/t", "REG_DWORD",
            "/d", HttpsDecryptNonBrowsersOnly.ToString(), "/f");
        SuppressStartupPrompts();
    }

    /// <summary>Value of Fiddler's `HTTPSProcessFilter` pref for "Decrypt HTTPS traffic … from
    /// non-browsers only". Not guessed: the value Fiddler's own HTTPS options write for that choice
    /// (browsing works while the game still reaches the private server), as read back from
    /// `HKCU\Software\Microsoft\Fiddler2` with Fiddler closed.</summary>
    private const int HttpsDecryptNonBrowsersOnly = 2;

    /// <summary>
    /// Silences Fiddler's first-run "AppContainer Configuration" dialog. It warns that Windows'
    /// AppContainer isolation can hide traffic from Immersive/Edge apps — irrelevant here, since the
    /// only thing we proxy is a plain Win32 game — but it is modal, so it steals focus in the middle
    /// of launching. Verified on 6.0.20261.7291: this is the exact value clicking "Cancel" writes, and
    /// flipping it back to True brings the dialog straight back.
    /// Note the prefs live in the Prefs\.default SUBKEY, not next to the capture settings above.
    /// </summary>
    public static void SuppressStartupPrompts()
    {
        try
        {
            Reg("add", PrefsKey + @"\Prefs\.default", "/v", "fiddler.proxy.warnaboutappcontainers",
                "/t", "REG_SZ", "/d", "False", "/f");
        }
        catch (Exception ex)
        {
            // Cosmetic only — a failure here costs one extra dialog, never a broken session.
            Log.Error("suppressing Fiddler's AppContainer prompt (non-fatal)", ex);
        }
    }

    // ── custom rules ──

    /// <summary>Write the templated CustomRules.js to <paramref name="path"/> (defaults to Fiddler's auto-load path).</summary>
    public static void WriteCustomRules(string host, int port, string? path = null)
    {
        path ??= CustomRulesPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, FiddlerRules.Render(host, port));
    }

    // ── root certificate (consented) ──

    /// <summary>True if any Fiddler root cert is trusted (CurrentUser or LocalMachine Root store).
    /// Deliberately permissive about validity: an EXPIRED root still has to be found and removed at
    /// uninstall. To decide whether this machine can actually decrypt, use
    /// <see cref="IsRootCertUsable"/> instead.</summary>
    public static bool IsRootCertTrusted()
    {
        foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.Root, loc);
            store.Open(OpenFlags.ReadOnly);
            foreach (var c in store.Certificates)
                if (c.Subject.Contains(CertSubjectMarker, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// True if the machine can really decrypt: a currently-valid root sits in CurrentUser\My (Fiddler
    /// signs every per-site certificate with it, so it needs that copy and its private key) AND that
    /// same certificate is trusted.
    ///
    /// The thumbprint match is the point. Asking the two stores separately lets an expired root in
    /// Root answer "trusted" while nothing works — and since the setup path returns early on
    /// "trusted", it would skip straight past creating a usable one and leave the user with a launch
    /// that silently fails to reach the private server.
    /// </summary>
    public static bool IsRootCertUsable()
    {
        using var cert = FindFiddlerCert();
        if (cert is null) return false;
        foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.Root, loc);
            store.Open(OpenFlags.ReadOnly);
            foreach (var c in store.Certificates)
                using (c)
                    if (string.Equals(c.Thumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                        return true;
        }
        return false;
    }

    /// <summary>The Fiddler-generated root cert, if Fiddler has created it (CurrentUser\My), else null.
    /// Expired roots are skipped: Fiddler signs every per-site certificate with this one, so an
    /// expired root cannot decrypt anything, and trusting it would plant a dead MITM root for
    /// nothing. When only expired copies exist we say so in the log — that is the one case where the
    /// machine looks set up but nothing works.</summary>
    public static X509Certificate2? FindFiddlerCert()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        X509Certificate2? best = null;
        string? expired = null;
        var now = DateTime.Now;
        // Everything we do not hand back is disposed: this runs on a 500 ms poll during certificate
        // creation, over a store that routinely holds hundreds of Fiddler per-site certificates.
        foreach (var c in store.Certificates)
        {
            if (!c.Subject.Contains(CertSubjectMarker, StringComparison.OrdinalIgnoreCase)) { c.Dispose(); continue; }
            if (c.NotAfter <= now || c.NotBefore > now)
            {
                expired ??= $"NotBefore={c.NotBefore}, NotAfter={c.NotAfter}";
                c.Dispose();
                continue;
            }
            // Newest wins: a re-created root sits alongside the old one until something prunes it.
            if (best is null || c.NotBefore > best.NotBefore) { best?.Dispose(); best = c; }
            else c.Dispose();
        }
        if (best is null && expired is not null)
            Log.Info($"Fiddler root cert found but not valid now ({expired}) — a new one is needed");
        return best;
    }

    /// <summary>
    /// Called immediately before the Windows "Security Warning" consent dialog, so the app can
    /// explain in the user's language what is about to appear and that the answer must be "Yes". Set by
    /// Relic.App at startup — Relic.Core stays UI-free, and a null hook simply means the raw Windows
    /// dialog appears on its own.
    ///
    /// This is the deliberate alternative to suppressing the dialog: the prompt cannot be turned off
    /// through the API (crypt32 protects the CurrentUser Root store), so the only way to skip it is
    /// to write the cert blob straight into the registry — which is what the invariant forbids, and
    /// what makes an unsigned launcher look exactly like MITM adware to an antivirus heuristic.
    /// Explaining the dialog costs one screen; going around it costs the user's consent.
    /// </summary>
    public static Action? BeforeCertConsent { get; set; }

    /// <summary>
    /// Trust the Fiddler root cert via the standard path. This intentionally raises the Windows
    /// "Security Warning" consent dialog exactly once — the user must approve trusting the MITM root.
    /// </summary>
    public static void TrustRootCertConsented(X509Certificate2 cert)
    {
        // Both consent windows — the notice behind BeforeCertConsent and Windows' Security Warning
        // from store.Add — are raised from a background task and can open BEHIND the launcher when
        // Windows denies the foreground switch; the install then looks hung while an invisible
        // dialog waits for a click. The watcher lifts every
        // dialog this process opens into the topmost band, which activation rules cannot veto.
        using var front = DialogFront.WatchAndPromote();

        // Non-fatal: a failure to explain must never stop the trust flow itself.
        try { BeforeCertConsent?.Invoke(); }
        catch (Exception ex) { Log.Error("certificate preparation screen (non-fatal)", ex); }

        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert); // shows the one-time consent dialog
    }

    // ── lifecycle around a play session ──

    public static Process StartFiddler()
    {
        string exe = FiddlerExe;
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!, // wherever it actually landed (per-user or per-machine)
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start Fiddler.exe");
        MinimizeWhenReady(p);
        return p;
    }

    /// <summary>
    /// Minimise Fiddler's window once it exists. The WindowStyle hint above is not enough on its own:
    /// Fiddler restores its own saved placement (HKCU\...\Fiddler2\UI\frmViewer_WState) and forces it
    /// back to Normal, so a freshly-started Fiddler pops up over the game and steals focus. Done on a
    /// background thread — the caller is about to launch the game and must not wait on a window.
    /// </summary>
    private static void MinimizeWhenReady(Process p)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 60; i++)
                {
                    if (p.HasExited) return;
                    p.Refresh();
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, SW_MINIMIZE);
                        return;
                    }
                    await Task.Delay(250);
                }
                Log.Info("Fiddler did not show a window to minimize within 15s — continuing");
            }
            catch (Exception ex) { Log.Error("minimizing Fiddler (non-fatal)", ex); }
        });
    }

    private const int SW_MINIMIZE = 6;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>True if a Fiddler process is already up. A session that ended deferred deliberately
    /// leaves Fiddler running, so the next session must reuse it instead of starting a second one
    /// that would fight for the proxy port.</summary>
    public static bool IsRunning
    {
        get
        {
            var procs = Process.GetProcessesByName("Fiddler");
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
    }

    /// <summary>Stop Fiddler gracefully (so it flushes), falling back to Kill.</summary>
    public static void StopFiddler()
    {
        foreach (var p in Process.GetProcessesByName("Fiddler"))
        {
            try
            {
                if (!p.CloseMainWindow() || !p.WaitForExit(4000))
                    p.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            finally { p.Dispose(); }
        }
    }

    // ── readiness / "ensure everything is set up" ──

    public readonly record struct Readiness(bool Installed, bool CertTrusted, bool RulesPresent)
    {
        public bool AllGood => Installed && CertTrusted && RulesPresent;
    }

    /// <summary>Snapshot of whether Fiddler is installed, the cert is trusted, and the rules point at
    /// the server. The rules check is an EXACT match against the freshly-rendered template — a changed
    /// port (or a user-mangled file) shows as not-ready, so the caller re-templates instead of letting
    /// Fiddler silently redirect to a dead endpoint.</summary>
    public static Readiness CheckReady(string host, int port)
    {
        bool rules = false;
        try
        {
            rules = File.Exists(CustomRulesPath)
                && File.ReadAllText(CustomRulesPath) == FiddlerRules.Render(host, port);
        }
        catch { /* ignore */ }
        return new Readiness(IsInstalled, IsRootCertUsable(), rules);
    }

    /// <summary>Poll for the installed exe — file presence is the only trustworthy success signal,
    /// since NSIS may relaunch/detach and its exit code says nothing about what landed on disk. The
    /// re-check after the loop is what guards the fallback: it runs immediately before the caller
    /// elevates, with the first installer process already exited, so two installers can never run
    /// over the same directory.</summary>
    private static async Task<bool> WaitInstalledAsync(int seconds, CancellationToken ct)
    {
        for (int i = 0; i < seconds; i++)
        {
            if (IsInstalled) return true;
            await Task.Delay(1000, ct);
        }
        return IsInstalled;
    }

    /// <summary>Install Fiddler silently if it isn't already present, without a UAC prompt when possible.</summary>
    public static Task EnsureInstalledAsync(CancellationToken ct = default) => EnsureInstalledAsync(true, ct);

    /// <summary>Install Fiddler silently if it isn't already present. With
    /// <paramref name="tryWithoutUac"/> the elevation-free attempt runs first and the consented UAC
    /// path is used only if that verifiably installed nothing.</summary>
    public static async Task EnsureInstalledAsync(bool tryWithoutUac, CancellationToken ct = default)
    {
        if (IsInstalled) return;
        Log.Info("Fiddler not installed — downloading + installing silently");
        string tmp = Path.Combine(Path.GetTempPath(), "relic-fiddler");
        string setup = await DownloadInstallerAsync(tmp, null, ct);

        if (tryWithoutUac)
        {
            // Verify only if the attempt actually ran: when the launch itself was refused there is
            // nothing to wait for, so the user shouldn't sit through the grace window for nothing.
            bool ran = await TryInstallSilentWithoutUacAsync(setup, ct);
            if (ran && await WaitInstalledAsync(NoUacVerifySeconds, ct))
            {
                Log.Info("Fiddler installed without UAC (RunAsInvoker)");
                return;
            }
            Log.Info("No-UAC install produced nothing — falling back to the consented UAC prompt");
        }

        InstallSilent(setup);
        if (!await WaitInstalledAsync(UacVerifySeconds, ct))
            throw new InvalidOperationException(L.T("core.fiddler.installFailed"));
        Log.Info("Fiddler installed via the consented UAC prompt");
    }

    // A poll, not a sleep. A flat wait of a few seconds after starting Fiddler is nowhere near enough
    // for a first-ever start on a cold machine, and no constant would be right for every box, so we
    // watch the certificate store and stop the moment the cert is actually there.
    private const int CertCreateTimeoutSeconds = 60;

    /// <summary>
    /// Get Fiddler to mint its root certificate by handing it
    /// <see cref="FiddlerRules.RenderCertBootstrap"/> and letting Fiddler's own script engine call
    /// createRootCert(). Returns the new cert, or null if it never appeared.
    ///
    /// This exists because there is no other headless trigger. Fiddler creates the root only from the
    /// "Decrypt HTTPS traffic" checkbox in its own UI; the CaptureHTTPS registry pref does NOT create
    /// it (it is just the persisted state of that checkbox), and GET /FiddlerRoot.cer only serves a
    /// root that already exists. That is why "set the pref, start Fiddler, wait" never
    /// produces a certificate on a machine where Fiddler has never decrypted anything.
    ///
    /// CustomRules.js is used as scratch space here — the caller writes the real redirect rules
    /// immediately after, and Fiddler picks those up on its own (AutoReloadScript).
    /// </summary>
    private static async Task<X509Certificate2?> CreateRootCertAsync(CancellationToken ct)
    {
        string path = CustomRulesPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, FiddlerRules.RenderCertBootstrap());

        // A Fiddler we did not start stays up: it may be proxying a deferred session, and killing it
        // would drop the proxy out from under a live client. It reloads the script by itself.
        if (IsRunning)
        {
            Log.Info("Fiddler already running — relying on its script reload to create the cert");
            return await PollForCertAsync(null, ct);
        }

        // Fiddler normally makes itself the SYSTEM PROXY on start (AttachOnBoot) and only puts the
        // old settings back on a GRACEFUL exit. This instance exists purely to mint a certificate —
        // it must proxy nothing — and if we ever have to kill it, a hijacked ProxyEnable/ProxyServer
        // pointing at a dead 127.0.0.1:8888 would take the whole machine's WinINET traffic (Edge,
        // Store, Windows Update) down with it. Booting it detached removes that failure mode
        // outright instead of trying to repair it afterwards.
        string? attach = ReadPref("AttachOnBoot");
        SetPref("AttachOnBoot", "False");
        // Before the very first start, not after: Fiddler's first-run "AppContainer Configuration"
        // dialog is MODAL, so it both blocks the script that mints the certificate and makes the
        // graceful CloseMainWindow below impossible.
        SuppressStartupPrompts();
        Process? started = null;
        try
        {
            started = StartFiddler();
            return await PollForCertAsync(started, ct);
        }
        finally
        {
            if (started is not null) StopOne(started);
            // After the stop, never before: a running Fiddler rewrites its whole prefs block from
            // memory on exit and would undo this.
            SetPref("AttachOnBoot", attach ?? "True");
        }
    }

    private static async Task<X509Certificate2?> PollForCertAsync(Process? started, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(CertCreateTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            var found = FindFiddlerCert();
            if (found is not null) return found;
            if (started is { HasExited: true })
            {
                Log.Info($"Fiddler exited (code {started.ExitCode}) before the root cert appeared");
                break;
            }
        }
        return FindFiddlerCert();
    }

    /// <summary>
    /// Stop exactly the Fiddler we started, leaving any other instance alone, and do not return until
    /// it is really gone. Both halves matter: the caller's next step asks <see cref="IsRunning"/>
    /// whether to start a session Fiddler, and a process still dying answers "yes, one is running" —
    /// which would launch the game with no proxy at all and a log that looks like success.
    /// </summary>
    private static void StopOne(Process p)
    {
        try
        {
            if (p.HasExited) return;
            // CloseMainWindow can only work once a window exists, and a freshly started Fiddler may
            // not have one for several seconds. Without this wait the graceful path is skipped
            // entirely and every bootstrap ends in a Kill.
            for (int i = 0; i < 40 && !p.HasExited && p.MainWindowHandle == IntPtr.Zero; i++)
            {
                Thread.Sleep(250);
                p.Refresh();
            }
            if (p.HasExited) return;
            if (!p.CloseMainWindow() || !p.WaitForExit(8000))
            {
                Log.Info("Fiddler did not close gracefully — killing it");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch (Exception ex) { Log.Error("stopping the bootstrap Fiddler instance (non-fatal)", ex); }
        finally { p.Dispose(); }
    }

    private static string? ReadPref(string name)
    {
        var r = Proc.Run("reg", "query", PrefsKey, "/v", name);
        if (!r.Ok) return null;
        // reg.exe prints "    <name>    REG_SZ    <value>"; the value is whatever follows the type.
        foreach (var line in r.Out.Split('\n'))
        {
            int at = line.IndexOf("REG_SZ", StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && line.Contains(name, StringComparison.OrdinalIgnoreCase))
                return line[(at + "REG_SZ".Length)..].Trim();
        }
        return null;
    }

    private static void SetPref(string name, string value)
    {
        try { Reg("add", PrefsKey, "/v", name, "/t", "REG_SZ", "/d", value, "/f"); }
        catch (Exception ex) { Log.Error($"writing Fiddler pref {name} (non-fatal)", ex); }
    }

    /// <summary>Ensure the Fiddler root cert exists and is trusted (one consent dialog if newly trusted).</summary>
    public static async Task EnsureCertTrustedAsync(CancellationToken ct = default)
    {
        if (IsRootCertUsable()) return;
        var cert = FindFiddlerCert();
        if (cert is null)
        {
            Log.Info("no usable Fiddler root cert — asking Fiddler to create one");
            cert = await CreateRootCertAsync(ct);
        }
        if (cert is null)
            // Both recoveries are named because we cannot tell the two apart from here: never created
            // (the checkbox makes one) and expired (only a reset makes a new one — Fiddler considers
            // the old one to still "exist", so asking it again changes nothing).
            throw new InvalidOperationException(L.T("core.fiddler.certCreateFailed"));
        TrustRootCertConsented(cert); // one-time Windows consent dialog
        Log.Info("Fiddler root cert trusted");
    }

    /// <summary>Install (if needed) + enable decrypt + write rules + trust cert — the full "ready to redirect" setup.</summary>
    public static Task EnsureReadyAsync(string host, int serverPort, bool enableDecrypt, bool trustCert, CancellationToken ct = default)
        => EnsureReadyAsync(host, serverPort, enableDecrypt, trustCert, true, ct);

    /// <inheritdoc cref="EnsureReadyAsync(string, int, bool, bool, CancellationToken)"/>
    /// <param name="tryWithoutUac">Try the elevation-free install before the consented UAC prompt.</param>
    public static async Task EnsureReadyAsync(
        string host, int serverPort, bool enableDecrypt, bool trustCert, bool tryWithoutUac, CancellationToken ct = default)
    {
        await EnsureInstalledAsync(tryWithoutUac, ct);
        // Prefs and rules LAST, and in a finally. The cert step runs Fiddler, and Fiddler rewrites its
        // whole prefs block from memory on exit — so anything written before it is at the mercy of
        // what that instance happened to load. It also borrows CustomRules.js as scratch space, so
        // redirect rules written earlier would just be overwritten, and a failed cert step would
        // leave the bootstrap script standing as this machine's rules. Writing both here means the
        // real prefs and the real rules are the last thing on disk either way.
        try
        {
            if (trustCert) await EnsureCertTrustedAsync(ct);
        }
        finally
        {
            if (enableDecrypt) EnableHttpsDecrypt();
            WriteCustomRules(host, serverPort);
        }
    }

    private static void Reg(params string[] args)
    {
        var r = Proc.Run("reg", args);
        if (!r.Ok)
            throw new InvalidOperationException($"reg {string.Join(' ', args)} failed ({r.Code}): {r.Err.Trim()}");
    }
}
