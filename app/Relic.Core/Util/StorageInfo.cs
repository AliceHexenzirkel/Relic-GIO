using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Relic.Core.Util;

/// <summary>
/// Asks the storage driver whether the volume behind a path is a spinning disk, so the extractor can
/// pick a worker count that suits the medium instead of one that suits the CPU.
///
/// Unelevated by design: the volume is opened with
/// <c>dwDesiredAccess = 0</c>, which CreateFile documents as enough to query device attributes
/// "without requiring higher-level data read/write permission" — the admin requirement applies to
/// opening a volume for raw READS/WRITES, which is exactly what we do not do.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StorageInfo
{
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private const uint PropertyStandardQuery = 0;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public uint AdditionalParameters; // the API's trailing BYTE[1], padded to a DWORD
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFile(
        string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device, uint code, ref StoragePropertyQuery inBuf, int inSize,
        out DeviceSeekPenaltyDescriptor outBuf, int outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// True = spinning disk, false = SSD/flash, <c>null</c> = could not tell. Callers MUST treat null
    /// as "unknown" and keep their normal default — never as "not an HDD". USB bridges, some RAID and
    /// NVMe stacks, and network paths answer ERROR_NOT_SUPPORTED or refuse the open entirely, and a
    /// guess there would silently mis-tune every install on that hardware.
    /// </summary>
    public static bool? HasSeekPenalty(string path)
    {
        string? volume = VolumeDevice(path);
        if (volume is null) return null;

        IntPtr handle = IntPtr.Zero;
        try
        {
            // access = 0: attributes only. Asking for GENERIC_READ here is what would make this
            // require administrator — the whole reason this probe is allowed to exist in a launcher
            // that must never elevate.
            handle = CreateFile(volume, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                Log.Info($"storage: could not open {volume} (err={Marshal.GetLastWin32Error()}) — disk type unknown");
                return null;
            }

            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery,
            };
            bool ok = DeviceIoControl(handle, IoctlStorageQueryProperty,
                ref query, Marshal.SizeOf<StoragePropertyQuery>(),
                out var descriptor, Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
                out uint returned, IntPtr.Zero);

            // A driver that answers with a SHORT buffer has not filled IncursSeekPenalty — that stale
            // zero would read as "SSD" and is exactly the false confidence this method must not emit.
            if (!ok || returned < Marshal.SizeOf<DeviceSeekPenaltyDescriptor>())
            {
                Log.Info($"storage: {volume} does not report a seek penalty (err={Marshal.GetLastWin32Error()}, " +
                         $"bytes={returned}) — disk type unknown");
                return null;
            }
            return descriptor.IncursSeekPenalty;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // not Windows
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
        }
    }

    /// <summary>Maps a path to the <c>\\.\X:</c> device its volume lives on, or null when there is no
    /// such device to ask — a UNC share, a path rooted somewhere exotic, or garbage.</summary>
    private static string? VolumeDevice(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            // Only a real drive letter has a \\.\X: device. A UNC root comes back as \\server\share.
            if (root is null || root.Length < 2 || root[1] != ':') return null;
            return $@"\\.\{char.ToUpperInvariant(root[0])}:";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
