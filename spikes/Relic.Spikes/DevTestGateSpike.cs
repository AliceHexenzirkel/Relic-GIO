using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The release gates of the DevTest build: the dev-only in-game test bed of the enhancements DLL, which never
/// ships. Such a build carries a marker text, and the release paths refuse a file that holds it as ASCII or as
/// UTF-16LE text anywhere: Test-DevBuildMarker in build/pe_imports.ps1 (the scan of the whole publish output in
/// build/publish.ps1, build_ee.ps1 -CopyToPayload, the DLL workflow) and its Python twin in
/// entertainment_experience/tools/payload_manifest.py (--set-sha, --verify). Held here:
///  - no file under payload/ carries the marker, hidden ones included;
///  - both scanners, run as the release paths run them (Windows PowerShell, PowerShell 7 when it is installed,
///    python), find it in synthetic files - at the start, at the end, at an odd offset, across read-chunk
///    boundaries of 4 KiB to 4 MiB - and nothing in clean files or in near misses;
///  - publish.ps1's own scan refuses a marked file, a hidden one and a DevTest* name in a synthetic output; with
///    -RemoveRefused, as the script calls it on its output, it removes the output it refused (the installer is
///    compiled from it in a step of its own) and keeps a clean one, without the switch it removes nothing; and it
///    runs once, on the output, after its last copy into it;
///  - the DLL workflow fails on the marker in its Release build, and no workflow builds the DevTest configuration;
///  - no gate script carries the marker whole.
/// Everything it writes is synthetic, in a temp folder; nothing of the repository is changed.
/// </summary>
static class DevTestGateSpike
{
    public const string Name = "devtestgate";

    /// <summary>Joined at run time: two literals joined by + are folded into one literal of this assembly.</summary>
    static readonly string Marker = string.Concat("RELIC-DEV", "TEST-BUILD");

    /// <summary>The read-chunk sizes a streamed search is likely to use; a marker is placed across each boundary.</summary>
    static readonly int[] Boundaries = { 4096, 65536, 81920, 1 << 20, 4 << 20 };

    sealed record Synthetic(string Src, byte[] Bytes, bool Marked);

    public static int Run()
    {
        Console.WriteLine($"== {Name} spike: no DevTest build in payload/, the marker scans of the release paths ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string? repo = FindRepo();
        if (repo is null)
        {
            Check("the repository is found from here (Relic.slnx beside payload/manifest.json)", false);
            return Summary(results);
        }
        byte[][] forms = { Encoding.ASCII.GetBytes(Marker), Encoding.Unicode.GetBytes(Marker) };

        // The shipped payload: every file under payload/, hidden and system ones too, read whole.
        string payloadDir = Path.Combine(repo, "payload");
        var payloadFiles = Directory.EnumerateFiles(payloadDir, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None, IgnoreInaccessible = false,
        }).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var payloadHits = payloadFiles.Where(f => Carries(f, forms)).Select(f => Path.GetRelativePath(repo, f)).ToList();
        long payloadBytes = payloadFiles.Sum(f => new FileInfo(f).Length);
        foreach (string hit in payloadHits) Console.WriteLine($"         carries the marker: {hit}");
        Check($"payload/: no file carries the marker of a DevTest build ({payloadFiles.Count} files, {payloadBytes / 1048576.0:F1} MB read)",
            payloadFiles.Count > 0 && payloadHits.Count == 0);

        // The gate scripts name the marker only in two parts, so none of them is itself a hit of a scan.
        string[] gateScripts =
        {
            "build/pe_imports.ps1", "build/publish.ps1", "build/build_dlls.ps1", "entertainment_experience/tools/build_ee.ps1",
            "entertainment_experience/tools/payload_manifest.py", ".github/workflows/dlls.yml", "spikes/Relic.Spikes/DevTestGateSpike.cs",
        };
        var whole = gateScripts.Select(s => Path.Combine(repo, s.Replace('/', Path.DirectorySeparatorChar)))
            .Where(f => File.Exists(f) && Carries(f, forms)).Select(f => Path.GetRelativePath(repo, f)).ToList();
        foreach (string w in whole) Console.WriteLine($"         carries the marker whole: {w}");
        Check("the gate scripts carry the marker only in parts (pe_imports.ps1, publish.ps1, build_dlls.ps1, build_ee.ps1, payload_manifest.py, dlls.yml, this spike)",
            whole.Count == 0);

        // The DLL workflow: the ee job reads its Release CLibrary.dll for the marker; no workflow builds DevTest.
        string workflows = Path.Combine(repo, ".github", "workflows");
        string dllsYml = File.ReadAllText(Path.Combine(workflows, "dlls.yml")).Replace("\r\n", "\n");
        int eeJob = dllsYml.IndexOf("\n  ee:\n", StringComparison.Ordinal), injectorJob = dllsYml.IndexOf("\n  injector:\n", StringComparison.Ordinal);
        int releaseDll = dllsYml.IndexOf("$dll = Get-Item \"entertainment_experience/mod/bin/v$env:GAME_VERSION/Release-x64/CLibrary.dll\"", StringComparison.Ordinal);
        var markerThrow = Regex.Match(dllsYml, @"^ +if \(Test-DevBuildMarker -Path \$dll\.FullName\) \{ throw ", RegexOptions.Multiline);
        Check("dlls.yml: the ee job fails when its Release CLibrary.dll carries the marker",
            eeJob >= 0 && injectorJob > eeJob && releaseDll > eeJob && markerThrow.Success && markerThrow.Index > releaseDll && markerThrow.Index < injectorJob);
        // Comment lines (YAML's and the scripts') may name the configuration; a build line is never one.
        var devBuilds = Directory.EnumerateFiles(workflows, "*.yml")
            .Where(f => File.ReadAllLines(f).Any(l => !l.TrimStart().StartsWith('#')
                && Regex.IsMatch(l, @"-Config\s+['""]?DevTest|-StageDev", RegexOptions.IgnoreCase)))
            .Select(Path.GetFileName).ToList();
        Check($"no workflow builds the DevTest configuration ({(devBuilds.Count > 0 ? string.Join(", ", devBuilds) : "none does")})", devBuilds.Count == 0);

        string tmp = Path.Combine(Path.GetTempPath(), "relic-devtest-gate-spike");
        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            var synthetic = MakeSynthetic();
            string synthDir = Path.Combine(tmp, "payload");
            foreach (var s in synthetic) Put(Path.Combine(synthDir, s.Src), s.Bytes);
            int marked = synthetic.Count(s => s.Marked), clean = synthetic.Count - marked;
            Check($"the spike's own reading agrees with how each synthetic file was made ({marked} marked, {clean} clean)",
                synthetic.All(s => Carries(Path.Combine(synthDir, s.Src), forms) == s.Marked));

            PowerShellChecks(repo, synthDir, synthetic, payloadFiles, Path.Combine(tmp, "out"), Check);
            PythonChecks(repo, synthDir, synthetic, Check);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) { ClearAttributes(tmp); Directory.Delete(tmp, true); } } catch { /* best effort */ }
        }
        return Summary(results);
    }

    static int Summary(List<(string name, bool ok)> results)
    {
        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"{Name} spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Clean files (empty, noise, near misses: one letter short, one letter off, lower case, the two parts a
    /// script joins, a UTF-16LE text one letter short) and marked ones, each with ONE marker: ASCII and UTF-16LE,
    /// at the start, at the end, at an odd offset and across every boundary of <see cref="Boundaries"/>.
    /// </summary>
    static List<Synthetic> MakeSynthetic()
    {
        var rnd = new Random(7919);
        byte[] Noise(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
        byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
        byte[] ascii = Encoding.ASCII.GetBytes(Marker), utf16 = Encoding.Unicode.GetBytes(Marker);
        string shortOne = Marker[..^1];
        byte[] near = Cat(Noise(777), Encoding.ASCII.GetBytes(
                "|" + shortOne + "|" + Marker[1..] + "|" + Marker[..^1] + "T|" + Marker.ToLowerInvariant() + "|"
                + Marker.Replace('-', '_') + "|" + "'RELIC-DEV' + 'TEST-BUILD'|" + "\"RELIC-DEV\", \"TEST-BUILD\"|"),
            Encoding.Unicode.GetBytes(shortOne + "|"), Noise(4096));

        var list = new List<Synthetic>
        {
            new("x/empty.bin", Array.Empty<byte>(), false),
            new("x/noise.bin", Noise(3 * 1048576 + 7), false),
            new("x/near-misses.bin", near, false),
            new("x/ascii-start.bin", Cat(ascii, Noise(1000)), true),
            new("x/ascii-end.bin", Cat(Noise(5000), ascii), true),
            new("x/ascii-odd.bin", Cat(Noise(4097), ascii, Noise(3)), true),
            new("x/utf16-start.bin", Cat(utf16, Noise(1000)), true),
            new("x/utf16-end.bin", Cat(Noise(5001), utf16), true),
            new("x/utf16-odd.bin", Cat(Noise(4097), utf16, Noise(3)), true),
        };
        foreach (int b in Boundaries)
        {
            list.Add(new($"x/ascii-across-{b}.bin", Cat(Noise(b - ascii.Length / 2), ascii, Noise(4096)), true));
            list.Add(new($"x/utf16-across-{b}.bin", Cat(Noise(b - utf16.Length / 2), utf16, Noise(4096)), true));
        }
        return list;
    }

    /// <summary>
    /// Stand-ins for publish\win-x64: one clean output and five that each hold one thing a ship build must not
    /// (key = the case, value = the folder and the relative path the refusal must name; null for the clean one).
    /// </summary>
    static Dictionary<string, (string dir, string? names)> MakePublishOutputs(string root, List<Synthetic> synthetic)
    {
        byte[] Of(string src) => synthetic.First(s => s.Src == src).Bytes;
        byte[] cleanDll = Of("x/near-misses.bin"), small = Of("x/ascii-odd.bin")[..1000];
        var cases = new Dictionary<string, (string dir, string? names)>();
        string Output(string name)
        {
            string dir = Path.Combine(root, name);
            Put(Path.Combine(dir, "Relic.exe"), cleanDll);
            Put(Path.Combine(dir, "ui", "lang", "en.json"), Encoding.UTF8.GetBytes("{\"app.title\": \"Relic\"}\n"));
            Put(Path.Combine(dir, "payload", "2.8", "ee", "CLibrary.dll.bin"), cleanDll);
            Put(Path.Combine(dir, "payload", "1.6", "ee", "CLibrary.dll.bin"), small);
            Put(Path.Combine(dir, "agent", "payloads", "2.8", "manifest.json"), Encoding.UTF8.GetBytes("{}\n"));
            Put(Path.Combine(dir, "agent", "payloads", "1.6", "test-dev.txt"), small);
            return dir;
        }
        cases["clean"] = (Output("clean"), null);
        string ascii = Output("ascii-dll");
        Put(Path.Combine(ascii, "payload", "2.8", "ee", "CLibrary.dll.bin"), Of($"x/ascii-across-{1 << 20}.bin"));
        cases["ascii-dll"] = (ascii, @"payload\2.8\ee\CLibrary.dll.bin (carries the marker");
        string utf16 = Output("utf16-deep");
        Put(Path.Combine(utf16, "agent", "payloads", "2.8", "deep", "data.bin"), Of("x/utf16-odd.bin"));
        cases["utf16-deep"] = (utf16, @"agent\payloads\2.8\deep\data.bin (carries the marker");
        string hidden = Output("hidden");
        string hiddenFile = Path.Combine(hidden, "payload", "1.6", "ee", "cache.bin");
        Put(hiddenFile, Of("x/ascii-end.bin"));
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        cases["hidden"] = (hidden, @"payload\1.6\ee\cache.bin (carries the marker");
        string named = Output("named");
        Put(Path.Combine(named, "DevTestRunner.dll"), small);
        cases["named"] = (named, "DevTestRunner.dll (named DevTest*)");
        string folder = Output("folder");
        Put(Path.Combine(folder, "payload", "2.8", "ee", "devtest", "status.txt"), Encoding.UTF8.GetBytes("{}\n"));
        cases["folder"] = (folder, @"payload\2.8\ee\devtest (named DevTest*)");
        return cases;
    }

    /// <summary>
    /// Test-DevBuildMarker, dot-sourced from build/pe_imports.ps1 the way publish.ps1 does, in every PowerShell
    /// of this PC: one bool per file, true exactly for the marked files, false for the shipped payload; then
    /// publish.ps1's own Test-NoDevBuild, taken from the script's syntax tree and run on stand-in outputs made for
    /// each PowerShell (with -RemoveRefused a refusal removes its output), and where the script calls it.
    /// </summary>
    static void PowerShellChecks(string repo, string synthDir, List<Synthetic> synthetic, List<string> payloadFiles,
        string outRoot, Action<string, bool> Check)
    {
        var editions = new List<(string label, string exe)>();
        string winPs = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPs)) editions.Add(("Windows PowerShell", winPs));
        if (OnPath("pwsh.exe") is string pwsh) editions.Add(("PowerShell 7", pwsh));
        Check($"a PowerShell to run the gate in ({(editions.Count > 0 ? string.Join(", ", editions.Select(e => e.label)) : "none")})", editions.Count > 0);

        var files = synthetic.Select(s => Path.Combine(synthDir, s.Src.Replace('/', Path.DirectorySeparatorChar))).ToList();
        bool orderChecked = false;
        int edition = 0;
        foreach (var (label, exe) in editions)
        {
            var outputs = MakePublishOutputs(Path.Combine(outRoot, (++edition).ToString()), synthetic);
            string script = PsScript
                .Replace("@@PE_IMPORTS@@", Q(Path.Combine(repo, "build", "pe_imports.ps1")))
                .Replace("@@PUBLISH@@", Q(Path.Combine(repo, "build", "publish.ps1")))
                .Replace("@@FILES@@", string.Join(", ", files.Select(Q)))
                .Replace("@@REAL@@", string.Join(", ", payloadFiles.Select(Q)))
                .Replace("@@CASES@@", string.Join(", ", outputs.Select(o => Q(o.Key + "|" + o.Value.dir))));
            var run = RunProcess(exe, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }, TimeSpan.FromMinutes(5), dropPsModulePath: true);
            var lines = run.Out.Split('\n').Select(l => l.TrimEnd('\r').TrimStart((char)0xFEFF))
                .Where(l => l.StartsWith("@@", StringComparison.Ordinal)).ToList();
            if (run.TimedOut || lines.Count == 0)
                Console.WriteLine($"         {label}: exit {run.Code}{(run.TimedOut ? " (timed out)" : "")}; {Tail(run.Err + run.Out)}");
            Check($"Test-DevBuildMarker ({label}): build/pe_imports.ps1 defines it",
                !run.TimedOut && lines.Contains("@@function|yes"));

            // One answer per file: "@@file|<index>|<count>|<type>|<value>".
            var answers = Answers(lines, "@@file|");
            var wrongShape = Enumerable.Range(0, files.Count).Where(i => !answers.TryGetValue(i, out var a) || a.count != "1" || a.type != "System.Boolean").ToList();
            var missed = Enumerable.Range(0, files.Count).Where(i => synthetic[i].Marked && !(answers.TryGetValue(i, out var a) && a.value == "True")).ToList();
            var falseHits = Enumerable.Range(0, files.Count).Where(i => !synthetic[i].Marked && !(answers.TryGetValue(i, out var a) && a.value == "False")).ToList();
            foreach (int i in wrongShape) Console.WriteLine($"         {synthetic[i].Src}: {(answers.TryGetValue(i, out var a) ? $"{a.count} value(s), {a.type} {a.value}" : "no answer")}");
            Check($"Test-DevBuildMarker ({label}): answers one bool per file, nothing else", wrongShape.Count == 0);
            Check($"Test-DevBuildMarker ({label}): finds the marker in every marked file ({synthetic.Count(s => s.Marked)}: ASCII and UTF-16LE, start, end, odd offset, across {string.Join(" / ", Boundaries.Select(Size))} boundaries){Names(missed, synthetic)}",
                missed.Count == 0);
            Check($"Test-DevBuildMarker ({label}): finds nothing in the clean files ({synthetic.Count(s => !s.Marked)}: empty, noise, near misses){Names(falseHits, synthetic)}",
                falseHits.Count == 0);

            var real = Answers(lines, "@@real|");
            var realHits = Enumerable.Range(0, payloadFiles.Count).Where(i => !(real.TryGetValue(i, out var a) && a.count == "1" && a.value == "False")).ToList();
            foreach (int i in realHits) Console.WriteLine($"         {Path.GetRelativePath(repo, payloadFiles[i])}: {(real.TryGetValue(i, out var a) ? a.value : "no answer")}");
            string ms = lines.FirstOrDefault(l => l.StartsWith("@@realms|", StringComparison.Ordinal))?.Split('|')[1] ?? "?";
            Check($"Test-DevBuildMarker ({label}): the shipped payload passes ({payloadFiles.Count} files in {ms} ms)", payloadFiles.Count > 0 && realHits.Count == 0);

            // publish.ps1's Test-NoDevBuild: "@@publish|<case>|passed" or "@@publish|<case>|refused|<message>".
            Check($"publish.ps1 ({label}): Test-NoDevBuild is found in the script's syntax tree", lines.Contains("@@fn|1"));
            foreach (var (name, (_, names)) in outputs)
            {
                string? line = lines.FirstOrDefault(l => l.StartsWith($"@@publish|{name}|", StringComparison.Ordinal));
                string[] parts = line?.Split('|', 4) ?? Array.Empty<string>();
                // A refusal names the one thing planted in that output and nothing of the clean files around it.
                bool ok = names is null
                    ? parts.Length >= 3 && parts[2] == "passed"
                    : parts.Length == 4 && parts[2] == "refused" && parts[3].Contains(names, StringComparison.Ordinal)
                      && Regex.Matches(parts[3], @" \((carries the marker of a DevTest build|named DevTest\*)\)").Count == 1;
                if (!ok) Console.WriteLine($"         {name}: {line ?? "no answer"}");
                string what = names is null ? "lets a clean output through" : $"refuses the output with {names.Split(" (")[0]} and names it";
                Check($"publish.ps1 ({label}): Test-NoDevBuild {what} ({name})", ok);
            }
            // Without -RemoveRefused nothing is removed; with it a refused output is gone (no installer can be compiled
            // from it later), said in the refusal, and a clean one is left as it was.
            var removedAnyway = outputs.Keys.Where(k => !lines.Contains($"@@kept|{k}|True")).ToList();
            foreach (string k in removedAnyway) Console.WriteLine($"         {k}: gone after a check without -RemoveRefused");
            Check($"publish.ps1 ({label}): Test-NoDevBuild without -RemoveRefused removes nothing", removedAnyway.Count == 0);
            var misplaced = outputs.Where(o => (o.Value.names is null) != Directory.Exists(o.Value.dir)
                || (o.Value.names is not null && !lines.Any(l => l.StartsWith($"@@removed|{o.Key}|refused|", StringComparison.Ordinal)
                    && l.Contains(" was removed: no installer can be compiled from it.", StringComparison.Ordinal))))
                .Select(o => o.Key).ToList();
            foreach (string k in misplaced) Console.WriteLine($"         {k}: {(Directory.Exists(outputs[k].dir) ? "still there" : "gone")}");
            Check($"publish.ps1 ({label}): Test-NoDevBuild -RemoveRefused removes every output it refuses ({outputs.Count(o => o.Value.names is not null)}), says so, and keeps the clean one",
                misplaced.Count == 0);

            // Where the script calls it: "@@order|<Copy-Item offsets>|<Test-ClientPayload offsets>|<gate offsets>|<gate arguments>".
            if (orderChecked) continue;
            orderChecked = true;
            string[] order = lines.FirstOrDefault(l => l.StartsWith("@@order|", StringComparison.Ordinal))?.Split('|') ?? Array.Empty<string>();
            int[] Offsets(int at) => order.Length > at ? order[at].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray() : Array.Empty<int>();
            int[] copies = Offsets(1), client = Offsets(2), gate = Offsets(3);
            string gateArg = order.Length > 4 ? order[4] : "";
            Console.WriteLine($"         publish.ps1: Copy-Item at {string.Join(", ", copies)}; Test-ClientPayload at {string.Join(", ", client)}; Test-NoDevBuild at {string.Join(", ", gate)} ({gateArg})");
            Check("publish.ps1: Test-NoDevBuild runs once, on $outDir with -RemoveRefused, after the client payload check and after every Copy-Item into the output",
                lines.Contains("@@parse|0") && gate.Length == 1 && gateArg == "$outDir -RemoveRefused" && copies.Length > 0 && client.Length > 0
                && gate[0] > copies.Max() && gate[0] > client.Max());
        }
    }

    /// <summary>
    /// The Python twin through payload_manifest.py, as build_ee.ps1 and the developer run it: --verify reports
    /// DEV BUILD for exactly the marked entries and exits 1, a payload of clean files passes, --set-sha refuses a
    /// marked file and leaves the manifest as it was, and still writes the hash of a clean one.
    /// </summary>
    static void PythonChecks(string repo, string synthDir, List<Synthetic> synthetic, Action<string, bool> Check)
    {
        string tool = Path.Combine(repo, "entertainment_experience", "tools", "payload_manifest.py");
        string manifest = Path.Combine(synthDir, "manifest.json");
        void WriteManifest(IEnumerable<Synthetic> entries) => File.WriteAllText(manifest,
            "{\n  \"common\": [\n" + string.Join(",\n", entries.Select(s =>
                $"    {{ \"src\": \"{s.Src}\", \"dst\": \"{s.Src[..^4]}.dll\", \"sha256\": \"{Convert.ToHexString(SHA256.HashData(s.Bytes)).ToLowerInvariant()}\" }}"))
            + "\n  ],\n  \"versions\": {}\n}\n", new UTF8Encoding(false));
        (int Code, string Out, string Err, bool TimedOut)? Py(params string[] args)
        {
            try
            {
                return RunProcess("python", new[] { "-B", tool, "--payload", synthDir }.Concat(args), TimeSpan.FromMinutes(2), dropPsModulePath: false,
                    env: new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8" });
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        WriteManifest(synthetic);
        var all = Py("--verify");
        if (all is null)
        {
            Console.WriteLine("  [SKIP] payload_manifest.py: no python on PATH");
            return;
        }
        string devLines = string.Join("\n", all.Value.Out.Split('\n').Where(l => l.StartsWith("DEV BUILD", StringComparison.Ordinal)));
        bool Reported(Synthetic s) => Regex.IsMatch(all.Value.Out, $@"^DEV BUILD +{Regex.Escape(s.Src)} ", RegexOptions.Multiline);
        var missed = synthetic.Where(s => s.Marked && !Reported(s)).Select(s => s.Src).ToList();
        var falseHits = synthetic.Where(s => !s.Marked && Reported(s)).Select(s => s.Src).ToList();
        bool hashesOk = !Regex.IsMatch(all.Value.Out, "^(MISMATCH|MISSING|BAD ENTRY|UNKNOWN KIND)", RegexOptions.Multiline)
            && synthetic.All(s => Regex.IsMatch(all.Value.Out, $@"^ok +{Regex.Escape(s.Src)} ", RegexOptions.Multiline));
        if (missed.Count + falseHits.Count > 0 || !hashesOk) Console.WriteLine($"         {Tail(all.Value.Out + all.Value.Err)}");
        Check($"payload_manifest.py --verify: DEV BUILD for every marked entry ({synthetic.Count(s => s.Marked)}){(missed.Count > 0 ? " - missed " + string.Join(", ", missed) : "")}",
            missed.Count == 0 && devLines.Split('\n').Length == synthetic.Count(s => s.Marked));
        Check($"payload_manifest.py --verify: none for a clean entry ({synthetic.Count(s => !s.Marked)}){(falseHits.Count > 0 ? " - reported " + string.Join(", ", falseHits) : "")}",
            falseHits.Count == 0);
        Check("payload_manifest.py --verify: exits 1 on a dev build whose hashes all match", all.Value.Code == 1 && hashesOk);

        WriteManifest(synthetic.Where(s => !s.Marked));
        var cleanRun = Py("--verify");
        Check("payload_manifest.py --verify: a payload of clean files passes (exit 0)", cleanRun is { Code: 0 } && !cleanRun.Value.Out.Contains("DEV BUILD"));

        WriteManifest(synthetic);
        byte[] before = File.ReadAllBytes(manifest);
        var markedSrc = synthetic.First(s => s.Marked && s.Src.Contains("across")).Src;
        string otherSha = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("another build"))).ToLowerInvariant();
        var refused = Py("--set-sha", markedSrc, otherSha);
        Check($"payload_manifest.py --set-sha: refuses a marked payload file ({markedSrc}), exit 1, the manifest byte for byte as it was",
            refused is { Code: 1 } && refused.Value.Out.Contains("DevTest build") && File.ReadAllBytes(manifest).AsSpan().SequenceEqual(before));
        var written = Py("--set-sha", "x/noise.bin", otherSha);
        Check("payload_manifest.py --set-sha: still writes the hash of a clean payload file (exit 0)",
            written is { Code: 0 } && File.ReadAllText(manifest).Contains($"\"src\": \"x/noise.bin\", \"dst\": \"x/noise.dll\", \"sha256\": \"{otherSha}\""));
    }

    // Dot-sources build/pe_imports.ps1 as publish.ps1 does, answers per file, then runs publish.ps1's own
    // Test-NoDevBuild out of the script's syntax tree; every machine-read line starts with "@@".
    const string PsScript = """
        $ErrorActionPreference = 'Stop'
        . @@PE_IMPORTS@@
        $fn = Get-Command Test-DevBuildMarker -CommandType Function -ErrorAction SilentlyContinue
        "@@function|$(if ($fn) { 'yes' } else { 'no' })"
        function Answer([string]$tag, [int]$i, [string]$path) {
          try {
            $r = @(Test-DevBuildMarker -Path $path)
            $t = if ($r.Count -gt 0 -and $null -ne $r[0]) { $r[0].GetType().FullName } else { '' }
            "@@$tag|$i|$($r.Count)|$t|$($r -join ',')"
          } catch { "@@$tag|$i|error||$($_.Exception.Message -replace '[\r\n|]+', ' ')" }
        }
        $i = 0; foreach ($p in @(@@FILES@@)) { Answer 'file' $i $p; $i++ }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $i = 0; foreach ($p in @(@@REAL@@)) { Answer 'real' $i $p; $i++ }
        "@@realms|$($watch.ElapsedMilliseconds)"
        $text = [IO.File]::ReadAllText(@@PUBLISH@@, [Text.UTF8Encoding]::new($false))
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
        "@@parse|$(@($errors).Count)"
        $defs = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Test-NoDevBuild' }, $true))
        "@@fn|$($defs.Count)"
        if ($defs.Count -eq 1) {
          . ([scriptblock]::Create($defs[0].Extent.Text))
          foreach ($c in @(@@CASES@@)) {
            $name, $dir = $c -split '\|', 2
            try { Test-NoDevBuild $dir *> $null; "@@publish|$name|passed" }
            catch { "@@publish|$name|refused|$($_.Exception.Message -replace '[\r\n|]+', ' ')" }
            "@@kept|$name|$(Test-Path -LiteralPath $dir)"
            try { Test-NoDevBuild $dir -RemoveRefused *> $null; "@@removed|$name|passed" }
            catch { "@@removed|$name|refused|$($_.Exception.Message -replace '[\r\n|]+', ' ')" }
          }
        }
        $cmds = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] }, $true))
        function Offsets([string]$name) { (@($cmds | Where-Object { $_.GetCommandName() -eq $name } | ForEach-Object { $_.Extent.StartOffset }) -join ',') }
        $gate = @($cmds | Where-Object { $_.GetCommandName() -eq 'Test-NoDevBuild' })
        $arg = if ($gate.Count -eq 1) { (@($gate[0].CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.Extent.Text })) -join ' ' } else { '' }
        "@@order|$(Offsets 'Copy-Item')|$(Offsets 'Test-ClientPayload')|$(Offsets 'Test-NoDevBuild')|$arg"
        """;

    static Dictionary<int, (string count, string type, string value)> Answers(List<string> lines, string tag)
    {
        var map = new Dictionary<int, (string, string, string)>();
        foreach (string l in lines.Where(l => l.StartsWith(tag, StringComparison.Ordinal)))
        {
            string[] p = l[tag.Length..].Split('|', 4);
            if (p.Length == 4 && int.TryParse(p[0], out int i)) map[i] = (p[1], p[2], p[3]);
        }
        return map;
    }

    /// <summary>
    /// The spike's own reading of the rule: the marker's ASCII or UTF-16LE bytes anywhere in the file, read in
    /// 1 MiB chunks with the end of each kept for the next.
    /// </summary>
    static bool Carries(string path, byte[][] forms)
    {
        int keep = forms.Max(f => f.Length) - 1;
        var buf = new byte[keep + (1 << 20)];
        int have = 0, n;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        while ((n = fs.Read(buf, have, buf.Length - have)) > 0)
        {
            have += n;
            var window = buf.AsSpan(0, have);
            foreach (var f in forms) if (window.IndexOf(f) >= 0) return true;
            int k = Math.Min(keep, have);
            window[(have - k)..].CopyTo(buf);
            have = k;
        }
        return false;
    }

    static (int Code, string Out, string Err, bool TimedOut) RunProcess(string exe, IEnumerable<string> args, TimeSpan timeout,
        bool dropPsModulePath, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        // A Windows PowerShell started from PowerShell 7 inherits its module path and cannot load its own modules.
        if (dropPsModulePath) psi.Environment.Remove("PSModulePath");
        foreach (var kv in env ?? new()) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        bool exited = p.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited) { try { p.Kill(true); } catch { /* gone already */ } p.WaitForExit(); }
        return (exited ? p.ExitCode : -1, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), !exited);
    }

    static string? FindRepo()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Relic.slnx")) && File.Exists(Path.Combine(dir.FullName, "payload", "manifest.json")))
                    return dir.FullName;
        }
        return null;
    }

    static string? OnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(d => { try { return Path.Combine(d.Trim('"'), exe); } catch { return ""; } })
        .FirstOrDefault(File.Exists);

    static void Put(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    static void ClearAttributes(string dir)
    {
        foreach (string f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None }))
            File.SetAttributes(f, FileAttributes.Normal);
    }

    /// <summary>A PowerShell single-quoted string literal.</summary>
    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    static string Size(int bytes) => bytes % 1048576 == 0 ? $"{bytes / 1048576} MiB" : bytes % 1024 == 0 ? $"{bytes / 1024} KiB" : $"{bytes} B";

    static string Names(List<int> which, List<Synthetic> synthetic) =>
        which.Count == 0 ? "" : " - " + string.Join(", ", which.Select(i => synthetic[i].Src));

    static string Tail(string text)
    {
        string flat = Regex.Replace(text.Trim(), @"\s+", " ");
        return flat.Length <= 600 ? flat : "..." + flat[^600..];
    }
}
