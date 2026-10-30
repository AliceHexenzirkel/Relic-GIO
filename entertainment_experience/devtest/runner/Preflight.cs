using System.Text.Json;
using Microsoft.Win32;
using Relic.Core.Fiddler;
using Relic.Core.Install;
using Relic.Core.Isolation;
using Relic.Core.Launch;
using Relic.Core.Play;
using Relic.Core.State;

namespace Relic.DevTest;

/// <summary>Everything a session of the runner works with, found and checked before anything changes.</summary>
internal sealed class Plan
{
    public string Version = "";
    public RelicState State = null!;
    public InstalledVersion Inst = null!;
    public string ExpectHost = "";
    public string DllPath = "";
    public string DllSha256 = "";
    public string SourcePayload = "";
    public InjectEntry SourceEntry = null!;
    public string StageDir = "";
    public ReleaseSource Release = null!;
    public string OwnFiles = OwnFileChoices.Keep;
    public WindowSpec? Window;
    public string SlotReg = "";
    public bool WindowLeftover;
    public string? ConfigFile;
    public List<string> Notes = new();

    public string ModDir => Path.Combine(Inst.GameDir, "ayy", "anime", "ee");
    public string GameDll => Path.Combine(Inst.GameDir, Payload.ModDllDst.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>The payload the release DLL is put back from after a session — read, never written.</summary>
internal sealed record ReleaseSource(string Root, string From, InjectEntry Entry);

internal static class Paths
{
    /// <summary>The folder this program runs from, taken at its start — before the base directory points at the
    /// stage folder.</summary>
    public static string RunnerDir { get; set; } = AppContext.BaseDirectory;

    public const string DefaultStage = @"D:\relic-devtest\stage";

    public static string RelicData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic");

    /// <summary>The repository the runner was built in: the first folder above it that holds Relic.slnx and
    /// payload\manifest.json.</summary>
    public static string? RepoRoot()
    {
        var dir = new DirectoryInfo(RunnerDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Relic.slnx"))
                && File.Exists(Path.Combine(dir.FullName, "payload", "manifest.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>A version's profile slot export, as Relic's ProfileStore names it.</summary>
    public static string SlotReg(string profileId) => Path.Combine(RelicData, "profiles", profileId, "genshin.reg");
}

/// <summary>The launcher the owner installed (Inno Setup, per user): where it is, from its uninstall entry, else its
/// default folder.</summary>
internal static class InstalledLauncher
{
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7A6F5E2-9C41-4E7A-9F2D-RELIC0000001}_is1";

    public static string? Root()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (k?.GetValue("InstallLocation") is string loc && loc.Trim().Length > 0
                && File.Exists(Path.Combine(loc.Trim(), "Relic.exe")))
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(loc.Trim()));
        }
        catch (Exception) { /* the default folder below */ }
        string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Relic");
        return File.Exists(Path.Combine(def, "Relic.exe")) ? def : null;
    }
}

internal static class Preflight
{
    /// <summary>A release payload that can put the version's DLL back: its manifest names the entry, its file is
    /// there, hashes to the entry's sha256 and is no DevTest build. Null, with the reason, otherwise.</summary>
    public static ReleaseSource? TryRelease(string root, string from, string version, out string? why)
    {
        why = null;
        try
        {
            // A payload that ships no enhancements DLL for the version: its release state of the game folder is no DLL.
            if (!Payload.TryFindInjectEntry(root, version, out var shipped))
                return new ReleaseSource(root, from, Payload.SyntheticEntry(version));
            var entry = shipped!;
            string file = Payload.SourcePath(root, entry.Src);
            if (!File.Exists(file)) { why = $"{file} is not there"; return null; }
            if (DevBuild.CarriesMarker(file)) { why = $"{file} is a DevTest build"; return null; }
            string sha = Files.Sha256(file);
            if (!Files.SameHash(sha, entry.Sha256)) { why = $"{file} hashes to {sha}, its manifest says {entry.Sha256}"; return null; }
            return new ReleaseSource(root, from, entry);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            why = ex.Message;
            return null;
        }
    }

    /// <summary>The release payload, in this order: <c>--restore-from</c> (only that one when it is given), the
    /// installed launcher's, the repository's.</summary>
    public static ReleaseSource? FindRelease(string? explicitRoot, string? repoPayload, string version,
        List<string> problems, List<string> notes)
    {
        if (explicitRoot is not null)
        {
            var r = TryRelease(Path.GetFullPath(explicitRoot), "--restore-from", version, out string? why);
            if (r is null) problems.Add($"--restore-from {explicitRoot}: {why}");
            return r;
        }
        string? installed = InstalledLauncher.Root();
        if (installed is not null)
        {
            var r = TryRelease(Path.Combine(installed, "payload"), "the installed launcher", version, out string? why);
            if (r is not null) return r;
            notes.Add($"the installed launcher's payload cannot put the release DLL back ({why}) -- the repository's is used");
        }
        else
            notes.Add("no installed launcher was found -- the release DLL is put back from the repository's payload");
        if (repoPayload is not null)
        {
            var r = TryRelease(repoPayload, "the repository", version, out string? why);
            if (r is not null) return r;
            problems.Add($"no payload can put the release DLL of {version} back afterwards (the repository's: {why})");
        }
        else
            problems.Add($"no payload can put the release DLL of {version} back afterwards");
        return null;
    }

    /// <summary>The folders a stage may neither lie in nor hold: Relic's data, the launcher's own folder (where it is
    /// installed, and its default one), every game folder, and the repository — a DevTest DLL staged there could be
    /// committed, or packed into an installer with the publish output.</summary>
    public static List<string> StageForbidden(string relicData, string? installedRoot, IEnumerable<string> gameDirs, string? repo)
    {
        var forbidden = new List<string>
        {
            relicData,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Relic"),
        };
        if (installedRoot is not null) forbidden.Add(installedRoot);
        forbidden.AddRange(gameDirs);
        if (repo is not null) forbidden.Add(repo);
        return forbidden;
    }

    /// <summary>The installed version, or a refusal that says why there is none.</summary>
    public static (RelicState State, InstalledVersion Inst) Installed(string version)
    {
        var state = RelicState.TryRead()
            ?? throw new Refusal($"Relic's state file ({RelicState.DefaultPath}) cannot be read");
        var inst = state.FindInstalled(version)
            ?? throw new Refusal($"version {version} is not installed in Relic");
        if (string.IsNullOrWhiteSpace(inst.ProfileId) || inst.ProfileId.Contains("..")
            || inst.ProfileId.IndexOfAny(new[] { '\\', '/' }) >= 0
            || string.Equals(inst.ProfileId, ProfileStore.LiveId, StringComparison.OrdinalIgnoreCase))
            throw new Refusal($"version {version} names the profile slot '{inst.ProfileId}', which the runner does not edit");
        return (state, inst);
    }

    /// <summary>Every check of <c>play</c> / <c>check</c>. Nothing is changed; every problem found is collected, and
    /// any problem refuses the run.</summary>
    public static Plan Run(Options o)
    {
        var problems = new List<string>();
        var plan = new Plan
        {
            Version = o.Require("version").Trim(),
            DllPath = Path.GetFullPath(o.Require("dll")),
            ExpectHost = o.Require("expect-host").Trim(),
            OwnFiles = (o.Get("own-files") ?? OwnFileChoices.Keep).Trim().ToLowerInvariant(),
            Window = WindowSpec.Parse(o.Get("window") ?? "1600x900"),
            ConfigFile = o.Get("config") is { } c ? Path.GetFullPath(c) : null,
            StageDir = Path.GetFullPath(o.Get("stage") ?? Paths.DefaultStage),
        };
        if (plan.OwnFiles != OwnFileChoices.Keep && plan.OwnFiles != OwnFileChoices.Replace)
            throw new UsageException($"--own-files '{plan.OwnFiles}': keep or replace");

        (plan.State, plan.Inst) = Installed(plan.Version);
        var s = plan.State.Settings;
        string gameDir = plan.Inst.GameDir;

        if (!File.Exists(Path.Combine(gameDir, "GenshinImpact.exe")))
            problems.Add($"{gameDir} holds no GenshinImpact.exe -- is the drive there?");
        if (!s.Enhancements)
            problems.Add("Relic's 'In-game enhancements' setting is off: the session would not inject the DevTest DLL -- switch it on in Relic's Settings");
        if (!string.Equals(s.ServerHost.Trim(), plan.ExpectHost, StringComparison.OrdinalIgnoreCase))
            problems.Add($"Relic points at the server '{s.ServerHost}', the run expects '{plan.ExpectHost}' -- enter '{plan.ExpectHost}' on Relic's start screen (the runner never writes Relic's state)");

        // PlaySession records an injector pair it finds and saves the state when the record changes; with the pair
        // recorded as it is on disk there is nothing to record, and the session writes no state file.
        string launcher = Path.Combine(gameDir, "ayy", "anime", "build", "launcher.exe");
        string mhynot = Path.Combine(gameDir, "ayy", "anime", "build", "mhynot2.dll");
        if (!File.Exists(launcher) || !File.Exists(mhynot)
            || !string.Equals(plan.Inst.LauncherExe, launcher, StringComparison.OrdinalIgnoreCase))
            problems.Add($"the injector of {plan.Version} is not recorded as it is on disk -- play {plan.Version} once from Relic, which records it");

        if (ProcessGuard.AnyRunning())
            problems.Add("a game client is running -- close it first");
        var profiles = ProfileStore.ForGenshin();
        if (profiles.IsTornLoad())
            problems.Add("a profile swap was cut halfway -- start Relic once (its recovery repairs it), or run 'DevTestRunner --tray'");
        else if (!profiles.LiveIsActive())
            problems.Add($"the profile '{profiles.ActiveId()}' is still loaded from an earlier session -- run 'DevTestRunner --tray' (it starts the installed Relic, whose recovery puts the live profile back), then try again");
        using (var gate = new Semaphore(1, 1, PlaySession.SessionGateName))
        {
            if (gate.WaitOne(0)) gate.Release();
            else problems.Add("a play session is in progress (Relic's PLAY or a desktop shortcut) -- wait until its game is closed");
        }

        var ready = FiddlerAutomation.CheckReady(s.ServerHost, s.ServerPort);
        if (!ready.AllGood)
            problems.Add($"Fiddler is not ready for this server (installed: {ready.Installed}, certificate trusted: {ready.CertTrusted}, rules: {ready.RulesPresent}) -- "
                + "the session would set it up, consent dialog included; play once from Relic so that it prepares Fiddler");
        else if (FiddlerAutomation.IsRunning)
            plan.Notes.Add("Fiddler is running: the session reuses it and stops it when the game is closed");

        if (!File.Exists(plan.DllPath))
            problems.Add($"{plan.DllPath} is not there -- build it: entertainment_experience\\tools\\build_ee.ps1 -Config DevTest");
        else
        {
            try
            {
                var pe = DevBuild.ReadPe(plan.DllPath);
                if (!pe.IsX64Dll) problems.Add($"{plan.DllPath} is not an x64 DLL");
                if (pe.VcRuntimeImports.Count > 0)
                    problems.Add($"{plan.DllPath} imports {string.Join(", ", pe.VcRuntimeImports)}: launcher.exe would load nothing on a PC without the Visual C++ redistributable -- build it with the static C runtime");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                problems.Add($"{plan.DllPath}: {ex.Message}");
            }
            if (!DevBuild.CarriesMarker(plan.DllPath))
                problems.Add($"{plan.DllPath} carries no DevTest marker: it is not a DevTest build (build_ee.ps1 -Config DevTest)");
            plan.DllSha256 = Files.Sha256(plan.DllPath);
        }

        string? repo = Paths.RepoRoot();
        string? repoPayload = repo is null ? null : Path.Combine(repo, "payload");
        plan.SourcePayload = o.Get("payload") is { } p ? Path.GetFullPath(p) : repoPayload ?? "";
        if (plan.SourcePayload.Length == 0)
            problems.Add("the repository's payload folder was not found above the runner -- pass --payload <folder>");
        else
        {
            try
            {
                if (Payload.TryFindInjectEntry(plan.SourcePayload, plan.Version, out var shipped))
                {
                    plan.SourceEntry = shipped!;
                    string src = Payload.SourcePath(plan.SourcePayload, plan.SourceEntry.Src);
                    if (!File.Exists(src))
                        problems.Add($"{src} is not there");
                    else if (DevBuild.CarriesMarker(src))
                        problems.Add($"{src} carries the DevTest marker: a DevTest build is in a payload folder, where it must never be");
                }
                else
                {
                    plan.SourceEntry = Payload.SyntheticEntry(plan.Version);
                    plan.Notes.Add($"the payload ships no enhancements DLL for {plan.Version}: the stage carries an entry of its own for the "
                        + "session, and afterwards the DevTest DLL is taken out of the game folder");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                problems.Add($"the payload to stage: {ex.Message}");
            }
        }

        var forbidden = StageForbidden(Paths.RelicData, InstalledLauncher.Root(),
            plan.State.Installed.Where(i => !string.IsNullOrWhiteSpace(i.GameDir)).Select(i => i.GameDir), repo);
        if (plan.SourcePayload.Length > 0 && Stage.Problem(plan.StageDir, plan.SourcePayload, forbidden) is { } stageProblem)
            problems.Add(stageProblem);

        plan.Release = FindRelease(o.Get("restore-from"), repoPayload, plan.Version, problems, plan.Notes)!;

        plan.SlotReg = Paths.SlotReg(plan.Inst.ProfileId);
        plan.WindowLeftover = File.Exists(plan.SlotReg + WindowMode.CopySuffix);
        if (plan.WindowLeftover)
        {
            plan.Notes.Add($"{plan.SlotReg + WindowMode.CopySuffix} is there from an earlier run: the owner's screen values are put back from it first");
            if (WindowMode.Problem(plan.SlotReg + WindowMode.CopySuffix) is { } leftoverProblem)
                problems.Add(leftoverProblem);
        }
        if (plan.Window is not null)
        {
            if (!File.Exists(plan.SlotReg))
                problems.Add($"{plan.SlotReg} is not there: the slot of {plan.Version} was never saved -- play {plan.Version} once from Relic, or pass --window keep");
            else if (WindowMode.Problem(plan.SlotReg) is { } regProblem)
                problems.Add(regProblem);
        }

        if (plan.ConfigFile is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(plan.ConfigFile));
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    problems.Add($"--config {plan.ConfigFile}: not a JSON object");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                problems.Add($"--config {plan.ConfigFile}: {ex.Message}");
            }
        }

        if (WindowsInfo.Detect().IsWindows11 && plan.SourcePayload.Length > 0
            && LaunchSupport.Windows11Unsupported(plan.Version, plan.SourcePayload))
            problems.Add($"the payload says the client of {plan.Version} is not known to start on Windows 11 -- the runner cannot ask the question PLAY asks");

        if (problems.Count > 0) throw new Refusal(problems);
        return plan;
    }
}
