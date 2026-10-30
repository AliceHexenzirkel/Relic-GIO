using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Relic.Core.Download;
using Relic.Core.Util;

namespace Relic.Core.Setup;

/// <summary>A Quick Start install that did not end well. <see cref="Code"/>: uacDeclined (the Windows permission
/// prompt was declined) | badHash (a pinned download is not the pinned file) | badSignature (Docker's installer is
/// not signed by its publisher) | download | failed | toolMissing (this build pins no usable file for it). The
/// message is the user's text.</summary>
public sealed class SetupInstallException : Exception
{
    public string Code { get; }

    public SetupInstallException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

public sealed record PythonInstalled(string Exe, string ExeW, string Version, string Dir);
public sealed record SevenZipInstalled(string Path);
public sealed record WslDone(bool RestartNeeded, int ExitCode);
public sealed record DockerInstalled(string Location);
public sealed record DockerStarted(string Version);
public sealed record WslMemoryWritten(int Gb, string Path);

/// <summary>
/// What Quick Start adds to this PC for a server here, one piece per call; each streams its lines to the caller and
/// ends with a result or a <see cref="SetupInstallException"/>.
/// <list type="bullet">
/// <item>Python's embeddable package and 7-Zip's <c>7zr.exe</c>: pinned files (size + sha256,
/// <c>config/setup-tools.json</c>) downloaded into Relic's tools folder — no administrator rights, nothing on PATH or
/// in the registry.</item>
/// <item>WSL 2: <c>wsl.exe --install --no-distribution</c> or <c>--update</c> — the ONE elevation Quick Start adds,
/// through a Windows permission prompt the user answers.</item>
/// <item>Docker Desktop: always the current installer, run only once its Authenticode signer is its publisher, PER
/// USER (<c>install --user</c>) and unelevated; its licence is accepted on the command line only when the user ticked
/// it.</item>
/// <item>Docker Desktop started, and WSL 2's memory cap written into <c>.wslconfig</c>.</item>
/// </list>
/// Downloads go through <see cref="Downloader"/>: resumable, watched for stalls, never through the system proxy.
/// </summary>
public static class SetupInstall
{
    /// <summary>Spikes only: replaces the download of (url, destination).</summary>
    public static Func<string, string, IProgress<DownloadProgress>?, CancellationToken, Task>? DownloadOverride { get; set; }

    /// <summary>The probe a freshly unpacked Python must pass: the modules the agent needs, and its version.</summary>
    public const string PythonProbe = "import sys,ssl,sqlite3,zlib,tarfile,gzip;print('%d.%d'%sys.version_info[:2])";

    /// <summary>The arguments of Docker Desktop's installer: a per-user, quiet install on the WSL 2 backend, and
    /// <c>--accept-license</c> only when <paramref name="acceptLicence"/> — the user ticked the agreement. Pure.</summary>
    public static IReadOnlyList<string> DockerInstallerArguments(bool acceptLicence)
    {
        var args = new List<string> { "install", "--user", "--quiet", "--backend=wsl-2" };
        if (acceptLicence) args.Add("--accept-license");
        return args;
    }

    // ── Python ──

    /// <summary>Python's embeddable package into <c>tools\python</c>: downloaded and checked (size + sha256), unpacked
    /// into <c>tools\python.new</c> from the very file that was checked (held open for reading alone meanwhile), proven
    /// (<see cref="PythonProbe"/> must name 3.10 or later), then put in place by one rename — an older copy set aside
    /// first and deleted after.</summary>
    public static async Task<PythonInstalled> PythonAsync(ToolEntry? entry, Action<string>? log = null, CancellationToken ct = default)
    {
        if (entry is not { Usable: true }) throw new SetupInstallException("toolMissing", L.T("core.setup.toolMissing", new { what = "Python" }));
        string what = "Python " + entry.Version;
        using var pinned = await DownloadPinnedAsync(what, entry, log, ct).ConfigureAwait(false);
        string zip = pinned.Name;
        string fresh = Path.Combine(SetupTools.ToolsDir, "python.new"), aside = Path.Combine(SetupTools.ToolsDir, "python.old");
        string version;
        try
        {
            DeleteDir(fresh);
            using (var archive = new ZipArchive(pinned, ZipArchiveMode.Read, leaveOpen: true)) archive.ExtractToDirectory(fresh);
            string exe = Path.Combine(fresh, "python.exe");
            var r = TimedProcess.Run(exe, new[] { "-I", "-c", PythonProbe }, TimeSpan.FromSeconds(60), null, ct);
            version = System.Text.Encoding.UTF8.GetString(r.Stdout).Trim();
            if (r.Code != 0 || SetupTools.ParseVersion(version) is not { } v || v < new Version(3, 10))
                throw new SetupInstallException("failed", L.T("core.setup.python.broken",
                    new { error = r.Code is null ? L.T("core.setup.timedOut", new { seconds = 60 })
                        : L.T("core.setup.exited", new { code = r.Code, output = Tail(r.Stderr.Length > 0 ? r.Stderr : version, 300) }) }));
            DeleteDir(aside);
            if (Directory.Exists(SetupTools.PythonDir)) Directory.Move(SetupTools.PythonDir, aside);
            Directory.Move(fresh, SetupTools.PythonDir);
        }
        catch (SetupInstallException) { DeleteDir(fresh); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception or InvalidOperationException)
        {
            DeleteDir(fresh);
            if (!Directory.Exists(SetupTools.PythonDir) && Directory.Exists(aside)) TryMove(aside, SetupTools.PythonDir);
            throw new SetupInstallException("failed", L.T("core.setup.python.broken", new { error = ex.Message }), ex);
        }
        pinned.Dispose(); // the download is let go before it is deleted
        DeleteDir(aside);
        TryDelete(zip);
        log?.Invoke(L.T("core.setup.python.ready", new { version, dir = SetupTools.PythonDir }));
        string exeW = File.Exists(SetupTools.PythonExeW) ? SetupTools.PythonExeW : SetupTools.PythonExe;
        return new PythonInstalled(SetupTools.PythonExe, exeW, version, SetupTools.PythonDir);
    }

    // ── 7-Zip's extractor ──

    /// <summary>7-Zip's <c>7zr.exe</c> into <c>tools\7zr.exe</c>: downloaded and checked (size + sha256), copied from the
    /// very file that was checked (held open for reading alone meanwhile) beside its place and renamed over it, then
    /// run once (any exit code within 10 s: it starts).</summary>
    public static async Task<SevenZipInstalled> SevenZipAsync(ToolEntry? entry, Action<string>? log = null, CancellationToken ct = default)
    {
        if (entry is not { Usable: true }) throw new SetupInstallException("toolMissing", L.T("core.setup.toolMissing", new { what = "7-Zip" }));
        string file, target = SetupTools.SevenZipExe, tmp = target + ".relic-tmp";
        using (var src = await DownloadPinnedAsync("7-Zip " + entry.Version, entry, log, ct).ConfigureAwait(false))
        {
            file = src.Name;
            try
            {
                Directory.CreateDirectory(SetupTools.ToolsDir);
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    src.CopyTo(dst);
                    dst.Flush(flushToDisk: true);
                }
                File.Move(tmp, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(tmp);
                throw new SetupInstallException("failed", ex.Message, ex);
            }
        }
        int? exit;
        try { exit = TimedProcess.Run(target, Array.Empty<string>(), TimeSpan.FromSeconds(10), null, ct).Code; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { exit = null; }
        if (exit is null) throw new SetupInstallException("failed", L.T("core.setup.sevenZip.noStart", new { path = target }));
        TryDelete(file);
        log?.Invoke(L.T("core.setup.sevenZip.ready", new { path = target }));
        return new SevenZipInstalled(target);
    }

    // ── WSL 2 ──

    /// <summary>Turn WSL 2 on: <c>wsl.exe --install --no-distribution</c> through the Windows permission prompt (20 minutes
    /// at most). A declined prompt is <c>uacDeclined</c>; exit 0 = done, and a restart is needed while Windows waits for
    /// one or the Virtual Machine Platform is not on yet; any other exit is <c>failed</c>.</summary>
    public static async Task<WslDone> EnableWslAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        log?.Invoke(L.T("core.setup.wsl.running"));
        var psi = new ProcessStartInfo(MachineCheck.WslExe, "--install --no-distribution")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory,
        };
        int code = await WaitPromptedAsync(psi, TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);
        return WslOutcome(code, log);
    }

    /// <summary>Update WSL (<c>wsl.exe --update</c>) through the Windows permission prompt, as <see cref="EnableWslAsync"/>.</summary>
    public static async Task<WslDone> UpdateWslAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        log?.Invoke(L.T("core.setup.wsl.updating"));
        var psi = new ProcessStartInfo(MachineCheck.WslExe, "--update")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory,
        };
        int code = await WaitPromptedAsync(psi, TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);
        return WslOutcome(code, log);
    }

    private static WslDone WslOutcome(int code, Action<string>? log)
    {
        if (code != 0)
            throw new SetupInstallException("failed", L.T("core.setup.wsl.failed",
                new { code, detail = "(0x" + code.ToString("X8", CultureInfo.InvariantCulture) + ")" }));
        bool restart = MachineCheck.RestartPending() == true || MachineCheck.ReadCim(CancellationToken.None).VmpOn != true;
        log?.Invoke(L.T(restart ? "core.setup.wsl.restart" : "core.setup.wsl.done"));
        return new WslDone(restart, code);
    }

    /// <summary>Start the program the caller prepared — one the user is asked about by Windows — and wait for it at
    /// most <paramref name="limit"/>. A declined prompt (Win32 error 1223) is <c>uacDeclined</c>.</summary>
    private static async Task<int> WaitPromptedAsync(ProcessStartInfo psi, TimeSpan limit, CancellationToken ct)
    {
        Process? p;
        try { p = Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new SetupInstallException("uacDeclined", L.T("core.setup.uacDeclined"), ex);
        }
        catch (Win32Exception ex)
        {
            throw new SetupInstallException("failed", ex.Message, ex);
        }
        if (p is null) throw new SetupInstallException("failed", L.T("core.setup.wsl.failed", new { code = -1, detail = "" }));
        using (p)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(limit);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SetupInstallException("failed", L.T("core.setup.wsl.timeout", new { minutes = (int)limit.TotalMinutes }));
            }
            return p.ExitCode;
        }
    }

    // ── Docker Desktop ──

    /// <summary>
    /// Docker Desktop for this user: its current installer downloaded (a size within half of the catalogue's), its
    /// Authenticode signer checked (<see cref="Authenticode.Verify"/>: anything else deletes the file and runs
    /// nothing), then run unelevated with <see cref="DockerInstallerArguments"/> (30 minutes at most). Exit 0 =
    /// installed, the installer deleted. The all-users installer is never run.
    /// </summary>
    public static async Task<DockerInstalled> DockerAsync(DockerEntry? entry, bool acceptLicence, Action<string>? log = null, CancellationToken ct = default)
    {
        if (entry is not { Usable: true }) throw new SetupInstallException("toolMissing", L.T("core.setup.toolMissing", new { what = "Docker Desktop" }));
        const string what = "Docker Desktop";
        Directory.CreateDirectory(SetupTools.DownloadDir);
        string part = Path.Combine(SetupTools.DownloadDir, "Docker Desktop Installer.exe.part");
        string installer = Path.Combine(SetupTools.DownloadDir, "Docker Desktop Installer.exe");
        long min = entry.Size / 2, max = entry.Size + entry.Size / 2;
        // The address always serves the current release: a part of an older one must not be continued, and an
        // installer left from an earlier day is fetched again.
        foreach (var stale in new[] { part, installer })
            if (File.Exists(stale) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale)).TotalHours > 24) TryDelete(stale);
        if (!File.Exists(installer))
        {
            await DownloadAsync(what, entry.Url, part, entry.Size, total => total is null || (total >= min && total <= max), log, ct).ConfigureAwait(false);
            File.Move(part, installer, overwrite: true);
        }
        Process? p;
        // Held open for reading alone from its check until it has started: nothing can write, rename or delete the
        // file between the two, so what runs is what was checked.
        FileStream held;
        try { held = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupInstallException("failed", ex.Message, ex);
        }
        using (held)
        {
            long size = held.Length;
            if (size < min || size > max)
            {
                held.Dispose();
                TryDelete(installer);
                throw new SetupInstallException("download", L.T("core.setup.downloadFailed",
                    new { what, error = L.T("core.setup.wrongSize", new { size = Mb(size), expected = Mb(entry.Size) }) }));
            }
            var verdict = Authenticode.Check(held, entry.Signer);
            if (verdict != AuthenticodeVerdict.Trusted)
            {
                held.Dispose();
                TryDelete(installer);
                // A revocation Windows could not ask about is no verdict on the signer: said as such, deleted all the same.
                throw new SetupInstallException("badSignature",
                    L.T(verdict == AuthenticodeVerdict.RevocationUnknown ? "core.setup.revocationUnknown" : "core.setup.badSignature"));
            }
            log?.Invoke(L.T("core.setup.docker.installing"));
            var psi = new ProcessStartInfo(installer) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = SetupTools.DownloadDir };
            foreach (var a in DockerInstallerArguments(acceptLicence)) psi.ArgumentList.Add(a);
            try { p = Process.Start(psi); }
            catch (Win32Exception ex) { throw new SetupInstallException("failed", ex.Message, ex); }
        }
        if (p is null) throw new SetupInstallException("failed", L.T("core.setup.docker.failed", new { code = -1, log = DockerInstallLog }));
        using (p)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(30));
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                throw new SetupInstallException("failed", L.T("core.setup.docker.timeout", new { minutes = 30, log = DockerInstallLog }));
            }
            if (p.ExitCode != 0) throw new SetupInstallException("failed", L.T("core.setup.docker.failed", new { code = p.ExitCode, log = DockerInstallLog }));
        }
        TryDelete(installer);
        log?.Invoke(L.T("core.setup.docker.installed"));
        string location = MachineCheck.DockerInstall()?.Location
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DockerDesktop");
        return new DockerInstalled(location);
    }

    /// <summary>Where Docker Desktop's installer writes its log for a per-user install.</summary>
    public static string DockerInstallLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Docker", "install-log.txt");

    /// <summary>Start Docker Desktop (its own <c>Docker Desktop.exe</c>, as this user and never through the shell) and
    /// wait for its engine to answer (<c>docker.exe version</c>) for 5 minutes at most. An engine that already answers
    /// is done at once. A Docker Desktop whose program asks for administrator rights is not started at all (Windows
    /// refuses such a start without the shell, error 740, where the shell would show the permission prompt).</summary>
    public static async Task<DockerStarted> DockerStartAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        if (MachineCheck.DockerInstall() is not { } install)
            throw new SetupInstallException("failed", L.T("core.setup.docker.noEngine", new { minutes = 5 }));
        if (MachineCheck.EngineVersion(install.Location, TimeSpan.FromSeconds(15), ct) is { } running)
        {
            log?.Invoke(L.T("core.setup.docker.ready", new { version = running }));
            return new DockerStarted(running);
        }
        log?.Invoke(L.T("core.setup.docker.starting"));
        string app = Path.Combine(install.Location, "Docker Desktop.exe");
        try { using var _ = Process.Start(new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = install.Location }); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            throw new SetupInstallException("failed", L.T("core.setup.docker.startElevated", new { path = app }), ex);
        }
        catch (Win32Exception ex) { throw new SetupInstallException("failed", ex.Message, ex); }
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (MachineCheck.EngineVersion(install.Location, TimeSpan.FromSeconds(15), ct) is { } version)
            {
                log?.Invoke(L.T("core.setup.docker.ready", new { version }));
                return new DockerStarted(version);
            }
        }
        throw new SetupInstallException("failed", L.T("core.setup.docker.noEngine", new { minutes = 5 }));
    }

    // ── WSL 2's memory ──

    /// <summary>Let WSL 2 — Docker's containers — use up to <paramref name="gb"/> GB: written into <c>.wslconfig</c>
    /// (<see cref="WslConfig.WriteMemory"/>). It applies from WSL's next start; nothing stops a running WSL.</summary>
    public static WslMemoryWritten WslMemory(int gb, Action<string>? log = null)
    {
        if (gb is < 1 or > 4096) throw new SetupInstallException("failed", L.T("core.setup.wslconfig.badSize", new { gb }));
        try
        {
            string path = WslConfig.WriteMemory(gb);
            log?.Invoke(L.T("core.setup.wslconfig.written", new { gb, path }));
            return new WslMemoryWritten(gb, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupInstallException("failed", ex.Message, ex);
        }
    }

    // ── Windows restart ──

    /// <summary>Restart Windows at once, <paramref name="comment"/> as its reason (<c>shutdown.exe /r /t 0</c>: Windows
    /// asks the open programs to close). Throws with <c>core.setup.restart.failed</c> when Windows refuses.</summary>
    public static void RestartWindows(string comment)
    {
        string shutdown = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        var r = TimedProcess.Run(shutdown, new[] { "/r", "/t", "0", "/c", (comment ?? "").Replace('"', '\'') }, TimeSpan.FromSeconds(20), null, CancellationToken.None);
        if (r.Code != 0)
            throw new InvalidOperationException(L.T("core.setup.restart.failed",
                new { error = r.Code is null ? L.T("core.setup.timedOut", new { seconds = 20 })
                    : L.T("core.setup.exited", new { code = r.Code, output = r.Stderr.Trim() }) }));
    }

    // ── downloads ──

    /// <summary>A pinned file into <c>tools\.download\</c>: a complete copy that already is the pinned file is used, a
    /// partial one is continued; the result must have the pinned size and sha256, else it is deleted (badHash). It comes
    /// back held open for reading alone (<see cref="OpenPinned"/>): the caller uses the very bytes that were checked, and
    /// lets the file go before it deletes it.</summary>
    private static async Task<FileStream> DownloadPinnedAsync(string what, ToolEntry entry, Action<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(SetupTools.DownloadDir);
        string name = Path.GetFileName(new Uri(entry.Url).AbsolutePath);
        if (name.Length == 0) name = "download.bin";
        string done = Path.Combine(SetupTools.DownloadDir, name), part = done + ".part";
        if (File.Exists(done))
        {
            if (OpenPinned(done, entry) is { } kept) return kept;
            TryDelete(done);
        }
        if (File.Exists(part) && new FileInfo(part).Length > entry.Size) TryDelete(part);
        await DownloadAsync(what, entry.Url, part, entry.Size, total => total is null || total == entry.Size, log, ct).ConfigureAwait(false);
        if (OpenPinned(part, entry) is not { } checkedPart)
        {
            TryDelete(part);
            throw new SetupInstallException("badHash", L.T("core.setup.badHash", new { what }));
        }
        checkedPart.Dispose();
        File.Move(part, done, overwrite: true);
        if (OpenPinned(done, entry) is { } fresh) return fresh;
        TryDelete(done);
        throw new SetupInstallException("badHash", L.T("core.setup.badHash", new { what }));
    }

    /// <summary>The file at <paramref name="path"/> opened for reading and shared for reading alone — nothing can write,
    /// rename or delete it while the stream is open — when it has the pinned size and sha256, at its start; null (and
    /// nothing held) otherwise.</summary>
    private static FileStream? OpenPinned(string path, ToolEntry entry)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length == entry.Size
                && string.Equals(Convert.ToHexString(SHA256.HashData(fs)), entry.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                fs.Position = 0;
                return fs;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"setup: {Path.GetFileName(path)} could not be checked ({ex.Message})");
        }
        fs?.Dispose();
        return null;
    }

    /// <summary>One download with its progress lines (one every two seconds at most). <paramref name="sizeOk"/> judges the
    /// size the server names; a size it refuses ends the download (code download).</summary>
    private static async Task DownloadAsync(string what, string url, string dest, long expected, Func<long?, bool> sizeOk, Action<string>? log, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long? refused = null;
        long lastTick = 0;
        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            if (p.Total is { } total && !sizeOk(total))
            {
                refused = total;
                stop.Cancel();
                return;
            }
            long now = Environment.TickCount64;
            bool end = p.Total is { } t && p.Received >= t;
            if (!end && now - lastTick < 2000) return;
            lastTick = now;
            log?.Invoke(L.T("core.setup.download", new { what, done = Mb(p.Received), total = Mb(p.Total ?? expected) }));
        });
        try
        {
            if (DownloadOverride is { } fake) await fake(url, dest, progress, stop.Token).ConfigureAwait(false);
            else await Downloader.DownloadResumableAsync(url, dest, progress, stop.Token, expectZip: dest.EndsWith(".zip.part", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (refused is null)
        {
            throw new SetupInstallException("download", L.T("core.setup.downloadFailed", new { what, error = ex.Message }), ex);
        }
        catch (OperationCanceledException) { }
        if (refused is { } size)
        {
            TryDelete(dest);
            throw new SetupInstallException("download", L.T("core.setup.downloadFailed",
                new { what, error = L.T("core.setup.wrongSize", new { size = Mb(size), expected = Mb(expected) }) }));
        }
    }

    /// <summary>An <see cref="IProgress{T}"/> whose callback runs on the thread that reports, before the download goes
    /// on — unlike <see cref="Progress{T}"/>, which hands every report to the thread pool when it is made off a UI
    /// thread: a report's size check cancels a token that dies with the download, and its line must not reach the
    /// caller after the install's outcome.</summary>
    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    /// <summary>Bytes as "12.6 MB".</summary>
    public static string Mb(long bytes) => (bytes / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info($"setup: {dir} was left behind: {ex.Message}"); }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info($"setup: {file} was left behind: {ex.Message}"); }
    }

    private static void TryMove(string from, string to)
    {
        try { Directory.Move(from, to); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info($"setup: {from} could not be put back: {ex.Message}"); }
    }
}
