using System.Text;
using System.Text.Json.Nodes;
using Relic.Core.Install;
using Relic.Core.State;

namespace Relic.DevTest;

/// <summary>
/// <c>DevTestRunner selftest</c>: the runner's own mechanics on files it makes in a temp folder — the marker
/// search, the import reader and its name rule, the screen-value edits of a registry export, the staged payload and
/// the base-directory switch that makes Relic's session apply it. It reads no game folder, no profile slot and no
/// state file, and changes nothing outside its temp folder.
/// </summary>
internal static class SelfTest
{
    private static int _checks, _failures;

    private static void Check(string name, bool ok, string? detail = null)
    {
        _checks++;
        if (!ok) _failures++;
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {name}{(ok || detail is null ? "" : " -- " + detail)}");
    }

    private static void Throws<T>(string name, Action action) where T : Exception
    {
        try
        {
            action();
            Check(name, false, $"no {typeof(T).Name}");
        }
        catch (T)
        {
            Check(name, true);
        }
        catch (Exception ex)
        {
            Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "relic-devtest-selftest-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        string runnerDir = Paths.RunnerDir;
        Console.WriteLine($"DevTestRunner selftest in {root}");
        try
        {
            Marker(root, runnerDir);
            Imports(root, runnerDir);
            Window(root);
            Staging(root);
            Forbidden(root);
            Arguments();
        }
        catch (Exception ex)
        {
            Check("the self-test ran to its end", false, ex.ToString());
        }
        finally
        {
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", runnerDir);
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder */ }
        }
        Console.WriteLine($"{_checks - _failures}/{_checks} checks passed");
        return _failures == 0 ? Program.ExitOk : Program.ExitSessionFailed;
    }

    private static void Marker(string root, string runnerDir)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(DevBuild.Marker);
        byte[] utf16 = Encoding.Unicode.GetBytes(DevBuild.Marker);
        Check("the marker is put together from two parts", DevBuild.Marker.Length == 19 && DevBuild.Marker.StartsWith("RELIC-"));

        string straddleAscii = Path.Combine(root, "a.bin");
        WriteWith(straddleAscii, 3 << 20, ascii, (1 << 20) - 7);
        Check("ASCII marker across the first chunk boundary is found", DevBuild.CarriesMarker(straddleAscii));
        string straddleWide = Path.Combine(root, "w.bin");
        WriteWith(straddleWide, 5 << 20, utf16, (2 << 20) - 11);
        Check("UTF-16LE marker across a later chunk boundary is found", DevBuild.CarriesMarker(straddleWide));
        string half = Path.Combine(root, "h.bin");
        WriteWith(half, (1 << 20) + 5, ascii.AsSpan(0, 9).ToArray(), (1 << 20) - 4);
        Check("half a marker is not the marker", !DevBuild.CarriesMarker(half));
        string empty = Path.Combine(root, "e.bin");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Check("an empty file carries no marker", !DevBuild.CarriesMarker(empty));
        string tail = Path.Combine(root, "t.bin");
        WriteWith(tail, 4096, ascii, 4096 - ascii.Length);
        Check("a marker at the very end of a file is found", DevBuild.CarriesMarker(tail));

        foreach (string own in new[] { "DevTestRunner.dll", "DevTestRunner.exe" })
        {
            string path = Path.Combine(runnerDir, own);
            if (File.Exists(path))
                Check($"the runner's own {own} does not carry the marker whole", !DevBuild.CarriesMarker(path));
        }
    }

    private static void WriteWith(string path, int size, byte[] pattern, int at)
    {
        byte[] b = new byte[size];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 31 + 7);
        pattern.CopyTo(b, at);
        File.WriteAllBytes(path, b);
    }

    private static void Imports(string root, string runnerDir)
    {
        string[] runtime = { "VCRUNTIME140.dll", "vcruntime140_1.dll", "MSVCP140.dll", "msvcp140d.dll", "ucrtbased.dll", "mfc140u.dll", "vcomp140.dll", "concrt140.dll" };
        string[] windows = { "KERNEL32.dll", "ucrtbase.dll", "api-ms-win-crt-runtime-l1-1-0.dll", "msvcrt.dll", "msvcp_win.dll", "msvcp140_clr0400.dll", "mfc42.dll", "msvcp60.dll" };
        Check("the Visual C++ runtime names are matched", runtime.All(DevBuild.IsVcRuntimeDllName), string.Join(", ", runtime.Where(n => !DevBuild.IsVcRuntimeDllName(n))));
        Check("Windows' own DLL names are not", !windows.Any(DevBuild.IsVcRuntimeDllName), string.Join(", ", windows.Where(DevBuild.IsVcRuntimeDllName)));

        string exe = Path.Combine(runnerDir, "DevTestRunner.exe");
        if (File.Exists(exe))
        {
            var pe = DevBuild.ReadPe(exe);
            Check("the import table of an x64 program is read", pe.Machine == 0x8664 && !pe.IsDll && pe.Is64
                && pe.Imports.Any(n => n.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase)), string.Join(", ", pe.Imports));
        }
        string core = Path.Combine(runnerDir, "Relic.Core.dll");
        if (File.Exists(core))
        {
            var pe = DevBuild.ReadPe(core);
            Check("a managed DLL is a DLL but not an x64 one", pe.IsDll && !pe.IsX64Dll
                && pe.Imports.Any(n => n.Equals("mscoree.dll", StringComparison.OrdinalIgnoreCase)), string.Join(", ", pe.Imports));
        }
        string cut = Path.Combine(root, "cut.dll");
        File.WriteAllBytes(cut, new byte[] { 0x4D, 0x5A, 0, 0 });
        Throws<InvalidDataException>("a file cut short is no PE file", () => DevBuild.ReadPe(cut));
    }

    private static readonly string Header = WindowMode.GameKeyHeader;

    private static byte[] Reg(params string[] lines)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xFE });
        foreach (string l in lines)
        {
            ms.Write(Encoding.Unicode.GetBytes(l));
            ms.Write(Encoding.Unicode.GetBytes("\r\n"));
        }
        return ms.ToArray();
    }

    private static void Window(string root)
    {
        string dir = Path.Combine(root, "slot");
        Directory.CreateDirectory(dir);
        string reg = Path.Combine(dir, "genshin.reg");
        byte[] original = Reg(
            "Windows Registry Editor Version 5.00",
            "",
            Header,
            "\"GENERAL_DATA_h2389025596\"=hex:7b,22,64,61,74,61,22,3a,31,7d,\\",
            "  00",
            "\"Screenmanager Resolution Width_h182942802\"=dword:00000d70",
            "\"Screenmanager Resolution Height_h2627697771\"=dword:000005a0",
            "\"Screenmanager Is Fullscreen mode_h3981298716\"=dword:00000001",
            "\"Other_h1\"=dword:00000002",
            "\"Text_h2\"=\"x☃y\"",
            "",
            Header.TrimEnd(']') + "\\Sub]",
            "\"Screenmanager Is Fullscreen mode_h3981298716\"=dword:00000001",
            "");
        // A lone surrogate cannot pass through an encoder (it becomes U+FFFD), so its bytes go in afterwards, in
        // the place of the placeholder character.
        int placeholder = original.AsSpan().IndexOf(new byte[] { (byte)'x', 0, 0x03, 0x26, (byte)'y', 0 });
        original[placeholder + 2] = 0x00;
        original[placeholder + 3] = 0xD8;
        File.WriteAllBytes(reg, original);
        Check("the export's three values are read", WindowMode.Problem(reg) is null, WindowMode.Problem(reg));

        WindowMode.Apply(reg, new WindowSpec(1600, 900));
        byte[] applied = File.ReadAllBytes(reg);
        var v = WindowMode.Read(applied);
        Check("the window is written into the three values",
            v[WindowMode.Fullscreen].Value == 0 && v[WindowMode.Width].Value == 1600 && v[WindowMode.Height].Value == 900);
        Check("the copy is the original byte for byte", File.ReadAllBytes(reg + WindowMode.CopySuffix).AsSpan().SequenceEqual(original));
        string sub = "\"Screenmanager Is Fullscreen mode_h3981298716\"=dword:00000001\r\n";
        Check("a value of the same name under another key is left alone",
            Encoding.Unicode.GetString(applied).EndsWith("\\Sub]\r\n" + sub + "\r\n", StringComparison.Ordinal));
        Check("an unreadable character elsewhere keeps its bytes",
            applied.AsSpan().IndexOf(new byte[] { (byte)'x', 0, 0x00, 0xD8, (byte)'y', 0 }) >= 0);
        Throws<InvalidOperationException>("a second window while the copy is there is refused", () => WindowMode.Apply(reg, new WindowSpec(800, 600)));

        string? was = WindowMode.Restore(reg);
        Check("restore right after apply gives the original back byte for byte",
            File.ReadAllBytes(reg).AsSpan().SequenceEqual(original), was);
        Check("the copy is gone after the restore", !File.Exists(reg + WindowMode.CopySuffix));
        Check("restore without a copy has nothing to do", WindowMode.Restore(reg) is null);

        // The session's own export: the game changed one value of its own and the width; Height went missing.
        WindowMode.Apply(reg, new WindowSpec(1280, 720));
        byte[] session = Reg(
            "Windows Registry Editor Version 5.00",
            "",
            Header,
            "\"GENERAL_DATA_h2389025596\"=hex:7b,22,64,61,74,61,22,3a,32,7d,\\",
            "  00",
            "\"Screenmanager Resolution Width_h182942802\"=dword:00000501",
            "\"Screenmanager Is Fullscreen mode_h3981298716\"=dword:00000000",
            "\"Other_h1\"=dword:00000003",
            "");
        File.WriteAllBytes(reg, session);
        WindowMode.Restore(reg);
        byte[] back = File.ReadAllBytes(reg);
        var b = WindowMode.Read(back);
        string text = Encoding.Unicode.GetString(back);
        Check("the owner's three values are back in the session's export",
            b[WindowMode.Fullscreen].Value == 1 && b[WindowMode.Width].Value == 0xd70 && b[WindowMode.Height].Value == 0x5a0);
        Check("what the session changed besides them stays",
            text.Contains("\"Other_h1\"=dword:00000003") && text.Contains("3a,32,7d"));
        Check("a value the session's export lacked is added under the game's key",
            text.Contains(Header + "\r\n\"Screenmanager Resolution Height_h2627697771\"=dword:000005a0\r\n", StringComparison.Ordinal));

        string none = Path.Combine(dir, "none.reg");
        File.WriteAllBytes(none, Reg("Windows Registry Editor Version 5.00", "", Header, "\"Other_h1\"=dword:00000002", ""));
        Check("an export without the values is refused with the reason", WindowMode.Problem(none)?.Contains("Screenmanager") == true);
        string ansi = Path.Combine(dir, "ansi.reg");
        File.WriteAllText(ansi, "REGEDIT4\r\n");
        Check("a file that is no UTF-16LE export is refused", WindowMode.Problem(ansi) is not null);
        string dup = Path.Combine(dir, "dup.reg");
        File.WriteAllBytes(dup, Reg("Windows Registry Editor Version 5.00", "", Header,
            "\"Screenmanager Resolution Width_h1\"=dword:00000001", "\"Screenmanager Resolution Width_h2\"=dword:00000002",
            "\"Screenmanager Resolution Height_h3\"=dword:00000001", "\"Screenmanager Is Fullscreen mode_h4\"=dword:00000001", ""));
        Check("a value named twice is refused", WindowMode.Problem(dup) is not null);

        Check("'1600x900' is a window", WindowSpec.Parse("1600x900") == new WindowSpec(1600, 900));
        Check("'keep' leaves the slot's values", WindowSpec.Parse("keep") is null);
        Throws<UsageException>("'10x10' is too small", () => WindowSpec.Parse("10x10"));
        Throws<UsageException>("'big' is no window", () => WindowSpec.Parse("big"));
    }

    private static void Staging(string root)
    {
        string source = Path.Combine(root, "payload-src");
        Directory.CreateDirectory(Path.Combine(source, "9.9", "ee"));
        Directory.CreateDirectory(Path.Combine(source, "9.8", "ee"));
        Directory.CreateDirectory(Path.Combine(source, "common"));
        Directory.CreateDirectory(Path.Combine(source, "9.7"));
        File.WriteAllBytes(Path.Combine(source, "9.9", "ee", "CLibrary.dll.bin"), Encoding.ASCII.GetBytes("release 9.9"));
        File.WriteAllBytes(Path.Combine(source, "9.8", "ee", "CLibrary.dll.bin"), Encoding.ASCII.GetBytes("release 9.8"));
        File.WriteAllBytes(Path.Combine(source, "common", "tool.exe.bin"), Encoding.ASCII.GetBytes("tool"));
        File.WriteAllBytes(Path.Combine(source, "9.7", "data.bin"), Encoding.ASCII.GetBytes("a file of 9.7, no enhancements DLL"));
        string r99 = Files.Sha256(Path.Combine(source, "9.9", "ee", "CLibrary.dll.bin"));
        string r98 = Files.Sha256(Path.Combine(source, "9.8", "ee", "CLibrary.dll.bin"));
        string tool = Files.Sha256(Path.Combine(source, "common", "tool.exe.bin"));
        string d97 = Files.Sha256(Path.Combine(source, "9.7", "data.bin"));
        string manifest = "{\n  \"_note\": \"a manifest \u2014 with a dash\",\n  \"common\": [\n    {\"src\": \"common/tool.exe.bin\", \"dst\": \"ayy/tool.exe\", \"sha256\": \"" + tool + "\"}\n  ],\n"
            + "  \"versions\": {\n    \"9.9\": [\n      {\"src\": \"9.9/ee/CLibrary.dll.bin\", \"dst\": \"ayy/anime/ee/CLibrary.dll\", \"sha256\": \"" + r99 + "\", \"inject\": true}\n    ],\n"
            + "    \"9.8\": [\n      {\"src\": \"9.8/ee/CLibrary.dll.bin\", \"dst\": \"ayy/anime/ee/CLibrary.dll\", \"sha256\": \"" + r98 + "\", \"inject\": true}\n    ],\n"
            + "    \"9.7\": [\n      {\"src\": \"9.7/data.bin\", \"dst\": \"x/data.bin\", \"sha256\": \"" + d97 + "\"}\n    ]\n  }\n}\n";
        File.WriteAllText(Path.Combine(source, "manifest.json"), manifest, new UTF8Encoding(false));

        var entry = Payload.FindInjectEntry(source, "9.9");
        Check("the version's enhancements entry is found", entry.Src == "9.9/ee/CLibrary.dll.bin" && entry.Sha256 == r99);
        Throws<InvalidDataException>("a version without one is refused", () => Payload.FindInjectEntry(source, "7.7"));

        string dev = Path.Combine(root, "dev.dll");
        File.WriteAllBytes(dev, Encoding.ASCII.GetBytes("dev build " + DevBuild.Marker + " end"));
        string devSha = Files.Sha256(dev);
        var log = new RunLog(null);

        string stage = Path.Combine(root, "stage");
        Check("a new stage folder is accepted", Stage.Problem(stage, source, Array.Empty<string>()) is null);
        Check("a stage under the payload folder is refused", Stage.Problem(Path.Combine(source, "x"), source, Array.Empty<string>()) is not null);
        Check("a stage holding the payload folder is refused", Stage.Problem(root, source, Array.Empty<string>()) is not null);
        Check("a stage over a forbidden folder is refused", Stage.Problem(Path.Combine(root, "game", "s"), source, new[] { Path.Combine(root, "game") }) is not null);
        string foreign = Path.Combine(root, "foreign");
        Directory.CreateDirectory(Path.Combine(foreign, "payload"));
        File.WriteAllText(Path.Combine(foreign, "payload", "keep.txt"), "not the runner's");
        Check("a payload folder the runner did not make is refused", Stage.Problem(foreign, source, Array.Empty<string>()) is not null);
        Throws<Refusal>("and building over it is refused", () => Stage.Build(foreign, source, "9.9", entry, dev, devSha, log));
        Check("its file is left alone", File.Exists(Path.Combine(foreign, "payload", "keep.txt")));
        Check("a drive root is refused", Stage.Problem(Path.GetPathRoot(root)!, source, Array.Empty<string>()) is not null);
        // build_ee.ps1 -StageDev puts <version>\CLibrary.dll into the same folder: it is no reason to refuse, and stays.
        Directory.CreateDirectory(Path.Combine(stage, "9.9"));
        File.WriteAllText(Path.Combine(stage, "9.9", "CLibrary.dll"), "the build's own staged copy");
        Check("a stage folder that also holds the build's staged DLL is accepted", Stage.Problem(stage, source, Array.Empty<string>()) is null);

        for (int round = 1; round <= 2; round++)
        {
            string staged = Stage.Build(stage, source, "9.9", entry, dev, devSha, log);
            string sp = Path.Combine(stage, "payload");
            Check($"round {round}: the DevTest DLL is in the entry's place", staged == devSha
                && File.ReadAllBytes(Path.Combine(sp, "9.9", "ee", "CLibrary.dll.bin")).AsSpan().SequenceEqual(File.ReadAllBytes(dev)));
            var before = JsonNode.Parse(manifest)!;
            before["versions"]!["9.9"]![0]!["sha256"] = devSha;
            var after = JsonNode.Parse(File.ReadAllText(Path.Combine(sp, "manifest.json")));
            Check($"round {round}: the staged manifest differs only in that sha256", JsonNode.DeepEquals(before, after));
            Check($"round {round}: the other files are the source's", Files.Sha256(Path.Combine(sp, "9.8", "ee", "CLibrary.dll.bin")) == r98
                && Files.Sha256(Path.Combine(sp, "common", "tool.exe.bin")) == tool);
            Check($"round {round}: the source payload is untouched", Files.Sha256(Path.Combine(source, "9.9", "ee", "CLibrary.dll.bin")) == r99
                && File.ReadAllText(Path.Combine(source, "manifest.json")) == manifest);
            Check($"round {round}: the staged payload is marked as the runner's", Stage.Problem(stage, source, Array.Empty<string>()) is null
                && File.Exists(Path.Combine(sp, Stage.MarkerFile)));
            Check($"round {round}: the build's staged DLL beside it is left alone",
                File.ReadAllText(Path.Combine(stage, "9.9", "CLibrary.dll")) == "the build's own staged copy");
        }

        string twice = manifest.Replace(r98, r99);
        Throws<InvalidDataException>("a sha256 that occurs twice is not rewritten",
            () => Payload.RewriteSha(Encoding.UTF8.GetBytes(twice), "9.9", entry, devSha));
        File.WriteAllBytes(Path.Combine(root, "plain.dll"), Encoding.ASCII.GetBytes("no marker"));
        Throws<Refusal>("a DLL without the marker is not staged",
            () => Stage.Build(stage, source, "9.9", entry, Path.Combine(root, "plain.dll"), Files.Sha256(Path.Combine(root, "plain.dll")), log));
        Throws<Refusal>("a DLL that changed after the preflight is not staged",
            () => Stage.Build(stage, source, "9.9", entry, dev, r99, log));

        // A version whose payload ships no enhancements DLL: the stage gets an entry of its own, and the release state of
        // the game folder is no DLL.
        Check("a version listed without an enhancements entry ships none", !Payload.TryFindInjectEntry(source, "9.7", out var none97) && none97 is null);
        Check("a version that is not listed ships none", !Payload.TryFindInjectEntry(source, "9.6", out _));
        Throws<InvalidDataException>("a synthetic entry is made only for a version id", () => Payload.SyntheticEntry("../9.7"));
        Throws<InvalidOperationException>("a shipped entry is never added to a manifest",
            () => Payload.AddEntry(Encoding.UTF8.GetBytes(manifest), "9.9", entry, devSha));
        foreach (string v in new[] { "9.7", "9.6" })
        {
            var syn = Payload.SyntheticEntry(v);
            string staged = Stage.Build(stage, source, v, syn, dev, devSha, log);
            string sp = Path.Combine(stage, "payload");
            var expected = JsonNode.Parse(manifest)!;
            var add = new JsonObject
            {
                ["src"] = syn.Src, ["dst"] = syn.Dst, ["sha256"] = devSha, ["inject"] = true,
                ["reason"] = "DevTest stage only: the payload ships no enhancements DLL for this version.",
            };
            if (expected["versions"]![v] is JsonArray list) list.Add(add);
            else expected["versions"]![v] = new JsonArray(add);
            Check($"{v}: the staged manifest is the source's plus the stage's own entry",
                JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllText(Path.Combine(sp, "manifest.json")))));
            Check($"{v}: the DevTest DLL is where a shipped one would be", staged == devSha
                && Files.Sha256(Path.Combine(sp, v, "ee", "CLibrary.dll.bin")) == devSha);
            Check($"{v}: the source payload is untouched", File.ReadAllText(Path.Combine(source, "manifest.json")) == manifest
                && !Directory.Exists(Path.Combine(source, v, "ee")));
        }
        var rel97 = Preflight.TryRelease(source, "test", "9.7", out _);
        Check("the release state of a version the payload ships no DLL for is 'no DLL'", rel97 is { Entry.Synthetic: true });
        string game97 = Path.Combine(root, "game97");
        string put97 = Path.Combine(game97, "ayy", "anime", "ee", "CLibrary.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(put97)!);
        var inst97 = new InstalledVersion { GameDir = game97, ProfileId = "priv97" };
        File.Copy(dev, put97, overwrite: true);
        var res97 = new JsonObject();
        Check("a held session gate leaves the DevTest DLL in place and says so",
            !Program.RemoveDevDll("9.7", inst97, rel97!, false, log, res97, clientRunning: false) && File.Exists(put97));
        Check("a running client leaves it in place too",
            !Program.RemoveDevDll("9.7", inst97, rel97!, true, log, res97, clientRunning: true) && File.Exists(put97));
        Check("with the game closed the DevTest DLL is taken out",
            Program.RemoveDevDll("9.7", inst97, rel97!, true, log, res97, clientRunning: false) && !File.Exists(put97)
            && res97["releaseDll"]?["removed"]?.GetValue<bool>() == true && res97["releaseDll"]?["shipped"]?.GetValue<bool>() == false);
        File.WriteAllText(put97, "a DLL of the player's own");
        Check("a file there that is no DevTest build is left alone",
            Program.RemoveDevDll("9.7", inst97, rel97!, true, log, res97, clientRunning: false) && File.ReadAllText(put97) == "a DLL of the player's own");
        Stage.Build(stage, source, "9.9", entry, dev, devSha, log);   // the stage of a shipped version again, for what follows

        Check("a release payload with its own file can put the DLL back", Preflight.TryRelease(source, "test", "9.9", out _) is not null);
        string devRelease = Path.Combine(stage, "payload");
        Check("a payload that holds a DevTest build cannot", Preflight.TryRelease(devRelease, "test", "9.9", out string? why) is null
            && why?.Contains("DevTest") == true, why);

        Stage.Build(stage, source, "9.9", entry, dev, devSha, log);
        Stage.Activate(stage);
        Check("Relic's payload root is the staged payload once the stage is the base directory",
            GamePatcher.PayloadRoot() is { } pr && Files.SamePath(pr, Path.Combine(stage, "payload")), GamePatcher.PayloadRoot());
        string game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        var inj = GamePatcher.Injectables("9.9", game);
        Check("the session would hand the staged DLL over", inj.Count == 1 && inj[0].Shipped
            && inj[0].DstPath is { } dp && Files.SamePath(dp, Path.Combine(game, "ayy", "anime", "ee", "CLibrary.dll")));
        var report = GamePatcher.Apply("9.9", game, null, withInjectables: true);
        string put = Path.Combine(game, "ayy", "anime", "ee", "CLibrary.dll");
        Check("the session's patch step puts the DevTest DLL in the game folder", report.Applied.Count == 2
            && File.Exists(put) && Files.Sha256(put) == devSha, report.Summary);
        var back = GamePatcher.Apply("9.9", game, null, payloadRoot: source, withInjectables: true);
        Check("the release payload puts the release DLL back", Files.Sha256(put) == r99 && !DevBuild.CarriesMarker(put), back.Summary);
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", Paths.RunnerDir);
    }

    private static void Arguments()
    {
        var o = Options.Parse(new[] { "--version", "2.8", "--dll=x.dll", "--window", "keep" }, new[] { "version", "dll", "window" });
        Check("options are read in both spellings", o.Get("version") == "2.8" && o.Get("dll") == "x.dll" && o.Get("window") == "keep");
        Throws<UsageException>("an unknown option is refused", () => Options.Parse(new[] { "--nope", "1" }, new[] { "version" }));
        Throws<UsageException>("an option given twice is refused", () => Options.Parse(new[] { "--version", "1", "--version", "2" }, new[] { "version" }));
        Throws<UsageException>("an option without its value is refused", () => Options.Parse(new[] { "--version" }, new[] { "version" }));
        Throws<UsageException>("a required option that is missing is named", () => Options.Parse(Array.Empty<string>(), new[] { "version" }).Require("version"));

        Check("exit: a DevTest DLL left in the game folder wins", Program.ExitCodeFor(true, true, true, true, true) == Program.ExitDevDllLeft);
        Check("exit: then screen values that could not be put back", Program.ExitCodeFor(false, true, true, true, true) == Program.ExitWindowNotRestored);
        Check("exit: then the session's profile still loaded", Program.ExitCodeFor(false, false, true, true, true) == Program.ExitSlotLoaded);
        Check("exit: then Fiddler still running", Program.ExitCodeFor(false, false, false, true, true) == Program.ExitFiddlerLeft);
        Check("exit: then a failed session", Program.ExitCodeFor(false, false, false, false, true) == Program.ExitSessionFailed);
        Check("exit: else done", Program.ExitCodeFor(false, false, false, false, false) == Program.ExitOk);

        Check("a folder beside another of a longer name is not inside it", !Files.IsInside(@"D:\relic-devtest\stage", @"D:\relic"));
        Check("a folder below another is inside it", Files.IsInside(@"D:\relic\Genshin 2.8", @"D:\relic"));

        string name = Program.SecretPrefix + "SELFTEST_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(name, "a secret");
        int dropped = Program.DropSecrets();
        Check("the scenario's secrets leave the runner's environment", dropped >= 1 && Environment.GetEnvironmentVariable(name) is null
            && Environment.GetEnvironmentVariable("PATH") is not null);
    }

    private static void Forbidden(string root)
    {
        string repo = Path.Combine(root, "repo"), source = Path.Combine(repo, "payload");
        string data = Path.Combine(root, "localappdata", "Relic"), installed = Path.Combine(root, "programs", "Relic");
        string game = Path.Combine(root, "games", "Genshin 2.8");
        var forbidden = Preflight.StageForbidden(data, installed, new[] { game }, repo);
        string launcherDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Relic");
        Check("a stage in the repository is refused (the publish output, the work tree)",
            Stage.Problem(Path.Combine(repo, "publish", "win-x64"), source, forbidden) is not null
            && Stage.Problem(Path.Combine(repo, "stage"), source, forbidden) is not null);
        Check("a stage in Relic's data, the installed launcher, the launcher's default folder or a game folder is refused",
            new[] { Path.Combine(data, "x"), Path.Combine(installed, "x"), Path.Combine(launcherDefault, "x"), Path.Combine(game, "x") }
                .All(s => Stage.Problem(s, source, forbidden) is not null));
        Check("a stage that would hold the repository is refused", Stage.Problem(root, source, forbidden) is not null);
        Check("a stage of its own beside them is accepted", Stage.Problem(Path.Combine(root, "relic-devtest", "stage"), source, forbidden) is null);
    }
}
