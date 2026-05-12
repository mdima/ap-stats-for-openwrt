using ApStatsForOpenWrt.Services.Polling.Ssh;
using Renci.SshNet;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Polling;

public sealed class SshAlgorithmFallbackPolicyTests
{
    /// <summary>
    /// W1 lock: T-2-09 (cipher rollback) regression. Asserts <c>ExtendForOldDropbear</c> EXTENDS
    /// the existing algorithm dictionaries — it MUST NOT call <c>.Clear()</c> before <c>.Add()</c>.
    /// A regression that wipes modern algorithms would silently weaken security on current
    /// OpenWRT 23.05+ APs (which negotiate curve25519-sha256, chacha20-poly1305, hmac-sha2-256
    /// by default).
    /// <para>
    /// Empirical finding (RESEARCH Open Question A7): SSH.NET 2025.1.0 ALREADY ships
    /// <c>diffie-hellman-group14-sha1</c> / <c>ssh-rsa</c> / <c>hmac-sha1</c> in defaults; the
    /// policy is therefore a defensive no-op on this version. The load-bearing assertion is
    /// "modern algorithms still present + legacy entries present" — the count is unchanged but
    /// the contract holds.
    /// </para>
    /// </summary>
    [Fact]
    public void SshAlgorithmFallbackPolicy_ExtendsAlgorithmDictionaries_DoesNotReplaceModern()
    {
        var info = new ConnectionInfo("dummy", "dummy", new PasswordAuthenticationMethod("dummy", "dummy"));
        var kexBefore = info.KeyExchangeAlgorithms.Count;
        var hostKeyBefore = info.HostKeyAlgorithms.Count;
        var hmacBefore = info.HmacAlgorithms.Count;

        // Capture an example modern algorithm name we expect to STILL be present after the call.
        // SSH.NET 2025.1.0 ships curve25519-sha256 by default in the KEX list; verify it survives.
        var hadModernKex = info.KeyExchangeAlgorithms.ContainsKey("curve25519-sha256");

        SshAlgorithmFallbackPolicy.ExtendForOldDropbear(info);

        // EXTEND, not REPLACE: post-call counts must be at least pre-call counts (not strictly
        // greater because SSH.NET 2025.1.0 already ships the legacy entries — see class doc-comment).
        Assert.True(info.KeyExchangeAlgorithms.Count >= kexBefore, "KEX dictionary must not shrink");
        Assert.True(info.HostKeyAlgorithms.Count >= hostKeyBefore, "HostKey dictionary must not shrink");
        Assert.True(info.HmacAlgorithms.Count >= hmacBefore, "Hmac dictionary must not shrink");

        // Modern algorithms (if present pre-call) must STILL be present post-call.
        if (hadModernKex)
            Assert.True(info.KeyExchangeAlgorithms.ContainsKey("curve25519-sha256"),
                "Modern KEX algorithms must NOT be removed by the fallback extension");

        // The legacy entries the policy guarantees are present (W1 lock — pinned literal strings).
        Assert.True(info.KeyExchangeAlgorithms.ContainsKey("diffie-hellman-group14-sha1"));
        Assert.True(info.HostKeyAlgorithms.ContainsKey("ssh-rsa"));
        Assert.True(info.HmacAlgorithms.ContainsKey("hmac-sha1"));
    }
}
