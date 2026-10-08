using System.Runtime.InteropServices;
using System.Text;

namespace Relic.Core.Util;

/// <summary>
/// Keeps the consent dialogs where the user can actually see them. Windows' own "Security Warning"
/// — raised by crypt32 inside <c>X509Store.Add</c>, with NO owner window and no topmost bit — opens
/// from a background task, and whenever Windows denies the foreground switch (any app, including
/// the launcher itself mid-install, holds the foreground lock) it lands BEHIND the active window:
/// the install then looks hung while an invisible dialog waits for a click. The launcher's own notice is
/// nominally covered by its invisible TopMost owner (on Win11 26200 the owned TaskDialog
/// comes up with WS_EX_TOPMOST already set), but that inheritance is undocumented behaviour and the
/// notice can hide too — so it promotes itself as well rather than lean on it.
///
/// The reliable half is the Z-order, not the focus. Every one of these dialogs is created IN THIS
/// process — crypt32 shows the Security Warning on the very thread that called
/// <c>store.Add</c>, window class "#32770" — so we can find our own dialog-class windows and lift
/// each into the topmost band; <c>SetWindowPos(HWND_TOPMOST)</c> on our own window succeeds even
/// while another process holds the foreground, unlike <c>SetForegroundWindow</c>,
/// which stays best-effort: it succeeds in the common case where the launcher already holds the
/// foreground, and when it is refused the dialog is still in plain sight, only not focused.
/// </summary>
public static class DialogFront
{
    /// <summary>
    /// Watch this process for newly visible dialog windows and promote each one — once per dialog
    /// lifetime, asserted rather than fought over: a promoted dialog the user later covers with some
    /// other topmost window stays covered — until the returned handle is disposed. Meant to span a
    /// consent sequence: <c>using var front = DialogFront.WatchAndPromote();</c>
    /// </summary>
    public static IDisposable WatchAndPromote()
    {
        var stopper = new Stopper();
        _ = Task.Run(async () =>
        {
            var promoted = new HashSet<IntPtr>();
            try
            {
                while (!stopper.Stopped)
                {
                    // A destroyed dialog's HWND value can be recycled for the next one — and the
                    // notice closes right before store.Add raises the Security Warning. Prune dead
                    // handles so a recycled value counts as the new dialog it now names, not as
                    // "already promoted" (which would silently leave that dialog unpromoted).
                    promoted.RemoveWhere(h => !IsWindow(h));
                    foreach (var h in FindProcessDialogs())
                        if (promoted.Add(h))
                            Promote(h);
                    await Task.Delay(150);
                }
            }
            catch (Exception ex) { Log.Error("promoting the consent dialogs (non-fatal)", ex); }
        });
        return stopper;
    }

    /// <summary>
    /// Promote every dialog-class window living on the CALLING thread. For the moment a dialog
    /// reports its own creation (TaskDialogPage.Created runs on the dialog's thread) — no polling
    /// gap, so the notice never even flashes behind the launcher. Visibility is deliberately not
    /// required: at creation time the window may not be visible yet, and WS_EX_TOPMOST set early
    /// simply sticks.
    /// </summary>
    public static void PromoteCurrentThreadDialogs()
    {
        try
        {
            EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
            {
                if (IsDialogWindow(h, requireVisible: false)) Promote(h);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("promoting the thread's dialog (non-fatal)", ex); }
    }

    private static void Promote(IntPtr h)
    {
        // Topmost first — this is the half that cannot be refused the way activation can.
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetForegroundWindow(h); // best-effort focus; see the class summary
    }

    private static List<IntPtr> FindProcessDialogs()
    {
        var found = new List<IntPtr>();
        uint pid = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint winPid);
            if (winPid == pid && IsDialogWindow(h, requireVisible: true)) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>True for the standard dialog window class "#32770" — MessageBox, TaskDialog and the
    /// crypt32 Security Warning all use it; the launcher's own WinForms windows never do.</summary>
    private static bool IsDialogWindow(IntPtr h, bool requireVisible)
    {
        if (requireVisible && !IsWindowVisible(h)) return false;
        var name = new StringBuilder(16);
        return GetClassName(h, name, name.Capacity) > 0 && name.ToString() == "#32770";
    }

    /// <summary>A plain flag instead of a CancellationTokenSource: the watcher may notice the stop
    /// up to one poll late (irrelevant), and in exchange there is no cancel-vs-dispose race at all.</summary>
    private sealed class Stopper : IDisposable
    {
        public volatile bool Stopped;
        public void Dispose() => Stopped = true;
    }

    // ── Win32 ──

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
