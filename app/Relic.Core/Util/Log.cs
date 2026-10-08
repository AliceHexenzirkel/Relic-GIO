namespace Relic.Core.Util;

/// <summary>Tiny thread-safe file logger. Logging must never throw.</summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Set by the uninstall cleanup right before %LOCALAPPDATA%\Relic is deleted: writing a
    /// single line after that point recreates the folder (Write does CreateDirectory) and leaves the
    /// machine looking un-cleaned — the exact leftover the cleanup exists to remove.</summary>
    public static bool Disabled { get; set; }

    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "logs");

    public static string LogFile => Path.Combine(LogDir, "relic.log");

    public static void Write(string level, string message)
    {
        if (Disabled) return;
        try
        {
            Directory.CreateDirectory(LogDir);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(LogFile, line);
        }
        catch { /* never throw from logging */ }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");
}
