using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Relic.Core.Util;

namespace Relic.Core.Setup;

/// <summary>What the Authenticode check of a file found (<see cref="Authenticode.Check(string, string)"/>).</summary>
public enum AuthenticodeVerdict
{
    /// <summary>Windows trusts its signature, and the certificate that signed it names the signer asked for.</summary>
    Trusted,
    /// <summary>No signature Windows trusts: none, a hash that is not the file's, a chain to no trusted root, a
    /// revoked certificate.</summary>
    NotTrusted,
    /// <summary>A signature Windows trusts, from another signer than the one asked for.</summary>
    OtherSigner,
    /// <summary>Whether a certificate of its chain was revoked could not be learned (the revocation server did not
    /// answer).</summary>
    RevocationUnknown,
    /// <summary>The file is not there, or could not be read.</summary>
    Unreadable,
}

/// <summary>
/// The Authenticode check of a downloaded installer: Windows' own verdict on its signature (WinVerifyTrust with
/// the generic verify action — the file's hash, the signature, a chain to a root this PC trusts, and that no
/// certificate of that chain but the root was revoked, asked online) and the name of whoever signed it, read from
/// the very signature Windows checked. An installer that fails either is not run.
/// </summary>
public static class Authenticode
{
    /// <summary><see cref="Check(string, string)"/> is <see cref="AuthenticodeVerdict.Trusted"/>. Never throws.</summary>
    public static bool Verify(string path, string signer) => Check(path, signer) == AuthenticodeVerdict.Trusted;

    /// <summary>The verdict on the file at <paramref name="path"/> for <paramref name="signer"/>, the file held open
    /// for reading alone while it is checked (<see cref="Check(FileStream, string)"/>). Never throws.</summary>
    public static AuthenticodeVerdict Check(string path, string signer)
    {
        try
        {
            if (!File.Exists(path)) return AuthenticodeVerdict.Unreadable;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Check(file, signer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"authenticode: {Path.GetFileName(path)} could not be read ({ex.Message})");
            return AuthenticodeVerdict.Unreadable;
        }
    }

    /// <summary>
    /// The verdict on the file <paramref name="file"/> holds open — opened for reading and shared for reading alone,
    /// so nothing can write, rename or delete it while the caller keeps the handle: what is checked here is what a
    /// caller that starts it before letting the handle go runs. Windows checks it through that handle; the signer
    /// must be named by the certificate of the signature Windows checked (<see cref="SubjectNames(X500DistinguishedName?, string?)"/>).
    /// Never throws.
    /// </summary>
    public static AuthenticodeVerdict Check(FileStream file, string signer)
    {
        string name = Path.GetFileName(file?.Name ?? "");
        try
        {
            ArgumentNullException.ThrowIfNull(file);
            var (trust, subject) = TrustAndSigner(file.Name, file.SafeFileHandle.DangerousGetHandle());
            if (trust is CertERevocationFailure or CryptERevocationOffline or CryptENoRevocationCheck)
            {
                Log.Info($"authenticode: {name} -- whether its certificate was revoked could not be learned (0x{trust:X8})");
                return AuthenticodeVerdict.RevocationUnknown;
            }
            if (trust != 0)
            {
                Log.Info($"authenticode: {name} is not trusted (0x{trust:X8})");
                return AuthenticodeVerdict.NotTrusted;
            }
            if (SubjectNames(subject, signer)) return AuthenticodeVerdict.Trusted;
            Log.Info($"authenticode: {name} is signed by \"{subject?.Name}\", not by \"{signer}\"");
            return AuthenticodeVerdict.OtherSigner;
        }
        catch (Exception ex)
        {
            Log.Info($"authenticode: {name} could not be checked ({ex.Message})");
            return AuthenticodeVerdict.Unreadable;
        }
    }

    /// <summary>The subject of the certificate that signed the file, or null when it has no signature that names
    /// one. It says nothing about whether that signature is valid: <see cref="Check(string, string)"/> asks Windows for
    /// that.</summary>
    public static string? SignerSubject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // the signer of an Authenticode signature is read from the file, not loaded as a certificate
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Does the distinguished name <paramref name="subject"/> name <paramref name="signer"/>: its organisation (O) — the
    /// name the certificate authority validated — equal to it, every O it holds and at least one, blanks trimmed and runs
    /// of blanks counted as one, letter case not counted? Punctuation counts ("Docker, Inc." is not "Docker Inc"). The
    /// common name (CN) is the subscriber's own label and decides nothing: a CN that says the signer beside another
    /// organisation does not pass, and a product's name there beside the signer's organisation (dotnet.exe:
    /// <c>CN=.NET, O=Microsoft Corporation</c>) does. A name with a multi-valued part is not judged: false. Pure.
    /// </summary>
    public static bool SubjectNames(X500DistinguishedName? subject, string? signer)
    {
        string want = Norm(signer);
        if (want.Length == 0 || subject is null) return false;
        try
        {
            int organisations = 0;
            foreach (var rdn in subject.EnumerateRelativeDistinguishedNames())
            {
                if (rdn.HasMultipleElements) return false;
                if (rdn.GetSingleElementType().Value != "2.5.4.10") continue;
                if (!string.Equals(Norm(rdn.GetSingleElementValue()), want, StringComparison.OrdinalIgnoreCase)) return false;
                organisations++;
            }
            return organisations > 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary><see cref="SubjectNames(X500DistinguishedName?, string?)"/> on a name written out ("CN=…, O=…"); false
    /// for one that cannot be read. Pure.</summary>
    public static bool SubjectNames(string? subject, string? signer)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        try { return SubjectNames(new X500DistinguishedName(subject, X500DistinguishedNameFlags.None), signer); }
        catch (Exception) { return false; }
    }

    private static string Norm(string? s) => Regex.Replace((s ?? "").Trim(), @"\s+", " ");

    private const uint WtdUiNone = 2, WtdRevokeWholeChain = 1, WtdChoiceFile = 1, WtdStateActionVerify = 1, WtdStateActionClose = 2;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x80;
    private const int CertERevocationFailure = unchecked((int)0x800B010E), CryptERevocationOffline = unchecked((int)0x80092013),
        CryptENoRevocationCheck = unchecked((int)0x80092012);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WintrustData pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    /// <summary>WinVerifyTrust's answer for the file at <paramref name="path"/>: 0 = a signature Windows trusts,
    /// anything else an HRESULT that says why not (TRUST_E_NOSIGNATURE for an unsigned file, CERT_E_REVOKED for a
    /// revoked certificate, CRYPT_E_REVOCATION_OFFLINE when the revocation server did not answer; the error of a file
    /// that cannot be opened). No window is shown. Never throws.</summary>
    public static int TrustResult(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TrustAndSigner(file.Name, file.SafeFileHandle.DangerousGetHandle()).Trust;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ex.HResult != 0 ? ex.HResult : -1;
        }
    }

    /// <summary>One WinVerifyTrust call on the open file <paramref name="handle"/> (<paramref name="path"/> names it),
    /// revocation asked online for every certificate of the chain but the root; with a trusted signature, also the
    /// subject of the certificate that made it, read from the provider data of that same call (its first signer, the
    /// first certificate of that signer's chain). The state is closed before it returns.</summary>
    private static (int Trust, X500DistinguishedName? Subject) TrustAndSigner(string path, IntPtr handle)
    {
        IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            var file = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = pathPtr, hFile = handle };
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeWholeChain,
                dwUnionChoice = WtdChoiceFile,
                pFile = filePtr,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckChainExcludeRoot,
            };
            var action = GenericVerifyV2;
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            X500DistinguishedName? subject = null;
            try
            {
                if (result == 0) subject = SignerOf(data.hWVTStateData);
            }
            finally
            {
                data.dwStateAction = WtdStateActionClose;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            return (result, subject);
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    /// <summary>The subject of the signing certificate in the provider data of a verify call: CRYPT_PROVIDER_SGNR's
    /// <c>pasCertChain</c> (after its size, a FILETIME and the chain's count) → the first CRYPT_PROVIDER_CERT's
    /// <c>pCert</c>. Null when the data holds none.</summary>
    private static X500DistinguishedName? SignerOf(IntPtr state)
    {
        if (state == IntPtr.Zero) return null;
        IntPtr provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero) return null;
        IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero || Marshal.ReadInt32(signer, 12) < 1) return null;
        IntPtr chain = Marshal.ReadIntPtr(signer, 16);
        if (chain == IntPtr.Zero) return null;
        IntPtr context = Marshal.ReadIntPtr(chain, IntPtr.Size);
        if (context == IntPtr.Zero) return null;
        using var cert = new X509Certificate2(context);
        return new X500DistinguishedName(cert.SubjectName.RawData);
    }
}
