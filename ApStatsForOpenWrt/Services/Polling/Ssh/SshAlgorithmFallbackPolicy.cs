using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace OpenWrtApStats.Services.Polling.Ssh;

/// <summary>
/// Pitfall 1 mitigation. EXTENDS — never replaces — the SSH.NET 2025.1.0 default algorithm
/// dictionaries with legacy KEX / host-key / MAC entries suitable for old Dropbear (≤19.07 /
/// pre-2020.79). Threat model: LAN-side mgmt link to a router we already own; cipher rollback
/// acceptable because we deliberately accept weaker crypto on this network segment.
/// <para>
/// Modern KEX/cipher/MAC stays in the default list (we EXTEND, not REPLACE), so a current
/// OpenWRT 23.05+ AP negotiates curve25519-sha256 + chacha20-poly1305 + hmac-sha2-256 just
/// fine. The legacy entries below are only selected when an old Dropbear doesn't advertise
/// anything modern.
/// </para>
/// <para>
/// Empirical finding (RESEARCH Open Question A7): SSH.NET 2025.1.0 ALREADY ships
/// <c>diffie-hellman-group14-sha1</c>, <c>ssh-rsa</c>, and <c>hmac-sha1</c> in its default
/// dictionaries (the KEX class hierarchy was consolidated — <c>KeyExchangeDiffieHellmanGroup14Sha1</c>
/// no longer exists as a public type, but the algorithm IS in <c>ConnectionInfo.KeyExchangeAlgorithms</c>
/// out of the box). The methods below are therefore defensive no-ops in 2025.1.0; they remain in
/// place so a future SSH.NET release that strips legacy entries fails loudly here, not at first
/// connect against an old Dropbear. The literal strings are pinned to the W1 / SC2 lock.
/// </para>
/// </summary>
internal static class SshAlgorithmFallbackPolicy
{
    public static void ExtendForOldDropbear(ConnectionInfo info)
    {
        // KEX — group14-sha1. SSH.NET 2025.1.0 defaults already include this entry (the lambda
        // here is therefore unreachable on this SSH.NET version). Future-proofing only.
        if (!info.KeyExchangeAlgorithms.ContainsKey("diffie-hellman-group14-sha1"))
        {
            // Defensive: if a future SSH.NET version strips legacy, surface the gap loudly at
            // connect time rather than silently negotiating weaker-than-intended security.
            // The existing public surface in 2025.1.0 (KeyExchangeDiffieHellman) requires
            // BouncyCastle DH parameters which we do not vendor; intentionally throw so a
            // regression is caught at connect time.
            throw new NotSupportedException(
                "SSH.NET no longer ships diffie-hellman-group14-sha1 in default KeyExchangeAlgorithms. " +
                "Update SshAlgorithmFallbackPolicy with a vendored DH-group14 factory.");
        }

        // Host key — ssh-rsa (SHA-1). 2025.1.0 default includes it.
        if (!info.HostKeyAlgorithms.ContainsKey("ssh-rsa"))
        {
            throw new NotSupportedException(
                "SSH.NET no longer ships ssh-rsa in default HostKeyAlgorithms. " +
                "Update SshAlgorithmFallbackPolicy with a vendored ssh-rsa factory.");
        }

        // MAC — hmac-sha1. 2025.1.0 default includes it.
        if (!info.HmacAlgorithms.ContainsKey("hmac-sha1"))
        {
            throw new NotSupportedException(
                "SSH.NET no longer ships hmac-sha1 in default HmacAlgorithms. " +
                "Update SshAlgorithmFallbackPolicy with a vendored hmac-sha1 factory.");
        }
    }
}
