using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Relic.Core.Setup;

/// <summary>
/// The memory line of <c>%UserProfile%\.wslconfig</c> — the cap of the WSL 2 virtual machine Docker Desktop runs
/// its containers in. WSL reads the file when it starts: a limit written here applies from WSL's next start, to
/// every WSL Linux of the PC, and nothing here stops a running WSL. The parsers are pure.
/// </summary>
public static class WslConfig
{
    /// <summary>Spikes only: the file to read and write in place of the user's.</summary>
    public static string? PathOverride { get; set; }

    /// <summary><c>%UserProfile%\.wslconfig</c>.</summary>
    public static string FilePath => PathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");

    private static readonly Regex Section = new(@"^\s*\[\s*([^\]]*?)\s*\]\s*$");
    private static readonly Regex MemoryLine = new(@"^\s*memory\s*=", RegexOptions.IgnoreCase);
    private static readonly Regex Size = new(@"^\s*([0-9]+(?:\.[0-9]+)?)\s*([A-Za-z]*)\s*$");

    /// <summary>A size as WSL writes it: a number and a unit — B, K / KB, M / MB, G / GB, T / TB, any letter case,
    /// powers of 1024 — or a bare number of bytes. Null for anything else. Pure.</summary>
    public static long? ParseSize(string? value)
    {
        var m = Size.Match(value ?? "");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n)) return null;
        double factor = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "" or "B" => 1,
            "K" or "KB" => 1L << 10,
            "M" or "MB" => 1L << 20,
            "G" or "GB" => 1L << 30,
            "T" or "TB" => 1L << 40,
            _ => -1,
        };
        if (factor < 0) return null;
        double bytes = n * factor;
        return bytes is >= 1 and < long.MaxValue ? (long)bytes : null;
    }

    /// <summary>The memory cap the text sets: the value of the last <c>memory=</c> line under a <c>[wsl2]</c>
    /// section (any letter case; <c>8GB</c>, <c>8192MB</c>, <c>12G</c>). A line that starts with <c>#</c> or
    /// <c>;</c> is a comment, the same key under another section is not WSL 2's, and a value WSL would not read
    /// is no cap. Null without one. Pure.</summary>
    public static long? MemoryBytes(string? text)
    {
        long? found = null;
        bool inWsl2 = false;
        foreach (var raw in (text ?? "").TrimStart('﻿').Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string t = line.Trim();
            if (t.Length == 0 || t[0] is '#' or ';') continue;
            var s = Section.Match(line);
            if (s.Success) { inWsl2 = string.Equals(s.Groups[1].Value, "wsl2", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inWsl2 || !MemoryLine.IsMatch(line)) continue;
            string value = t[(t.IndexOf('=') + 1)..];
            int hash = value.IndexOf('#');
            if (hash >= 0) value = value[..hash];
            if (ParseSize(value) is { } bytes) found = bytes;
        }
        return found;
    }

    /// <summary>
    /// The text with WSL 2's memory set to <paramref name="gb"/> GB: every <c>memory=</c> line under a <c>[wsl2]</c>
    /// section becomes <c>memory=&lt;gb&gt;GB</c>; without one the line goes right under the first <c>[wsl2]</c>
    /// header, and without that section the section is added at the end. Every other line stays as it was, and so
    /// do the text's line ends (CRLF for a text that has none yet). Pure.
    /// </summary>
    public static string WithMemory(string? text, int gb)
    {
        if (gb <= 0) throw new ArgumentOutOfRangeException(nameof(gb));
        string src = text ?? "";
        bool bom = src.StartsWith('﻿');
        if (bom) src = src[1..];
        string nl = src.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : src.Contains('\n') ? "\n" : "\r\n";
        string memory = "memory=" + gb.ToString(CultureInfo.InvariantCulture) + "GB";
        var lines = src.Length == 0 ? new List<string>() : src.Split('\n').ToList();
        bool endsWithBreak = src.EndsWith('\n');
        if (endsWithBreak) lines.RemoveAt(lines.Count - 1);
        // Each line keeps its own end: a CR left on a line of a CRLF text is written back with it.
        bool inWsl2 = false, replaced = false;
        int firstHeader = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].TrimEnd('\r');
            string t = line.Trim();
            if (t.Length > 0 && t[0] is '#' or ';') continue;
            var s = Section.Match(line);
            if (s.Success)
            {
                inWsl2 = string.Equals(s.Groups[1].Value, "wsl2", StringComparison.OrdinalIgnoreCase);
                if (inWsl2 && firstHeader < 0) firstHeader = i;
                continue;
            }
            if (inWsl2 && MemoryLine.IsMatch(line))
            {
                lines[i] = memory + (lines[i].EndsWith('\r') ? "\r" : "");
                replaced = true;
            }
        }
        string cr = nl == "\r\n" ? "\r" : "";
        if (!replaced)
        {
            if (firstHeader >= 0) lines.Insert(firstHeader + 1, memory + cr);
            else
            {
                if (lines.Count > 0 && lines[^1].TrimEnd('\r').Trim().Length > 0) lines.Add(cr);
                lines.Add("[wsl2]" + cr);
                lines.Add(memory + cr);
                endsWithBreak = true;
            }
        }
        var sb = new StringBuilder();
        if (bom) sb.Append('﻿');
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Count - 1 || endsWithBreak) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The cap the user's file sets (<see cref="MemoryBytes"/>); null when there is no file, no memory line
    /// or the file cannot be read.</summary>
    public static long? ReadCap()
    {
        try { return File.Exists(FilePath) ? MemoryBytes(File.ReadAllText(FilePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Write the memory cap into the user's file (<see cref="WithMemory"/> over what it holds, a new file
    /// when there is none): written beside it, then put in its place in one step. The file keeps its own encoding and
    /// byte order mark (<see cref="Decode"/> / <see cref="Encode"/>); a new one is UTF-8 without a mark. Throws with the
    /// file system's error.</summary>
    public static string WriteMemory(int gb)
    {
        string path = FilePath;
        string? current = null;
        Encoding encoding = new UTF8Encoding(false);
        if (File.Exists(path)) (current, encoding) = Decode(File.ReadAllBytes(path));
        string tmp = path + ".relic-tmp";
        File.WriteAllBytes(tmp, Encode(WithMemory(current, gb), encoding));
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>A file's bytes as text and the encoding they are in, told by the byte order mark — UTF-16 little or big
    /// endian, else UTF-8 —, the mark kept as the text's first character (U+FEFF) so <see cref="WithMemory"/> writes it
    /// back. Pure.</summary>
    public static (string Text, Encoding Encoding) Decode(byte[]? bytes)
    {
        var b = bytes ?? Array.Empty<byte>();
        Encoding encoding = b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE ? new UnicodeEncoding(bigEndian: false, byteOrderMark: false)
            : b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF ? new UnicodeEncoding(bigEndian: true, byteOrderMark: false)
            : new UTF8Encoding(false);
        return (encoding.GetString(b), encoding);
    }

    /// <summary>The text as bytes in <paramref name="encoding"/>; a U+FEFF it starts with becomes that encoding's byte
    /// order mark. Pure.</summary>
    public static byte[] Encode(string text, Encoding encoding) => encoding.GetBytes(text ?? "");
}
