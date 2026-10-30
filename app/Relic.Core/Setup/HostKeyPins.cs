using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Relic.Core.Setup;

/// <summary>One SSH host key Relic trusts for a host and port: the key's own type (what its blob names —
/// <c>ssh-ed25519</c>, <c>ssh-rsa</c>, <c>ecdsa-sha2-nistp256</c>), for display only, and its fingerprint in
/// OpenSSH's form, <c>SHA256:&lt;base64 without padding&gt;</c> — what <c>ssh-keygen -lf</c> prints. Not a
/// secret.</summary>
public sealed record HostKeyPin(string Type, string Fingerprint);

/// <summary>How a key a server presents stands against the keys pinned for its host and port.</summary>
public enum HostKeyVerdict
{
    /// <summary>Nothing is pinned for that host and port: the key is seen for the first time.</summary>
    New,
    /// <summary>The key is one of the pinned ones.</summary>
    Match,
    /// <summary>Keys are pinned there and this one is none of them — a key of another type included.</summary>
    Changed,
}

/// <summary>What a run over SSH accepts as the server's key (<c>AgentDeployer</c>). <see cref="Pins"/> = the keys
/// pinned for the run's host and port (empty: none yet). <see cref="Accepted"/> = the very fingerprint the user
/// confirmed (Quick Start): when it is set, that key and no other is trusted — and a key the pins call changed only
/// with <see cref="AcceptChanged"/>, which only the user's answer to the changed-key warning sets ("Trust the new
/// key"). Without an accepted fingerprint a pinned key is trusted, a key seen for the first time is trusted for a run
/// that records it only once it succeeded, and a changed key is refused.</summary>
public sealed record HostKeyCheck(IReadOnlyList<HostKeyPin> Pins, string? Accepted = null, bool AcceptChanged = false);

/// <summary>
/// The SSH host-key pins of the Linux boxes Relic installs an agent on: one list per <c>host:port</c>
/// (<see cref="Key"/>), one decision (<see cref="Decide"/>). The pins themselves are kept by the caller; nothing
/// here reads or writes a file.
/// </summary>
public static class HostKeyPins
{
    /// <summary>The key a host and port are pinned under: the host trimmed, one pair of brackets around it dropped
    /// (<c>[2001:db8::1]</c> is the host <c>2001:db8::1</c>), its trailing dots dropped; an IP literal then in its
    /// canonical form — the address a connection to it reaches: an IPv6 one compressed and lower-cased
    /// (<c>2001:0db8:0:0:0:0:0:1</c> is <c>2001:db8::1</c>), an IPv4 one written with leading zeros as the address it
    /// is read as —, any other host lower-cased; a colon, the port. An IPv6 host is told apart by the LAST colon.
    /// Pure.</summary>
    public static string Key(string? host, int port)
    {
        string h = (host ?? "").Trim();
        if (h.Length >= 2 && h[0] == '[' && h[^1] == ']') h = h[1..^1].Trim();
        h = h.TrimEnd('.');
        h = System.Net.IPAddress.TryParse(h, out var ip) ? ip.ToString() : h.ToLowerInvariant();
        return h + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The ONE decision: no pin for the host and port = <see cref="HostKeyVerdict.New"/>; the fingerprint
    /// among the pinned ones = <see cref="HostKeyVerdict.Match"/>; anything else — another fingerprint, a key of
    /// another type than the pinned one — <see cref="HostKeyVerdict.Changed"/>. Fingerprints are compared as
    /// written (base64 is case-sensitive). Pure.</summary>
    public static HostKeyVerdict Decide(IEnumerable<HostKeyPin>? pins, string? fingerprint)
    {
        var list = (pins ?? Enumerable.Empty<HostKeyPin>()).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Fingerprint)).ToList();
        if (list.Count == 0) return HostKeyVerdict.New;
        string fp = (fingerprint ?? "").Trim();
        return list.Any(p => string.Equals(p.Fingerprint.Trim(), fp, StringComparison.Ordinal)) ? HostKeyVerdict.Match : HostKeyVerdict.Changed;
    }

    /// <summary>"new", "match" or "changed" — the verdict as the page reads it.</summary>
    public static string Name(HostKeyVerdict verdict) => verdict switch
    {
        HostKeyVerdict.Match => "match",
        HostKeyVerdict.Changed => "changed",
        _ => "new",
    };

    /// <summary>Is the key with <paramref name="fingerprint"/> trusted under <paramref name="check"/>? No check at
    /// all = yes (a caller that keeps no pins). An accepted fingerprint = that key alone, and — when the pins call it
    /// changed (<see cref="Decide"/>) — only with <see cref="HostKeyCheck.AcceptChanged"/>. Otherwise every key but a
    /// changed one. Pure.</summary>
    public static bool Trusts(HostKeyCheck? check, string? fingerprint)
    {
        if (check is null) return true;
        string fp = (fingerprint ?? "").Trim();
        if (fp.Length == 0) return false;
        bool changed = Decide(check.Pins, fp) == HostKeyVerdict.Changed;
        if (!string.IsNullOrWhiteSpace(check.Accepted))
            return string.Equals(fp, check.Accepted.Trim(), StringComparison.Ordinal) && (!changed || check.AcceptChanged);
        return !changed;
    }

    /// <summary>The key type a host key algorithm makes the server present: the algorithm's own name, except RSA's
    /// SHA-2 signatures, which sign with an <c>ssh-rsa</c> key (<c>rsa-sha2-256</c>, <c>rsa-sha2-512</c>, and their
    /// certificate forms with <c>ssh-rsa-cert-v01@openssh.com</c>). Pure.</summary>
    public static string KeyTypeOf(string? algorithm) => (algorithm ?? "").Trim() switch
    {
        "rsa-sha2-256" or "rsa-sha2-512" => "ssh-rsa",
        "rsa-sha2-256-cert-v01@openssh.com" or "rsa-sha2-512-cert-v01@openssh.com" => "ssh-rsa-cert-v01@openssh.com",
        var name => name,
    };

    /// <summary>
    /// The host key algorithms of <paramref name="offered"/> (the client's list, in its order) that make the server
    /// present a key of a type <paramref name="pins"/> name (<see cref="KeyTypeOf"/>) — what a connection to a pinned
    /// host and port offers, so a server that holds keys of several types presents the pinned one, the way OpenSSH
    /// asks a host it knows: a key of another type the server adds later is no changed key. Null = offer them all: no
    /// pin, a pin of no known type, or none of <paramref name="offered"/> fits. Pure.
    /// </summary>
    public static IReadOnlyList<string>? AlgorithmsFor(IEnumerable<string>? offered, IEnumerable<HostKeyPin>? pins)
    {
        var types = (pins ?? Enumerable.Empty<HostKeyPin>()).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Fingerprint))
            .Select(p => (p.Type ?? "").Trim()).ToList();
        if (types.Count == 0 || types.Any(t => t.Length == 0)) return null;
        var kept = (offered ?? Enumerable.Empty<string>()).Where(a => types.Contains(KeyTypeOf(a), StringComparer.Ordinal)).ToList();
        return kept.Count > 0 ? kept : null;
    }

    /// <summary>OpenSSH's fingerprint of a key blob: <c>SHA256:</c> and the base64 of its sha256 without the
    /// padding. Pure.</summary>
    public static string Fingerprint(byte[]? keyBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(keyBlob ?? Array.Empty<byte>())).TrimEnd('=');

    /// <summary>The type a key blob names itself by: its first SSH string (a 4-byte big-endian length, then
    /// that many bytes of ASCII) — <c>ssh-ed25519</c>, <c>ssh-rsa</c>, <c>ecdsa-sha2-nistp256</c>. Never the
    /// algorithm the connection negotiated, which for an RSA key is the signature name and changes from one
    /// connection to the next. "" for a blob that does not start with such a string. Pure.</summary>
    public static string KeyType(byte[]? keyBlob)
    {
        if (keyBlob is null || keyBlob.Length < 4) return "";
        long length = ((long)keyBlob[0] << 24) | ((long)keyBlob[1] << 16) | ((long)keyBlob[2] << 8) | keyBlob[3];
        if (length <= 0 || length > 64 || 4 + length > keyBlob.Length) return "";
        var sb = new StringBuilder((int)length);
        for (int i = 4; i < 4 + length; i++)
        {
            byte b = keyBlob[i];
            if (b < 0x21 || b > 0x7E) return "";
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>The pins as a sentence part: "<c>&lt;type&gt; &lt;fingerprint&gt;</c>" per pin, joined by ", "
    /// ("" for none). Pure.</summary>
    public static string Describe(IEnumerable<HostKeyPin>? pins) =>
        string.Join(", ", (pins ?? Enumerable.Empty<HostKeyPin>()).Where(p => p is not null)
            .Select(p => (p.Type ?? "").Trim().Length > 0 ? p.Type!.Trim() + " " + p.Fingerprint : p.Fingerprint));
}
