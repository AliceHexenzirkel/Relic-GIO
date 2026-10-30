using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Relic.Core.Server;
using Relic.Core.Util;

/// <summary>
/// One install choice per server version: what an agent install does with each version (download now, I have it,
/// not now, keep), and the rules around it.
///  - the names derived from a version id (GIO_STACK_&lt;nn&gt;, the form's stack&lt;nn&gt;) and the choice tokens;
///  - the install plan: a choice per version, the installer environment (GIO_STACK_&lt;nn&gt; for every version, never
///    a GIO_FETCH_&lt;nn&gt;), the config lines, the folder rules (a Windows folder is a full path on a drive letter, and a
///    folder the form names is written in the agent's own spelling), a download into a folder that holds other files,
///    the paths one root probe answers;
///  - a folder the target already stores stays (KeepStoredFolders): what a re-install keeps, what it says;
///  - the installers carry the same key and never write it into the config;
///  - the agent on this PC: what stands at a folder, the drives the agent refuses, the install's order (a stored
///    folder kept before the checks, an unreadable configuration refused before anything is written, the folders
///    of a configuration the running agent changed meanwhile taken as they are when it is written);
///  - the follow-up of an install: what the Server page runs next, decided from the agent that answers after it;
///  - the package sizes the form quotes; the root probe's script and the reading of its output (and, with Git Bash
///    on this PC, the script run for real); an SSH install looks at the box only as root, its stored folders
///    first; the installer's refusal of a relocation that ended while it ran, and the text of an update over SSH it
///    stopped with the new files in place (AgentDeployer.UpdateStoppedText);
///  - the Backend's order (deploy.done after the busy flag is free), what deploy.done carries, and the texts.
/// Everything it writes is in temp folders; the agent root is a temp root (LocalAgent.RootOverride).
/// </summary>
static class StackChoiceSpike
{
    public const string Name = "stackchoice";

    /// <summary>The texts of the install choice, the install's follow-up, the root probe and the installer's
    /// refusal of a relocation that ended while it ran (en.json).</summary>
    static readonly string[] TextKeys =
    {
        "core.deploy.badStackChoice", "core.deploy.chainAlreadyRunning", "core.deploy.chainAskTimeout", "core.deploy.chainAsking",
        "core.deploy.chainKeptDir", "core.deploy.chainNeedsRestart", "core.deploy.chainNoHealth", "core.deploy.chainNoStatus",
        "core.deploy.chainNotConfigured", "core.deploy.chainPresent", "core.deploy.chainRefused", "core.deploy.chainRepoint",
        "core.deploy.chainRepointOnly", "core.deploy.chainRestartFirst", "core.deploy.chainRestartToken", "core.deploy.chainUnknownVersion",
        "core.deploy.configUnreadable", "core.deploy.configUnreadableLate", "core.deploy.dirDriveType", "core.deploy.dirKept",
        "core.deploy.dirNotLocal", "core.deploy.dirsChanged", "core.deploy.dirsChangedFilesIn", "core.deploy.downloadDirNotEmpty",
        "core.deploy.drive.cd", "core.deploy.drive.network", "core.deploy.drive.none", "core.deploy.drive.ram",
        "core.deploy.haveNeedsDir", "core.deploy.probeFailed", "core.deploy.probeTimeout",
    };

    const string Linux = AgentInstallPlan.TargetLinux, Win = AgentInstallPlan.TargetWindows;

    public static int Run(string? repoRoot = null)
    {
        Console.WriteLine($"== {Name} spike: one install choice per server version, kept folders, root probes, the install's follow-up ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string? repo = repoRoot is { Length: > 0 } ? repoRoot
            : Environment.GetEnvironmentVariable("RELIC_REPO_ROOT") is { Length: > 0 } fromEnv ? fromEnv
            : FindRepo();
        if (repo is null || !File.Exists(Path.Combine(repo, "Relic.slnx")))
        {
            Check("the repository is found (the argument, RELIC_REPO_ROOT, or a folder holding Relic.slnx above here)", false);
            return Summary(results);
        }

        Names(Check);
        PlanRules(Check);
        StoredFolders(Check);
        InstallerKeys(repo, Check);
        LocalAgentRules(repo, Check);
        FollowUp(Check);
        Packages(repo, Check);
        Probe(Check);
        InstallerRefusal(repo, Check);
        DeployerText(repo, Check);
        BackendText(repo, Check);
        var missingKeys = TextKeys.Where(k => !L.Has(k)).ToList();
        Check($"i18n: the install-choice texts are in en.json ({TextKeys.Length} keys)"
              + (missingKeys.Count > 0 ? $" — missing {missingKeys.Count}: {string.Join(", ", missingKeys.Take(4))}{(missingKeys.Count > 4 ? ", …" : "")}" : ""),
            missingKeys.Count == 0);
        return Summary(results);
    }

    static int Summary(List<(string name, bool ok)> results)
    {
        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"{Name} spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    static AgentInstallPlan P(string target, IReadOnlyList<string>? versions = null) => new()
    {
        Target = target, Listen = "0.0.0.0:18080", Token = new string('a', 32), Region = "dev_docker",
        Versions = versions ?? ServerVersions.All,
    };

    static string Line(string cfg, string key) => cfg.Split('\n').FirstOrDefault(l => l.StartsWith(key + "=")) ?? "";

    static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── A. names ──
    static void Names(Action<string, bool> Check)
    {
        Check("names: the installer-only choice key is GIO_STACK_ + the id without its dot (1.6, 2.8, 3.2, 12.34)",
            ServerVersions.StackKey("1.6") == "GIO_STACK_16" && ServerVersions.StackKey("2.8") == "GIO_STACK_28"
            && ServerVersions.StackKey("3.2") == "GIO_STACK_32" && ServerVersions.StackKey("12.34") == "GIO_STACK_1234");
        Check("names: the form's choice field is stack + the id without its dot",
            ServerVersions.FormStack("1.6") == "stack16" && ServerVersions.FormStack("3.2") == "stack32");
        bool parsed = StackChoices.TryParse(" Download ", out var dl) && dl == StackChoice.Download
            && StackChoices.TryParse("HAVE", out var hv) && hv == StackChoice.Have
            && StackChoices.TryParse("later", out var lt) && lt == StackChoice.Later
            && StackChoices.TryParse("Keep", out var kp) && kp == StackChoice.Keep;
        bool refused = new[] { "", "  ", "y", "fetch", "1", "0", "later now", null }.All(s => !StackChoices.TryParse(s, out _));
        Check("names: the four tokens are later / have / download / keep; a token is read trimmed in any letter case, and nothing else is one (\"\", \"y\", \"fetch\", a number, null)",
            StackChoices.Token(StackChoice.Later) == "later" && StackChoices.Token(StackChoice.Have) == "have"
            && StackChoices.Token(StackChoice.Download) == "download" && StackChoices.Token(StackChoice.Keep) == "keep"
            && parsed && refused);
    }

    // ── B. the plan ──
    static void PlanRules(Action<string, bool> Check)
    {
        var later = P(Linux);
        foreach (var v in ServerVersions.All) later.SetChoice(v, StackChoice.Later);
        string laterCfg = later.RenderConfig();
        var laterEnv = later.ToEnvironment();
        Check("plan: every version Not now validates — no version at all is a valid install (the Server page adds versions)",
            later.Validate().Count == 0);
        Check("plan: Not now everywhere writes each folder line SET AND EMPTY and sends GIO_STACK_<nn>=later for every version, no GIO_FETCH_ key, no download",
            ServerVersions.All.All(v => Line(laterCfg, ServerVersions.DirKey(v)) == ServerVersions.DirKey(v) + "=")
            && ServerVersions.All.All(v => laterEnv.TryGetValue(ServerVersions.StackKey(v), out var s) && s == "later")
            && !laterEnv.Keys.Any(k => k.StartsWith("GIO_FETCH_", StringComparison.Ordinal)) && later.FetchVersions().Count == 0);
        var laterDir = P(Linux); laterDir.SetDir("2.8", "/srv/2.8_live"); laterDir.SetChoice("2.8", StackChoice.Later);
        Check("plan: Not now with a folder left in the form writes that version's folder line empty — GIO_DIR_28= and GIO_STACK_28=later",
            Line(laterDir.RenderConfig(), "GIO_DIR_28") == "GIO_DIR_28=" && laterDir.ToEnvironment()["GIO_STACK_28"] == "later"
            && laterDir.ToEnvironment()["GIO_DIR_28"] == "");
        var haveBlank = P(Linux); haveBlank.SetChoice("2.8", StackChoice.Have);
        Check("plan: I have it without a folder is refused by its own text (haveNeedsDir), and nothing else",
            haveBlank.Validate().SequenceEqual(new[] { L.T("core.deploy.haveNeedsDir", new { version = "2.8" }) }));
        var dlBlank = P(Linux); dlBlank.SetChoice("3.2", StackChoice.Download);
        Check("plan: Download now without a folder is refused (fetchNeedsDir), and nothing else",
            dlBlank.Validate().SequenceEqual(new[] { L.T("core.deploy.fetchNeedsDir", new { version = "3.2" }) }));
        var dl32 = P(Linux); dl32.SetChoice("3.2", StackChoice.Download); dl32.SetDir("3.2", "/srv/3.2_live");
        var dl32Env = dl32.ToEnvironment();
        Check("plan: Download now into /srv/3.2_live — GIO_STACK_32=download and GIO_DIR_32 in the environment, no GIO_FETCH_ key, a download of 3.2, and nothing of the choice in the config",
            dl32.Validate().Count == 0 && dl32Env["GIO_STACK_32"] == "download" && dl32Env["GIO_DIR_32"] == "/srv/3.2_live"
            && !dl32Env.Keys.Any(k => k.StartsWith("GIO_FETCH_", StringComparison.Ordinal)) && dl32.FetchVersions().SequenceEqual(new[] { "3.2" })
            && !dl32.RenderConfig().Contains("GIO_STACK_") && !dl32.RenderConfig().Contains("GIO_FETCH_"));
        var keepRel = P(Linux); keepRel.SetChoice("1.6", StackChoice.Keep); keepRel.SetDir("1.6", "srv/1.6_live");
        Check("plan: Keep holds no folder rule (the folder is the target's own) and is no folder to check for a stack",
            keepRel.Validate().Count == 0 && keepRel.MissingStackEntries(_ => false).Count == 0);
        var legacy = P(Linux); legacy.SetFetch("1.6", true); legacy.SetDir("1.6", "/srv/1.6_live"); legacy.SetDir("2.8", "/x");
        bool legacyReads = legacy.ChoiceFor("1.6") == StackChoice.Download && legacy.ChoiceFor("2.8") == StackChoice.Have
            && legacy.ChoiceFor("3.2") == StackChoice.Later;
        legacy.SetChoice("1.6", StackChoice.Later);
        bool explicitWins = legacy.ChoiceFor("1.6") == StackChoice.Later && !legacy.FetchVersions().Contains("1.6") && !legacy.FetchFor("1.6");
        legacy.SetFetch("1.6", true);
        Check("plan: a plan built the old way reads as before — a mark = Download, a folder = Have, nothing = Later; an explicit choice wins over the mark, and the mark set again takes the choice back",
            legacyReads && explicitWins && legacy.ChoiceFor("1.6") == StackChoice.Download && legacy.Fetch16);
        IReadOnlyList<string> WinHave(string dir)
        {
            var w = P(Win); w.SetChoice("2.8", StackChoice.Have); w.SetDir("2.8", dir);
            return w.Validate();
        }
        Check(@"plan: on Windows a UNC folder (\\server\share\2.8_live) is refused with dirNotLocal, and so is a drive-relative C:relative",
            WinHave(@"\\server\share\2.8_live").SequenceEqual(new[] { L.T("core.deploy.dirNotLocal", new { version = "2.8", dir = @"\\server\share\2.8_live" }) })
            && WinHave("C:relative").SequenceEqual(new[] { L.T("core.deploy.dirNotLocal", new { version = "2.8", dir = "C:relative" }) }));
        Check(@"plan: on Windows D:/gio/2.8_live and C:\relic_servers\2.8_live are full paths on a drive letter",
            WinHave("D:/gio/2.8_live").Count == 0 && WinHave(@"C:\relic_servers\2.8_live").Count == 0
            && AgentInstallPlan.IsWindowsDrivePath(@"c:\x") && !AgentInstallPlan.IsWindowsDrivePath(@"\\?\C:\x") && !AgentInstallPlan.IsWindowsDrivePath(null));
        var rel = P(Linux, new[] { "1.6", "2.8", "9.9" }); rel.SetChoice("9.9", StackChoice.Have); rel.SetDir("9.9", "srv/9.9_live");
        Check("plan: on Linux a relative folder is refused (dirNotAbsolute)",
            rel.Validate().SequenceEqual(new[] { L.T("core.deploy.dirNotAbsolute", new { version = "9.9", dir = "srv/9.9_live" }) }));
        Check("plan: SameFolder — Windows ignores letter case, the slash kind and a trailing separator; Linux only a trailing slash; two blanks are no folder",
            AgentInstallPlan.SameFolder(@"C:\A\b\", "c:/a/B", true) && AgentInstallPlan.SameFolder("/home/a/", "/home/a", false)
            && !AgentInstallPlan.SameFolder("/home/A", "/home/a", false) && !AgentInstallPlan.SameFolder("", "", false)
            && !AgentInstallPlan.SameFolder("  ", null, true));
        Check(@"plan: a Windows folder in the agent's own spelling (os.path.normpath) — C:\relic_servers\\3.2_live, C:\a\.\b, C:\a\..\b, D:/gio/2.8_live/ come out whole; a drive's root keeps its separator; a UNC, a relative and a blank name stay as they are",
            AgentInstallPlan.NormalWindowsFolder(@"C:\relic_servers\\3.2_live") == @"C:\relic_servers\3.2_live"
            && AgentInstallPlan.NormalWindowsFolder(@"C:\a\.\b") == @"C:\a\b" && AgentInstallPlan.NormalWindowsFolder(@"C:\a\..\b") == @"C:\b"
            && AgentInstallPlan.NormalWindowsFolder("D:/gio/2.8_live/") == @"D:\gio\2.8_live" && AgentInstallPlan.NormalWindowsFolder(@"C:\") == @"C:\"
            && AgentInstallPlan.NormalWindowsFolder(@"\\server\share\\x") == @"\\server\share\\x" && AgentInstallPlan.NormalWindowsFolder("rel\\x") == "rel\\x"
            && AgentInstallPlan.NormalWindowsFolder(null) == "" && AgentInstallPlan.NormalWindowsFolder(@" C:\x\\y ") == @"C:\x\y");
        var norm = P(Win);
        norm.SetChoice("1.6", StackChoice.Have); norm.SetDir("1.6", @"C:\s\\1.6_live");
        norm.SetChoice("3.2", StackChoice.Download); norm.SetDir("3.2", @"C:\relic_servers\.\3.2_live\");
        norm.SetChoice("2.8", StackChoice.Have); norm.SetDir("2.8", @"C:\form\2.8_live");
        norm.KeepStoredFolders(v => v == "2.8" ? @"C:\stored\\2.8_live" : "");
        var normChanged = norm.NormalizeWindowsFolders();
        var linuxNorm = P(Linux); linuxNorm.SetChoice("1.6", StackChoice.Have); linuxNorm.SetDir("1.6", "/srv//1.6_live");
        Check("plan: NormalizeWindowsFolders puts every folder the FORM names in that spelling — the choice kept —, never a folder the target stores (the agent's own), and does nothing on a Linux plan",
            normChanged.SequenceEqual(new[] { "1.6", "3.2" }) && norm.DirFor("1.6") == @"C:\s\1.6_live" && norm.ChoiceFor("1.6") == StackChoice.Have
            && norm.DirFor("3.2") == @"C:\relic_servers\3.2_live" && norm.ChoiceFor("3.2") == StackChoice.Download
            && norm.DirFor("2.8") == @"C:\stored\\2.8_live" && norm.FolderIsStored("2.8") && norm.NormalizeWindowsFolders().Count == 0
            && linuxNorm.NormalizeWindowsFolders().Count == 0 && linuxNorm.DirFor("1.6") == "/srv//1.6_live");
        Check(@"plan: SameFolder on Windows compares full paths in the agent's spelling — C:\x\\y is C:\x\y, C:\a\.\b is C:\a\b, C:\a\..\b is C:\b — so a folder stored raw by an earlier build, or typed so, reads as the folder the agent answers; a UNC name keeps the plain rule, and Linux is unchanged",
            AgentInstallPlan.SameFolder(@"C:\x\\y", @"C:\x\y", true) && AgentInstallPlan.SameFolder(@"C:\a\.\b", @"c:\A\B\", true)
            && AgentInstallPlan.SameFolder(@"C:\a\..\b", @"C:\b", true) && AgentInstallPlan.SameFolder("D:/gio//2.8_live/", @"D:\gio\2.8_live", true)
            && AgentInstallPlan.SameFolder(@"C:\stored\\2.8_live", @"C:\stored\2.8_live", true)
            && !AgentInstallPlan.SameFolder(@"C:\a\..\b", @"C:\a\b", true) && !AgentInstallPlan.SameFolder(@"C:\x\y", @"D:\x\y", true)
            && AgentInstallPlan.SameFolder(@"\\srv\share\x", @"\\srv\share\x\", true) && !AgentInstallPlan.SameFolder("/home//a", "/home/a", false));
        var keptSpelling = P(Win); keptSpelling.SetChoice("2.8", StackChoice.Have); keptSpelling.SetDir("2.8", @"C:\stored\.\2.8_live");
        var keptNotes = keptSpelling.KeepStoredFolders(v => v == "2.8" ? @"C:\stored\\2.8_live" : "");
        Check(@"plan: a form folder that is the stored one in another spelling is kept without a note (the same folder, not a replaced one)",
            keptNotes.Count == 1 && !keptNotes[0].Replaced && keptSpelling.DirFor("2.8") == @"C:\stored\\2.8_live");

        // A download into a folder the form names that holds other files; a stored folder is the agent's to judge.
        IReadOnlyList<string> Target(FolderKind kind)
        {
            var t = P(Linux); t.SetChoice("1.6", StackChoice.Download); t.SetDir("1.6", "/srv/x");
            return t.DownloadTargetProblems(_ => kind);
        }
        Check("plan: a download into a folder that holds other files is refused by name (downloadDirNotEmpty); an absent, empty, stack or unknown folder is not",
            Target(FolderKind.Other).SequenceEqual(new[] { L.T("core.deploy.downloadDirNotEmpty", new { version = "1.6", dir = "/srv/x" }) })
            && new[] { FolderKind.Absent, FolderKind.Empty, FolderKind.Stack, FolderKind.Unknown }.All(k => Target(k).Count == 0));
        var asked = new List<string>();
        var mixed = P(Linux); mixed.SetChoice("1.6", StackChoice.Have); mixed.SetDir("1.6", "/srv/have16");
        mixed.SetChoice("2.8", StackChoice.Download); mixed.SetDir("2.8", "/srv/form28");
        mixed.SetChoice("3.2", StackChoice.Download);
        mixed.KeepStoredFolders(v => v == "3.2" ? "/srv/stored32" : "");
        mixed.DownloadTargetProblems(p => { asked.Add(p); return FolderKind.Other; });
        Check("plan: the folder check asks only about a download's folder the form names — never a Have row's, never a stored one's",
            asked.SequenceEqual(new[] { "/srv/form28" }) && mixed.ChoiceFor("3.2") == StackChoice.Download && mixed.DirFor("3.2") == "/srv/stored32");

        // One probe answers every check: the paths each one asks are among ProbePaths, each once.
        foreach (string target in new[] { Linux, Win })
        {
            var pp = P(target);
            bool win = target == Win;
            pp.SetChoice("1.6", StackChoice.Have); pp.SetDir("1.6", win ? @"C:\s\1.6_live" : "/srv/1.6_live/");
            pp.SetChoice("2.8", StackChoice.Download); pp.SetDir("2.8", win ? @"C:\s\2.8_live" : "/srv/2.8_live");
            pp.SetChoice("3.2", StackChoice.Download); pp.SetDir("3.2", win ? @"C:\s\3.2_live" : "/srv/3.2_live");
            var probePaths = pp.ProbePaths();
            var used = new List<string>();
            pp.MissingStackEntries(p => { used.Add(p); return true; });
            pp.FetchVersionsReally(p => { used.Add(p); return false; });
            pp.DownloadTargetProblems(p => { used.Add(p); return FolderKind.Empty; });
            Check($"plan ({target}): ProbePaths names every path the stack check, the download list and the folder check ask about, each once ({probePaths.Count} paths)",
                used.All(probePaths.Contains) && probePaths.Distinct(StringComparer.Ordinal).Count() == probePaths.Count && probePaths.Count == 1 + 5 + 2 + 2
                && (win || probePaths.Contains("/srv/1.6_live/docker-compose.yml.tmpl") && probePaths.Contains("/srv/2.8_live/docker-compose.yml.tmpl")));
        }
    }

    // ── C. a folder the target already stores ──
    static void StoredFolders(Action<string, bool> Check)
    {
        var w = P(Win);
        w.SetChoice("1.6", StackChoice.Have); w.SetDir("1.6", @"C:\new\1.6_live");
        w.SetChoice("2.8", StackChoice.Download); w.SetDir("2.8", @"C:\relic_servers\2.8_live");
        w.SetChoice("3.2", StackChoice.Later);
        var stored = new Dictionary<string, string> { ["1.6"] = @"C:\old\1.6_live", ["2.8"] = "" };
        var kept = w.KeepStoredFolders(v => stored.TryGetValue(v, out var s) ? s : "");
        Check(@"stored: the folder the configuration stores for 1.6 replaces the form's — one entry, Replaced (the form named C:\new\1.6_live); 1.6 is Keep at the stored folder, 2.8 (stored empty) and 3.2 (not stored) as the form said",
            kept.SequenceEqual(new[] { new StoredFolder("1.6", @"C:\old\1.6_live", @"C:\new\1.6_live", true) })
            && w.ChoiceFor("1.6") == StackChoice.Keep && w.DirFor("1.6") == @"C:\old\1.6_live" && w.FolderIsStored("1.6")
            && w.ChoiceFor("2.8") == StackChoice.Download && w.DirFor("2.8") == @"C:\relic_servers\2.8_live" && !w.FolderIsStored("2.8")
            && w.ChoiceFor("3.2") == StackChoice.Later && Line(w.RenderConfig(), "GIO_DIR_16") == @"GIO_DIR_16=C:\old\1.6_live"
            && w.ToEnvironment()["GIO_STACK_16"] == "keep");
        AgentInstallPlan Stored16(StackChoice choice, string formDir)
        {
            var s = P(Win); s.SetChoice("1.6", choice); s.SetDir("1.6", formDir);
            return s;
        }
        var dlOther = Stored16(StackChoice.Download, @"C:\elsewhere\1.6_live");
        var dlOtherKept = dlOther.KeepStoredFolders(v => v == "1.6" ? @"C:\old\1.6_live" : "");
        var dlSame = Stored16(StackChoice.Download, "c:/old/1.6_live/");
        var dlSameKept = dlSame.KeepStoredFolders(v => v == "1.6" ? @"C:\old\1.6_live" : "");
        Check("stored: Download now into another folder downloads into the STORED folder (Replaced); into the same folder spelled otherwise it is not Replaced and the row takes the stored spelling",
            dlOther.ChoiceFor("1.6") == StackChoice.Download && dlOther.DirFor("1.6") == @"C:\old\1.6_live" && dlOtherKept.Single().Replaced
            && dlSame.ChoiceFor("1.6") == StackChoice.Download && dlSame.DirFor("1.6") == @"C:\old\1.6_live" && !dlSameKept.Single().Replaced
            && dlSameKept.Single().Given == "c:/old/1.6_live/");
        var laterStored = Stored16(StackChoice.Later, "");
        var laterKept = laterStored.KeepStoredFolders(v => v == "1.6" ? @"C:\old\1.6_live" : "");
        Check("stored: Not now for a version the target stores becomes Keep — never Replaced, no form folder given",
            laterStored.ChoiceFor("1.6") == StackChoice.Keep && laterKept.Single() == new StoredFolder("1.6", @"C:\old\1.6_live", "", false));
        // Idempotent: the same answers again change nothing; a folder relocated meanwhile wins; one taken off goes.
        var again = w.KeepStoredFolders(v => stored.TryGetValue(v, out var s) ? s : "");
        Check("stored: a second pass with the same stored folders changes nothing — every entry not Replaced",
            again.All(k => !k.Replaced) && again.Count == 1 && w.DirFor("1.6") == @"C:\old\1.6_live" && w.ChoiceFor("1.6") == StackChoice.Keep
            && w.ChoiceFor("2.8") == StackChoice.Download && w.DirFor("2.8") == @"C:\relic_servers\2.8_live");
        var moved = w.KeepStoredFolders(v => v == "1.6" ? @"C:\moved\1.6_live" : "");
        Check(@"stored: a second pass where the stored 1.6 is now C:\moved\1.6_live (relocated meanwhile) moves the row there, still Keep, not Replaced",
            w.DirFor("1.6") == @"C:\moved\1.6_live" && w.ChoiceFor("1.6") == StackChoice.Keep && w.FolderIsStored("1.6") && moved.All(k => !k.Replaced));
        w.KeepStoredFolders(_ => "");
        Check("stored: a second pass where the configuration names no 1.6 folder (taken off meanwhile) sets 1.6 up later — GIO_DIR_16=, not a stored row",
            w.ChoiceFor("1.6") == StackChoice.Later && Line(w.RenderConfig(), "GIO_DIR_16") == "GIO_DIR_16=" && !w.FolderIsStored("1.6")
            && w.ToEnvironment()["GIO_STACK_16"] == "later");
        bool cleared = true;
        foreach (Action<AgentInstallPlan> setter in new Action<AgentInstallPlan>[]
        {
            x => x.SetDir("1.6", @"C:\y"), x => x.SetChoice("1.6", StackChoice.Have), x => x.SetFetch("1.6", false),
        })
        {
            var s = Stored16(StackChoice.Have, @"C:\x");
            s.KeepStoredFolders(v => v == "1.6" ? @"C:\old\1.6_live" : "");
            bool before = s.FolderIsStored("1.6");
            setter(s);
            cleared &= before && !s.FolderIsStored("1.6");
        }
        Check("stored: SetDir, SetChoice and SetFetch each make the row the caller's again (FolderIsStored false)", cleared);
        var unc = Stored16(StackChoice.Have, @"C:\form\1.6_live");
        unc.KeepStoredFolders(v => v == "1.6" ? @"\\server\share\1.6_live" : "");
        Check(@"stored: a stored UNC folder on a Windows plan is the target's own — Validate names nothing for it",
            unc.FolderIsStored("1.6") && unc.Validate().Count == 0 && unc.DirFor("1.6") == @"\\server\share\1.6_live");
    }

    // ── D. the installers carry the key and keep it out of the config ──
    static void InstallerKeys(string repo, Action<string, bool> Check)
    {
        string Text(string file)
        {
            string path = Path.Combine(repo, "agent", file);
            return File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        }
        string sh = Text("install_agent.sh"), ps1 = Text("deploy_from_windows.ps1");
        Check("installer: install_agent.sh derives the choice key from the id like the folder key — stack_key() { echo \"GIO_STACK_${1//./}\"; }",
            sh.Contains("\nstack_key() { echo \"GIO_STACK_${1//./}\"; }\n", StringComparison.Ordinal));
        // The same patterns the server spike reads the config heredoc and the managed keys with.
        var shBody = Regex.Match(sh, @"\nrender_config\(\) \{\n(?:[^\n]*\n)*?  cat <<EOF\n(.*?)\nEOF\n", RegexOptions.Singleline);
        var shManaged = Regex.Match(sh, "MANAGED=\"([^\"]*)\"");
        var psBody = Regex.Match(ps1, @"cat > /etc/gio-agent/config <<'EOF'\n(.*?)\nEOF", RegexOptions.Singleline);
        var psManaged = Regex.Match(ps1, @"\$managed = ([^\n]*)");
        Check("installer: neither installer's config text nor its managed keys name GIO_STACK_, and install_agent.sh names GIO_STACK_<nn> among the installer-only keys",
            shBody.Success && shManaged.Success && psBody.Success && psManaged.Success
            && !new[] { shBody, shManaged, psBody, psManaged }.Any(m => m.Groups[1].Value.Contains("GIO_STACK_", StringComparison.Ordinal))
            && Regex.IsMatch(sh, @"\n# GIO_STACK_<nn>, [^\n]*installer-only"));
        Check("installer: install_agent.sh counts a download wish from GIO_STACK_<nn> in its dependency pre-loop, and drops the folder a later / keep choice hands over",
            sh.Contains("if [ \"${ENV_STACK[$v]}\" = download ]; then want=y", StringComparison.Ordinal)
            && sh.Contains("case \"$c\" in later|keep) unset \"$key\" ;; esac", StringComparison.Ordinal));
        byte[] psBytes = File.Exists(Path.Combine(repo, "agent", "deploy_from_windows.ps1")) ? File.ReadAllBytes(Path.Combine(repo, "agent", "deploy_from_windows.ps1")) : [];
        Check("installer: deploy_from_windows.ps1 is pure ASCII and asks one choice per version (Get-RemoteKinds, d / h / l)",
            psBytes.Length > 0 && psBytes.All(b => b < 0x80) && ps1.Contains("function Get-RemoteKinds(", StringComparison.Ordinal)
            && ps1.Contains("-- d / h / l", StringComparison.Ordinal));
    }

    // ── E. the agent on this PC ──
    static void LocalAgentRules(string repo, Action<string, bool> Check)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "relic-stackchoice-spike-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            Directory.CreateDirectory(tmp);
            string Folder(string name) { string d = Path.Combine(tmp, name); Directory.CreateDirectory(d); return d; }
            string empty = Folder("empty"), tmplStack = Folder("tmpl"), ymlStack = Folder("yml"), notes = Folder("notes");
            File.WriteAllText(Path.Combine(tmplStack, "docker-compose.yml.tmpl"), "x");
            File.WriteAllText(Path.Combine(ymlStack, "docker-compose.yml"), "x");
            File.WriteAllText(Path.Combine(notes, "notes.txt"), "x");
            string aFile = Path.Combine(tmp, "a-file");
            File.WriteAllText(aFile, "x");
            int driveAsks = 0;
            Check("localagent: what stands at a folder — absent, empty, a stack (docker-compose.yml.tmpl, or docker-compose.yml alone), other files, a file",
                LocalAgent.FolderKindOf(Path.Combine(tmp, "absent")) == FolderKind.Absent && LocalAgent.FolderKindOf(Path.Combine(tmp, "absent", "deeper")) == FolderKind.Absent
                && LocalAgent.FolderKindOf(empty) == FolderKind.Empty && LocalAgent.FolderKindOf(tmplStack) == FolderKind.Stack
                && LocalAgent.FolderKindOf(ymlStack) == FolderKind.Stack && LocalAgent.FolderKindOf(notes) == FolderKind.Other
                && LocalAgent.FolderKindOf(aFile) == FolderKind.Other && LocalAgent.FolderKindName("") == "none" && LocalAgent.FolderKindName(null) == "none"
                && LocalAgent.FolderKindName(tmplStack) == "stack");
            try
            {
                LocalAgent.DriveTypeOverride = letter => { driveAsks++; return letter == 'Z' ? 4 : 3; };
                var sw = Stopwatch.StartNew();
                bool unknown = LocalAgent.FolderKindOf(@"\\server\x") == FolderKind.Unknown && LocalAgent.FolderKindOf("//server/x") == FolderKind.Unknown
                    && LocalAgent.FolderKindOf(@"Z:\x") == FolderKind.Unknown && LocalAgent.FolderKindName(@"z:\x") == "unknown";
                Check($"localagent: a network path — UNC, or a drive letter of a network drive — is unknown and not touched ({sw.ElapsedMilliseconds} ms)",
                    unknown && driveAsks == 2 && sw.ElapsedMilliseconds < 2000);
                string dir = @"Z:\gio\2.8_live";
                string Why(int type)
                {
                    LocalAgent.DriveTypeOverride = _ => type;
                    return LocalAgent.DriveProblem("2.8", dir) ?? "(none)";
                }
                string Expected(string why) => L.T("core.deploy.dirDriveType", new { version = "2.8", dir, drive = "Z:", why });
                Check("localagent: the drives the agent refuses — network, CD, RAM disk, no such drive — each with its own reason",
                    Why(4) == Expected(L.T("core.deploy.drive.network")) && Why(5) == Expected(L.T("core.deploy.drive.cd"))
                    && Why(6) == Expected(L.T("core.deploy.drive.ram")) && Why(1) == Expected(L.T("core.deploy.drive.none")));
                LocalAgent.DriveTypeOverride = _ => 4;
                Check("localagent: unknown, removable and fixed drives are no problem, and neither is a folder that is not on a drive letter",
                    new[] { 0, 2, 3 }.All(t => Why(t) == "(none)") && LocalAgent.DriveProblem("2.8", "/srv/x") is null
                    && LocalAgent.DriveProblem("2.8", @"\\server\share\x") is null);
            }
            finally { LocalAgent.DriveTypeOverride = null; }
            Check("localagent: the config parser — the FIRST line of a key wins, a BOM and quotes are dropped",
                LocalAgent.ParseConfig("GIO_DIR_16=a\nGIO_DIR_16=b").DirOf("1.6") == "a"
                && LocalAgent.ParseConfig("\uFEFFGIO_DIR_28= \"x\" ").DirOf("2.8") == "x" && LocalAgent.ParseConfig(null).All.Count == 0);
            Check("localagent: a key is one name in any letter case, as in the environment of Windows the agent reads it into — gio_dir_16 is the 1.6 folder, the first line of either spelling wins",
                LocalAgent.ParseConfig("gio_dir_16=a\nGIO_DIR_16=b").DirOf("1.6") == "a" && LocalAgent.ParseConfig("Gio_Agent_Token=t").Token == "t"
                && LocalAgent.ParseConfig("GIO_DIR_16=a\ngio_dir_16=b").All.Count == 1);
            InstallOrder(tmp, Check);
        }
        finally
        {
            LocalAgent.DriveTypeOverride = null;
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* best effort */ }
        }
    }

    /// <summary>LocalAgent.Install's order under a temp root: every refusal of the plan before anything is written.</summary>
    static void InstallOrder(string tmp, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "agent-root"), ship = Path.Combine(tmp, "ship");
        Directory.CreateDirectory(Path.Combine(ship, "payloads", "1.6"));
        File.WriteAllText(Path.Combine(ship, "gio_agent.py"), "# the stand-in agent to copy\n");
        File.WriteAllText(Path.Combine(ship, "payloads", "1.6", "stack.json"),
            "{\"version\":\"1.6\",\"label\":\"Internet Archive\",\"url\":\"https://archive.org/download/item/1.6_pkg.zip\",\"size\":4100000000}");
        var python = new LocalAgent.PythonInfo("python", "pythonw", "3.12");
        try
        {
            LocalAgent.RootOverride = root;
            LocalAgent.DriveTypeOverride = _ => 3;
            Directory.CreateDirectory(root);
            bool NothingInstalled() => !File.Exists(LocalAgent.ScriptPath) && !Directory.Exists(LocalAgent.PayloadsDir);
            string Refusal(AgentInstallPlan plan, List<string>? lines = null)
            {
                try { LocalAgent.Install(plan, python, lines is null ? null : lines.Add, ship); return ""; }
                catch (InvalidOperationException ex) { return ex.Message; }
            }
            string Prefix(string key, string pathName, string path) =>
                L.T(key, new Dictionary<string, object?> { [pathName] = path, ["error"] = "\u0001" }).Split('\u0001')[0];
            string occupied = Path.Combine(tmp, "occupied", "1.6_live");
            Directory.CreateDirectory(occupied);
            File.WriteAllText(Path.Combine(occupied, "notes.txt"), "the admin's own");
            var dlPlan = P(Win); dlPlan.SetChoice("1.6", StackChoice.Download); dlPlan.SetDir("1.6", occupied);
            Check("install: a download into a folder that holds other files is refused by name before anything is written (no config, nothing installed)",
                Refusal(dlPlan) == L.T("core.deploy.downloadDirNotEmpty", new { version = "1.6", dir = occupied }) && NothingInstalled()
                && !File.Exists(LocalAgent.ConfigPath));
            string haveDir = Path.Combine(tmp, "have16");
            var havePlan = P(Win); havePlan.SetChoice("1.6", StackChoice.Have); havePlan.SetDir("1.6", haveDir);
            LocalAgent.DriveTypeOverride = _ => 4;
            string networkRefusal = Refusal(havePlan);
            LocalAgent.DriveTypeOverride = _ => 3;
            Check("install: a folder on a network drive is refused with the drive's reason (dirDriveType), nothing installed",
                networkRefusal == L.T("core.deploy.dirDriveType", new { version = "1.6", dir = haveDir, drive = char.ToUpperInvariant(haveDir[0]) + ":", why = L.T("core.deploy.drive.network") })
                && NothingInstalled());
            // A stored folder without a stack and a form folder without one: the row becomes Keep, so the stack
            // check passes it by — the install goes on to its files and stops at the held script.
            string storedDir = Path.Combine(tmp, "stored16"), formDir = Path.Combine(tmp, "form16");
            Directory.CreateDirectory(storedDir);
            File.WriteAllText(LocalAgent.ConfigPath, $"GIO_AGENT_LISTEN=127.0.0.1:18977\nGIO_DIR_16={storedDir}\n", new UTF8Encoding(false));
            var keepPlan = P(Win); keepPlan.SetChoice("1.6", StackChoice.Have); keepPlan.SetDir("1.6", formDir);
            var keepLines = new List<string>();
            string keepRefusal;
            File.WriteAllText(LocalAgent.ScriptPath, "# an installed script, held open\n");
            using (new FileStream(LocalAgent.ScriptPath, FileMode.Open, FileAccess.Read, FileShare.None)) keepRefusal = Refusal(keepPlan, keepLines);
            File.Delete(LocalAgent.ScriptPath);
            Check("install: a folder the configuration stores is kept — the form's I have it becomes Keep (no stack check of either folder), the log says so, and the install goes on to its files",
                keepRefusal.StartsWith(Prefix("core.deploy.scriptInUse", "path", LocalAgent.ScriptPath), StringComparison.Ordinal)
                && keepLines.Contains(L.T("core.deploy.dirKept", new { version = "1.6", dir = storedDir, given = formDir }))
                && keepPlan.ChoiceFor("1.6") == StackChoice.Keep && keepPlan.DirFor("1.6") == storedDir && keepPlan.FolderIsStored("1.6")
                && !Directory.Exists(LocalAgent.PayloadsDir));
            byte[] heldBytes = File.ReadAllBytes(LocalAgent.ConfigPath);
            string heldRefusal;
            var sw = Stopwatch.StartNew();
            using (new FileStream(LocalAgent.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None)) heldRefusal = Refusal(P(Win));
            Check($"install: a configuration that cannot be read refuses the install before anything is written ({sw.ElapsedMilliseconds} ms) — configUnreadable names it, its bytes unchanged",
                heldRefusal.StartsWith(Prefix("core.deploy.configUnreadable", "path", LocalAgent.ConfigPath), StringComparison.Ordinal)
                && heldRefusal.Length > 0 && File.ReadAllBytes(LocalAgent.ConfigPath).SequenceEqual(heldBytes) && NothingInstalled());
            File.WriteAllText(LocalAgent.ConfigPath, "GIO_DIR_16=\\\\server\\share\\1.6_live\n", new UTF8Encoding(false));
            var uncPlan = P(Win); uncPlan.SetChoice("1.6", StackChoice.Have); uncPlan.SetDir("1.6", formDir);
            string uncRefusal;
            File.WriteAllText(LocalAgent.ScriptPath, "# an installed script, held open\n");
            using (new FileStream(LocalAgent.ScriptPath, FileMode.Open, FileAccess.Read, FileShare.None)) uncRefusal = Refusal(uncPlan);
            File.Delete(LocalAgent.ScriptPath);
            Check(@"install: a stored UNC folder is not held to the drive-letter rule — no dirNotLocal; the next refusal is the held script",
                uncRefusal.StartsWith(Prefix("core.deploy.scriptInUse", "path", LocalAgent.ScriptPath), StringComparison.Ordinal)
                && uncPlan.DirFor("1.6") == @"\\server\share\1.6_live");

            // The install's own reading of the configuration — the Backend takes the running agent's token from it.
            var readable = LocalAgent.ReadConfigForInstall();
            string? readRefusal = null;
            using (new FileStream(LocalAgent.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { LocalAgent.ReadConfigForInstall(); }
                catch (InvalidOperationException ex) { readRefusal = ex.Message; }
            }
            File.Delete(LocalAgent.ConfigPath);
            Check("install: the install's reading of the configuration — the file as parsed; while it cannot be read, the install's own refusal (never a configuration read as empty); null without a file",
                readable is not null && readable.DirOf("1.6") == @"\\server\share\1.6_live"
                && readRefusal is not null && readRefusal.StartsWith(Prefix("core.deploy.configUnreadable", "path", LocalAgent.ConfigPath), StringComparison.Ordinal)
                && LocalAgent.ReadConfigForInstall() is null);

            // Installs that run to their end, with a stand-in agent whose self-test passes: what the running agent
            // does to its configuration while the files are copied (a relocation) decides the folders written.
            var py = LocalAgent.FindPython();
            if (py is null)
                Console.WriteLine("        (skipped: no Python 3.10 or later on this PC — the installs that run to their end are not run)");
            else
            {
                LocalAgent.PythonInfo interpreter = py;
                Directory.CreateDirectory(Path.Combine(ship, "payloads", "3.2"));
                File.WriteAllText(Path.Combine(ship, "payloads", "3.2", "stack.json"),
                    "{\"version\":\"3.2\",\"label\":\"Internet Archive\",\"url\":\"https://archive.org/download/item/3.2_pkg.zip\",\"size\":4100000000}");
                string copying = L.T("core.deploy.copyingAgent", new { dir = LocalAgent.Root });
                const string listen = "GIO_AGENT_LISTEN=127.0.0.1:18977\n";
                void WriteConfig(string text) => File.WriteAllText(LocalAgent.ConfigPath, text, new UTF8Encoding(false));
                string Written(string key) => Line(File.ReadAllText(LocalAgent.ConfigPath).Replace("\r\n", "\n"), key);
                AgentInstallPlan LaterPlan()
                {
                    var p = P(Win);
                    foreach (var v in ServerVersions.All) p.SetChoice(v, StackChoice.Later);
                    return p;
                }
                // One install over `config`; `meanwhile` runs as the agent's files start to be copied — what the
                // agent that runs does to its configuration while the install works. Whatever it throws is its answer.
                (IReadOnlyList<string>? Fetch, string Refusal) Complete(AgentInstallPlan plan, string config, Action meanwhile)
                {
                    WriteConfig(config);
                    try { return (LocalAgent.Install(plan, interpreter, line => { if (line == copying) meanwhile(); }, ship), ""); }
                    catch (Exception ex) { return (null, $"{ex.GetType().Name}: {ex.Message}"); }
                }
                string a16 = Path.Combine(tmp, "late", "a16"), b16 = Path.Combine(tmp, "late", "b16");
                string r32 = Path.Combine(tmp, "late", "r32"), f32 = Path.Combine(tmp, "late", "f32");

                var moved = LaterPlan();
                var movedRun = Complete(moved, listen + $"GIO_DIR_16={a16}\n",
                    () => WriteConfig(listen + $"GIO_DIR_16={b16}\nGIO_FETCH_DISK_RESERVE=4096\n"));
                Check("install: a folder the running agent relocated while the files were copied is the one written — the row is kept there, nothing to download — and a setting it saved meanwhile is kept",
                    movedRun.Refusal == "" && movedRun.Fetch is { Count: 0 } && Written("GIO_DIR_16") == "GIO_DIR_16=" + b16
                    && Written("GIO_FETCH_DISK_RESERVE") == "GIO_FETCH_DISK_RESERVE=4096"
                    && moved.DirFor("1.6") == b16 && moved.ChoiceFor("1.6") == StackChoice.Keep && moved.FolderIsStored("1.6"));
                var added = LaterPlan(); added.SetChoice("3.2", StackChoice.Download); added.SetDir("3.2", f32);
                var addedRun = Complete(added, listen, () => WriteConfig(listen + $"GIO_DIR_32={r32}\n"));
                Check("install: a folder the running agent gave a version meanwhile takes over that version's download — written, downloaded there, the row the stored one",
                    addedRun.Refusal == "" && addedRun.Fetch is { } addedFetch && addedFetch.SequenceEqual(new[] { "3.2" })
                    && Written("GIO_DIR_32") == "GIO_DIR_32=" + r32 && added.DirFor("3.2") == r32
                    && added.ChoiceFor("3.2") == StackChoice.Download && added.FolderIsStored("3.2"));
                var dropped = LaterPlan();
                var droppedRun = Complete(dropped, listen + $"GIO_DIR_16={a16}\n", () => WriteConfig(listen + "GIO_DIR_16=\n"));
                Check("install: a version the running agent took off meanwhile is written without a folder (GIO_DIR_16=) and set up later",
                    droppedRun.Refusal == "" && Written("GIO_DIR_16") == "GIO_DIR_16=" && dropped.ChoiceFor("1.6") == StackChoice.Later
                    && !dropped.FolderIsStored("1.6"));

                // A folder typed with a doubled separator and a "." segment: the agent answers it normalised
                // (os.path.normpath), so the install writes it — and the follow-up names it — that way.
                string normal32 = Path.Combine(tmp, "late", "n32");
                string typed32 = Path.Combine(tmp, "late") + "\\\\.\\n32";
                var doubled = LaterPlan(); doubled.SetChoice("3.2", StackChoice.Download); doubled.SetDir("3.2", typed32);
                var doubledRun = Complete(doubled, listen, () => { });
                var doubledChain = InstallFollowUp.Decide(doubled, doubledRun.Fetch ?? Array.Empty<string>(),
                    J("{\"versions\":{\"3.2\":{\"configured\":false,\"dir\":\"\",\"present\":false}}}"), true, false, "", true);
                Check("install: a folder the form names with a doubled separator or a \".\" segment is written in the agent's own spelling, and the follow-up re-points the version there — the folder the agent then answers is the step's",
                    doubledRun.Refusal == "" && Written("GIO_DIR_32") == "GIO_DIR_32=" + normal32 && doubled.DirFor("3.2") == normal32
                    && doubled.ChoiceFor("3.2") == StackChoice.Download && !doubled.FolderIsStored("3.2")
                    && doubledChain.Steps.SequenceEqual(new[] { new FetchStep("3.2", normal32, true, true) })
                    && AgentInstallPlan.SameFolder(doubledChain.Steps[0].Dir, normal32, true));

                // The agent relocates 1.6, and then something holds the file so that it cannot be read while it
                // could still be written: merged over the first reading, the install would point 1.6 back at the
                // folder it left.
                File.Delete(LocalAgent.ScriptPath);
                FileStream? hold = null;
                string relocated = listen + $"GIO_DIR_16={b16}\n";
                (IReadOnlyList<string>? Fetch, string Refusal) heldRun;
                try
                {
                    heldRun = Complete(LaterPlan(), listen + $"GIO_DIR_16={a16}\n", () =>
                    {
                        WriteConfig(relocated);
                        hold = new FileStream(LocalAgent.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Write);
                    });
                }
                finally { hold?.Dispose(); }
                Check("install: a configuration that cannot be read again right before it is written is not written at all — its own refusal, the new files in place, the folder the agent relocated meanwhile left as it is",
                    heldRun.Refusal.StartsWith("InvalidOperationException: " + Prefix("core.deploy.configUnreadableLate", "path", LocalAgent.ConfigPath), StringComparison.Ordinal)
                    && File.ReadAllText(LocalAgent.ConfigPath) == relocated && File.Exists(LocalAgent.ScriptPath));
            }
        }
        finally
        {
            LocalAgent.RootOverride = null;
            LocalAgent.DriveTypeOverride = null;
        }
        Check("install: RootOverride and DriveTypeOverride are cleared again", LocalAgent.RootOverride is null && LocalAgent.DriveTypeOverride is null);
    }

    // ── F. the install's follow-up ──
    static void FollowUp(Action<string, bool> Check)
    {
        const string dir32 = @"C:\relic_servers\3.2_live";
        AgentInstallPlan Plan32()
        {
            var p = P(Win); p.SetChoice("3.2", StackChoice.Download); p.SetDir("3.2", dir32);
            return p;
        }
        string[] dl = { "3.2" };
        FetchChain D(string? status, bool sameProcess, bool pageRunsChain = true, bool tokenChanged = false, string error = "", AgentInstallPlan? plan = null,
            IReadOnlyList<string>? downloads = null) =>
            InstallFollowUp.Decide(plan ?? Plan32(), downloads ?? dl, status is null ? null : J(status), sameProcess, tokenChanged, error, pageRunsChain);
        bool Held(FetchChain c, string message) => c.Steps.Count == 0 && c.Held.Count == 1 && c.Held[0] == new FetchHeld("3.2", message);

        var repoint = D("""{"versions":{"3.2":{"configured":false,"dir":"","present":false}}}""", sameProcess: true);
        Check("follow-up: a download of a version the agent that kept running has no folder for is a repoint, then the download — one step, one note",
            repoint.Steps.SequenceEqual(new[] { new FetchStep("3.2", dir32, true, true) }) && repoint.Held.Count == 0
            && repoint.Notes.SequenceEqual(new[] { L.T("core.deploy.chainRepoint", new { version = "3.2", dir = dir32 }) }));
        Check("follow-up: ...and for a page that runs no chains it is held: the agent must be restarted first",
            Held(D("""{"versions":{"3.2":{"configured":false,"dir":"","present":false}}}""", sameProcess: true, pageRunsChain: false),
                L.T("core.deploy.chainNeedsRestart", new { version = "3.2", dir = dir32 })));
        var same = D("""{"versions":{"3.2":{"configured":true,"dir":"c:\\relic_servers\\3.2_live","present":false}}}""", sameProcess: true);
        Check("follow-up: a version the agent has in the same folder (spelled its own way) is downloaded there — the agent's spelling, no note",
            same.Steps.SequenceEqual(new[] { new FetchStep("3.2", @"c:\relic_servers\3.2_live", false, true) }) && same.Held.Count == 0 && same.Notes.Count == 0);
        Check("follow-up: a version the agent that kept running has in ANOTHER folder waits for a restart — never a repoint, never the agent's refusal of that folder",
            Held(D("""{"versions":{"3.2":{"configured":true,"dir":"D:\\other\\3.2_live","present":false,"fetch":{"problem":"x"}}}}""", sameProcess: true),
                L.T("core.deploy.chainRestartFirst", new { version = "3.2", running = @"D:\other\3.2_live", dir = dir32 })));
        var linux = P(Linux); linux.SetChoice("2.8", StackChoice.Download); linux.SetDir("2.8", "/home/2.8_live");
        var kept = D("""{"versions":{"2.8":{"configured":true,"dir":"/root/2.8_live","present":false}}}""", sameProcess: false, plan: linux, downloads: new[] { "2.8" });
        Check("follow-up: after a fresh start in another folder (the installer kept the box's) the download goes there, with a note",
            kept.Steps.SequenceEqual(new[] { new FetchStep("2.8", "/root/2.8_live", false, true) }) && kept.Held.Count == 0
            && kept.Notes.SequenceEqual(new[] { L.T("core.deploy.chainKeptDir", new { version = "2.8", dir = "/root/2.8_live" }) }));
        Check("follow-up: an agent that does not know the version, no status with an error, no status without one — each held with its own reason",
            Held(D("""{"versions":{"1.6":{}}}""", sameProcess: false), L.T("core.deploy.chainUnknownVersion", new { version = "3.2" }))
            && Held(D(null, sameProcess: false, error: "boom"), L.T("core.deploy.chainNoStatus", new { version = "3.2", error = "boom" }))
            && Held(D(null, sameProcess: false), L.T("core.deploy.chainNoHealth", new { version = "3.2" })));
        var present = D("""{"versions":{"3.2":{"configured":true,"dir":"C:\\relic_servers\\3.2_live","present":true}}}""", sameProcess: false);
        Check("follow-up: a version already on the server is a note, nothing to run",
            present.Steps.Count == 0 && present.Held.Count == 0
            && present.Notes.SequenceEqual(new[] { L.T("core.deploy.chainPresent", new { version = "3.2", dir = dir32 }) }));
        const string disk = "Not enough free disk space: 9.9 GB needed, 1.0 GB free on C:\\.";
        Check("follow-up: a download the agent would refuse now is held with the agent's own sentence",
            Held(D("""{"versions":{"3.2":{"configured":true,"dir":"C:\\relic_servers\\3.2_live","present":false,"fetch":{"problem":"Not enough free disk space: 9.9 GB needed, 1.0 GB free on C:\\."}}}}""", sameProcess: false),
                L.T("core.deploy.chainRefused", new { version = "3.2", problem = disk })));
        Check("follow-up: a process that kept running with another token is asked nothing — held whatever the status says",
            Held(D("""{"versions":{"3.2":{"configured":true,"dir":"C:\\relic_servers\\3.2_live","present":true}}}""", sameProcess: true, tokenChanged: true),
                L.T("core.deploy.chainRestartToken", new { version = "3.2" }))
            && Held(D(null, sameProcess: true, tokenChanged: true), L.T("core.deploy.chainRestartToken", new { version = "3.2" })));
        var order = P(Win);
        foreach (var v in ServerVersions.All) { order.SetChoice(v, StackChoice.Download); order.SetDir(v, $@"C:\s\{v}_live"); }
        var ordered = D("""{"versions":{"1.6":{"configured":true,"dir":"C:\\s\\1.6_live"},"2.8":{"configured":false,"dir":""},"3.2":{"dir":"C:\\s\\3.2_live"}}}""",
            sameProcess: false, plan: order, downloads: new[] { "3.2", "1.6", "2.8" });
        Check("follow-up: after a fresh start a version the agent has no folder for is held (add it on the Server page); the steps keep the plan's version order (a missing configured flag reads from the folder)",
            ordered.Steps.Select(s => s.Version).SequenceEqual(new[] { "1.6", "3.2" }) && ordered.Steps.All(s => !s.Repoint && s.Fetch)
            && ordered.Held.SequenceEqual(new[] { new FetchHeld("2.8", L.T("core.deploy.chainNotConfigured", new { version = "2.8" })) }));
        var running = D("""{"busy":{"kind":"fetch","version":"3.2"},"versions":{"3.2":{"configured":true,"dir":"C:\\relic_servers\\3.2_live","present":false}}}""", sameProcess: false);
        Check("follow-up: a download of that version already running on the agent is a note, not a second job",
            running.Steps.Count == 0 && running.Held.Count == 0
            && running.Notes.SequenceEqual(new[] { L.T("core.deploy.chainAlreadyRunning", new { version = "3.2" }) }));
        // /status names the job the agent started last, and a download in its long phase lets the agent's own
        // jobs start beside it: only the jobs list tells that the download still runs.
        const string revive = """{"busy":{"kind":"revive","version":"1.6"},"versions":{"3.2":{"configured":true,"dir":"C:\\relic_servers\\3.2_live","present":false}}}""";
        var hidden = InstallFollowUp.Decide(Plan32(), dl, J(revive), false, false, "", true, new[] { "3.2" });
        var unseen = D(revive, sameProcess: false);
        Check("follow-up: a download the jobs list names as running while /status names another job is a note, not a second job — without that list it is handed on",
            hidden.Steps.Count == 0 && hidden.Held.Count == 0
            && hidden.Notes.SequenceEqual(new[] { L.T("core.deploy.chainAlreadyRunning", new { version = "3.2" }) })
            && unseen.Steps.SequenceEqual(new[] { new FetchStep("3.2", dir32, false, true) }));
        Check("follow-up: the jobs list is asked only while /status names a job other than a download as busy",
            InstallFollowUp.BusyMayHideFetch(J(revive)) && !InstallFollowUp.BusyMayHideFetch(J("""{"busy":null}"""))
            && !InstallFollowUp.BusyMayHideFetch(J("""{"busy":{"kind":"fetch","version":"3.2"}}""")) && !InstallFollowUp.BusyMayHideFetch(null)
            && !InstallFollowUp.BusyMayHideFetch(J("""{"busy":"revive"}""")) && !InstallFollowUp.BusyMayHideFetch(J("[1]")));
        const string jobs = """{"jobs":[{"kind":"fetch","version":"3.2","state":"running"},{"kind":"fetch","version":"1.6","state":"done"},"""
            + """{"kind":"revive","version":"2.8","state":"running"},{"kind":"fetch","version":"3.2","state":"running"},{"kind":"fetch","version":null,"state":"running"}]}""";
        Check("follow-up: the running downloads of the jobs list — kind fetch, state running, each version once; any other shape reads as none",
            InstallFollowUp.RunningFetches(J(jobs)).SequenceEqual(new[] { "3.2" })
            && InstallFollowUp.RunningFetches(null).Count == 0 && InstallFollowUp.RunningFetches(J("""{"jobs":{}}""")).Count == 0
            && InstallFollowUp.RunningFetches(J("[]")).Count == 0);
        // Repoint-only: a folder the form named that the process which kept running does not know.
        var only = P(Win);
        only.SetChoice("1.6", StackChoice.Have); only.SetDir("1.6", @"C:\s\1.6_live");
        only.SetChoice("2.8", StackChoice.Have); only.SetDir("2.8", @"C:\form\2.8_live");
        only.KeepStoredFolders(v => v == "2.8" ? @"C:\stored\2.8_live" : "");
        only.SetChoice("3.2", StackChoice.Download); only.SetDir("3.2", dir32);
        const string unknownAll = """{"versions":{"1.6":{"configured":false,"dir":""},"2.8":{"configured":false,"dir":""},"3.2":{"configured":false,"dir":""}}}""";
        var repointOnly = D(unknownAll, sameProcess: true, plan: only, downloads: Array.Empty<string>());
        Check("follow-up: a package already in its folder (I have it, or a download whose folder holds a stack) of a version the running agent does not know is a repoint alone — a stored folder is not",
            repointOnly.Steps.SequenceEqual(new[] { new FetchStep("1.6", @"C:\s\1.6_live", true, false), new FetchStep("3.2", dir32, true, false) })
            && repointOnly.Held.Count == 0
            && repointOnly.Notes.SequenceEqual(new[]
            {
                L.T("core.deploy.chainRepointOnly", new { version = "1.6", dir = @"C:\s\1.6_live" }),
                L.T("core.deploy.chainRepointOnly", new { version = "3.2", dir = dir32 }),
            }));
        Check("follow-up: ...never after a fresh start, never for a page without chains, and the status is asked only when there is something to decide",
            D(unknownAll, sameProcess: false, plan: only, downloads: Array.Empty<string>()) is { Steps.Count: 0, Held.Count: 0, Notes.Count: 0 }
            && D(unknownAll, sameProcess: true, pageRunsChain: false, plan: only, downloads: Array.Empty<string>()) is { Steps.Count: 0, Held.Count: 0 }
            && InstallFollowUp.NeedsStatus(only, Array.Empty<string>(), sameProcess: true, tokenChanged: false)
            && !InstallFollowUp.NeedsStatus(only, Array.Empty<string>(), sameProcess: false, tokenChanged: false)
            && !InstallFollowUp.NeedsStatus(Plan32(), dl, sameProcess: true, tokenChanged: true)
            && InstallFollowUp.NeedsStatus(Plan32(), dl, sameProcess: false, tokenChanged: false));
        bool threw = false;
        var odd = new List<FetchChain>();
        foreach (string shape in new[] { """{"versions":[]}""", """{"versions":null}""", """{"versions":{"3.2":{"fetch":"x","configured":"yes","dir":5}}}""", "[1,2]", "\"x\"" })
        {
            try { odd.Add(D(shape, sameProcess: true)); odd.Add(D(shape, sameProcess: false)); }
            catch (Exception) { threw = true; }
        }
        Check("follow-up: never throws on a shape it does not expect — versions a list or null, members of the wrong type, a status that is no object",
            !threw && odd.Count == 10 && odd.Take(4).All(c => c.Steps.Count == 0 && c.Held.Count == 1)
            && odd[4].Steps.SequenceEqual(new[] { new FetchStep("3.2", dir32, true, true) })
            && odd[5].Held.SequenceEqual(new[] { new FetchHeld("3.2", L.T("core.deploy.chainNotConfigured", new { version = "3.2" })) })
            && odd.Skip(6).All(c => c.Steps.Count == 0 && c.Held.SequenceEqual(new[] { new FetchHeld("3.2", L.T("core.deploy.chainUnknownVersion", new { version = "3.2" })) })));
    }

    // ── G. the package sizes the form quotes ──
    static void Packages(string repo, Action<string, bool> Check)
    {
        string root = Path.Combine(Path.GetTempPath(), "relic-stackchoice-cat-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            void Catalogue(string version, string json)
            {
                Directory.CreateDirectory(Path.Combine(root, version));
                File.WriteAllText(Path.Combine(root, version, "stack.json"), json, new UTF8Encoding(false));
            }
            Catalogue("7.0", """{"url":"https://archive.org/download/i/7.0.7z","size":1000,"extractedSize":600,"alt":[{"url":"https://archive.org/download/i/7.0.zip","size":4100000000}]}""");
            Catalogue("7.1", """{"url":"https://archive.org/download/i/7.1.ZIP","size":2000,"extractedSize":0}""");
            Catalogue("7.2", """{"url":"https://archive.org/download/i/7.2.7z","size":3000}""");
            Catalogue("7.3", """{"url":"https://archive.org/download/i/7.3.7z","size":3000,"extractedSize":-1}""");
            Catalogue("7.4", """{"url":"https://archive.org/download/i/7.4.7z","size":3000,"extractedSize":"600"}""");
            Catalogue("7.5", """{"url":"https://archive.org/download/i/7.5.7z","size":3000,"alt":[{"url":"https://archive.org/download/i/7.5.zip","size":"9"},{"url":"https://archive.org/download/i/7.5b.zip","size":7}]}""");
            Catalogue("7.6", """{"url":"https://archive.org/download/i/7.6.7z","size":3000,"extractedSize":6.5}""");
            StackPackage Pkg(string v) => ServerVersions.Package(v, root);
            Check("package: the unpacked size and the .zip's size come from the catalogue — extractedSize, and the .zip under alt",
                Pkg("7.0") is { Published: true, Size: 1000, ExtractedSize: 600, ZipSize: 4100000000, NeedsExtractor: false });
            Check("package: a .zip under url is its own .zip size; no alt names no .zip size",
                Pkg("7.1") is { Published: true, ZipSize: 2000, ExtractedSize: 0 } && Pkg("7.2") is { Published: true, ZipSize: 0 });
            Check("package: an extractedSize that is missing, negative, a string or a fraction reads 0 — never a refused catalogue",
                Pkg("7.2").ExtractedSize == 0 && Pkg("7.3") is { Published: true, ExtractedSize: 0 } && Pkg("7.4") is { Published: true, ExtractedSize: 0 }
                && Pkg("7.6") is { Published: true, ExtractedSize: 0 });
            Check("package: the .zip size is the first alt .zip with a positive integer size",
                Pkg("7.5") is { Published: true, ZipSize: 7, NeedsExtractor: false });
            Check("package: no package carries no sizes", StackPackage.None is { ExtractedSize: 0, ZipSize: 0 } && Pkg("6.6") == StackPackage.None);
        }
        finally { try { Directory.Delete(root, true); } catch { /* best effort */ } }
        string shipped = Path.Combine(repo, "agent", "payloads");
        long Number(JsonElement e, string name) => e.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt64() : -1;
        try
        {
            using var doc32 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(shipped, "3.2", "stack.json")));
            var cat32 = doc32.RootElement;
            var p32 = ServerVersions.Package("3.2", shipped);
            long alt0 = Number(cat32.GetProperty("alt")[0], "size");
            Check($"package: the shipped 3.2 catalogue — a .zip of {alt0} bytes beside the .7z, {Number(cat32, "extractedSize")} bytes unpacked",
                p32.Published && p32.ZipSize == alt0 && alt0 > 0 && p32.ExtractedSize == Number(cat32, "extractedSize") && p32.ExtractedSize > 0);
            var p16 = ServerVersions.Package("1.6", shipped);
            var p28 = ServerVersions.Package("2.8", shipped);
            Check("package: the shipped 1.6 and 2.8 catalogues name no .zip, and each its unpacked size",
                p16 is { Published: true, ZipSize: 0 } && p28 is { Published: true, ZipSize: 0 } && p16.ExtractedSize > p16.Size && p28.ExtractedSize > p28.Size);
        }
        catch (Exception ex) { Check($"package: the shipped catalogues can be read ({ex.Message})", false); }
    }

    // ── H. the root probe ──
    static void Probe(Action<string, bool> Check)
    {
        var sb = new StringBuilder();
        sb.AppendLine("P0 yes stack"); sb.AppendLine("P1 no absent"); sb.AppendLine("S GIO_DIR_16=/a"); sb.AppendLine("S GIO_DIR_16 = \"/b\" ");
        sb.AppendLine("PY yes"); sb.AppendLine("DC no"); sb.AppendLine("noise");
        string crlf = "P0 yes stack\r\nP1 no absent\r\nS GIO_DIR_16=/a\r\nS GIO_DIR_16 = \"/b\" \r\nPY yes\r\nDC no\r\nnoise\r\n";
        string[] paths = { "/a", "/b" };
        bool Reads(ProbeFacts f) => f.Exists("/a") && f.KindOf("/a") == FolderKind.Stack && !f.Exists("/b") && f.KindOf("/b") == FolderKind.Absent
            && f.StoredDir("1.6") == "/b" && f.HasPython && !f.HasDocker
            && !f.Exists("/c") && f.KindOf("/c") == FolderKind.Absent && f.StoredDir("2.8") == "";
        Check("probe: its output is read whatever the line end (AppendLine's, CRLF) — exists and kind per path, the LAST stored line of a key with its quotes stripped, python3 and docker; a missing line reads as nothing",
            Reads(AgentDeployer.ParseProbe(sb.ToString(), paths)) && Reads(AgentDeployer.ParseProbe(crlf, paths)) && Reads(AgentDeployer.ParseProbe(crlf.Replace("\r\n", "\n"), paths)));
        var odd = AgentDeployer.ParseProbe("P7 yes stack\nP0 maybe stack\nS GIO_TOKEN=/x\nS GIO_DIR_X=/y\nS =z\nPY\n", paths);
        Check("probe: an index past the paths, an unknown word, a key that is no GIO_DIR_<digits> are ignored — and null output reads as nothing",
            !odd.Exists("/a") && odd.StoredDirs.Count == 0 && !odd.HasPython && AgentDeployer.ParseProbe(null, paths) is { HasDocker: false, ExistsMap.Count: 0 });
        string script = AgentDeployer.ProbeScript(new[] { "/x y", "/q'z" }, false);
        Check("probe: the script quotes every path, reads nothing from stdin, prints one P line per path and asks nothing of the configuration without the stored part",
            script.StartsWith("exec </dev/null\n", StringComparison.Ordinal) && script.Contains("'/x y'") && script.Contains(@"'/q'\''z'")
            && script.Contains(@"echo ""P1 $e $(k '/q'\''z')""") && !script.Contains("grep") && script.Contains("k() {") && script.EndsWith("exit 0\n", StringComparison.Ordinal));
        string storedScript = AgentDeployer.ProbeScript(Array.Empty<string>(), true);
        Check("probe: the stored part prints only the GIO_DIR_<nn> lines of the configuration (no secret leaves the box), and whether python3 and docker compose are there",
            storedScript.Contains("grep -E '^[[:space:]]*GIO_DIR_[0-9]+[[:space:]]*=' /etc/gio-agent/config | sed 's/^/S /'")
            && storedScript.Contains("echo 'PY yes'") && storedScript.Contains("echo 'DC no'") && !storedScript.Contains("k() {"));
        ProbeRun(Check);
    }

    /// <summary>The probe script run for real by Git Bash over temp folders, when this PC has it.</summary>
    static void ProbeRun(Action<string, bool> Check)
    {
        string? bash = new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") }
            .Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.Combine(p!, "Git", "bin", "bash.exe")).FirstOrDefault(File.Exists);
        if (bash is null)
        {
            Console.WriteLine("        (skipped: no Git Bash on this PC — the probe script is not run for real)");
            return;
        }
        string bashExe = bash;
        string tmp = Path.Combine(Path.GetTempPath(), "relic-stackchoice-probe-" + Guid.NewGuid().ToString("n")[..8]);
        static string Posix(string p) => "/" + char.ToLowerInvariant(p[0]) + p[2..].Replace('\\', '/');
        (int Code, string Output) RunBash(string text)
        {
            string file = Path.Combine(tmp, "probe-" + Guid.NewGuid().ToString("n")[..6] + ".sh");
            File.WriteAllText(file, text, new UTF8Encoding(false));
            var psi = new ProcessStartInfo(bashExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(Posix(file));
            using var proc = Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEndAsync();
            string stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return (-1, stdout); }
            stderr.Wait(5000);
            return (proc.ExitCode, stdout);
        }
        try
        {
            Directory.CreateDirectory(tmp);
            string Folder(string name) { string d = Path.Combine(tmp, name); Directory.CreateDirectory(d); return d; }
            string empty = Folder("empty"), stack = Folder("stack"), yml = Folder("yml"), other = Folder("other with blank");
            File.WriteAllText(Path.Combine(stack, "docker-compose.yml.tmpl"), "x");
            File.WriteAllText(Path.Combine(yml, "docker-compose.yml"), "x");
            File.WriteAllText(Path.Combine(other, "notes.txt"), "x");
            string file = Path.Combine(tmp, "it's a file");
            File.WriteAllText(file, "x");
            string[] paths = { Posix(Path.Combine(tmp, "absent")), Posix(empty), Posix(stack), Posix(yml), Posix(other), Posix(file) };
            var (code, output) = RunBash(AgentDeployer.ProbeScript(paths, false));
            var f = AgentDeployer.ParseProbe(output, paths);
            Check("probe (Git Bash): the script runs and tells absent, empty, a stack (.tmpl or .yml), a folder with other files and a file apart — a blank and a quote in the names included",
                code == 0 && !f.Exists(paths[0]) && f.KindOf(paths[0]) == FolderKind.Absent && f.KindOf(paths[1]) == FolderKind.Empty
                && f.KindOf(paths[2]) == FolderKind.Stack && f.KindOf(paths[3]) == FolderKind.Stack && f.KindOf(paths[4]) == FolderKind.Other
                && f.KindOf(paths[5]) == FolderKind.Other && paths.Skip(1).All(f.Exists));
            string config = Path.Combine(tmp, "config");
            File.WriteAllText(config, "GIO_AGENT_TOKEN=do-not-print-me\nGIO_DIR_16=/first\n  GIO_DIR_16 = '/last' \r\nGIO_DIR_28=\n# GIO_DIR_32=/commented\nGIO_DIR_32X=/no\n", new UTF8Encoding(false));
            string grepLine = AgentDeployer.ProbeScript(Array.Empty<string>(), true).Split('\n').Single(l => l.Contains("grep -E", StringComparison.Ordinal));
            var (code2, output2) = RunBash("exec </dev/null\n" + grepLine.Replace("/etc/gio-agent/config", Posix(config), StringComparison.Ordinal) + "\nexit 0\n");
            var g = AgentDeployer.ParseProbe(output2, Array.Empty<string>());
            Check("probe (Git Bash): the stored lines of a configuration — the LAST line of a key, quotes, blanks and a CR dropped, a key stored empty, a comment ignored — and no other line of the file printed",
                code2 == 0 && g.StoredDir("1.6") == "/last" && g.StoredDirs.ContainsKey("GIO_DIR_28") && g.StoredDir("2.8") == "" && g.StoredDir("3.2") == ""
                && g.StoredDirs.Count == 2 && !output2.Contains("do-not-print-me", StringComparison.Ordinal));
        }
        catch (Exception ex) { Check($"probe (Git Bash): the script ran ({ex.Message})", false); }
        finally { try { Directory.Delete(tmp, true); } catch { /* best effort */ } }
    }

    // ── the installer's refusal of a relocation that ended while it ran ──
    static void InstallerRefusal(string repo, Action<string, bool> Check)
    {
        string shPath = Path.Combine(repo, "agent", "install_agent.sh");
        string sh = File.Exists(shPath) ? File.ReadAllText(shPath).Replace("\r\n", "\n") : "";
        int fn = sh.IndexOf("\nrefuse_if_dirs_changed() {\n", StringComparison.Ordinal);
        int fnEnd = fn < 0 ? -1 : sh.IndexOf("\n}\n", fn, StringComparison.Ordinal);
        string body = fn >= 0 && fnEnd > fn ? sh[fn..fnEnd] : "";
        Check("installer refusal: install_agent.sh prints the lines the launcher reads it by, and exits 1 — before anything is replaced, and once more with the new files in place",
            body.Contains("echo \"" + AgentDeployer.DirsChangedHead + "$CONFIG" + AgentDeployer.DirsChangedTail + "\"", StringComparison.Ordinal)
            && body.Contains("echo \"" + AgentDeployer.DirsChangedEnd + " $1 ", StringComparison.Ordinal) && body.Contains("\n  exit 1", StringComparison.Ordinal)
            && sh.Contains("\nrefuse_if_dirs_changed \"Nothing of the agent was replaced.\"\n", StringComparison.Ordinal)
            && sh.Contains("\nrefuse_if_dirs_changed \"" + AgentDeployer.DirsChangedFilesIn + "\"\n", StringComparison.Ordinal));
        string head = AgentDeployer.DirsChangedHead + "/etc/gio-agent/config" + AgentDeployer.DirsChangedTail;
        string before = "  agent   : running, no job in progress\r\n" + head + "\r\n  GIO_DIR_28=/root/2.8_live  ->  GIO_DIR_28=/srv/2.8_live\r\n  GIO_DIR_32 unset  ->  GIO_DIR_32=/srv/3.2_live\r\n"
            + AgentDeployer.DirsChangedEnd + " Nothing of the agent was replaced. Run this again: a folder the box uses is kept.\r\n";
        string after = "  previous : agent(1.2) config kept in /var/lib/gio-agent/previous\n" + head + "\n  the file cannot be read any more\n"
            + AgentDeployer.DirsChangedEnd + " " + AgentDeployer.DirsChangedFilesIn + " Run this again: a folder the box uses is kept.\n";
        Check("installer refusal: read as its own refusal — the changed folders named, nothing replaced; or the new files in place, the configuration unwritten",
            AgentDeployer.DirsChangedRefusal(1, before) == L.T("core.deploy.dirsChanged",
                new { changes = "GIO_DIR_28=/root/2.8_live  ->  GIO_DIR_28=/srv/2.8_live; GIO_DIR_32 unset  ->  GIO_DIR_32=/srv/3.2_live" })
            && AgentDeployer.DirsChangedRefusal(1, after) == L.T("core.deploy.dirsChangedFilesIn", new { changes = "the file cannot be read any more" }));
        Check("installer refusal: any other exit, or exit 1 without that line, is not that refusal",
            AgentDeployer.DirsChangedRefusal(0, before) is null && AgentDeployer.DirsChangedRefusal(2, before) is null
            && AgentDeployer.DirsChangedRefusal(1, "The running agent has a job in progress (fetch).") is null && AgentDeployer.DirsChangedRefusal(1, null) is null);
        UpdateStopped(Check);
    }

    /// <summary>The catch clauses of an update over SSH, with the installer run replaced by a stand-in that streams
    /// lines and then fails the way AgentDeployer's run does: (the message posted, replaced, the clause that caught it).</summary>
    static (string Message, bool Replaced, string Clause) SimulateUpdate(IReadOnlyList<string> installer, int code)
    {
        bool replacing = false;
        var lines = new List<string>();
        void log(string s)
        {
            if (s.TrimStart().StartsWith("previous :", StringComparison.Ordinal)) replacing = true;
            lines.Add(s);
        }
        try
        {
            log("Asking the agent at 192.0.2.10:18080 whether it is in the middle of a job...");
            foreach (var l in installer) log(l.TrimEnd());
            string output = string.Join("\n", installer) + "\n";
            if (AgentDeployer.DirsChangedRefusal(code, output) is { } changed) throw new InvalidOperationException(changed);
            if (code != 0) throw new InvalidOperationException("install_agent.sh failed (exit " + code + "):\n" + output);
            return ("done", false, "done");
        }
        catch (InvalidOperationException ex) when (AgentDeployer.UpdateStoppedText(ex.Message, lines) is { } stopped)
        {
            return (stopped, false, "stopped");
        }
        catch (Exception ex)
        {
            return (ex.Message, replacing, "generic");
        }
    }

    // ── an update over SSH the installer stopped with the new files in place ──
    static void UpdateStopped(Action<string, bool> Check)
    {
        const string key = "backend.agent.updateDirsChangedFilesIn";
        string Stopped(string changes) => L.T(key, new { changes });
        Check("update stopped: the dedicated text is in en.json and takes the changes the installer named",
            L.Has(key) && L.T(key, new { changes = "\u0001" }).Contains('\u0001'));

        string head = AgentDeployer.DirsChangedHead + "/etc/gio-agent/config" + AgentDeployer.DirsChangedTail;
        string endIn = AgentDeployer.DirsChangedEnd + " " + AgentDeployer.DirsChangedFilesIn + " Run this again: a folder the box uses is kept.";
        string endBefore = AgentDeployer.DirsChangedEnd + " Nothing of the agent was replaced. Run this again: a folder the box uses is kept.";
        var filesIn = new List<string>
        {
            "Running the installer on the server...",
            "  agent   : running, no job in progress",
            "  previous : agent(1.2) config state unit payloads kept in /var/lib/gio-agent/previous",
            "  uninstall: bash /opt/gio-agent/uninstall_agent.sh",
            "  payloads: 31 files",
            head,
            "  GIO_DIR_28=/root/2.8_live  ->  GIO_DIR_28=/srv/2.8_live",
            "  GIO_DIR_32 unset  ->  GIO_DIR_32=/srv/3.2_live",
            endIn,
        };
        const string twoChanges = "GIO_DIR_28=/root/2.8_live  ->  GIO_DIR_28=/srv/2.8_live; GIO_DIR_32 unset  ->  GIO_DIR_32=/srv/3.2_live";
        string msgIn = AgentDeployer.DirsChangedRefusal(1, string.Join("\n", filesIn))!;

        // 1. The refusal with the new files in place: the dedicated text, with the changes as the installer named them.
        Check("update stopped: files in — the dedicated text with both changes",
            AgentDeployer.UpdateStoppedText(msgIn, filesIn) == Stopped(twoChanges));
        var s1 = SimulateUpdate(filesIn, 1);
        Check("update stopped: files in — the specific clause posts it with replaced false although \"previous :\" was seen",
            s1.Clause == "stopped" && s1.Message == Stopped(twoChanges) && !s1.Replaced);

        // 2. The refusal before anything was replaced: not this case — the generic clause, replaced false.
        var before = new List<string> { "  agent   : running, no job in progress", head, "  GIO_DIR_16=/home/1.6_live  ->  GIO_DIR_16=", endBefore };
        string msgBefore = AgentDeployer.DirsChangedRefusal(1, string.Join("\n", before))!;
        Check("update stopped: nothing replaced — no dedicated text", AgentDeployer.UpdateStoppedText(msgBefore, before) is null);
        var s2 = SimulateUpdate(before, 1);
        Check("update stopped: nothing replaced — the generic clause, the core text, replaced false",
            s2.Clause == "generic" && s2.Message == msgBefore && !s2.Replaced);

        // 3. A configuration that cannot be read any more: its line is the change named.
        var unreadable = new List<string> { "  previous : agent(1.2) config kept in /var/lib/gio-agent/previous", head, "  the file cannot be read any more", endIn };
        string msgUnreadable = AgentDeployer.DirsChangedRefusal(1, string.Join("\n", unreadable))!;
        Check("update stopped: files in, unreadable configuration — that line is the change",
            AgentDeployer.UpdateStoppedText(msgUnreadable, unreadable) == Stopped("the file cannot be read any more"));

        // 4. CR line ends (the lines as a box may print them) read the same.
        var crlf = filesIn.Select(l => l + "\r").ToList();
        Check("update stopped: files in, CR line ends — the same text",
            AgentDeployer.UpdateStoppedText(AgentDeployer.DirsChangedRefusal(1, string.Join("\n", crlf))!, crlf) == Stopped(twoChanges));

        // 5. Another failure while the refusal lines are there (the message is not that refusal): null.
        Check("update stopped: another message over the refusal's lines — null",
            AgentDeployer.UpdateStoppedText("install_agent.sh failed (exit 1):\n...", filesIn) is null);

        // 6. A failure after "previous :" that is not the refusal: the generic clause keeps replaced true.
        var crashed = new List<string> { "  previous : agent(1.2) config kept in /var/lib/gio-agent/previous", "  payloads: 31 files", "systemctl: unit failed" };
        var s6 = SimulateUpdate(crashed, 3);
        Check("update stopped: another failure after \"previous :\" — the generic clause, replaced true",
            s6.Clause == "generic" && s6.Replaced && s6.Message.StartsWith("install_agent.sh failed (exit 3)", StringComparison.Ordinal));
        Check("update stopped: another failure — no dedicated text", AgentDeployer.UpdateStoppedText(s6.Message, crashed) is null);

        // 7. Nothing streamed at all: null, never an exception.
        Check("update stopped: no lines — null", AgentDeployer.UpdateStoppedText("x", new List<string>()) is null);
        Check("update stopped: an empty message and no lines — null",
            AgentDeployer.UpdateStoppedText("", new List<string>()) is null && AgentDeployer.UpdateStoppedText(null, null) is null);

        // 8. A streamed element that holds a line break (a launcher text) before the refusal: the lines are read the
        //    way DirsChangedRefusal reads the output, so the head is still found.
        var multi = new List<string> { "first line\nsecond line", "  previous : agent(1.2) config kept in /var/lib/gio-agent/previous" };
        multi.AddRange(filesIn.Skip(5));
        Check("update stopped: a multi-line element before the refusal — the same text",
            AgentDeployer.UpdateStoppedText(AgentDeployer.DirsChangedRefusal(1, string.Join("\n", multi))!, multi) == Stopped(twoChanges));

        // 9. A head line without its end line: DirsChangedRefusal reads it as the "nothing replaced" variant — no dedicated text.
        var cut = new List<string> { "  previous : x kept in y", head, "  GIO_DIR_28=/a  ->  GIO_DIR_28=/b" };
        string? msgCut = AgentDeployer.DirsChangedRefusal(1, string.Join("\n", cut));
        Check("update stopped: a head without its end line — no dedicated text", msgCut is not null && AgentDeployer.UpdateStoppedText(msgCut, cut) is null);

        // 10. A run that ended well never reaches the filter.
        var s10 = SimulateUpdate(new List<string> { "  payloads: 31 files" }, 0);
        Check("update stopped: a finished run — done", s10.Clause == "done");

        // 11. No changes listed between head and end: "?".
        var none = new List<string> { "  previous : x kept in y", head, endIn };
        Check("update stopped: no change lines — ?", AgentDeployer.UpdateStoppedText(AgentDeployer.DirsChangedRefusal(1, string.Join("\n", none))!, none) == Stopped("?"));
    }

    /// <summary>A method's body in <paramref name="cs"/>: from its signature to the brace that closes it at member
    /// indentation ("}", or "});" for a member whose body is a lambda); "" when the signature is not there.</summary>
    static string Body(string cs, string signature)
    {
        int at = cs.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";
        var end = new Regex(@"\n    \}\)?;?\n").Match(cs, at);
        return end.Success ? cs[at..end.Index] : "";
    }

    // ── an SSH install looks at the box as root ──
    static void DeployerText(string repo, Action<string, bool> Check)
    {
        string path = Path.Combine(repo, "app", "Relic.Core", "Server", "AgentDeployer.cs");
        string cs = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        string install = Body(cs, "public static DeployResult Install(SshTarget ssh, AgentInstallPlan plan, string agentDir, Action<string>? log = null, HostKeyCheck? hostKeys = null)");
        int[] order = new[]
        {
            "ProbeAsRoot(client, ssh, Array.Empty<string>(), stored: true)", "plan.KeepStoredFolders(box.StoredDir)", "plan.ProbePaths()",
            "plan.MissingStackEntries(facts.Exists)", "plan.DownloadTargetProblems(facts.KindOf)", "plan.FetchVersionsReally(facts.Exists)",
            "UploadAgent(session, agentDir)",
        }.Select(s => install.IndexOf(s, StringComparison.Ordinal)).ToArray();
        Check("deployer: an SSH install asks the box as root for its stored folders first, keeps them in the plan, then checks the plan's folders in one root probe — all before anything is uploaded",
            order.All(i => i >= 0) && order.Zip(order.Skip(1)).All(p => p.First < p.Second));
        string probe = Body(cs, "private static ProbeFacts ProbeAsRoot(SshClient c, SshTarget ssh, IReadOnlyList<string> paths, bool stored)");
        Check("deployer: no folder of the box is looked at as the SSH user — no test -e anywhere, no command -v or docker compose version in the install outside the root probe's script, which RootCommand runs from where the login lands",
            install.Length > 0 && !cs.Contains("test -e", StringComparison.Ordinal) && !install.Contains("command -v", StringComparison.Ordinal)
            && !install.Contains("docker compose version", StringComparison.Ordinal)
            && probe.Contains("RootCommand(c, ssh, null, ProbeScript(paths, stored))", StringComparison.Ordinal));
        Check("deployer: the probe's lines are all read (none left out for holding the sudo password — a folder name may), and the end of a failure is blanked of it",
            probe.Contains("RunStreaming(c, cmdText, stdin, Array.Empty<string>(), null, ProbeTimeout,", StringComparison.Ordinal)
            && probe.Contains("tail = Redact(Tail(output, 400), secrets)", StringComparison.Ordinal)
            && probe.Contains("string[] secrets = ssh.EffectiveSudoPassword is { Length: >= 8 } pw ? new[] { pw } : Array.Empty<string>();", StringComparison.Ordinal));
    }

    // ── I. Backend.Modes.cs ──
    static void BackendText(string repo, Action<string, bool> Check)
    {
        string path = Path.Combine(repo, "app", "Relic.App", "Backend.Modes.cs");
        string cs = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        string BodyOf(string signature) => Body(cs, signature);
        foreach (string signature in new[] { "private object StartLocalAgentInstall(JsonElement p)", "private object StartRemoteAgentInstall(JsonElement p)" })
        {
            string body = BodyOf(signature);
            int freed = body.IndexOf("Volatile.Write(ref _serverJobBusy, 0)", StringComparison.Ordinal);
            int done = body.IndexOf("PostEvent(\"deploy.done\"", StringComparison.Ordinal);
            int error = body.IndexOf("PostEvent(\"deploy.error\"", StringComparison.Ordinal);
            int chain = body.IndexOf("Bool(p, \"chain\")", StringComparison.Ordinal);
            int job = body.IndexOf("Task.Run(", StringComparison.Ordinal);
            Check($"backend: {signature[15..signature.IndexOf('(')]} posts deploy.done / deploy.error only after the busy flag is free, and reads the page's chain flag before the job starts",
                body.Length > 0 && freed > 0 && done > freed && error > freed && chain > 0 && job > chain);
        }
        Check("backend: ReadPlan reads the choice of each version (stack<nn>), LocalAgentStatusAsync answers kinds and extractor, the follow-up decides inside a try, and the folder check is there",
            BodyOf("private static AgentInstallPlan ReadPlan(JsonElement p)").Contains("Str(p, ServerVersions.FormStack(v))", StringComparison.Ordinal)
            && BodyOf("private Task<object> LocalAgentStatusAsync()") is var status && status.Contains("kinds = ", StringComparison.Ordinal)
            && status.Contains("extractor = ", StringComparison.Ordinal)
            && Regex.IsMatch(BodyOf("private static async Task<FetchChain> InstallFollowUpAsync("), @"try\s*\{\s*chain = InstallFollowUp\.Decide\(")
            && cs.Contains("private static Task<object> LocalFolderCheckAsync(JsonElement p)", StringComparison.Ordinal));
        // What deploy.done carries, under the names the page reads: fetch = the plain downloads alone (a page that runs
        // no chains queues them, so a step that needs a repoint first is never among them), chain = every step, held.
        int payloadAt = cs.IndexOf("ChainPayload(FetchChain chain) => (", StringComparison.Ordinal);
        int payloadEnd = payloadAt < 0 ? -1 : cs.IndexOf(";\n", payloadAt, StringComparison.Ordinal);
        string payload = payloadAt >= 0 && payloadEnd > payloadAt ? cs[payloadAt..payloadEnd] : "";
        Check("backend: deploy.done carries fetch (the downloads that need no repoint), chain {version, dir, repoint, fetch} and held {version, message} on both install paths",
            new[] { "private object StartLocalAgentInstall(JsonElement p)", "private object StartRemoteAgentInstall(JsonElement p)" }
                .All(s => BodyOf(s).Contains("fetch, chain = steps, held = holds", StringComparison.Ordinal))
            && payload.Contains("chain.Steps.Where(s => !s.Repoint && s.Fetch).Select(s => s.Version)", StringComparison.Ordinal)
            && payload.Contains("new { version = s.Version, dir = s.Dir, repoint = s.Repoint, fetch = s.Fetch }", StringComparison.Ordinal)
            && payload.Contains("new { version = h.Version, message = h.Message }", StringComparison.Ordinal));
        string followUp = BodyOf("private static async Task<FetchChain> InstallFollowUpAsync(");
        int asking = followUp.IndexOf("log(L.T(\"core.deploy.chainAsking\"", StringComparison.Ordinal);
        int statusAt = followUp.IndexOf("StatusAsync(cts.Token)", StringComparison.Ordinal);
        Check("backend: the follow-up says in the install log that it asks the agent, and waits for /status only so long; the jobs list is asked while another job is busy",
            asking >= 0 && statusAt > asking && followUp.Contains("new CancellationTokenSource(FollowUpStatusTimeout)", StringComparison.Ordinal)
            && followUp.Contains("InstallFollowUp.BusyMayHideFetch(", StringComparison.Ordinal) && followUp.Contains("JobsAsync(cts.Token)", StringComparison.Ordinal)
            && followUp.Contains("pageRunsChain, fetching)", StringComparison.Ordinal));
        string local = BodyOf("private object StartLocalAgentInstall(JsonElement p)");
        Check("backend: the token the running agent answers to is read the way the install reads the configuration — never as \"none\" for a file held a moment",
            local.Contains("string beforeToken = LocalAgent.ReadConfigForInstall()?.Token ?? \"\";", StringComparison.Ordinal)
            && !local.Contains("LocalAgent.ReadConfig()?.Token", StringComparison.Ordinal));
    }

    static string? FindRepo()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Relic.slnx"))) return dir.FullName;
        }
        return null;
    }
}
