using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Relic.Core.Util;

/// <summary>
/// Puts the CALLING OS thread into Windows' background processing mode until the returned scope is
/// disposed — used by the extract workers so a 20–80 GB install stops starving everything else on
/// the machine.
///
/// Why the thread mode and not just a lower CPU priority: the Win32 docs are explicit that CPU
/// priority alone "is not sufficient... even an idle CPU priority thread can easily interfere with
/// system responsiveness when it uses the disk and memory". THREAD_MODE_BACKGROUND_BEGIN lowers CPU,
/// **I/O** and **memory** priority together, and the memory half is the one that stops a multi-GB
/// extract from evicting every other app's working set through the file cache.
///
/// Deliberately per-THREAD, never PROCESS_MODE_BACKGROUND_BEGIN: that one demotes every thread
/// including the WebView2 UI/message pump (the docs say outright that processes which interact with
/// the user should not use it), and its END "resets all threads" without being able to tell which
/// were already in background mode — an asymmetry there is no way to undo correctly.
///
/// Requires no privileges — Relic stays asInvoker. This LOWERS priority, it never raises any.
/// </summary>
/// <remarks>
/// Two rules the callers must keep, both from the documented priority-inversion hazard ("a background
/// thread should minimize sharing resources such as critical sections, heaps, and handles with other
/// threads in the process"):
/// <list type="number">
/// <item>Only ever use this on a DEDICATED thread (<c>TaskCreationOptions.LongRunning</c>), never on
/// a pool thread — the mode outlives the work item and would poison whatever runs there next — and
/// never across an <c>await</c>, since the continuation may resume on a different thread while this
/// one stays demoted forever.</item>
/// <item>A thread in background mode must not hold a lock the UI thread waits on. Very-low-priority
/// I/O can be starved for a long time, and the UI thread would wait exactly that long. In practice
/// that means leaving the mode before touching <see cref="Log"/> (its file-append is serialized by a
/// process-wide lock the UI thread also takes) — hence <see cref="Dispose"/> being idempotent, so an
/// error path can end the mode early and the enclosing <c>using</c> still compiles and is harmless.
/// </item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BackgroundIoScope : IDisposable
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    private bool _active;

    private BackgroundIoScope(bool active) => _active = active;

    /// <summary>
    /// Enters background mode on the current thread. Never throws and never returns null: if the call
    /// fails (already in background mode, or an OS that refuses it) the scope is simply inert, so the
    /// caller does the same work at normal priority instead of failing an install over a hint.
    /// </summary>
    public static BackgroundIoScope Begin()
    {
        // GetCurrentThread returns a PSEUDO-handle valid only on this thread — which is exactly the
        // scope we want, and why it must not be cached or handed to another thread.
        bool ok = false;
        try { ok = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin); }
        catch (DllNotFoundException) { /* not Windows — stay at normal priority */ }
        catch (EntryPointNotFoundException) { }
        return new BackgroundIoScope(ok);
    }

    /// <summary>Leaves background mode. Idempotent, and safe to call early (see the class remarks):
    /// the second call from the enclosing <c>using</c> does nothing.</summary>
    public void Dispose()
    {
        if (!_active) return;
        _active = false; // cleared FIRST: a failed END must not leave Dispose retrying forever
        try { SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
