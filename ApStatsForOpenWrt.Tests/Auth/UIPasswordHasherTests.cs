using Microsoft.AspNetCore.Identity;
using ApStatsForOpenWrt.Services.Auth;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Auth;

/// <summary>
/// SEC-04 PBKDF2 hasher contract tests. Plus Pitfall 8 detection — a committed Identity V3
/// hash MUST still verify on future runs (re-hash if SuccessRehashNeeded fires; never
/// silently downgrade).
///
/// <para>
/// M-SEC-03 (plan 15-02) added a policy gate to <see cref="UIPasswordHasher.Hash"/>; the
/// plaintext fixtures below were updated from the original Phase 3 values
/// (<c>"hunter2"</c>, <c>"p"</c>) to policy-compliant equivalents (<c>"Hunter22!"</c>,
/// <c>"Passw0rd!"</c>) that preserve the original test intent (round-trip / wrong-pw fail /
/// V3 marker). Plan 15-02's <c>UIPasswordHasherPolicyTests</c> pins the policy contract;
/// these tests cover the hash format invariants.
/// </para>
/// </summary>
public sealed class UIPasswordHasherTests
{
    [Fact]
    public void HashThenVerify_RoundTrips()
    {
        var hasher = new UIPasswordHasher();
        var hash = hasher.Hash("Hunter22!");
        Assert.Equal(PasswordVerificationResult.Success, hasher.Verify(hash, "Hunter22!"));
    }

    [Fact]
    public void Verify_WithWrongPassword_ReturnsFailed()
    {
        var hasher = new UIPasswordHasher();
        var hash = hasher.Hash("Hunter22!");
        // "wrong" is intentionally NOT policy-compliant -- this proves Verify is not
        // policy-gated on the candidate plaintext (legacy v0.9-v0.11 passwords must
        // still authenticate; M-SEC-03 enforcement is hash-side only).
        Assert.Equal(PasswordVerificationResult.Failed, hasher.Verify(hash, "wrong"));
    }

    [Fact]
    public void Hash_Deterministic_OutputIsNonEmpty_AndStartsWithIdentityV3Marker()
    {
        // Identity V3 hashes are versioned: first byte is 0x01 (V3 marker). Encoded base64,
        // V3 hashes start with 'AQAAA' (the leading 'AQAA' = base64 of byte 0x01,...).
        var hasher = new UIPasswordHasher();
        var hash = hasher.Hash("Passw0rd!");
        Assert.False(string.IsNullOrEmpty(hash));
        Assert.StartsWith("AQAAA", hash);
    }
}
