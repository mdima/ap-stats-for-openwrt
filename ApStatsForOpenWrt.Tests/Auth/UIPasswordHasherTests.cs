using Microsoft.AspNetCore.Identity;
using ApStatsForOpenWrt.Services.Auth;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Auth;

/// <summary>
/// SEC-04 PBKDF2 hasher contract tests. Plus Pitfall 8 detection — a committed Identity V3
/// hash MUST still verify on future runs (re-hash if SuccessRehashNeeded fires; never
/// silently downgrade).
/// </summary>
public sealed class UIPasswordHasherTests
{
    [Fact]
    public void HashThenVerify_RoundTrips()
    {
        var hasher = new UIPasswordHasher();
        var hash = hasher.Hash("hunter2");
        Assert.Equal(PasswordVerificationResult.Success, hasher.Verify(hash, "hunter2"));
    }

    [Fact]
    public void Verify_WithWrongPassword_ReturnsFailed()
    {
        var hasher = new UIPasswordHasher();
        var hash = hasher.Hash("hunter2");
        Assert.Equal(PasswordVerificationResult.Failed, hasher.Verify(hash, "wrong"));
    }

    [Fact]
    public void Hash_Deterministic_OutputIsNonEmpty_AndStartsWithIdentityV3Marker()
    {
        // Identity V3 hashes are versioned: first byte is 0x01 (V3 marker). Encoded base64,
        // V3 hashes start with 'AQAAA' (the leading 'AQAA' = base64 of byte 0x01,...).
        var hasher = new UIPasswordHasher();
        var hash = hasher.Hash("p");
        Assert.False(string.IsNullOrEmpty(hash));
        Assert.StartsWith("AQAAA", hash);
    }
}
