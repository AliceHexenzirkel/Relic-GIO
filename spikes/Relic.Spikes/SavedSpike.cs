using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Relic.Core.Server;
using Relic.Core.Setup;
using Relic.Core.State;
using Relic.Core.Util;

/// <summary>
/// One secrets record per host, the start screen's saved servers, the SSH host-key pins, the one lock on the state, Quick
/// Start's plan file and its resume value:
///  - server.json (ServerConfigStore): a file of one record (the form every earlier build wrote) reads as a list of one;
///    Save is an upsert per host (another letter case or a trailing dot is the same host) that keeps the other hosts'
///    records, the newest first; LoadFor is the record of one host, whichever was saved last; Forget removes one host
///    and deletes the file with the last, ForgetWhere only the records it picks (an open key, never a real token saved
///    meanwhile); a plaintext file of either form loads and is written again protected once; a corrupt file reads as
///    nothing and the next Save replaces it — a protected one kept beside it as it was, each such file under a name of
///    its own, the same bytes once, at most three, the oldest going first; one written in the clear never kept; a file
///    another program holds makes Save throw and stays as it was; two threads saving different hosts keep both;
///    TokenHosts names hosts with a real token only; the file never holds a token in the clear; Delete takes the kept
///    copies and the temporary file a cut write left; no Core caller lends the newest record's token;
///  - the state (RelicState): ForgetServer forgets a host's saved entry, logins, server answers and pins on any port —
///    the host told by the last colon of its key, an IPv6 host's own colons included, a bracketed one too — and nothing
///    of another host; the saved list's rules (one entry per host, its id kept, the name, the mode, the address as typed
///    without a scheme or a path, at most 50); the pins (one spelling per address whatever a caller or a state file
///    writes — an IP literal in its canonical form, so every spelling of one IPv6 box and an IPv4 address written with
///    leading zeros share one slot —; one confirmed key takes the place of the earlier ones); the Gate (concurrent
///    changes and saves never tear the file); a failed registration's take-back (the version's entries and selection
///    back, nothing of another's);
///    the new fields round-trip, and a state file without them reads as empty;
///  - the uninstall's report: every firewall rule left is named once, the agent's in its own words; Docker Desktop and
///    WSL's memory limit are named as kept when they are there (stand-in values and a stand-in .wslconfig — the real
///    PC's are never looked at, the uninstall never run);
///  - the plan (QuickStartPlan, QuickStartStore): a document holding secret keys loads without them and is stored
///    without them, and a step's note keeps no secret assignment's value; the refusals (unknown version ids, a server
///    kind outside the list, more than 10 versions, 64 KiB or more, the words, the addresses, the account name, the step
///    ids); the stored form and its round trip; a plan that is done never stays on disk;
///  - the resume value (Startup, against a synthetic RunOnce key): armed as "&lt;exe&gt;" --quickstart, read, disarmed,
///    RelicRecover left alone.
/// Everything it writes is under one temp folder and one synthetic registry key; the real state, server.json, plan file
/// and RunOnce key are never touched.
/// </summary>
static class SavedSpike
{
    public const string Name = "saved";

    static readonly string[] GameIds = { "1.6", "2.8", "3.2" };

    public static int Run()
    {
        Console.WriteLine($"== {Name} spike: server.json per host, saved servers, host-key pins, the state's lock, the Quick Start plan and its resume ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string tmp = Path.Combine(Path.GetTempPath(), "relic-saved-spike-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            RecordStore(tmp, Check);
            RecordStoreMore(tmp, Check);
            StateForget(Check);
            SavedList(Check);
            Pins(Check);
            StateGate(tmp, Check);
            StateRoundTrip(tmp, Check);
            Registration(Check);
            UninstallReport(tmp, Check);
            PlanSecrets(tmp, Check);
            PlanRefusals(tmp, Check);
            PlanStore(tmp, Check);
            ResumeValue(Check);
            CoreSource(Check);
        }
        catch (Exception ex)
        {
            Check($"the spike ran to its end ({ex.GetType().Name}: {ex.Message})", false);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"{Name} spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    static ServerConfig Rec(string host, string token, bool openKey = false, int agentPort = 18080) =>
        new(host, 22, "root", "", token, agentPort, "direct", openKey);

    static bool Throws(Action a)
    {
        try { a(); return false; } catch { return true; }
    }

    static byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

    static string Unprotected(string path) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));

    static bool IsProtected(string path)
    {
        try { Unprotected(path); return true; } catch (CryptographicException) { return false; }
    }

    static string Hosts(IEnumerable<ServerConfig> records) => string.Join(",", records.Select(r => r.Host));

    // ── A. server.json: one record per host ──
    static void RecordStore(string tmp, Action<string, bool> Check)
    {
        string dir = Path.Combine(tmp, "secrets");
        Directory.CreateDirectory(dir);
        var a = Rec("host-a.example.com", "token-a");
        var b = Rec("host-b.example.com", "token-b");

        // A file of one record — what every earlier build wrote: a protected ServerConfig object.
        string single = Path.Combine(dir, "single.json");
        File.WriteAllBytes(single, Protect(JsonSerializer.SerializeToUtf8Bytes(a)));
        var one = ServerConfigStore.LoadAll(single);
        Check("records: a protected file of one record reads as a list of one, and Load returns that record",
            one.Count == 1 && one[0] == a && ServerConfigStore.Load(single) == a);
        Check("records: reading a file never writes it (the single-record file is left in its own form)",
            !Unprotected(single).Contains("\"servers\"", StringComparison.Ordinal));

        string path = Path.Combine(dir, "server.json");
        Check("records: no file reads as no record, and Load answers null",
            ServerConfigStore.LoadAll(path).Count == 0 && ServerConfigStore.Load(path) is null && !File.Exists(path));
        ServerConfigStore.Save(a, path);
        ServerConfigStore.Save(b, path);
        var ba = ServerConfigStore.LoadAll(path);
        Check("records: Save(A) then Save(B) keeps both, the newest first — [B, A]; Load is B",
            Hosts(ba) == "host-b.example.com,host-a.example.com" && ServerConfigStore.Load(path) == b);
        string doc = Unprotected(path);
        using (var parsed = JsonDocument.Parse(doc))
            Check("records: the file is the protected object {\"servers\": [...]}, and no token is in it in the clear",
                parsed.RootElement.TryGetProperty("servers", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 2
                && !Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("token-a", StringComparison.Ordinal));
        Check("records: an older build reading the list form gets one empty record (no host, no token)",
            JsonSerializer.Deserialize<ServerConfig>(doc) is { } older && string.IsNullOrEmpty(older.Host) && string.IsNullOrEmpty(older.AgentToken));

        var a2 = Rec("HOST-A.Example.com.", "token-a2");
        ServerConfigStore.Save(a2, path);
        var a2b = ServerConfigStore.LoadAll(path);
        Check("records: saving A's host in another letter case and with a trailing dot replaces A and puts it first",
            a2b.Count == 2 && a2b[0] == a2 && a2b[1] == b);
        var a3 = Rec("host-a.example.com", "token-a3");
        ServerConfigStore.Save(a3, path);
        Check("records: saving it again in its own spelling replaces it once more — still one record per host",
            ServerConfigStore.LoadAll(path) is { Count: 2 } l3 && l3[0] == a3 && l3[1] == b);

        Check("records: Forget of a host with no record answers false and changes nothing",
            !ServerConfigStore.Forget("host-c.example.com", path) && ServerConfigStore.LoadAll(path).Count == 2
            && !ServerConfigStore.Forget("  ", path));
        bool forgotB = ServerConfigStore.Forget("Host-B.example.com.", path);
        Check("records: Forget(B) (any letter case, a trailing dot) → [A]", forgotB && Hosts(ServerConfigStore.LoadAll(path)) == "host-a.example.com");
        bool forgotA = ServerConfigStore.Forget("host-a.example.com", path);
        Check("records: Forget(A), the last record, deletes the file", forgotA && !File.Exists(path));
        Check("records: Forget on a file that is not there answers false", !ServerConfigStore.Forget("host-a.example.com", path));

        // A plaintext file of either form: read, written again protected once, and no recursion.
        string plain = Path.Combine(dir, "plain.json");
        File.WriteAllBytes(plain, JsonSerializer.SerializeToUtf8Bytes(a));
        var fromPlain = ServerConfigStore.LoadAll(plain);
        bool protectedNow = IsProtected(plain);
        byte[] afterFirst = File.ReadAllBytes(plain);
        DateTime stampFirst = File.GetLastWriteTimeUtc(plain);
        var again = ServerConfigStore.LoadAll(plain);
        Check("records: a plaintext single record loads, and the file is written again protected, in the list form",
            fromPlain.Count == 1 && fromPlain[0] == a && protectedNow && Unprotected(plain).Contains("\"servers\"", StringComparison.Ordinal));
        Check("records: the plaintext file is written ONCE — the next load reads the protected file and writes nothing (no recursion)",
            again.Count == 1 && again[0] == a && File.ReadAllBytes(plain).SequenceEqual(afterFirst) && File.GetLastWriteTimeUtc(plain) == stampFirst);
        string plainList = Path.Combine(dir, "plain-list.json");
        File.WriteAllText(plainList, "{\"servers\":[" + JsonSerializer.Serialize(b) + "," + JsonSerializer.Serialize(a) + "]}");
        Check("records: a plaintext file of the list form loads in its order and is written again protected",
            Hosts(ServerConfigStore.LoadAll(plainList)) == "host-b.example.com,host-a.example.com" && IsProtected(plainList));

        // Elements that are no record are passed over; the shape decides the form.
        string mixed = Path.Combine(dir, "mixed.json");
        File.WriteAllBytes(mixed, Protect(Encoding.UTF8.GetBytes(
            "{\"servers\":[1,\"x\",null,{\"Host\":\"\",\"AgentToken\":\"t\"},{\"Host\":\"  \"},{\"Host\":\"ok.example.com\",\"Port\":22,\"User\":\"root\",\"Password\":\"\",\"AgentToken\":\"t\",\"AgentPort\":18080,\"Mode\":\"direct\"},{\"Host\":\"bad.example.com\",\"Port\":\"x\"}]}")));
        Check("records: an element of servers that is not an object, names no host or cannot be read is passed over",
            Hosts(ServerConfigStore.LoadAll(mixed)) == "ok.example.com");
        string notList = Path.Combine(dir, "notlist.json");
        File.WriteAllBytes(notList, Protect(Encoding.UTF8.GetBytes("{\"servers\":\"x\",\"Host\":\"\"}")));
        Check("records: an object whose servers is not an array is no list — a record without a host, so none",
            ServerConfigStore.LoadAll(notList).Count == 0);

        // A corrupt file: nothing, no throw; the next Save replaces it. Random bytes are no protected file: nothing is kept.
        string corrupt = Path.Combine(dir, "corrupt.json");
        byte[] corruptBytes = RandomNumberGenerator.GetBytes(300);
        corruptBytes[0] = 0; // never the first byte of a DPAPI blob
        File.WriteAllBytes(corrupt, corruptBytes);
        bool corruptThrew = Throws(() => ServerConfigStore.LoadAll(corrupt));
        var corruptRead = corruptThrew ? null : ServerConfigStore.LoadAll(corrupt);
        Check("records: a file that cannot be decrypted or parsed reads as no record, without throwing — and reading it keeps no copy",
            !corruptThrew && corruptRead is { Count: 0 } && ServerConfigStore.Load(corrupt) is null
            && ServerConfigStore.UnreadableCopies(corrupt).Count == 0 && File.ReadAllBytes(corrupt).SequenceEqual(corruptBytes));
        Check("records: Forget leaves a corrupt file alone (nothing readable to remove)",
            !ServerConfigStore.Forget("host-a.example.com", corrupt) && File.Exists(corrupt) && !IsProtected(corrupt));
        ServerConfigStore.Save(b, corrupt);
        Check("records: the next Save over a corrupt file writes [cfg]; bytes that are no protected file are not kept (only a DPAPI blob is)",
            ServerConfigStore.LoadAll(corrupt) is { Count: 1 } c1 && c1[0] == b && IsProtected(corrupt)
            && ServerConfigStore.UnreadableCopies(corrupt).Count == 0 && Directory.GetFiles(dir, "corrupt.json*").Length == 1);
        string notObject = Path.Combine(dir, "array.json");
        File.WriteAllBytes(notObject, Protect(Encoding.UTF8.GetBytes("[1,2,3]")));
        Check("records: a document that is not a JSON object reads as no record", ServerConfigStore.LoadAll(notObject).Count == 0);

        // A file another program holds: Save throws and the file stays as it was.
        string held = Path.Combine(dir, "held.json");
        ServerConfigStore.Save(a, held);
        byte[] heldBefore = File.ReadAllBytes(held);
        bool heldThrew;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            heldThrew = Throws(() => ServerConfigStore.Save(b, held));
        Check("records: a file held open with FileShare.None makes Save throw, and the file stays exactly as it was",
            heldThrew && File.ReadAllBytes(held).SequenceEqual(heldBefore) && Hosts(ServerConfigStore.LoadAll(held)) == "host-a.example.com"
            && !File.Exists(held + ".tmp"));
        bool forgetHeldThrew;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            forgetHeldThrew = Throws(() => ServerConfigStore.Forget("host-a.example.com", held));
        Check("records: Forget of a held file throws too, and the record stays",
            forgetHeldThrew && Hosts(ServerConfigStore.LoadAll(held)) == "host-a.example.com");
        IReadOnlyList<ServerConfig>? heldLoad = null;
        bool heldLoadThrew;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            heldLoadThrew = Throws(() => heldLoad = ServerConfigStore.LoadAll(held));
        Check("records: LoadAll of a held file never throws — it reads as no record", !heldLoadThrew && heldLoad is { Count: 0 });

        // Two threads saving different hosts.
        string race = Path.Combine(dir, "race.json");
        Exception? raceFault = null;
        Thread Saver(string host) => new(() =>
        {
            try
            {
                for (int i = 0; i < 200; i++) ServerConfigStore.Save(Rec(host, $"{host}-{i}"), race);
            }
            catch (Exception ex) { raceFault ??= ex; }
        });
        var t1 = Saver("one.example.com");
        var t2 = Saver("two.example.com");
        t1.Start(); t2.Start(); t1.Join(); t2.Join();
        var raced = ServerConfigStore.LoadAll(race);
        Check("records: two threads saving different hosts 200 times each end with both records (their last tokens)"
              + (raceFault is null ? "" : $" — {raceFault.GetType().Name}: {raceFault.Message}"),
            raceFault is null && raced.Count == 2
            && raced.Any(r => r.Host == "one.example.com" && r.AgentToken == "one.example.com-199")
            && raced.Any(r => r.Host == "two.example.com" && r.AgentToken == "two.example.com-199"));

        // TokenHosts.
        var hosts = ServerConfig.TokenHosts(new[]
        {
            Rec(" Real.example.com ", "t1"), Rec("open.example.com", "key", openKey: true), Rec("empty.example.com", "  "),
            Rec("real.example.com.", "t2"), Rec("second.example.com", "t3"), null!,
        });
        Check("records: TokenHosts names the hosts with a real token, trimmed, once each, in order — never an open key or an empty token",
            string.Join(",", hosts) == "Real.example.com,second.example.com"
            && ServerConfig.TokenHosts(null).Count == 0 && ServerConfig.TokenHosts(Array.Empty<ServerConfig>()).Count == 0);

        Check("records: Delete removes the whole file (every host's record)",
            File.Exists(race) && Do(() => ServerConfigStore.Delete(race)) && !File.Exists(race));
    }

    static bool Do(Action a) { a(); return true; }

    /// <summary>The bytes of a file, or null when it is not there (a check then fails instead of ending the spike).</summary>
    static byte[]? BytesOf(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    // ── A2. server.json: the record of one host, forgetting what one picks, an unreadable file kept ──
    static void RecordStoreMore(string tmp, Action<string, bool> Check)
    {
        string dir = Path.Combine(tmp, "secrets-more");
        Directory.CreateDirectory(dir);
        var a = Rec("host-a.example.com", "token-a");
        var b = Rec("host-b.example.com", "token-b");

        // LoadFor: the record of the host asked for, whichever host was saved last.
        string path = Path.Combine(dir, "server.json");
        ServerConfigStore.Save(a, path);
        ServerConfigStore.Save(b, path);
        Check("loadFor: with [B, A] on file, LoadFor(A) is A's record (Load, the newest, is B's) — in any letter case, with a trailing dot",
            ServerConfigStore.Load(path) == b && ServerConfigStore.LoadFor("host-a.example.com", path) == a
            && ServerConfigStore.LoadFor(" HOST-A.Example.com. ", path) == a && ServerConfigStore.LoadFor("host-b.example.com", path) == b);
        Check("loadFor: a host without a record, and no host, is null",
            ServerConfigStore.LoadFor("host-c.example.com", path) is null && ServerConfigStore.LoadFor("", path) is null
            && ServerConfigStore.LoadFor(null, path) is null && ServerConfigStore.LoadFor("host-a.example.com", Path.Combine(dir, "none.json")) is null);
        Check("loadFor: what the PLAY pre-check lends — the configured host's token, never the newest record's",
            ServerConfig.Effective(ServerConfigStore.LoadFor("host-a.example.com", path), "host-a.example.com", 18080)?.AgentToken == "token-a"
            && ServerConfig.Effective(ServerConfigStore.Load(path), "host-a.example.com", 18080) is not { AgentToken: "token-a" or "token-b" });

        // ForgetWhere: only the records it picks.
        string keys = Path.Combine(dir, "keys.json");
        var open = Rec("open.example.com", "open-key", openKey: true);
        ServerConfigStore.Save(open, keys);
        ServerConfigStore.Save(b, keys);
        Check("forgetWhere: the host's open key goes, the other host stays",
            ServerConfigStore.ForgetWhere("open.example.com", r => r.OpenKey, keys) && Hosts(ServerConfigStore.LoadAll(keys)) == "host-b.example.com");
        ServerConfigStore.Save(open, keys);
        var real = Rec("OPEN.example.com", "real-token");
        ServerConfigStore.Save(real, keys); // a login of the same host saved a real token meanwhile
        Check("forgetWhere: a real token saved for the host since is not an open key — nothing is removed, the token stays",
            !ServerConfigStore.ForgetWhere("open.example.com.", r => r.OpenKey, keys)
            && ServerConfigStore.LoadFor("open.example.com", keys) == real && ServerConfigStore.LoadAll(keys).Count == 2);
        Check("forgetWhere: Forget is every record of the host; the file goes with the last",
            ServerConfigStore.Forget("open.example.com", keys) && Hosts(ServerConfigStore.LoadAll(keys)) == "host-b.example.com"
            && ServerConfigStore.ForgetWhere("host-b.example.com", _ => true, keys) && !File.Exists(keys)
            && !ServerConfigStore.ForgetWhere("", _ => true, keys));

        // A blob this user cannot decrypt (here: protected with other entropy) — the tokens of every host it held.
        string blob = Path.Combine(dir, "blob.json");
        byte[] otherKey = ProtectedData.Protect(Encoding.UTF8.GetBytes("{\"servers\":[" + JsonSerializer.Serialize(a) + "," + JsonSerializer.Serialize(b) + "]}"),
            new byte[] { 1, 2, 3, 4 }, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(blob, otherKey);
        Check("unreadable: a DPAPI blob this user cannot decrypt reads as no record, and nothing is written",
            ServerConfigStore.LoadAll(blob).Count == 0 && File.ReadAllBytes(blob).SequenceEqual(otherKey)
            && ServerConfigStore.UnreadableCopies(blob).Count == 0);
        var c = Rec("host-c.example.com", "token-c");
        ServerConfigStore.Save(c, blob);
        var kept1 = ServerConfigStore.UnreadableCopies(blob);
        Check("unreadable: the Save that replaces it writes [cfg] and keeps the blob beside it as it was, under a name of its own (server.json.unreadable-<UTC time>) — still protected, no token in the clear",
            Hosts(ServerConfigStore.LoadAll(blob)) == "host-c.example.com" && kept1.Count == 1
            && Regex.IsMatch(Path.GetFileName(kept1[0]), @"^blob\.json\.unreadable-[0-9]{17}\z")
            && BytesOf(kept1[0]) is { } keptBlob && keptBlob.SequenceEqual(otherKey)
            && !Encoding.UTF8.GetString(keptBlob).Contains("token-a", StringComparison.Ordinal));

        // Every later unreadable file is kept as well, each under a name of its own, the oldest first; the same bytes met
        // again are not kept twice; a fourth copy makes the oldest go.
        byte[] second = ProtectedData.Protect(Encoding.UTF8.GetBytes("{\"servers\":[" + JsonSerializer.Serialize(c) + "]}"),
            new byte[] { 5, 6, 7 }, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(blob, second);
        ServerConfigStore.Save(a, blob);
        var kept2 = ServerConfigStore.UnreadableCopies(blob);
        Check("unreadable: a later unreadable file is kept too, under a name of its own — the first copy stays as it was, the oldest first",
            Hosts(ServerConfigStore.LoadAll(blob)) == "host-a.example.com" && kept2.Count == 2 && kept2[0] == kept1[0]
            && BytesOf(kept2[0]) is { } k20 && k20.SequenceEqual(otherKey) && BytesOf(kept2[1]) is { } k21 && k21.SequenceEqual(second));
        File.WriteAllBytes(blob, otherKey);
        ServerConfigStore.Save(b, blob);
        Check("unreadable: the same file met again (a save that failed after keeping it) is not kept twice",
            ServerConfigStore.UnreadableCopies(blob).SequenceEqual(kept2) && Hosts(ServerConfigStore.LoadAll(blob)) == "host-b.example.com");
        // A blob of this very user damaged in its body, and one that decrypts to something that is no list of records.
        byte[] damaged = Protect(Encoding.UTF8.GetBytes("{\"servers\":[" + JsonSerializer.Serialize(c) + "]}"));
        damaged[^5] ^= 0x5A;
        File.WriteAllBytes(blob, damaged);
        ServerConfigStore.Save(c, blob);
        var kept3 = ServerConfigStore.UnreadableCopies(blob);
        byte[] notJson = Protect(Encoding.UTF8.GetBytes("not a document"));
        File.WriteAllBytes(blob, notJson);
        ServerConfigStore.Save(a, blob);
        var kept4 = ServerConfigStore.UnreadableCopies(blob);
        Check($"unreadable: a damaged blob of this user and one that decrypts to no list are kept too; a fourth copy makes the oldest go — {ServerConfigStore.UnreadableCopiesKept} stay, in the order they were made",
            kept3.Count == 3 && kept3.Take(2).SequenceEqual(kept2) && BytesOf(kept3[2]) is { } k32 && k32.SequenceEqual(damaged)
            && kept4.Count == 3 && kept4.Take(2).SequenceEqual(kept3.Skip(1)) && !File.Exists(kept1[0])
            && BytesOf(kept4[2]) is { } k42 && k42.SequenceEqual(notJson)
            && kept4.Select(Path.GetFileName).SequenceEqual(kept4.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal))
            && Hosts(ServerConfigStore.LoadAll(blob)) == "host-a.example.com");
        Check("unreadable: a protected file is told by the first bytes every DPAPI blob starts with — this user's or another's, damaged or whole; never a file written in the clear, never the bare header",
            ServerConfigStore.IsProtectedBlob(Protect(new byte[] { 1 })) && ServerConfigStore.IsProtectedBlob(otherKey) && ServerConfigStore.IsProtectedBlob(damaged)
            && !ServerConfigStore.IsProtectedBlob(Encoding.UTF8.GetBytes("{\"servers\":[]}")) && !ServerConfigStore.IsProtectedBlob(otherKey[..20])
            && !ServerConfigStore.IsProtectedBlob(ReadOnlySpan<byte>.Empty));
        Check("unreadable: Delete removes the file and every kept copy",
            Do(() => ServerConfigStore.Delete(blob)) && !File.Exists(blob) && ServerConfigStore.UnreadableCopies(blob).Count == 0
            && Directory.GetFiles(dir, "blob.json*").Length == 0);
        // A write cut short leaves WriteRecords' own temporary file beside server.json — the records it was about to rename
        // over the file, tokens included: Delete takes it as well, with the file and without it.
        string cut = Path.Combine(dir, "cut.json");
        ServerConfigStore.Save(a, cut);
        File.WriteAllBytes(cut + ".tmp", Protect(Encoding.UTF8.GetBytes("{\"servers\":[" + JsonSerializer.Serialize(b) + "]}")));
        bool plantedBoth = File.Exists(cut) && File.Exists(cut + ".tmp");
        ServerConfigStore.Delete(cut);
        string cutAlone = Path.Combine(dir, "cut-alone.json");
        File.WriteAllBytes(cutAlone + ".tmp", Protect(Encoding.UTF8.GetBytes("{\"servers\":[" + JsonSerializer.Serialize(a) + "]}")));
        bool plantedAlone = !File.Exists(cutAlone) && File.Exists(cutAlone + ".tmp");
        ServerConfigStore.Delete(cutAlone);
        Check("delete: Delete also removes WriteRecords' temporary file a cut write left (server.json.tmp, the records it held) — beside the file, and when the file itself is gone",
            plantedBoth && !File.Exists(cut) && !File.Exists(cut + ".tmp") && plantedAlone && !File.Exists(cutAlone + ".tmp")
            && Directory.GetFiles(dir, "cut*").Length == 0);

        // A plaintext file cut short (written by hand, or by an earlier build) that holds a token in the clear: replaced
        // without a copy, so no file of the folder keeps that token readable outside DPAPI.
        string clearDir = Path.Combine(dir, "clear");
        Directory.CreateDirectory(clearDir);
        string clear = Path.Combine(clearDir, "server.json");
        const string clearToken = "clear-token-0123456789abcdef";
        File.WriteAllText(clear, "{\"Host\":\"cut.example.com\",\"AgentToken\":\"" + clearToken + "\",\"AgentPo");
        Check("unreadable: a plaintext file cut short reads as no record, and reading it writes nothing",
            ServerConfigStore.LoadAll(clear).Count == 0 && File.ReadAllText(clear).Contains(clearToken, StringComparison.Ordinal)
            && Directory.GetFiles(clearDir).Length == 1);
        ServerConfigStore.Save(c, clear);
        Check("unreadable: the Save that replaces a plaintext file keeps no copy of it — afterwards no file of the folder holds its token, and the file is protected [cfg]",
            ServerConfigStore.UnreadableCopies(clear).Count == 0 && IsProtected(clear) && Hosts(ServerConfigStore.LoadAll(clear)) == "host-c.example.com"
            && Directory.GetFiles(clearDir).All(f => !Encoding.UTF8.GetString(File.ReadAllBytes(f)).Contains(clearToken, StringComparison.Ordinal)));

        // Elements of the list that are no record: a write drops them (they are counted in its log line, never shown).
        string mixed = Path.Combine(dir, "mixed.json");
        File.WriteAllBytes(mixed, Protect(Encoding.UTF8.GetBytes(
            "{\"servers\":[1,{\"Host\":\"\"},{\"Host\":\"ok.example.com\",\"Port\":22,\"User\":\"root\",\"Password\":\"\",\"AgentToken\":\"t\",\"AgentPort\":18080,\"Mode\":\"direct\"},{\"Host\":\"bad.example.com\",\"Port\":\"x\"}]}")));
        ServerConfigStore.Save(a, mixed);
        using (var after = JsonDocument.Parse(Unprotected(mixed)))
            Check("records: a Save over a list with elements that are no record writes the readable records alone — [cfg, ok]",
                after.RootElement.GetProperty("servers").GetArrayLength() == 2 && Hosts(ServerConfigStore.LoadAll(mixed)) == "host-a.example.com,ok.example.com");
    }

    // ── B. the state forgets a host ──
    static RelicState Populated()
    {
        var st = new RelicState();
        st.RememberAccount("Host.Example.com", 21000, "2.8", "alice", "g1");
        st.RememberAccount("host.example.com", 22000, "1.6", "bob", "g1");
        st.RememberAccount("other.example.com", 21000, "2.8", "carol", "g1");
        st.RememberAccount("2001:db8::1", 21000, "2.8", "dave", "g1");
        st.RememberServerAccount("Host.Example.com", 21000, "2.8", "premade");
        st.RememberServerAccount("host.example.com", 22000, "1.6", "");
        st.RememberServerAccount("other.example.com", 21000, "2.8", "premade");
        st.RememberServerAccount("2001:db8::1", 21000, "2.8", "premade");
        st.PinHostKey("host.example.com:22", "ssh-ed25519", "SHA256:" + new string('A', 43));
        st.PinHostKey("host.example.com:2222", "ssh-rsa", "SHA256:" + new string('B', 43));
        st.PinHostKey("other.example.com:22", "ssh-ed25519", "SHA256:" + new string('C', 43));
        st.PinHostKey("2001:db8::1:22", "ssh-ed25519", "SHA256:" + new string('D', 43));
        foreach (var (addr, mode) in new[] { ("host.example.com:21000", "admin"), ("other.example.com", "player"), ("[2001:db8::1]:21000", "player") })
            st.SaveServer(SavedServer.Create(addr, mode, "", 0, out _)!);
        return st;
    }

    static bool HasHostKey<T>(Dictionary<string, T> map, string host) =>
        map.Keys.Any(k => ServerConfig.SameHost(RelicState.HostPartOf(k), host));

    static void StateForget(Action<string, bool> Check)
    {
        var st = Populated();
        Check("state: the fixture holds the host in all four maps and the saved list (two ports, two letter cases)",
            st.Accounts.ContainsKey("Host.Example.com:21000") && st.AccountLists.ContainsKey("host.example.com:22000")
            && st.ServerAccounts.ContainsKey("host.example.com:22000") && st.SshHostKeys.ContainsKey("host.example.com:2222")
            && st.SavedServers.Count == 3);
        bool forgot = st.ForgetServer("HOST.example.com.");
        Check("state: ForgetServer removes Host.Example.com:21000 and host.example.com:22000 from Accounts, AccountLists, ServerAccounts, every pin of the host and its saved entry",
            forgot && !HasHostKey(st.Accounts, "host.example.com") && !HasHostKey(st.AccountLists, "host.example.com")
            && !HasHostKey(st.ServerAccounts, "host.example.com") && !HasHostKey(st.SshHostKeys, "host.example.com")
            && !st.SavedServers.Any(s => ServerConfig.SameHost(s.Host, "host.example.com")));
        Check("state: another host's keys and entries stay",
            st.Accounts.ContainsKey("other.example.com:21000") && st.AccountLists.ContainsKey("other.example.com:21000")
            && st.ServerAccounts.ContainsKey("other.example.com:21000") && st.SshHostKeys.ContainsKey("other.example.com:22")
            && st.SavedServers.Any(s => s.Host == "other.example.com")
            && st.AccountFor("other.example.com", 21000, "2.8")?.Name == "carol");
        Check("state: ForgetServer of a host it never knew answers false and changes nothing",
            !st.ForgetServer("never.example.com") && !st.ForgetServer("") && st.Accounts.Count == 2 && st.SavedServers.Count == 2);
        Check("state: a key's host is the text before its LAST colon — 2001:db8::1:21000 is the host 2001:db8::1",
            RelicState.HostPartOf("2001:db8::1:21000") == "2001:db8::1" && RelicState.HostPartOf("host.example.com:21000") == "host.example.com"
            && RelicState.HostPartOf("nocolon") == "nocolon");
        bool forgotV6 = st.ForgetServer("2001:db8::1");
        Check("state: ForgetServer of the IPv6 host removes its logins, its answer, its pin and its entry — and the other host still stays",
            forgotV6 && !st.Accounts.ContainsKey("2001:db8::1:21000") && !st.ServerAccounts.ContainsKey("2001:db8::1:21000")
            && !st.SshHostKeys.ContainsKey("2001:db8::1:22") && st.SavedServers.Count == 1
            && st.Accounts.ContainsKey("other.example.com:21000") && st.SshHostKeys.ContainsKey("other.example.com:22"));
        st.Settings.ServerHost = "other.example.com";
        string idBefore = st.ClientIdFor("x.example.com");
        string? secretBefore = st.ClientSecret;
        bool forgotConfigured = st.ForgetServer("other.example.com.");
        Check("state: ForgetServer of the configured host leaves the configured host and the ClientSecret alone",
            forgotConfigured && st.Settings.ServerHost == "other.example.com" && secretBefore is { Length: 64 }
            && st.ClientSecret == secretBefore && st.ClientIdFor("x.example.com") == idBefore && st.SavedServers.Count == 0);

        // An IPv6 host written in brackets — by the caller, or in a key — is that host.
        var v6 = new RelicState();
        v6.RememberAccount("[2001:db8::1]", 21000, "2.8", "eve", "g1");
        v6.RememberAccount("2001:db8::1", 22000, "1.6", "fred", "g1");
        v6.RememberServerAccount("[2001:db8::1]", 21000, "2.8", "premade");
        v6.PinHostKey("[2001:db8::1]:22", "ssh-ed25519", "SHA256:" + new string('G', 43));
        v6.RememberAccount("2001:db8::2", 21000, "2.8", "gina", "g1");
        bool forgotBracketed = v6.ForgetServer("[2001:DB8::1]");
        Check("state: ForgetServer(\"[2001:db8::1]\") forgets the host's keys written with and without brackets — and not 2001:db8::2",
            forgotBracketed && !HasHostKey(v6.Accounts, "2001:db8::1") && !v6.Accounts.ContainsKey("[2001:db8::1]:21000")
            && !v6.AccountLists.ContainsKey("2001:db8::1:22000") && v6.ServerAccounts.Count == 0 && v6.SshHostKeys.Count == 0
            && v6.Accounts.ContainsKey("2001:db8::2:21000") && !v6.ForgetServer("[]") && !v6.ForgetServer("[  ]"));
    }

    // ── C. the saved list ──
    static void SavedList(Action<string, bool> Check)
    {
        var made = SavedServer.Create("  http://Game.Example.com:21000/path?x ", "Admin", "  My server  ", 18085, out var p0);
        Check("saved: the address is kept as typed, trimmed, without the scheme and the path; the host is parsed without DNS; the mode is lower-cased; the name trimmed",
            p0 == SavedServerProblem.None && made is { Address: "Game.Example.com:21000", Host: "Game.Example.com", Mode: "admin", Label: "My server", AgentPort: 18085 }
            && DateTimeOffset.TryParse(made.SavedAt, out _) && made.Id == "");
        Check("saved: [v6]:port and a bare IPv6 address keep their form; a host alone has no port",
            SavedServer.Create("[2001:db8::1]:21000", "player", "", 0, out _)?.Address == "[2001:db8::1]:21000"
            && SavedServer.Create("[2001:db8::1]:21000", "player", "", 0, out _)?.Host == "2001:db8::1"
            && SavedServer.Create("2001:db8::1", "player", "", 0, out _)?.Address == "2001:db8::1"
            && SavedServer.Create("192.0.2.10", "player", "", 0, out _)?.Address == "192.0.2.10");
        Check("saved: an address the parser refuses is BadAddress (empty, a bad port, spaces)",
            new[] { "", "   ", "host.example.com:99999", "a b.example.com", "[::1" }.All(a => SavedServer.Create(a, "player", "", 0, out var p) is null && p == SavedServerProblem.BadAddress));
        Check("saved: a mode other than player / admin is BadMode",
            new[] { "", "root", "players", null }.All(m => SavedServer.Create("game.example.com", m, "", 0, out var p) is null && p == SavedServerProblem.BadMode));
        Check("saved: a name of 40 characters after trimming is taken; 41, or one with a control character (a line break), is BadLabel",
            SavedServer.Create("game.example.com", "player", "  " + new string('n', 40) + "  ", 0, out _) is { Label.Length: 40 }
            && SavedServer.Create("game.example.com", "player", new string('n', 41), 0, out var p41) is null && p41 == SavedServerProblem.BadLabel
            && SavedServer.Create("game.example.com", "player", "two\nlines", 0, out var pnl) is null && pnl == SavedServerProblem.BadLabel
            && SavedServer.Create("game.example.com", "player", "tab\there", 0, out _) is null);
        Check("saved: an agent port outside 1–65535 is 0 (inferred)",
            SavedServer.Create("game.example.com", "player", "", 0, out _)?.AgentPort == 0
            && SavedServer.Create("game.example.com", "player", "", 70000, out _)?.AgentPort == 0
            && SavedServer.Create("game.example.com", "player", "", -1, out _)?.AgentPort == 0
            && SavedServer.Create("game.example.com", "player", "", 65535, out _)?.AgentPort == 65535);

        var st = new RelicState();
        var first = SavedServer.Create("game.example.com", "player", "First", 0, out _)!;
        Check("saved: SaveServer adds the entry and gives it an id of 8 hex characters",
            st.SaveServer(first) == SavedServerProblem.None && st.SavedServers is [{ Label: "First" } e1] && System.Text.RegularExpressions.Regex.IsMatch(e1.Id, "^[0-9a-f]{8}$"));
        string id1 = st.SavedServers[0].Id;
        st.SaveServer(SavedServer.Create("other.example.com", "admin", "", 0, out _)!);
        Thread.Sleep(5);
        var again = SavedServer.Create("GAME.example.com.", "admin", "Again", 18090, out _)!;
        Check("saved: saving the host again (another letter case, a trailing dot) replaces its entry, keeps its id and puts it first",
            st.SaveServer(again) == SavedServerProblem.None && st.SavedServers.Count == 2
            && st.SavedServers[0] is { Label: "Again", Mode: "admin", AgentPort: 18090 } e2 && e2.Id == id1
            && st.SavedServers.Count(s => ServerConfig.SameHost(s.Host, "game.example.com")) == 1);
        Check("saved: SavedServerList is newest first (by SavedAt), copies; SavedServerById finds an entry (a copy) and null for another id",
            st.SavedServerList() is [{ Label: "Again" }, { Host: "other.example.com" }]
            && st.SavedServerById(id1) is { Label: "Again" } byId && !ReferenceEquals(byId, st.SavedServers[0])
            && st.SavedServerById("00000000") is null && st.SavedServerById(null) is null);
        var full = new RelicState();
        for (int i = 0; i < SavedServer.MaxEntries; i++)
            full.SaveServer(SavedServer.Create($"h{i}.example.com", "player", "", 0, out _)!);
        Check("saved: the list holds at most 50 entries — a 51st host is Full and nothing changes",
            full.SavedServers.Count == 50 && full.SaveServer(SavedServer.Create("h50.example.com", "player", "", 0, out _)!) == SavedServerProblem.Full
            && full.SavedServers.Count == 50 && !full.SavedServers.Any(s => s.Host == "h50.example.com"));
        Check("saved: at 50, saving a host already in the list again is no new entry and is taken",
            full.SaveServer(SavedServer.Create("h7.example.com", "admin", "Seven", 0, out _)!) == SavedServerProblem.None
            && full.SavedServers.Count == 50 && full.SavedServers[0] is { Host: "h7.example.com", Label: "Seven" });
        Check("saved: the ids of the list are distinct", full.SavedServers.Select(s => s.Id).Distinct().Count() == 50);
        Check("saved: an entry that names no host is BadAddress and nothing changes",
            st.SaveServer(new SavedServer { Address = "x", Host = "  ", Mode = "player" }) == SavedServerProblem.BadAddress && st.SavedServers.Count == 2);
    }

    // ── D. the host-key pins ──
    static void Pins(Action<string, bool> Check)
    {
        var st = new RelicState();
        string fpA = "SHA256:" + new string('A', 43), fpB = "SHA256:" + new string('B', 43);
        Check("pins: no pin → an empty list (a copy)", st.HostKeyPinsFor("box.example.com:22").Count == 0);
        Check("pins: PinHostKey records the key; pinning the same key again changes nothing (false)",
            st.PinHostKey("box.example.com:22", "ssh-ed25519", fpA) && !st.PinHostKey("box.example.com:22", "ssh-ed25519", fpA)
            && st.HostKeyPinsFor("box.example.com:22") is [{ Type: "ssh-ed25519" } p] && p.Fingerprint == fpA);
        Check("pins: a confirmed other key takes the place of the earlier pin (the old one is no longer trusted)",
            st.PinHostKey("box.example.com:22", "ssh-rsa", fpB) && st.HostKeyPinsFor("box.example.com:22") is [{ Type: "ssh-rsa" } q] && q.Fingerprint == fpB);
        Check("pins: another port of the host is another address", st.HostKeyPinsFor("box.example.com:2222").Count == 0);
        Check("pins: nothing is pinned for an empty key or fingerprint",
            !st.PinHostKey("", "ssh-ed25519", fpA) && !st.PinHostKey("box.example.com:22", "ssh-ed25519", "  ") && st.SshHostKeys.Count == 1);
        var copy = st.HostKeyPinsFor("box.example.com:22");
        copy[0].Fingerprint = "changed";
        Check("pins: HostKeyPinsFor hands out copies", st.HostKeyPinsFor("box.example.com:22")[0].Fingerprint == fpB);

        // One spelling per address, whatever a caller writes: no pin is missed, doubled or left behind by another spelling.
        Check("pins: the key's spelling — the host trimmed, without brackets, without its trailing dots, lower-cased; the port 1–65535",
            RelicState.PinKeyFor(" Box.Example.com. ", 22) == "box.example.com:22" && RelicState.PinKeyFor("[2001:DB8::1]", 22) == "2001:db8::1:22"
            && RelicState.PinKeyFor("192.0.2.10", 2222) == "192.0.2.10:2222" && RelicState.PinKeyFor("", 22) == ""
            && RelicState.PinKeyFor("box.example.com", 0) == "" && RelicState.PinKeyFor("box.example.com", 65536) == "" && RelicState.PinKeyFor(null, 22) == "");
        Check("pins: a key written \"<host>:<port>\" is read by its LAST colon, a bracketed IPv6 host included; one without a host or a port is none",
            RelicState.PinKey("Box.Example.com.:22") == "box.example.com:22" && RelicState.PinKey("[2001:db8::1]:22") == "2001:db8::1:22"
            && RelicState.PinKey("2001:db8::1:22") == "2001:db8::1:22" && RelicState.PinKey("box.example.com") == ""
            && RelicState.PinKey("box.example.com:") == "" && RelicState.PinKey(":22") == "" && RelicState.PinKey("box:x") == ""
            && RelicState.PinKey("box: 22") == "" && RelicState.PinKey(null) == "");
        Check("pins: HostKeyPins.Key's form reads in the store's own spelling",
            new[] { ("Box.Example.com.", 22), ("2001:db8::1", 22), ("[2001:db8::1]", 2222), ("192.0.2.10", 22) }
                .All(hp => RelicState.PinKey(HostKeyPins.Key(hp.Item1, hp.Item2)) == RelicState.PinKeyFor(hp.Item1, hp.Item2)));
        var sp = new RelicState();
        Check("pins: a pin under \"Box.Example.com.:22\" is found by HostKeyPins.Key(\"box.example.com\", 22), by host and port, and under a trailing dot",
            sp.PinHostKey("Box.Example.com.:22", "ssh-ed25519", fpA)
            && sp.HostKeyPinsFor(HostKeyPins.Key("box.example.com", 22)) is [{ } f1] && f1.Fingerprint == fpA
            && sp.HostKeyPinsFor("box.example.com", 22).Count == 1 && sp.HostKeyPinsFor("box.example.com.:22").Count == 1);
        Check("pins: the same key pinned under another spelling is the same pin (false, still one address); another key replaces it there",
            !sp.PinHostKey("box.example.com.:22", "ssh-ed25519", fpA) && !sp.PinHostKey("BOX.example.com", 22, "ssh-ed25519", fpA)
            && sp.SshHostKeys.Count == 1 && sp.SshHostKeys.ContainsKey("box.example.com:22")
            && sp.PinHostKey("box.example.com", 22, "ssh-rsa", fpB) && sp.HostKeyPinsFor("Box.Example.com.:22") is [{ Type: "ssh-rsa" }] && sp.SshHostKeys.Count == 1);
        Check("pins: an IPv6 box pinned as \"[2001:db8::1]:22\" is found as 2001:db8::1 port 22, and forgotten by either spelling of its host",
            sp.PinHostKey("[2001:db8::1]:22", "ssh-ed25519", fpA) && sp.HostKeyPinsFor("2001:db8::1", 22).Count == 1
            && sp.HostKeyPinsFor(HostKeyPins.Key("2001:db8::1", 22)).Count == 1 && sp.HostKeyPinsFor("[2001:DB8::1]", 22).Count == 1
            && sp.ForgetServer("2001:db8::1") && sp.HostKeyPinsFor("2001:db8::1", 22).Count == 0 && sp.SshHostKeys.Count == 1);
        Check("pins: a key without a port, or with port 0, pins nothing",
            !sp.PinHostKey("box2.example.com", "ssh-ed25519", fpA) && !sp.PinHostKey("box2.example.com", 0, "ssh-ed25519", fpA)
            && sp.HostKeyPinsFor("box2.example.com", 22).Count == 0 && sp.SshHostKeys.Count == 1);

        // An IP literal is kept in its canonical form — the address a connection to it reaches: every spelling of one IPv6
        // box (its zeros written out or left out, capitals, brackets, blanks) and an IPv4 address written with leading zeros
        // (read as the address it is: 192.0.2.010 is 192.0.2.8) share ONE pin slot, so no other spelling of a pinned box
        // meets it at first sight — where a check without a confirmed key (the install form, the update) would take any key.
        string[] v6 = { "2001:db8::1", "2001:0db8:0:0:0:0:0:1", "[2001:DB8::1]", "2001:db8:0::1", " 2001:DB8:0000::0001 " };
        Check("pins: one IPv6 box written five ways (zeros written out or left out, capitals, brackets, blanks) is ONE key, 2001:db8::1:22 — the store's spelling and HostKeyPins.Key's alike",
            v6.All(h => RelicState.PinKeyFor(h, 22) == "2001:db8::1:22" && HostKeyPins.Key(h, 22) == "2001:db8::1:22"
                        && RelicState.PinKey(HostKeyPins.Key(h, 22)) == RelicState.PinKeyFor(h, 22)));
        Check("pins: an IPv4 address written with leading zeros is the address a connection reaches (192.0.2.010 is 192.0.2.8) — one key with 192.0.2.8, the store's and HostKeyPins.Key's alike; a host that is no IP literal stays as written, lower-cased",
            RelicState.PinKeyFor("192.0.2.010", 22) == "192.0.2.8:22" && HostKeyPins.Key("192.0.2.010", 22) == "192.0.2.8:22"
            && RelicState.PinKeyFor("192.0.2.8", 22) == "192.0.2.8:22" && RelicState.PinKey("192.0.2.010:22") == "192.0.2.8:22"
            && RelicState.PinKeyFor("Box-1.Example.com", 22) == "box-1.example.com:22" && HostKeyPins.Key("Box-1.Example.com", 22) == "box-1.example.com:22");
        var canon = new RelicState();
        bool pinnedOnce = canon.PinHostKey("2001:0db8:0:0:0:0:0:1", 22, "ssh-ed25519", fpA);
        Check("pins: a box pinned under one spelling has its pin under every other — no second slot, no key at first sight there — and the same key pinned again under another spelling changes nothing",
            pinnedOnce && v6.All(h => canon.HostKeyPinsFor(h, 22) is [{ } pin] && pin.Fingerprint == fpA)
            && canon.HostKeyPinsFor("[2001:db8::1]:22").Count == 1 && canon.SshHostKeys.Keys.SequenceEqual(new[] { "2001:db8::1:22" })
            && !canon.PinHostKey("[2001:DB8::1]", 22, "ssh-ed25519", fpA) && canon.SshHostKeys.Count == 1
            && HostKeyPins.Decide(canon.HostKeyPinsFor("2001:db8:0::1", 22).Select(p => new HostKeyPin(p.Type, p.Fingerprint)).ToList(), fpB) == HostKeyVerdict.Changed);
        bool pinnedV4 = canon.PinHostKey("192.0.2.010", 22, "ssh-rsa", fpB);
        canon.PinHostKey("[2001:db8::1]:2222", "ssh-ed25519", fpB);
        canon.PinHostKey("2001:db8::2", 22, "ssh-ed25519", fpB);
        Check("pins: the leading-zero IPv4 box is pinned as 192.0.2.8; forgetting a box by another spelling of it (2001:0DB8::1, 192.0.2.010) drops its pins on every port and keeps another host's",
            pinnedV4 && canon.HostKeyPinsFor("192.0.2.8", 22) is [{ Type: "ssh-rsa" }] && canon.SshHostKeys.Count == 4
            && canon.ForgetServer("2001:0DB8::1") && canon.HostKeyPinsFor("2001:db8::1", 22).Count == 0 && canon.HostKeyPinsFor("2001:db8::1", 2222).Count == 0
            && canon.HostKeyPinsFor("2001:db8::2", 22).Count == 1
            && canon.ForgetServer("192.0.2.010") && canon.HostKeyPinsFor("192.0.2.8", 22).Count == 0
            && canon.SshHostKeys.Keys.SequenceEqual(new[] { "2001:db8::2:22" }));
    }

    // ── E. the one lock on the state ──
    static void StateGate(string tmp, Action<string, bool> Check)
    {
        string file = Path.Combine(tmp, "gate", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var st = new RelicState();
        Exception? fault = null;
        Thread Worker(int t) => new(() =>
        {
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    lock (RelicState.Gate)
                    {
                        st.Installed.Add(new InstalledVersion { Id = $"{t}.{i}", GameDir = $@"C:\Games\Genshin {t}.{i}", ProfileId = $"p{t}{i}" });
                        if (st.Installed.Count > 30) st.Installed.RemoveAt(0);
                        st.RememberAccount("host.example.com", 21000, $"{t}.0", $"acct{t}x{i}", "g");
                        st.Settings.SetOwnFilesChoice($"{t}.{i % 5}", i % 2 == 0 ? OwnFileChoices.Keep : OwnFileChoices.Replace);
                        st.Save(file);
                    }
                }
            }
            catch (Exception ex) { fault ??= ex; }
        });
        var a = Worker(1);
        var b = Worker(2);
        a.Start(); b.Start(); a.Join(); b.Join();
        RelicState? read = null;
        bool parses = !Throws(() => read = JsonSerializer.Deserialize<RelicState>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
        Check("gate: two threads, 200 rounds each, change Installed / Accounts / Settings.OwnFiles under the Gate and save to one file — no exception, and the file parses"
              + (fault is null ? "" : $" — {fault.GetType().Name}: {fault.Message}"),
            fault is null && parses && read is { Installed.Count: 30 } && read.AccountListFor("host.example.com", 21000, "1.0").Count == 200
            && read.AccountListFor("host.example.com", 21000, "2.0").Count == 200);

        // Save takes the Gate by itself: saves made without holding it while another thread changes the state under it.
        // The changes add and remove dictionary keys — a dictionary's enumeration notices a change made under it, which a
        // list's does not (the serializer walks a list by index) — so a Save that did not take the Gate would fault here.
        var st2 = new RelicState();
        for (int k = 0; k < 300; k++) st2.RememberServerAccount($"seed{k}.example.com", 21000, "2.8", "premade");
        Exception? fault2 = null;
        bool stop = false;
        var changer = new Thread(() =>
        {
            try
            {
                int i = 0;
                while (!Volatile.Read(ref stop))
                {
                    lock (RelicState.Gate)
                    {
                        st2.Installed.Add(new InstalledVersion { Id = $"x{i}", GameDir = $@"C:\Games\x{i}" });
                        if (st2.Installed.Count > 50) st2.Installed.RemoveRange(0, 25);
                        st2.RememberAccount($"h{i}.example.com", 21000, $"2.{i % 7}", $"n{i}", null);
                        st2.RememberServerAccount($"h{i}.example.com", 21000, "2.8", $"p{i}");
                        st2.PinHostKey($"h{i}.example.com:22", "ssh-ed25519", "SHA256:" + new string('K', 43));
                        if (i >= 100) st2.ForgetServer($"h{i - 100}.example.com");
                    }
                    i++;
                }
            }
            catch (Exception ex) { fault2 ??= ex; }
        });
        string file2 = Path.Combine(tmp, "gate", "state2.json");
        changer.Start();
        try
        {
            for (int i = 0; i < 200; i++) st2.Save(file2);
        }
        catch (Exception ex) { fault2 ??= ex; }
        Volatile.Write(ref stop, true);
        changer.Join();
        Check("gate: Save serialises under the Gate by itself — 200 saves without holding it while another thread changes the lists under it never tear"
              + (fault2 is null ? "" : $" — {fault2.GetType().Name}: {fault2.Message}"),
            fault2 is null && !Throws(() => JsonDocument.Parse(File.ReadAllText(file2)).Dispose()));

        // The state's own methods are atomic: concurrent RememberAccount / ForgetServer / SaveServer / PinHostKey.
        var st3 = new RelicState();
        Exception? fault3 = null;
        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    st3.RememberAccount($"h{t}.example.com", 21000, "2.8", $"n{i}", null);
                    st3.SaveServer(SavedServer.Create($"h{t}.example.com", "player", $"L{i}", 0, out _)!);
                    st3.PinHostKey($"h{t}.example.com:22", "ssh-ed25519", "SHA256:" + new string((char)('A' + i % 20), 43));
                    if (i % 50 == 49) st3.ForgetServer($"h{t}.example.com");
                    _ = st3.SavedServerList();
                }
            }
            catch (Exception ex) { fault3 ??= ex; }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Check("gate: the state's own methods take the Gate — four threads remembering, saving, pinning and forgetting at once never fault"
              + (fault3 is null ? "" : $" — {fault3.GetType().Name}: {fault3.Message}"),
            fault3 is null && st3.SavedServers.Count == 0 && st3.SshHostKeys.Count == 0 && st3.Accounts.Count == 0);
    }

    // ── F. the new fields round-trip ──
    static void StateRoundTrip(string tmp, Action<string, bool> Check)
    {
        string file = Path.Combine(tmp, "roundtrip", "state.json");
        var st = Populated();
        st.Save(file);
        var back = RelicState.Load(file);
        Check("round trip: SavedServers come back as saved (ids, addresses, hosts, modes, names, ports, times)",
            back.SavedServers.Count == 3
            && back.SavedServers.Zip(st.SavedServers).All(z => z.First.Id == z.Second.Id && z.First.Address == z.Second.Address
                && z.First.Host == z.Second.Host && z.First.Mode == z.Second.Mode && z.First.Label == z.Second.Label
                && z.First.AgentPort == z.Second.AgentPort && z.First.SavedAt == z.Second.SavedAt));
        Check("round trip: SshHostKeys come back, compared without letter case",
            back.SshHostKeys.Count == 4 && back.HostKeyPinsFor("HOST.example.com:22") is [{ Type: "ssh-ed25519" } p] && p.Fingerprint == "SHA256:" + new string('A', 43));
        Check("round trip: the state file names the fields SavedServers and SshHostKeys",
            File.ReadAllText(file) is var text && text.Contains("\"SavedServers\"", StringComparison.Ordinal) && text.Contains("\"SshHostKeys\"", StringComparison.Ordinal));

        string old = Path.Combine(tmp, "roundtrip", "old.json");
        File.WriteAllText(old, "{\"Settings\":{\"ServerHost\":\"game.example.com\"},\"Installed\":[],\"Accounts\":{}}");
        var fromOld = RelicState.Load(old);
        Check("round trip: a state file without the fields reads as an empty saved list and no pins",
            fromOld.Settings.ServerHost == "game.example.com" && fromOld.SavedServers.Count == 0 && fromOld.SshHostKeys.Count == 0
            && fromOld.SavedServerList().Count == 0 && fromOld.HostKeyPinsFor("x:22").Count == 0);
        string nulls = Path.Combine(tmp, "roundtrip", "nulls.json");
        File.WriteAllText(nulls, "{\"SavedServers\":null,\"SshHostKeys\":null}");
        var fromNulls = RelicState.Load(nulls);
        Check("round trip: null fields read as empty (never a null list)",
            fromNulls.SavedServers is { Count: 0 } && fromNulls.SshHostKeys is { Count: 0 });
        string odd = Path.Combine(tmp, "roundtrip", "odd.json");
        File.WriteAllText(odd, "{\"SavedServers\":[null,{\"Id\":\"0123abcd\",\"Host\":\"game.example.com\",\"Address\":\"game.example.com\",\"Mode\":\"player\"}],"
                               + "\"SshHostKeys\":{\"Box.example.com:22\":[{\"Type\":\"ssh-ed25519\",\"Fingerprint\":\"SHA256:" + new string('E', 43) + "\"}],"
                               + "\"box.example.com:22\":[{\"Type\":\"ssh-rsa\",\"Fingerprint\":\"SHA256:" + new string('F', 43) + "\"}],"
                               + "\"empty.example.com:22\":[{\"Type\":\"x\",\"Fingerprint\":\"\"}],\"nolist.example.com:22\":null}}");
        var fromOdd = RelicState.Load(odd);
        Check("round trip: a null saved entry is dropped; pins keyed alike but for letter case keep the first; a pin without a fingerprint and a null list are dropped",
            fromOdd.SavedServers.Count == 1 && fromOdd.SshHostKeys.Count == 1
            && fromOdd.HostKeyPinsFor("box.example.com:22") is [{ Type: "ssh-ed25519" }]);
        string spelled = Path.Combine(tmp, "roundtrip", "spelled.json");
        File.WriteAllText(spelled, "{\"SshHostKeys\":{\"Box.Example.com.:22\":[{\"Type\":\"ssh-ed25519\",\"Fingerprint\":\"SHA256:" + new string('H', 43) + "\"}],"
                                   + "\"box.example.com:22\":[{\"Type\":\"ssh-rsa\",\"Fingerprint\":\"SHA256:" + new string('I', 43) + "\"}],"
                                   + "\"[2001:DB8::1]:2222\":[{\"Type\":\"ssh-ed25519\",\"Fingerprint\":\"SHA256:" + new string('J', 43) + "\"}],"
                                   + "\"noport.example.com\":[{\"Type\":\"ssh-ed25519\",\"Fingerprint\":\"SHA256:" + new string('K', 43) + "\"}]}}");
        var fromSpelled = RelicState.Load(spelled);
        Check("round trip: a state file's pin keys are read in the store's own spelling — two spellings of one address keep the first, a key without a port is dropped",
            fromSpelled.SshHostKeys.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(new[] { "2001:db8::1:2222", "box.example.com:22" })
            && fromSpelled.HostKeyPinsFor("box.example.com", 22) is [{ Type: "ssh-ed25519" }]
            && fromSpelled.HostKeyPinsFor("2001:db8::1", 2222).Count == 1);
        // An IP literal a state file spells another way (an older file, a hand edit) is read in its canonical form too.
        string literal = Path.Combine(tmp, "roundtrip", "literal.json");
        File.WriteAllText(literal, "{\"SshHostKeys\":{\"2001:0DB8:0:0:0:0:0:1:22\":[{\"Type\":\"ssh-ed25519\",\"Fingerprint\":\"SHA256:" + new string('L', 43) + "\"}],"
                                   + "\"2001:db8::1:22\":[{\"Type\":\"ssh-rsa\",\"Fingerprint\":\"SHA256:" + new string('M', 43) + "\"}],"
                                   + "\"192.0.2.010:2222\":[{\"Type\":\"ssh-ed25519\",\"Fingerprint\":\"SHA256:" + new string('N', 43) + "\"}]}}");
        var fromLiteral = RelicState.Load(literal);
        Check("round trip: a state file's IP literals are read in their canonical form — 2001:0DB8:0:0:0:0:0:1 and 2001:db8::1 are one address (the first kept), 192.0.2.010 is 192.0.2.8",
            fromLiteral.SshHostKeys.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(new[] { "192.0.2.8:2222", "2001:db8::1:22" })
            && fromLiteral.HostKeyPinsFor("2001:db8::1", 22) is [{ Type: "ssh-ed25519" }]
            && fromLiteral.HostKeyPinsFor("192.0.2.8", 2222).Count == 1);
    }

    // ── F2. a failed registration is taken back, and nothing else ──
    static void Registration(Action<string, bool> Check)
    {
        InstalledVersion Inst(string id, string dir) => new() { Id = id, GameDir = dir, ProfileId = "p" + id };
        var a = Inst("1.6", @"C:\Games\Genshin 1.6");
        var x = Inst("2.8", @"C:\Games\Genshin 2.8");
        var b = Inst("3.2", @"C:\Games\Genshin 3.2");

        // The import of 2.8 at another folder registered it (its entry out, a new one at the end, the selection on it)
        // and then failed; meanwhile another part of the launcher registered 9.9.
        var st = new RelicState();
        st.Installed.AddRange(new[] { a, x, b });
        st.SelectedVersionId = "1.6";
        var before = st.Installed.ToList();
        string? selectedBefore = st.SelectedVersionId;
        st.Installed.RemoveAll(i => i.Id == "2.8");
        st.Installed.Add(Inst("2.8", @"D:\Copy\Genshin 2.8"));
        st.SelectedVersionId = "2.8";
        var other = Inst("9.9", @"C:\Games\Genshin 9.9");
        st.Installed.Add(other);
        st.RestoreRegistration("2.8", before, selectedBefore);
        Check("registration: the failed registration's version gets its entry back, at its place — the entry another part made meanwhile stays",
            st.Installed.Count == 4 && ReferenceEquals(st.Installed[0], a) && ReferenceEquals(st.Installed[1], x)
            && ReferenceEquals(st.Installed[2], b) && ReferenceEquals(st.Installed[3], other) && st.SelectedVersionId == "1.6");

        var fresh = new RelicState();
        fresh.Installed.Add(a);
        fresh.SelectedVersionId = "1.6";
        var before2 = fresh.Installed.ToList();
        fresh.Installed.Add(Inst("2.8", @"D:\Copy\Genshin 2.8"));
        fresh.SelectedVersionId = "2.8";
        fresh.RestoreRegistration("2.8", before2, "1.6");
        Check("registration: a version that had no entry has none again, and the selection it took goes back",
            fresh.Installed.Count == 1 && ReferenceEquals(fresh.Installed[0], a) && fresh.SelectedVersionId == "1.6");

        var picked = new RelicState();
        picked.Installed.AddRange(new[] { a, b });
        var before3 = picked.Installed.ToList();
        picked.Installed.Add(Inst("2.8", @"D:\Copy\Genshin 2.8"));
        picked.SelectedVersionId = "3.2"; // the player picked another version since
        picked.RestoreRegistration("2.8", before3, "1.6");
        Check("registration: a selection made since (another version) stays as it is",
            picked.Installed.Count == 2 && picked.SelectedVersionId == "3.2");
    }

    // ── F3. the uninstall names every firewall rule it leaves, and what a server set up on this PC leaves with its user ──
    static void UninstallReport(string tmp, Action<string, bool> Check)
    {
        var names = LocalAgent.FirewallRuleNames(18080);
        var lines = Uninstaller.KeptFirewallLines(18080, names);
        Check("uninstall: the agent's rule and the game ports' two rules left are three lines — the agent's in its own words, the others by their names",
            names.Count == 3 && lines.Count == 3
            && lines[0] == L.T("core.uninstall.kept.firewallRule", new { port = 18080 })
            && lines[1] == L.T("core.uninstall.kept.serverRule", new { rule = names[1] })
            && lines[2] == L.T("core.uninstall.kept.serverRule", new { rule = names[2] }));
        Check("uninstall: each line names its rule (the port of the agent's, the name of the others) once its text is known",
            lines[0].Contains("18080", StringComparison.Ordinal)
            && (!L.Has("core.uninstall.kept.serverRule") || (lines[1].Contains(names[1], StringComparison.Ordinal) && lines[2].Contains(names[2], StringComparison.Ordinal))));
        Check("uninstall: a rule named twice (letter case, spaces), an empty name and no list are no line; an agent rule of another port is named as itself",
            Uninstaller.KeptFirewallLines(18080, new[] { names[1], " " + names[1].ToUpperInvariant() + " ", "", null }).Count == 1
            && Uninstaller.KeptFirewallLines(18080, null).Count == 0 && Uninstaller.KeptFirewallLines(18080, Array.Empty<string>()).Count == 0
            && Uninstaller.KeptFirewallLines(18080, new[] { LocalAgent.FirewallRuleName(18081) }) is [var otherPort]
            && otherPort == L.T("core.uninstall.kept.serverRule", new { rule = LocalAgent.FirewallRuleName(18081) }));

        // Docker Desktop and WSL's memory limit in .wslconfig stay with the user — other programs may rely on either — and the
        // report names each that is there (Uninstaller.KeptSetupLines, pure; the real PC's Docker and .wslconfig are never
        // looked at here: stand-in values, and a .wslconfig of a temp folder through WslConfig.PathOverride).
        const string dockerDir = @"C:\Users\me\AppData\Local\Programs\DockerDesktop";
        const string wslPath = @"C:\Users\me\.wslconfig";
        var both = Uninstaller.KeptSetupLines(dockerDir, 12L << 30, wslPath);
        Check("uninstall: Docker Desktop and WSL's memory limit are two kept lines — Docker's folder named, .wslconfig's path and the limit in GB",
            both.Count == 2 && both[0] == L.T("core.uninstall.kept.docker", new { dir = dockerDir })
            && both[1] == L.T("core.uninstall.kept.wslMemory", new { path = wslPath, gb = "12" })
            && (!L.Has("core.uninstall.kept.docker") || both[0].Contains(dockerDir, StringComparison.Ordinal))
            && (!L.Has("core.uninstall.kept.wslMemory") || (both[1].Contains(wslPath, StringComparison.Ordinal) && both[1].Contains("12", StringComparison.Ordinal))));
        Check("uninstall: neither is named when it is not there — no Docker folder (null, empty, blanks), no memory line (null, 0, below 0)",
            Uninstaller.KeptSetupLines(null, null, null).Count == 0 && Uninstaller.KeptSetupLines("", 0, wslPath).Count == 0
            && Uninstaller.KeptSetupLines("   ", -1, wslPath).Count == 0
            && Uninstaller.KeptSetupLines(dockerDir, null, wslPath) is [var dockerOnly] && dockerOnly == L.T("core.uninstall.kept.docker", new { dir = dockerDir })
            && Uninstaller.KeptSetupLines(null, 8L << 30, wslPath) is [var memoryOnly] && memoryOnly == L.T("core.uninstall.kept.wslMemory", new { path = wslPath, gb = "8" }));
        Check("uninstall: the limit is said in GB with one decimal at most, whatever unit the line used — 512 MB is 0.5, 8192 MB is 8, 3 GB and 100 MB is 3.1",
            Uninstaller.KeptSetupLines(null, 512L << 20, wslPath) is [var half] && half == L.T("core.uninstall.kept.wslMemory", new { path = wslPath, gb = "0.5" })
            && Uninstaller.KeptSetupLines(null, 8192L << 20, wslPath) is [var eight] && eight == L.T("core.uninstall.kept.wslMemory", new { path = wslPath, gb = "8" })
            && Uninstaller.KeptSetupLines(null, (3L << 30) + (100L << 20), wslPath) is [var three] && three == L.T("core.uninstall.kept.wslMemory", new { path = wslPath, gb = "3.1" }));
        string wslDir = Path.Combine(tmp, "wslconfig");
        Directory.CreateDirectory(wslDir);
        string standIn = Path.Combine(wslDir, ".wslconfig");
        try
        {
            WslConfig.PathOverride = standIn;
            File.WriteAllText(standIn, "[wsl2]\r\n# a comment\r\nmemory = 6GB\r\nprocessors=4\r\n");
            var withLine = Uninstaller.KeptSetupLines(null, WslConfig.ReadCap(), WslConfig.FilePath);
            File.WriteAllText(standIn, "[wsl2]\r\nprocessors=4\r\n[experimental]\r\nmemory=6GB\r\n");
            var withoutLine = Uninstaller.KeptSetupLines(null, WslConfig.ReadCap(), WslConfig.FilePath);
            File.Delete(standIn);
            var noFile = Uninstaller.KeptSetupLines(null, WslConfig.ReadCap(), WslConfig.FilePath);
            Check("uninstall: what the report hands KeptSetupLines from a .wslconfig (a stand-in file): its [wsl2] memory line is named with the file's path, a file without one — or no file — names nothing",
                withLine is [var line] && line == L.T("core.uninstall.kept.wslMemory", new { path = standIn, gb = "6" })
                && withoutLine.Count == 0 && noFile.Count == 0);
        }
        finally { WslConfig.PathOverride = null; }
        Check("uninstall: the .wslconfig seam is cleared again", WslConfig.PathOverride is null);
    }

    // ── G. the plan holds no secret ──
    const string SecretMarker = "SPIKE-SECRET-7f3a";

    static string FullPlanJson(string extraTop = "", string extraServer = "", string extraSsh = "", string extraAccount = "") => $$"""
        {
          "id": "6F1C0E2A", "state": "Running", "express": true, "startedAt": "2026-01-02T03:04:05Z", "updatedAt": "",
          {{extraTop}}
          "game": { "installRoot": "C:\\Users\\me\\Relic Games", "enhancements": true, "fiddlerDecrypt": true, "installCert": false,
                    "createShortcut": true, "source": "archive",
                    "versions": [ { "id": "2.8", "from": "Download", "voices": ["english(us)", "Japanese", "English(US)"],
                                    "localPath": "", "replaceInstalledAt": "" },
                                  { "id": "1.6", "from": "local", "voices": [], "localPath": "D:\\Games\\Genshin 1.6\\GenshinImpact.exe",
                                    "replaceInstalledAt": "" } ] },
          "server": { "kind": "Linux", "address": "", "agentPort": 18080, "admin": false, "reach": "public",
                      "bindIp": "", "advertisedIp": "203.0.113.10", "listen": "0.0.0.0:18080", "keepEnv": null, "firewall": false,
                      "autostart": true, {{extraServer}}
                      "ssh": { "host": "box.example.com", "port": 22, "user": "root", "keyPath": "C:\\keys\\box", {{extraSsh}}
                               "hostKey": { "type": "ssh-ed25519", "fingerprint": "SHA256:{{new string('Q', 43)}}" } },
                      "versions": [ { "id": "2.8", "choice": "download", "dir": "/home/2.8_live", "progress": "keep",
                                      "pathfinding": false, "fetchedByRun": true } ],
                      "installs": { "python": false, "sevenZip": false, "wsl": false, "wslUpdate": false, "docker": false,
                                    "dockerStart": false, "acceptLicence": false, "wslMemoryGb": 0, "agentUpdate": false } },
          "account": { "name": "Player.One", {{extraAccount}} "create": { "2.8": { "template": "pre-gaa" } } },
          "steps": { "g.host": { "state": "done", "note": "" }, "s.agentUpdate": { "state": "Skipped", "note": "kept" },
                     "s.2.8.prepare": { "state": "pending", "note": "" } }
        }
        """;

    static IEnumerable<string> PropertyNames(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                yield return p.Name;
                foreach (string n in PropertyNames(p.Value)) yield return n;
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray())
                foreach (string n in PropertyNames(x)) yield return n;
    }

    static void PlanSecrets(string tmp, Action<string, bool> Check)
    {
        string s = SecretMarker;
        string json = FullPlanJson(
            extraTop: $"\"password\": \"{s}-1\", \"token\": \"{s}-2\", \"sudoPassword\": \"{s}-3\", \"passphrase\": \"{s}-4\",",
            extraServer: $"\"password\": \"{s}-5\", \"token\": \"{s}-6\", \"sshPassword\": \"{s}-7\", \"sudoPassword\": \"{s}-8\",",
            extraSsh: $"\"password\": \"{s}-9\", \"passphrase\": \"{s}-10\", \"sudoPassword\": \"{s}-11\",",
            extraAccount: $"\"password\": \"{s}-12\",");
        var plan = QuickStartPlan.FromJson(json);
        string path = Path.Combine(tmp, "plan", "quickstart.json");
        var stored = QuickStartStore.Save(plan, path, GameIds);
        string file = File.ReadAllText(path);
        string[] secretKeys = { "password", "token", "sudoPassword", "passphrase", "sshPassword" };
        using var doc = JsonDocument.Parse(file);
        var names = PropertyNames(doc.RootElement).ToList();
        Check("plan: a document holding password / token / sudoPassword / passphrase / sshPassword keys (at the top, in server, in ssh, in account) loads without them",
            plan.Server.Ssh.Host == "box.example.com" && !plan.ToJson().Contains(s, StringComparison.Ordinal));
        Check("plan: its re-save holds none of those keys at any depth, and no secret value",
            !names.Any(n => secretKeys.Contains(n, StringComparer.OrdinalIgnoreCase)) && !file.Contains(s, StringComparison.Ordinal));
        var loaded = QuickStartStore.Load(path, GameIds);
        Check("plan: the stored plan loads back without them", loaded is not null && !loaded.ToJson().Contains(s, StringComparison.Ordinal));
        Check("plan: the model has no property a secret could live in (every property's name and type)",
            !typeof(QuickStartPlan).Assembly.GetTypes().Where(t => t.Namespace == "Relic.Core.Setup" && t.Name.StartsWith("QuickStart", StringComparison.Ordinal))
                .SelectMany(t => t.GetProperties()).Any(p => secretKeys.Any(k => p.Name.Contains(k, StringComparison.OrdinalIgnoreCase))
                    || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Pass", StringComparison.OrdinalIgnoreCase)));
        Check("plan: the stored form — words in their own spelling, the id lower-cased, the voices in catalogue spelling once each, a step id with capitals kept",
            stored is { Id: "6f1c0e2a", State: "running" } && stored.Game.Versions[0].From == "download"
            && stored.Game.Versions[0].Voices.SequenceEqual(new[] { "English(US)", "Japanese" }) && stored.Server.Kind == "linux"
            && stored.Steps.ContainsKey("s.agentUpdate") && stored.Steps["s.agentUpdate"].State == "skipped"
            && stored.StartedAt == "2026-01-02T03:04:05.0000000Z" && DateTimeOffset.TryParse(stored.UpdatedAt, out _));

        // A step's note is the page's text — an installer's failure among them: it keeps no secret assignment's value.
        var noted = QuickStartPlan.FromJson(FullPlanJson());
        noted.Steps["s.agent"] = new QuickStartStep
        {
            State = "failed",
            Note = $"The installer failed (code 1):\nGIO_AGENT_TOKEN={s}-13\nGIO_MYSQL_ROOT_PASSWORD={s}-14 left\nthe rest stays",
        };
        string notedPath = Path.Combine(tmp, "plan", "noted.json");
        var notedStored = QuickStartStore.Save(noted, notedPath, GameIds);
        string notedFile = File.ReadAllText(notedPath);
        string note = notedStored.Steps["s.agent"].Note;
        Check("plan: a step's note is stored with the value of every secret assignment blanked, the rest of the text kept",
            !notedFile.Contains(s, StringComparison.Ordinal) && note.Contains("GIO_AGENT_TOKEN=***", StringComparison.Ordinal)
            && note.Contains("GIO_MYSQL_ROOT_PASSWORD=***", StringComparison.Ordinal) && note.StartsWith("The installer failed (code 1):\n", StringComparison.Ordinal)
            && note.EndsWith("\nthe rest stays", StringComparison.Ordinal));
        string once = notedStored.ToJson();
        Check("plan: the note's blanking is idempotent (normalised again, the plan is the same)", notedStored.Normalize().ToJson() == once);
    }

    // ── H. the refusals ──
    static string? DetailOf(string json)
    {
        try
        {
            var plan = QuickStartPlan.FromJson(json).Normalize();
            return plan.Problems(GameIds).FirstOrDefault();
        }
        catch (QuickStartPlanException ex) { return ex.Detail; }
    }

    static string Swap(string json, string from, string to)
    {
        if (!json.Contains(from, StringComparison.Ordinal)) throw new InvalidOperationException($"fixture lacks {from}");
        return json.Replace(from, to, StringComparison.Ordinal);
    }

    static void PlanRefusals(string tmp, Action<string, bool> Check)
    {
        string ok = FullPlanJson();
        Check("refusals: the fixture plan is usable", DetailOf(ok) is null);
        Check("refusals: an unknown game version id → $.game.versions[0].id",
            DetailOf(Swap(ok, "\"id\": \"2.8\", \"from\"", "\"id\": \"9.9\", \"from\"")) == "$.game.versions[0].id");
        Check("refusals: an unknown server version id → $.server.versions[0].id",
            DetailOf(Swap(ok, "\"id\": \"2.8\", \"choice\"", "\"id\": \"2.9\", \"choice\"")) == "$.server.versions[0].id");
        Check("refusals: an unknown version in account.create → $.account.create",
            DetailOf(Swap(ok, "\"create\": { \"2.8\"", "\"create\": { \"7.7\"")) == "$.account.create");
        Check("refusals: the same game version twice → $.game.versions[1].id",
            DetailOf(Swap(ok, "\"id\": \"1.6\", \"from\"", "\"id\": \"2.8\", \"from\"")) == "$.game.versions[1].id");
        Check("refusals: a server kind outside the list (or none) → $.server.kind",
            DetailOf(Swap(ok, "\"kind\": \"Linux\"", "\"kind\": \"cloud\"")) == "$.server.kind"
            && DetailOf(Swap(ok, "\"kind\": \"Linux\"", "\"kind\": \"\"")) == "$.server.kind");
        string many = string.Join(",", Enumerable.Range(0, 11).Select(_ => "{ \"id\": \"2.8\", \"from\": \"installed\" }"));
        Check("refusals: more than 10 game versions → $.game.versions",
            DetailOf(Swap(ok, "\"versions\": [ { \"id\": \"2.8\", \"from\": \"Download\"", $"\"versions\": [ {many}, {{ \"id\": \"2.8\", \"from\": \"Download\"")) == "$.game.versions");
        string manyServer = string.Join(",", Enumerable.Range(0, 11).Select(_ => "{ \"id\": \"1.6\" }"));
        Check("refusals: more than 10 server versions → $.server.versions",
            DetailOf(Swap(ok, "\"versions\": [ { \"id\": \"2.8\", \"choice\"", $"\"versions\": [ {manyServer}, {{ \"id\": \"2.8\", \"choice\"")) == "$.server.versions");
        string pad = new string(' ', QuickStartPlan.MaxBytes);
        Check("refusals: a document of 64 KiB or more → $ (before it is read)",
            DetailOf(ok + pad) == "$" && DetailOf(ok.TrimEnd() + new string(' ', QuickStartPlan.MaxBytes - Encoding.UTF8.GetByteCount(ok.TrimEnd()) - 1)) is null);
        var bigNotes = QuickStartPlan.FromJson(ok).Normalize();
        for (int i = 0; i < 70; i++) bigNotes.Steps[$"s.big{i}"] = new QuickStartStep { State = "failed", Note = new string('x', 2000) };
        string never = Path.Combine(tmp, "refusals", "quickstart.json");
        Check("refusals: a plan whose JSON reaches 64 KiB is refused at save (notes cut to 1000 characters do not save it)",
            (Catch(() => QuickStartStore.Save(bigNotes, never, GameIds)) as QuickStartPlanException)?.Detail == "$"
            && !File.Exists(never) && bigNotes.Steps["s.big0"].Note.Length == QuickStartPlan.MaxNote);
        Check("refusals: not JSON, or a value of the wrong type → the JSON path",
            DetailOf("{ not json") is { Length: > 0 } && DetailOf(Swap(ok, "\"agentPort\": 18080", "\"agentPort\": \"x\"")) == "$.server.agentPort"
            && DetailOf("null") == "$");
        Check("refusals: a state, a source, a progress, a choice outside their words",
            DetailOf(Swap(ok, "\"state\": \"Running\"", "\"state\": \"busy\"")) == "$.state"
            && DetailOf(Swap(ok, "\"from\": \"local\"", "\"from\": \"usb\"")) == "$.game.versions[1].from"
            && DetailOf(Swap(ok, "\"progress\": \"keep\"", "\"progress\": \"wipe\"")) == "$.server.versions[0].progress"
            && DetailOf(Swap(ok, "\"choice\": \"download\"", "\"choice\": \"steal\"")) == "$.server.versions[0].choice");
        Check("refusals: an unknown voice language → $.game.versions[0].voices[1]",
            DetailOf(Swap(ok, "\"Japanese\"", "\"Klingon\"")) == "$.game.versions[0].voices[1]");
        Check("refusals: reach follows the kind — a Linux box lan|public, a Windows server local|lan|internet",
            DetailOf(Swap(ok, "\"reach\": \"public\"", "\"reach\": \"local\"")) == "$.server.reach"
            && DetailOf(Swap(Swap(ok, "\"kind\": \"Linux\"", "\"kind\": \"windows\""), "\"reach\": \"public\"", "\"reach\": \"internet\"")) is null
            && DetailOf(Swap(ok, "\"kind\": \"Linux\"", "\"kind\": \"windows\"")) == "$.server.reach");
        Check("refusals: bind / advertised addresses are IPv4 literals or empty; the listen address an IPv4 literal and a port",
            DetailOf(Swap(ok, "\"advertisedIp\": \"203.0.113.10\"", "\"advertisedIp\": \"game.example.com\"")) == "$.server.advertisedIp"
            && DetailOf(Swap(ok, "\"advertisedIp\": \"203.0.113.10\"", "\"advertisedIp\": \"203.0.113.256\"")) == "$.server.advertisedIp"
            && DetailOf(Swap(ok, "\"advertisedIp\": \"203.0.113.10\"", "\"advertisedIp\": \"192.0.2.010\"")) == "$.server.advertisedIp"
            && DetailOf(Swap(ok, "\"bindIp\": \"\"", "\"bindIp\": \"2001:db8::1\"")) == "$.server.bindIp"
            && DetailOf(Swap(ok, "\"listen\": \"0.0.0.0:18080\"", "\"listen\": \"0.0.0.0\"")) == "$.server.listen"
            && DetailOf(Swap(ok, "\"listen\": \"0.0.0.0:18080\"", "\"listen\": \"0.0.0.0:0\"")) == "$.server.listen"
            && DetailOf(Swap(ok, "\"listen\": \"0.0.0.0:18080\"", "\"listen\": \"\"")) is null);
        Check("refusals: ports — the agent port 0–65535, the SSH port 1–65535",
            DetailOf(Swap(ok, "\"agentPort\": 18080", "\"agentPort\": 65536")) == "$.server.agentPort"
            && DetailOf(Swap(ok, "\"agentPort\": 18080", "\"agentPort\": 0")) is null
            && DetailOf(Swap(ok, "\"port\": 22", "\"port\": 0")) == "$.server.ssh.port");
        Check("refusals: a path over 4096 characters, a host over 255, a control character in a path",
            DetailOf(Swap(ok, "\"dir\": \"/home/2.8_live\"", $"\"dir\": \"/{new string('d', 4096)}\"")) == "$.server.versions[0].dir"
            && DetailOf(Swap(ok, "\"host\": \"box.example.com\"", $"\"host\": \"{new string('h', 250)}.example.com\"")) == "$.server.ssh.host"
            && DetailOf(Swap(ok, "\"keyPath\": \"C:\\\\keys\\\\box\"", "\"keyPath\": \"C:\\\\keys\\nbox\"")) == "$.server.ssh.keyPath");
        Check("refusals: the account name follows the agent's rule (3–20, not digits alone, no reserved prefix); empty is fine",
            DetailOf(Swap(ok, "\"name\": \"Player.One\"", "\"name\": \"ab\"")) == "$.account.name"
            && DetailOf(Swap(ok, "\"name\": \"Player.One\"", "\"name\": \"123456\"")) == "$.account.name"
            && DetailOf(Swap(ok, "\"name\": \"Player.One\"", "\"name\": \"AdminBob\"")) == "$.account.name"
            && DetailOf(Swap(ok, "\"name\": \"Player.One\"", "\"name\": \"GMx99\"")) == "$.account.name"
            && DetailOf(Swap(ok, "\"name\": \"Player.One\"", "\"name\": \"\"")) is null
            && QuickStartPlan.AccountNameOk("raf_01") && !QuickStartPlan.AccountNameOk(".lead") && !QuickStartPlan.AccountNameOk("tpl-x")
            && !QuickStartPlan.AccountNameOk("raf_01\n") && !QuickStartPlan.AccountNameOk(null));
        Check("refusals: a step id outside letters, digits, dots and dashes, or a step state outside its words",
            DetailOf(Swap(ok, "\"g.host\"", "\"g host\"")) == "$.steps"
            && DetailOf(Swap(ok, "\"state\": \"done\"", "\"state\": \"finished\"")) == "$.steps['g.host'].state");
        Check("refusals: a fingerprint not in OpenSSH's SHA256 form, a template id outside its characters",
            DetailOf(Swap(ok, $"SHA256:{new string('Q', 43)}", "MD5:aa:bb")) == "$.server.ssh.hostKey.fingerprint"
            && DetailOf(Swap(ok, "\"template\": \"pre-gaa\"", "\"template\": \"pre gaa\"")) == "$.account.create['2.8'].template");
        Check("refusals: a null section reads as an empty one (installs nothing); a null version entry is named — never a crash",
            DetailOf(Swap(ok, "\"installs\": {", "\"installs\": null, \"x\": {")) is null
            && QuickStartPlan.FromJson(Swap(ok, "\"installs\": {", "\"installs\": null, \"x\": {")).Normalize().Server.Installs is { Docker: false, WslMemoryGb: 0 }
            && DetailOf(Swap(ok, "\"versions\": [ { \"id\": \"2.8\", \"choice\"", "\"versions\": [ null, { \"id\": \"2.8\", \"choice\"")) == "$.server.versions[0]"
            && DetailOf(Swap(ok, "\"game\": {", "\"game\": null, \"x\": {")) is null
            && QuickStartPlan.FromJson(Swap(ok, "\"game\": {", "\"game\": null, \"x\": {")).Normalize().Game.Versions.Count == 0);
    }

    // ── I. the plan file ──
    static void PlanStore(string tmp, Action<string, bool> Check)
    {
        string path = Path.Combine(tmp, "store", "quickstart.json");
        Check("store: no file → no plan", QuickStartStore.Load(path, GameIds) is null && !QuickStartStore.Clear(path));
        var plan = QuickStartPlan.FromJson(FullPlanJson());
        plan.Id = "";
        var stored = QuickStartStore.Save(plan, path, GameIds);
        string text = File.ReadAllText(path);
        byte[] raw = File.ReadAllBytes(path);
        Check("store: a plan without an id gets 8 hex characters; the file is UTF-8 without a BOM, indented, camelCase, and no .tmp is left",
            System.Text.RegularExpressions.Regex.IsMatch(stored.Id, "^[0-9a-f]{8}$") && raw[0] == (byte)'{' && text.Contains("\n  \"id\"", StringComparison.Ordinal)
            && text.Contains("\"fetchedByRun\": true", StringComparison.Ordinal) && !File.Exists(path + ".tmp"));
        var back = QuickStartStore.Load(path, GameIds);
        Check("store: the plan round-trips (the game, the server, the account, the steps)",
            back is not null && back.Id == stored.Id && back.Game.Versions.Count == 2 && back.Game.Versions[1].LocalPath == @"D:\Games\Genshin 1.6\GenshinImpact.exe"
            && back.Server.Versions[0] is { Id: "2.8", FetchedByRun: true, Pathfinding: false } && back.Server.KeepEnv is null
            && back.Server.Ssh.HostKey.Fingerprint == "SHA256:" + new string('Q', 43) && back.Account.Create["2.8"].Template == "pre-gaa"
            && back.Steps.Count == 3 && back.ToJson() == stored.ToJson());
        Check("store: ResumeWanted — running and waitingRestart arm; paused, done and no plan disarm",
            QuickStartStore.ResumeWanted(new QuickStartPlan { State = "running" }) && QuickStartStore.ResumeWanted(new QuickStartPlan { State = "waitingRestart" })
            && !QuickStartStore.ResumeWanted(new QuickStartPlan { State = "paused" }) && !QuickStartStore.ResumeWanted(new QuickStartPlan { State = "done" })
            && !QuickStartStore.ResumeWanted(null));
        byte[] beforeBad = File.ReadAllBytes(path);
        var bad = QuickStartPlan.FromJson(FullPlanJson());
        bad.Server.Kind = "cloud";
        bool badThrew = Throws(() => QuickStartStore.Save(bad, path, GameIds));
        Check("store: a plan that cannot be used is refused with its JSON path and nothing is written",
            badThrew && File.ReadAllBytes(path).SequenceEqual(beforeBad)
            && (Catch(() => QuickStartStore.Save(bad, path, GameIds)) as QuickStartPlanException)?.Detail == "$.server.kind");
        var done = QuickStartPlan.FromJson(FullPlanJson());
        done.State = "Done";
        var doneStored = QuickStartStore.Save(done, path, GameIds);
        Check("store: saving a plan that is done deletes the file — done never stays on disk", doneStored.State == "done" && !File.Exists(path));
        File.WriteAllText(path, FullPlanJson().Replace("\"state\": \"Running\"", "\"state\": \"done\"", StringComparison.Ordinal));
        Check("store: a done plan found on disk loads as none", QuickStartStore.Load(path, GameIds) is null);
        File.WriteAllText(path, "{ this is not a plan");
        Check("store: a corrupt file loads as none, never throws", !Throws(() => QuickStartStore.Load(path, GameIds)) && QuickStartStore.Load(path, GameIds) is null);
        File.WriteAllText(path, FullPlanJson().Replace("\"kind\": \"Linux\"", "\"kind\": \"cloud\"", StringComparison.Ordinal));
        Check("store: a file whose plan cannot be used loads as none", QuickStartStore.Load(path, GameIds) is null);
        Check("store: Clear deletes the file and answers whether there was one", QuickStartStore.Clear(path) && !File.Exists(path) && !QuickStartStore.Clear(path));
        var paused = QuickStartPlan.FromJson(FullPlanJson());
        paused.State = "paused";
        QuickStartStore.Save(paused, path, GameIds);
        Check("store: a paused plan is stored and loads", QuickStartStore.Load(path, GameIds)?.State == "paused");
    }

    static Exception? Catch(Action a)
    {
        try { a(); return null; } catch (Exception ex) { return ex; }
    }

    // ── J. the resume value ──
    static void ResumeValue(Action<string, bool> Check)
    {
        const string regRoot = @"HKCU\Software\RelicSavedSpikeTest";
        string runOnce = regRoot + @"\RunOnce";
        Proc.Run("reg", "delete", regRoot, "/f");
        try
        {
            Startup.RunOnceKeyOverride = runOnce;
            string exe = @"C:\Program Files\Relic\Relic.exe";
            Startup.ArmLogonRecovery(exe);
            Check("resume: not armed before ArmQuickStartResume", !Startup.QuickStartResumeArmed());
            Startup.ArmQuickStartResume(exe);
            Check("resume: ArmQuickStartResume writes RelicQuickStart = \"<exe>\" --quickstart",
                RegValue(runOnce, "RelicQuickStart") == "\"C:\\Program Files\\Relic\\Relic.exe\" --quickstart");
            Check("resume: QuickStartResumeArmed reads it", Startup.QuickStartResumeArmed());
            Startup.ArmQuickStartResume(exe);
            Check("resume: arming again (every resume does) leaves one value", Startup.QuickStartResumeArmed() && RegValue(runOnce, "RelicQuickStart") is not null);
            Startup.DisarmQuickStartResume();
            Check("resume: DisarmQuickStartResume removes it and leaves RelicRecover alone",
                !Startup.QuickStartResumeArmed() && Startup.LogonRecoveryArmed() && RegValue(runOnce, "RelicRecover") == "\"C:\\Program Files\\Relic\\Relic.exe\" --tray");
            Startup.DisarmQuickStartResume();
            Check("resume: disarming a value that is not there is fine", !Startup.QuickStartResumeArmed());
            Startup.ArmQuickStartResume("  ");
            Check("resume: an empty exe path arms nothing", !Startup.QuickStartResumeArmed());
            Startup.ArmQuickStartResume(exe);
            Startup.DisarmLogonRecovery();
            Check("resume: disarming the logon recovery leaves the resume value alone", Startup.QuickStartResumeArmed() && !Startup.LogonRecoveryArmed());
        }
        finally
        {
            Startup.RunOnceKeyOverride = null;
            Proc.Run("reg", "delete", regRoot, "/f");
        }
    }

    // ── K. no Core caller lends the newest record's token ──
    static void CoreSource(Action<string, bool> Check)
    {
        string? repo = Environment.GetEnvironmentVariable("RELIC_REPO_ROOT") is { Length: > 0 } fromEnv ? fromEnv : FindRepo();
        if (repo is null || !File.Exists(Path.Combine(repo, "Relic.slnx")))
        {
            Check("source: the repository is found (RELIC_REPO_ROOT, or a folder holding Relic.slnx above here)", false);
            return;
        }
        string core = Path.Combine(repo, "app", "Relic.Core");
        var newest = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(f => File.ReadAllText(f).Contains("ServerConfigStore.Load()", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(repo, f)).ToList();
        string probe = File.ReadAllText(Path.Combine(core, "Server", "ServerProbe.cs"));
        Check("source: no file of Relic.Core lends the token of server.json's newest record (ServerConfigStore.Load()) — ServerProbe asks for the configured host's"
              + (newest.Count == 0 ? "" : " — " + string.Join(", ", newest)),
            newest.Count == 0
            && probe.Contains("ServerConfig.Effective(ServerConfigStore.LoadFor(s.ServerHost), s.ServerHost, s.AgentPort)", StringComparison.Ordinal));
        // The uninstall's report takes Docker Desktop and the memory line from this PC (MachineCheck.DockerInstall, WslConfig)
        // through KeptSetupLines — after the server folders, before the app's own data goes, a failure one log line. Read from
        // the source: the uninstall itself is never run here.
        string uninstaller = File.ReadAllText(Path.Combine(core, "State", "Uninstaller.cs")).Replace("\r\n", "\n");
        int foldersAt = uninstaller.IndexOf("ReportServerFolders(r, state);\n        ReportSetupTools(r);", StringComparison.Ordinal);
        int appDataAt = foldersAt < 0 ? -1 : uninstaller.IndexOf("RemoveAppData(r, profilesSafeToDelete);", foldersAt, StringComparison.Ordinal);
        int reportAt = uninstaller.IndexOf("private static void ReportSetupTools(CleanupReport r)", StringComparison.Ordinal);
        int reportEnd = reportAt < 0 ? -1 : uninstaller.IndexOf("\n    }\n", reportAt, StringComparison.Ordinal);
        string reportBody = reportAt >= 0 && reportEnd > reportAt ? uninstaller[reportAt..reportEnd] : "";
        Check("source: the uninstall names what a server set up on this PC leaves with its user (ReportSetupTools, after the server folders, before the app's own data goes) — Docker Desktop's folder (MachineCheck.DockerInstall) and .wslconfig's memory line (WslConfig.ReadCap, its path) through KeptSetupLines, a failure one log line",
            foldersAt > 0 && appDataAt > foldersAt
            && reportBody.Contains("string? docker = Setup.MachineCheck.DockerInstall()?.Location;", StringComparison.Ordinal)
            && reportBody.Contains("foreach (string line in KeptSetupLines(docker, Setup.WslConfig.ReadCap(), Setup.WslConfig.FilePath))", StringComparison.Ordinal)
            && reportBody.Contains("r.Kept.Add(line);", StringComparison.Ordinal)
            && reportBody.Contains("catch (Exception ex) { Log.Error(", StringComparison.Ordinal));
    }

    static string? FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Relic.slnx"))) return dir.FullName;
        return null;
    }

    static string? RegValue(string key, string name)
    {
        var r = Proc.Run("reg", "query", key, "/v", name);
        if (!r.Ok) return null;
        foreach (var line in r.Out.Split('\n'))
        {
            int idx = line.IndexOf("REG_SZ", StringComparison.Ordinal);
            if (idx >= 0 && line.Contains(name, StringComparison.Ordinal))
                return line[(idx + "REG_SZ".Length)..].Trim();
        }
        return null;
    }
}
