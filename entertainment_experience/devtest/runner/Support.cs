using System.Security.Cryptography;
using System.Text;

namespace Relic.DevTest;

/// <summary>The runner's own log: every line to stdout and, with <c>--log</c>, appended to that file — shared for
/// reading and flushed per line, so the scenario runner can follow it while a session runs.</summary>
internal sealed class RunLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _file;

    public string? FilePath { get; }

    public RunLog(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        FilePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _file = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Line(string text)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}";
        lock (_gate)
        {
            try { Console.Out.WriteLine(line); } catch { /* a process without a console */ }
            try { _file?.WriteLine(line); } catch { /* a log must never end a session */ }
        }
    }

    public void Dispose()
    {
        lock (_gate) _file?.Dispose();
    }
}

/// <summary>A refusal before anything was changed: every problem found, each with what to do about it.</summary>
internal sealed class Refusal : Exception
{
    public IReadOnlyList<string> Problems { get; }

    public Refusal(IReadOnlyList<string> problems) : base(string.Join("; ", problems)) => Problems = problems;

    public Refusal(string problem) : this(new[] { problem }) { }
}

/// <summary>A command line that cannot be read.</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}

/// <summary><c>--key value</c> / <c>--key=value</c> pairs, each named at most once, only the names a command takes.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static Options Parse(IReadOnlyList<string> args, IReadOnlyCollection<string> known)
    {
        var o = new Options();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal) || a.Length < 3)
                throw new UsageException($"unexpected argument '{a}'");
            string key = a[2..];
            string? value = null;
            int eq = key.IndexOf('=');
            if (eq >= 0)
            {
                value = key[(eq + 1)..];
                key = key[..eq];
            }
            if (!known.Contains(key))
                throw new UsageException($"unknown option --{key}");
            if (value is null)
            {
                if (i + 1 >= args.Count)
                    throw new UsageException($"--{key} needs a value");
                value = args[++i];
            }
            if (!o._values.TryAdd(key, value))
                throw new UsageException($"--{key} is given twice");
        }
        return o;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public string Require(string key) =>
        Get(key) is { Length: > 0 } v ? v : throw new UsageException($"--{key} is required");
}

internal static class Files
{
    /// <summary>The sha256 of a file, read in a stream (lowercase hex, as payload/manifest.json writes it).</summary>
    public static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Write a whole file through a temp file beside it (flushed to the disk) and one rename over the target:
    /// a file under the target's name is never a half-written one. A reader that holds the target for a moment
    /// (another process reading it) makes the rename fail: it is tried again shortly.</summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        string tmp = path + ".devtest-tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 40)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>Is <paramref name="path"/> the folder <paramref name="root"/> or below it (full paths, no letter case)?</summary>
    public static bool IsInside(string path, string root)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every file of a folder copied into another (which must not exist yet), sub-folders included.</summary>
    public static int CopyTree(string from, string to)
    {
        int n = 0;
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: false);
            n++;
        }
        return n;
    }
}
