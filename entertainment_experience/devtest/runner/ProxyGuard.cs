using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Relic.Core.Fiddler;

namespace Relic.DevTest;

/// <summary>
/// The WinINET proxy settings around a session. Fiddler makes itself the system proxy when it starts and puts the old
/// settings back only on a graceful exit: a Fiddler that is killed (a crash, a process tree ended under it) leaves every
/// WinINET program of the PC — browsers, Windows Update — pointed at a 127.0.0.1 port nobody listens on. The runner keeps
/// the settings as they were before the session and, once the session is over and no Fiddler runs, puts them back when
/// they still point at Fiddler's listener.
/// </summary>
internal static class ProxyGuard
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string FiddlerListener = "127.0.0.1:8888";
    private static readonly string[] Names = { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL" };

    public static string SnapshotPath(string stageDir) => Path.Combine(stageDir, "proxy-before.json");

    /// <summary>The settings as they are now, by value name (absent values are left out).</summary>
    public static JsonObject Read()
    {
        var o = new JsonObject();
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        if (k is null) return o;
        foreach (string n in Names)
        {
            object? v = k.GetValue(n);
            if (v is int i) o[n] = i;
            else if (v is string s) o[n] = s;
        }
        return o;
    }

    /// <summary>Whether the settings make Fiddler's listener the system proxy.</summary>
    public static bool PointsAtFiddler(JsonObject s) =>
        s["ProxyEnable"]?.GetValue<int>() == 1
        && (s["ProxyServer"]?.GetValue<string>() ?? "").Contains(FiddlerListener, StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps the settings of before the session in the stage folder — only clean ones: settings that already
    /// point at Fiddler are not a state to go back to (an earlier session's leftover, or a Fiddler the owner runs).</summary>
    public static void Snapshot(string stageDir, RunLog log, JsonObject result)
    {
        var now = Read();
        if (PointsAtFiddler(now))
        {
            log.Line("proxy: the system proxy already points at Fiddler before the session -- no snapshot is kept");
            result["proxyBefore"] = null;
            return;
        }
        Directory.CreateDirectory(stageDir);
        Files.WriteAtomic(SnapshotPath(stageDir), new UTF8Encoding(false).GetBytes(now.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        result["proxyBefore"] = now.DeepClone();
    }

    /// <summary>
    /// After a session: when no Fiddler runs and the settings still make its listener the system proxy, the settings of
    /// before the session go back (from the stage folder's snapshot; without one, the proxy is switched off and the
    /// listener's address removed, which is what a PC without another proxy had). Running programs are told at once.
    /// True when the settings were left as they are or put back; false when they still point at a dead listener.
    /// </summary>
    public static bool RepairIfHijacked(string stageDir, RunLog log, JsonObject result)
    {
        if (FiddlerAutomation.IsRunning) return true;
        var now = Read();
        if (!PointsAtFiddler(now)) return true;
        JsonObject? before = null;
        try
        {
            string p = SnapshotPath(stageDir);
            if (File.Exists(p)) before = JsonNode.Parse(File.ReadAllText(p)) as JsonObject;
        }
        catch (Exception ex)
        {
            log.Line($"proxy: the snapshot could not be read -- {ex.Message}");
        }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                ?? throw new InvalidOperationException("the Internet Settings key cannot be opened");
            if (before is not null && !PointsAtFiddler(before))
            {
                foreach (string n in Names)
                {
                    var v = before[n];
                    if (v is null) k.DeleteValue(n, throwOnMissingValue: false);
                    else if (n == "ProxyEnable") k.SetValue(n, v.GetValue<int>(), RegistryValueKind.DWord);
                    else k.SetValue(n, v.GetValue<string>(), RegistryValueKind.String);
                }
                log.Line("proxy: Fiddler is gone and the system proxy still pointed at it -- the settings of before the session are back");
            }
            else
            {
                k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                string server = now["ProxyServer"]?.GetValue<string>() ?? "";
                if (server.Equals($"http={FiddlerListener};https={FiddlerListener}", StringComparison.OrdinalIgnoreCase)
                    || server.Equals(FiddlerListener, StringComparison.OrdinalIgnoreCase))
                    k.DeleteValue("ProxyServer", throwOnMissingValue: false);
                log.Line("proxy: Fiddler is gone and the system proxy still pointed at it -- the proxy is switched off (no snapshot)");
            }
        }
        catch (Exception ex)
        {
            log.Line($"proxy: the system proxy still points at the dead Fiddler and could not be put back -- {ex.Message}; "
                + "switch it off in Windows' proxy settings");
            result["proxyRestored"] = false;
            return false;
        }
        Notify();
        result["proxyRestored"] = true;
        return true;
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);

    /// <summary>Running WinINET programs read the new settings now (INTERNET_OPTION_SETTINGS_CHANGED, then REFRESH).</summary>
    private static void Notify()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
        catch { /* the registry holds the settings: the next program start reads them */ }
    }
}
