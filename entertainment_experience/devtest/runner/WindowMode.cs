using System.Text;
using System.Text.RegularExpressions;

namespace Relic.DevTest;

/// <summary>The window a session runs in: <see cref="Width"/> x <see cref="Height"/>, not fullscreen.</summary>
internal sealed record WindowSpec(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";

    /// <summary>"WxH", or "keep" (null: the slot's own screen values are left as they are).</summary>
    public static WindowSpec? Parse(string text)
    {
        text = text.Trim();
        if (text.Equals("keep", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Regex.Match(text, @"^(\d{3,5})[xX](\d{3,5})$");
        if (!m.Success) throw new UsageException($"--window '{text}': WxH (e.g. 1600x900) or keep");
        int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
        if (w < 320 || h < 200 || w > 16384 || h > 16384)
            throw new UsageException($"--window '{text}': between 320x200 and 16384x16384");
        return new WindowSpec(w, h);
    }
}

/// <summary>
/// The game's screen settings in a version's profile slot. Every slot is exported as a UTF-16LE <c>.reg</c> file
/// (<c>%LOCALAPPDATA%\Relic\profiles\&lt;id&gt;\genshin.reg</c>) that the session imports when it loads the slot and
/// exports again when it saves it. Three of its values decide how the client opens: Unity's
/// <c>Screenmanager Is Fullscreen mode</c>, <c>Resolution Width</c> and <c>Resolution Height</c> (dword values of the
/// game's own key, each name ending in a hash). Before a session the runner keeps a byte copy of the export
/// (<c>&lt;file&gt;.devtest-orig</c>) and writes the window into those three values; after the session, once the slot
/// is saved again and no longer loaded, it puts the owner's three values back into the new export and deletes the
/// copy. Nothing else of the file is ever changed — every edit is proven line by line — and the live registry key
/// is never touched: the session's own swap carries the values in and out.
/// </summary>
internal static class WindowMode
{
    public const string CopySuffix = ".devtest-orig";
    public const string GameKeyHeader = @"[HKEY_CURRENT_USER\Software\miHoYo\Genshin Impact]";
    public const string Fullscreen = "Is Fullscreen mode";
    public const string Width = "Resolution Width";
    public const string Height = "Resolution Height";
    public static readonly string[] Kinds = { Fullscreen, Width, Height };

    private static readonly Regex ValueLine = new(
        @"^""(?<name>Screenmanager (?<kind>Is Fullscreen mode|Resolution Width|Resolution Height)_h\d+)""=dword:(?<value>[0-9a-fA-F]{8})$",
        RegexOptions.CultureInvariant);

    /// <summary>One line of the file: its bytes (without the line end), the line end's bytes and the key section
    /// it stands in.</summary>
    private sealed record RegLine(int Start, int Length, int EolLength, string Text, string Section);

    /// <summary>One of the three values: where it stands, its full name, its value and its line.</summary>
    public sealed record ScreenValue(int LineIndex, string Name, string Kind, uint Value, string Text);

    private static List<RegLine> Split(byte[] b)
    {
        if (b.Length < 2 || b[0] != 0xFF || b[1] != 0xFE)
            throw new InvalidDataException("not a UTF-16LE registry export (no FF FE at its start)");
        if (b.Length % 2 != 0)
            throw new InvalidDataException("a UTF-16LE registry export of an odd length");
        var lines = new List<RegLine>();
        string section = "";
        int start = 2;
        while (start < b.Length)
        {
            int nl = -1;
            for (int i = start; i + 1 < b.Length; i += 2)
                if (b[i] == 0x0A && b[i + 1] == 0x00) { nl = i; break; }
            int contentEnd, eol;
            if (nl < 0) { contentEnd = b.Length; eol = 0; }
            else
            {
                contentEnd = nl;
                eol = 2;
                if (contentEnd - 2 >= start && b[contentEnd - 2] == 0x0D && b[contentEnd - 1] == 0x00) { contentEnd -= 2; eol = 4; }
            }
            string text = Encoding.Unicode.GetString(b, start, contentEnd - start);
            if (text.StartsWith('[')) section = text;
            lines.Add(new RegLine(start, contentEnd - start, eol, text, section));
            start = contentEnd + eol;
        }
        return lines;
    }

    private static Dictionary<string, ScreenValue> Values(List<RegLine> lines)
    {
        var found = new Dictionary<string, ScreenValue>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (!string.Equals(l.Section, GameKeyHeader, StringComparison.OrdinalIgnoreCase)) continue;
            var m = ValueLine.Match(l.Text);
            if (!m.Success) continue;
            string kind = m.Groups["kind"].Value;
            if (found.ContainsKey(kind))
                throw new InvalidDataException($"the export names 'Screenmanager {kind}' twice");
            found[kind] = new ScreenValue(i, m.Groups["name"].Value, kind,
                Convert.ToUInt32(m.Groups["value"].Value, 16), l.Text);
        }
        return found;
    }

    /// <summary>The three values of an export (each one that is there).</summary>
    public static Dictionary<string, ScreenValue> Read(byte[] file) => Values(Split(file));

    /// <summary>Why the export at <paramref name="reg"/> cannot take the window, or null.</summary>
    public static string? Problem(string reg)
    {
        try
        {
            var values = Read(File.ReadAllBytes(reg));
            var missing = Kinds.Where(k => !values.ContainsKey(k)).ToList();
            return missing.Count == 0 ? null
                : $"{reg} does not name 'Screenmanager {string.Join("', '", missing)}' under the game's key -- play the version once, or pass --window keep";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"{reg} cannot be read as a registry export ({ex.Message})";
        }
    }

    private static string LineFor(string name, uint value) => $"\"{name}\"=dword:{value:x8}";

    /// <summary>The file with some lines replaced and some added after a given line (each added line ends the way
    /// that line ends); every other byte as it was.</summary>
    private static byte[] Join(byte[] file, List<RegLine> lines, IReadOnlyDictionary<int, string> replace,
        IReadOnlyDictionary<int, List<string>> addAfter)
    {
        using var ms = new MemoryStream(file.Length + 256);
        ms.Write(file, 0, 2);
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (replace.TryGetValue(i, out string? text))
            {
                byte[] b = Encoding.Unicode.GetBytes(text);
                ms.Write(b, 0, b.Length);
                ms.Write(file, l.Start + l.Length, l.EolLength);
            }
            else
                ms.Write(file, l.Start, l.Length + l.EolLength);
            if (addAfter.TryGetValue(i, out var added))
            {
                foreach (string a in added)
                {
                    if (l.EolLength == 0) ms.Write(Encoding.Unicode.GetBytes("\r\n"));
                    byte[] b = Encoding.Unicode.GetBytes(a);
                    ms.Write(b, 0, b.Length);
                    if (l.EolLength > 0) ms.Write(file, l.Start + l.Length, l.EolLength);
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>The proof of an edit: the two files hold the same lines, byte for byte and in the same order, once
    /// the three screen values of the game's key are taken out of both.</summary>
    private static bool OtherLinesEqual(byte[] a, byte[] b)
    {
        static List<(int Start, int Count)> Others(byte[] f, out List<RegLine> lines)
        {
            lines = Split(f);
            var values = Values(lines);
            var skip = values.Values.Select(v => v.LineIndex).ToHashSet();
            var keep = new List<(int, int)>();
            for (int i = 0; i < lines.Count; i++)
                if (!skip.Contains(i)) keep.Add((lines[i].Start, lines[i].Length + lines[i].EolLength));
            return keep;
        }
        var ra = Others(a, out _);
        var rb = Others(b, out _);
        if (ra.Count != rb.Count) return false;
        for (int i = 0; i < ra.Count; i++)
            if (!a.AsSpan(ra[i].Start, ra[i].Count).SequenceEqual(b.AsSpan(rb[i].Start, rb[i].Count)))
                return false;
        return true;
    }

    /// <summary>Before a session: the byte copy, then the window written into the three values. Returns the values
    /// as they were, for the log.</summary>
    public static string Apply(string reg, WindowSpec spec)
    {
        string copy = reg + CopySuffix;
        if (File.Exists(copy))
            throw new InvalidOperationException($"{copy} is there already -- it holds the owner's values and is restored first");
        byte[] before = File.ReadAllBytes(reg);
        var lines = Split(before);
        var values = Values(lines);
        foreach (string k in Kinds)
            if (!values.ContainsKey(k)) throw new InvalidDataException($"{reg} does not name 'Screenmanager {k}'");

        // The copy is on the disk before the export changes: a power cut in between cannot leave a cut copy of the
        // owner's values beside an export that holds the window's instead.
        Files.WriteAtomic(copy, before);
        if (!File.ReadAllBytes(copy).AsSpan().SequenceEqual(before))
        {
            File.Delete(copy);
            throw new IOException($"the copy {copy} is not the file it was made from");
        }

        var replace = new Dictionary<int, string>
        {
            [values[Fullscreen].LineIndex] = LineFor(values[Fullscreen].Name, 0),
            [values[Width].LineIndex] = LineFor(values[Width].Name, (uint)spec.Width),
            [values[Height].LineIndex] = LineFor(values[Height].Name, (uint)spec.Height),
        };
        byte[] after = Join(before, lines, replace, new Dictionary<int, List<string>>());
        var now = Read(after);
        if (!OtherLinesEqual(before, after) || now[Fullscreen].Value != 0
            || now[Width].Value != (uint)spec.Width || now[Height].Value != (uint)spec.Height)
            throw new InvalidDataException("the edited export is not the original with the three screen values changed");
        Files.WriteAtomic(reg, after);
        return Describe(values);
    }

    /// <summary>After a session: the owner's three values (from the copy) put back into the slot's export as the
    /// session left it, then the copy deleted. A value the new export lacks is added under the game's key. Returns
    /// what was replaced, for the log; null when there was no copy (nothing to restore).</summary>
    public static string? Restore(string reg)
    {
        string copy = reg + CopySuffix;
        if (!File.Exists(copy)) return null;
        var owner = Read(File.ReadAllBytes(copy));
        foreach (string k in Kinds)
            if (!owner.ContainsKey(k)) throw new InvalidDataException($"{copy} does not name 'Screenmanager {k}'");
        if (!File.Exists(reg))
            throw new FileNotFoundException($"{reg} is not there: the slot has no registry export to restore the values into", reg);

        byte[] current = File.ReadAllBytes(reg);
        var lines = Split(current);
        var now = Values(lines);
        var replace = new Dictionary<int, string>();
        var add = new List<string>();
        foreach (string k in Kinds)
        {
            if (now.TryGetValue(k, out var v)) replace[v.LineIndex] = owner[k].Text;
            else add.Add(owner[k].Text);
        }
        var addAfter = new Dictionary<int, List<string>>();
        if (add.Count > 0)
        {
            int header = lines.FindIndex(l => string.Equals(l.Text, GameKeyHeader, StringComparison.OrdinalIgnoreCase));
            if (header < 0) throw new InvalidDataException($"{reg} has no section {GameKeyHeader}");
            addAfter[header] = add;
        }
        byte[] after = Join(current, lines, replace, addAfter);
        var back = Read(after);
        if (!OtherLinesEqual(current, after) || Kinds.Any(k => !back.TryGetValue(k, out var b) || b.Text != owner[k].Text))
            throw new InvalidDataException("the restored export is not the session's export with the owner's three screen values");
        if (!after.AsSpan().SequenceEqual(current)) Files.WriteAtomic(reg, after);
        File.Delete(copy);
        return Describe(now);
    }

    private static string Describe(Dictionary<string, ScreenValue> values) =>
        string.Join(", ", Kinds.Select(k => values.TryGetValue(k, out var v) ? $"{k}={v.Value}" : $"{k}=absent"));
}
