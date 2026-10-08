using System.Runtime.InteropServices;
using Relic.Core.Util;

namespace Relic.App;

/// <summary>Minimal Win32 for a borderless window whose title bar lives in the web layer.</summary>
internal static partial class Interop
{
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    /// <summary>
    /// Relic's identity in the shell. Must stay STABLE across versions: it is the key the taskbar
    /// groups windows by and the one a pinned button is tied to the running app with.
    /// </summary>
    private const string AppUserModelId = "Relic.Launcher";

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appID);

    /// <summary>
    /// Tells the shell who we are, once, BEFORE any window exists.
    ///
    /// Without it the taskbar button borrows the icon of the SHORTCUT that started the process — and
    /// the desktop game shortcut deliberately carries GenshinImpact.exe's icon. That shortcut
    /// also starts the launcher, so the Relic window would appear in the
    /// taskbar with the Genshin icon. For a process started from a
    /// .lnk carrying a foreign icon: without an AppUserModelID the button shows the .lnk's icon, with
    /// it the window's. The shortcut's icon stays untouched — and must, it is the game's.
    /// </summary>
    public static void DeclareAppIdentity()
    {
        try
        {
            int hr = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (hr != 0) Log.Info($"SetCurrentProcessExplicitAppUserModelID returned 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            // Purely cosmetic: without it only the correct taskbar icon is lost.
            Log.Error("AppUserModelID (non-fatal)", ex);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    /// <summary>Let the user drag the window by a web-layer "title bar" element.</summary>
    public static void DragWindow(nint handle)
    {
        ReleaseCapture();
        SendMessageW(handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }
}
