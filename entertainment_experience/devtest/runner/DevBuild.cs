using System.Text;
using System.Text.RegularExpressions;

namespace Relic.DevTest;

/// <summary>
/// What the runner asks of a DLL file before it stages it: does it carry the DevTest marker, is it an x64 DLL, and
/// which DLLs does it import. Every read is a stream or a seek: a DLL is never read whole.
/// </summary>
internal static class DevBuild
{
    /// <summary>The ASCII marker every DevTest build carries (an RCDATA resource and the status file's
    /// <c>marker</c>). Put together from two parts, as in every script that looks for it, so that no file of the
    /// repository — this program's own build included — carries it whole.</summary>
    public static readonly string Marker = string.Join("", new[] { "RELIC-DEV", "TEST-BUILD" });

    /// <summary>The marker as ASCII or as UTF-16LE anywhere in the file.</summary>
    public static bool CarriesMarker(string path) =>
        ContainsAny(path, Encoding.ASCII.GetBytes(Marker), Encoding.Unicode.GetBytes(Marker));

    /// <summary>Whether any of the byte patterns occurs in the file: 1 MiB at a time, each chunk searched together
    /// with the end of the one before it, so a pattern that straddles two chunks is found.</summary>
    public static bool ContainsAny(string path, params byte[][] patterns)
    {
        int keep = patterns.Max(p => p.Length) - 1;
        byte[] buf = new byte[(1 << 20) + keep];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        int carried = 0;
        while (true)
        {
            int n = fs.Read(buf, carried, buf.Length - carried);
            if (n <= 0) return false;
            int len = carried + n;
            var span = buf.AsSpan(0, len);
            foreach (byte[] p in patterns)
                if (span.IndexOf(p) >= 0) return true;
            carried = Math.Min(keep, len);
            buf.AsSpan(len - carried, carried).CopyTo(buf);
        }
    }

    /// <summary>The name rule of build/pe_imports.ps1 (<c>Test-VcRuntimeDllName</c>): the C, C++, OpenMP, MFC or
    /// ATL runtime of MSVC, release or debug — DLLs that are not a part of Windows. That expression and
    /// build/make_release_bundles.py's <c>VC_RUNTIME_RE</c> change together, and so does this copy.</summary>
    private static readonly Regex VcRuntimeName = new(
        @"^(vcruntime|msvcp|msvcr|concrt|vccorlib|vcomp|vcamp|mfcm?|atl|libomp)(70|71|80|90|100|110|120|140)(?!_win|_clr|_1_clr)|^ucrtbased\.dll$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsVcRuntimeDllName(string name) => VcRuntimeName.IsMatch(name);

    /// <summary>What the PE header says about a file.</summary>
    public sealed record PeInfo(ushort Machine, bool IsDll, bool Is64, IReadOnlyList<string> Imports)
    {
        public bool IsX64Dll => Machine == 0x8664 && IsDll && Is64;
        public IReadOnlyList<string> VcRuntimeImports => Imports.Where(IsVcRuntimeDllName).ToList();
    }

    /// <summary>Reads the PE header, the section table and the import directory by seeking: the names of the DLLs
    /// a file imports, in the order of its import descriptors (as build/pe_imports.ps1's <c>Get-PeImports</c>).</summary>
    public static PeInfo ReadPe(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] dos = ReadAt(fs, 0, 0x40, path);
        if (dos[0] != 0x4D || dos[1] != 0x5A) throw new InvalidDataException($"not a PE file: {path}");
        int pe = BitConverter.ToInt32(dos, 0x3C);
        if (pe < 0x40 || pe > fs.Length - 24) throw new InvalidDataException($"not a PE file: {path}");
        byte[] nt = ReadAt(fs, pe, 24, path);
        if (BitConverter.ToUInt32(nt, 0) != 0x4550) throw new InvalidDataException($"not a PE file: {path}");
        ushort machine = BitConverter.ToUInt16(nt, 4);
        ushort nSections = BitConverter.ToUInt16(nt, 6);
        ushort optSize = BitConverter.ToUInt16(nt, 20);
        ushort characteristics = BitConverter.ToUInt16(nt, 22);
        if (optSize < 96) throw new InvalidDataException($"no optional header: {path}");
        byte[] opt = ReadAt(fs, pe + 24, optSize, path);
        bool is64 = BitConverter.ToUInt16(opt, 0) == 0x20B;
        int dirs = is64 ? 112 : 96;
        if (dirs + 16 > opt.Length) throw new InvalidDataException($"no data directories: {path}");
        uint importRva = BitConverter.ToUInt32(opt, dirs + 8);   // directory 1 = imports

        byte[] table = ReadAt(fs, pe + 24 + optSize, 40 * nSections, path);
        var sections = new List<(uint Va, uint Size, uint Raw)>();
        for (int i = 0; i < nSections; i++)
        {
            int s = 40 * i;
            uint virt = BitConverter.ToUInt32(table, s + 8);
            uint va = BitConverter.ToUInt32(table, s + 12);
            uint rawSize = BitConverter.ToUInt32(table, s + 16);
            uint raw = BitConverter.ToUInt32(table, s + 20);
            sections.Add((va, Math.Max(virt, rawSize), raw));
        }
        long ToOffset(uint rva)
        {
            foreach (var (va, size, raw) in sections)
                if (rva >= va && rva < va + size) return (long)rva - va + raw;
            throw new InvalidDataException($"rva 0x{rva:x} is outside every section of {path}");
        }

        var names = new List<string>();
        if (importRva != 0)
        {
            long d = ToOffset(importRva);
            for (int guard = 0; ; guard++)
            {
                if (guard > 4096) throw new InvalidDataException($"an import directory without an end: {path}");
                byte[] desc = ReadAt(fs, d, 20, path);
                uint nameRva = BitConverter.ToUInt32(desc, 12);
                if (nameRva == 0) break;
                names.Add(ReadCString(fs, ToOffset(nameRva), path));
                d += 20;
            }
        }
        return new PeInfo(machine, (characteristics & 0x2000) != 0, is64, names);
    }

    private static byte[] ReadAt(FileStream fs, long offset, int count, string path)
    {
        if (offset < 0 || count < 0 || offset + count > fs.Length)
            throw new InvalidDataException($"the file ends before byte {offset + count}: {path}");
        byte[] b = new byte[count];
        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(b, 0, count);
        return b;
    }

    private static string ReadCString(FileStream fs, long offset, string path)
    {
        var sb = new StringBuilder();
        fs.Seek(offset, SeekOrigin.Begin);
        for (int i = 0; i < 512; i++)
        {
            int c = fs.ReadByte();
            if (c < 0) throw new InvalidDataException($"an import name runs past the end of {path}");
            if (c == 0) return sb.ToString();
            sb.Append((char)c);
        }
        throw new InvalidDataException($"an import name without an end in {path}");
    }
}
