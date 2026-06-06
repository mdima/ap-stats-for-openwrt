using Xunit;

namespace ApStatsForOpenWrt.Tests.Dropbear;

/// <summary>
/// M-FLD-01 (Phase 22 / Plan 22-03 / D-07) — hardware-free Dropbear-in-Docker SSH-negotiation arm.
/// Drives the PRODUCTION SSH.NET path (<see cref="DropbearCompatFixture.CreateProductionClient"/> ==
/// the exact <c>RealSshSession.ConnectAsync</c> construction) against two in-repo Dropbear servers —
/// a legacy ~2019.78 build and a current build — and regression-locks the five
/// <c>ConnectionInfo.Current*</c> negotiated-algorithm properties the M-SEC-08 logging surfaces,
/// plus one BusyBox-shell exec round-trip per version.
///
/// Mirrors <c>Tests/Smoke/HealthcheckSmokeTests.cs</c> (trait + collection + try/finally lifecycle).
/// Trait-gated <c>Category=DropbearCompat</c> (+ <c>RequiresDocker</c>) so the default ex-Smoke run
/// AND the Smoke run both exclude it; the dropbear-compat.yml CI job runs it with the inverse filter.
///
/// <para>
/// OBSERVED-AND-PINNED algorithm strings (RESEARCH A1 — captured on the first run, then pinned;
/// re-capture and re-pin if an SSH.NET bump changes them, which is exactly the regression this arm
/// guards):
/// </para>
/// <list type="bullet">
/// <item>LEGACY (dropbear 2019.78-r1):  KEX=curve25519-sha256, HostKey=<b>ssh-rsa</b> (SHA-1 family),
///   Enc=aes128-ctr, C-HMAC=hmac-sha2-256, S-HMAC=hmac-sha2-256</item>
/// <item>CURRENT (dropbear 2024.86-r0): KEX=curve25519-sha256, HostKey=<b>rsa-sha2-256</b> (SHA-2),
///   Enc=aes128-ctr, C-HMAC=hmac-sha2-256, S-HMAC=hmac-sha2-256</item>
/// </list>
/// <para>
/// The DIFFERENTIATING, SHA-1-family fallback the production <c>SshAlgorithmFallbackPolicy.
/// ExtendForOldDropbear</c> guards is the <b>host-key signature algorithm</b>: dropbear 2019.78 only
/// offers the legacy <c>ssh-rsa</c> (RSA-with-SHA-1) host-key shape — exactly the literal
/// <c>RealSshSession.IsSha1Family("HostKey","ssh-rsa")</c> flags as weak (M-SEC-08, WR-03) — whereas
/// the current build advertises <c>rsa-sha2-256</c>. The 2019.78 build already supports modern
/// KEX/cipher/MAC (curve25519 / aes-ctr / hmac-sha2), so those slots land on the same modern values
/// on both servers; the host-key slot is the regression-lock for the SHA-1 fallback path.
/// </para>
/// </summary>
[Collection("DropbearCompat")]
[Trait("Category", "DropbearCompat")]
[Trait("RequiresDocker", "true")]
public sealed class DropbearCompatTests(DropbearCompatFixture fixture) : IClassFixture<DropbearCompatFixture>
{
    private readonly DropbearCompatFixture _fx = fixture;

    [Fact]
    public async Task LegacyDropbear_NegotiatesSha1FamilyFallback_AndRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _fx.CreateProductionClient(_fx.LegacyEndpoint);
        try
        {
            // Production wraps the blocking Connect() in Task.Run + ct (RealSshSession.cs:59).
            await Task.Run(() => client.Connect(), ct);

            var ci = client.ConnectionInfo;

            // OBSERVED legacy (dropbear 2019.78-r1) negotiated set — pinned (RESEARCH A1).
            // The host-key slot is the SHA-1-family fallback the arm regression-locks: a future
            // SSH.NET / fallback-policy change that stops negotiating ssh-rsa against old Dropbear
            // breaks here at PR/nightly time, hardware-free.
            Assert.Equal("curve25519-sha256", ci.CurrentKeyExchangeAlgorithm);
            Assert.Equal("ssh-rsa", ci.CurrentHostKeyAlgorithm);          // SHA-1 family (RSA-with-SHA-1)
            Assert.Equal("aes128-ctr", ci.CurrentClientEncryption);
            Assert.Equal("hmac-sha2-256", ci.CurrentClientHmacAlgorithm);
            Assert.Equal("hmac-sha2-256", ci.CurrentServerHmacAlgorithm);

            // One exec round-trip against the baked-in BusyBox sentinel.
            using var cmd = client.RunCommand("/usr/local/bin/sentinel.sh");
            Assert.Equal("apstats-ok\n", cmd.Result);
        }
        finally
        {
            // SSH.NET dispose-hang mitigation (RealSshSession.DisposeAsync) — swallow.
            try { client.Disconnect(); } catch { /* swallow — SSH.NET issue #1275 */ }
            try { client.Dispose(); } catch { /* swallow */ }
        }
    }

    [Fact]
    public async Task CurrentDropbear_NegotiatesModernAlgorithms_AndRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _fx.CreateProductionClient(_fx.CurrentEndpoint);
        try
        {
            await Task.Run(() => client.Connect(), ct);

            var ci = client.ConnectionInfo;

            // OBSERVED current (dropbear 2024.86-r0) negotiated set — pinned (RESEARCH A1).
            // Host-key slot is rsa-sha2-256 (SHA-2) — the modern counterpart of the legacy ssh-rsa.
            Assert.Equal("curve25519-sha256", ci.CurrentKeyExchangeAlgorithm);
            Assert.Equal("rsa-sha2-256", ci.CurrentHostKeyAlgorithm);     // SHA-2 (modern)
            Assert.Equal("aes128-ctr", ci.CurrentClientEncryption);
            Assert.Equal("hmac-sha2-256", ci.CurrentClientHmacAlgorithm);
            Assert.Equal("hmac-sha2-256", ci.CurrentServerHmacAlgorithm);

            using var cmd = client.RunCommand("/usr/local/bin/sentinel.sh");
            Assert.Equal("apstats-ok\n", cmd.Result);
        }
        finally
        {
            try { client.Disconnect(); } catch { /* swallow — SSH.NET issue #1275 */ }
            try { client.Dispose(); } catch { /* swallow */ }
        }
    }
}
