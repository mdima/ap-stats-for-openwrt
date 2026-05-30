using ApStatsForOpenWrt.Services.Auth;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Services.Auth;

/// <summary>
/// M-SEC-03 — pins the password policy at the <see cref="IUIPasswordHasher.Hash"/> trust
/// boundary.
///
/// <para>
/// Plan 15-02 ships the hasher half of M-SEC-03: <see cref="UIPasswordHasher.Hash"/> enforces
/// the four-part policy (>=8 chars + upper + lower + digit-or-symbol) BEFORE delegating to
/// the underlying <see cref="PasswordHasher{T}"/>. Policy violations throw
/// <see cref="ArgumentException"/> (charter line 181 verbatim; CONTEXT.md decision
/// "Password-policy exception type") with the pinned operator-facing message:
/// </para>
///
/// <para>
/// <c>"Password must be at least 8 characters and include upper, lower, and a digit or symbol."</c>
/// </para>
///
/// <para>
/// Plan 15-05 will catch <see cref="ArgumentException"/> from the three call sites
/// (Login.razor / Settings/Index.razor / <c>/setup/submit-password</c> MapPost lambda) and
/// surface <c>ex.Message</c> verbatim in the banner. The verbatim-message <see cref="Fact"/>
/// uses <see cref="Assert.Equal"/> (NOT <see cref="Assert.Contains"/>) so any copy drift
/// (case, punctuation, whitespace) trips the test before plan 15-05's bUnit banner gates
/// ever run.
/// </para>
///
/// <para>
/// <see cref="UIPasswordHasher.Verify"/> is intentionally NOT policy-gated; legacy v0.9-v0.11
/// passwords that predate the policy must still authenticate. The
/// <see cref="Verify_NotPolicyGated_ReturnsFailedForNonHashInput"/> fact pins that contract.
/// </para>
///
/// <para>
/// Tests carry <c>[Trait("Category","SecurityFix")]</c> so the M-SEC-03 unit-test gate is
/// selectable via <c>dotnet test --filter "Category=SecurityFix"</c> for the milestone
/// verifier.
/// </para>
/// </summary>
[Trait("Category", "SecurityFix")]
public sealed class UIPasswordHasherPolicyTests
{
    /// <summary>
    /// Pinned operator-facing copy from REQUIREMENTS.md line 16 / charter line 559 /
    /// CONTEXT.md. The literal lives inline here (not as a shared constant with the
    /// production code) so any drift in either place trips the test independently.
    /// </summary>
    private const string PinnedPolicyMessage =
        "Password must be at least 8 characters and include upper, lower, and a digit or symbol.";

    // -----------------------------------------------------------------------------------
    // Rejection cases — every non-compliant password throws ArgumentException.
    // The ParamName assertion pins the `throw new ArgumentException(message, nameof(plaintext))`
    // contract; plan 15-05 catches `ArgumentException` and surfaces ex.Message.
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]              // empty: length floor
    [InlineData("a")]             // 1 char
    [InlineData("short")]         // 5 chars, lower only
    [InlineData("sevench")]       // 7 chars, lower only -- boundary: just below the length floor
    [InlineData("password")]      // 8 chars, lower only, no upper, no digit-or-symbol
    [InlineData("PASSWORD")]      // 8 chars, upper only, no lower
    [InlineData("PASSWORD1")]     // 9 chars, upper + digit, no lower
    [InlineData("Password")]      // 8 chars, upper + lower, no digit-or-symbol
    [InlineData("12345678")]      // 8 chars, digit only
    [InlineData("!!!!!!!!")]      // 8 chars, symbol only
    public void Hash_NonCompliantPassword_ThrowsArgumentException(string plaintext)
    {
        var hasher = new UIPasswordHasher();

        var ex = Assert.Throws<ArgumentException>(() => hasher.Hash(plaintext));
        Assert.Equal("plaintext", ex.ParamName);
    }

    // -----------------------------------------------------------------------------------
    // Acceptance cases — every compliant password hashes successfully and the returned
    // hash verifies through the same hasher.
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Password1")]     // 8 chars, upper + lower + digit -- boundary at exactly 8
    [InlineData("Password!")]     // 8 chars, upper + lower + symbol
    [InlineData("PaSsWoRd!")]     // 9 chars, mixed + symbol
    [InlineData("AbcdefgH1")]     // 9 chars, upper + lower + digit
    [InlineData("AAAaaa11")]      // 8 chars, upper + lower + digit -- length boundary
    public void Hash_CompliantPassword_ReturnsNonEmptyHashThatVerifies(string plaintext)
    {
        var hasher = new UIPasswordHasher();

        var hash = hasher.Hash(plaintext);

        Assert.False(string.IsNullOrEmpty(hash));
        Assert.Equal(PasswordVerificationResult.Success, hasher.Verify(hash, plaintext));
    }

    // -----------------------------------------------------------------------------------
    // Verbatim-message fact: pins the EXACT operator-facing copy with Assert.Equal.
    // Any drift (Title-case, missing period, paraphrase, extra whitespace) trips here
    // before plan 15-05's banner-gate bUnit tests ever execute.
    // -----------------------------------------------------------------------------------

    [Fact]
    public void Hash_NonCompliantPassword_ExceptionMessageMatchesPinnedOperatorCopyVerbatim()
    {
        var hasher = new UIPasswordHasher();

        var ex = Assert.Throws<ArgumentException>(() => hasher.Hash("short"));

        // ArgumentException renders Message as "{msg} (Parameter '{paramName}')" when a
        // paramName is supplied; the load-bearing pinned copy lives in ex.Message.Split
        // before the " (Parameter '" suffix. We assert via the framework-stable
        // ArgumentException.Message contract by checking the message starts with the
        // pinned copy. The bUnit banner gates in plan 15-05 surface ex.Message directly
        // and the framework-emitted " (Parameter 'plaintext')" suffix is trimmed by the
        // catch handler there per the CONTEXT.md decision; here we pin both halves:
        // (a) the prefix is the verbatim pinned copy, (b) the paramName is "plaintext".
        Assert.StartsWith(PinnedPolicyMessage, ex.Message);
        Assert.Equal("plaintext", ex.ParamName);
    }

    /// <summary>
    /// Tightest possible pin on the pinned copy: catch the throw and assert the
    /// "message-half" of <see cref="ArgumentException.Message"/> matches verbatim. This
    /// is the test plan 15-05's bUnit banner gate relies on.
    /// </summary>
    [Fact]
    public void Hash_NonCompliantPassword_ExceptionMessage_FirstLine_EqualsPinnedCopy()
    {
        var hasher = new UIPasswordHasher();

        var ex = Assert.Throws<ArgumentException>(() => hasher.Hash("short"));

        // ArgumentException with paramName formats as:
        //   "{message} (Parameter '{paramName}')"
        // The pinned message is the substring before " (Parameter '".
        const string sep = " (Parameter '";
        var idx = ex.Message.IndexOf(sep, StringComparison.Ordinal);
        Assert.True(idx > 0, $"Expected paramName suffix in message: '{ex.Message}'");
        var messageHalf = ex.Message[..idx];

        Assert.Equal(PinnedPolicyMessage, messageHalf);
    }

    // -----------------------------------------------------------------------------------
    // Verify-unchanged facts: legacy v0.9-v0.11 passwords must still authenticate.
    // The plan is hash-side-only -- Verify does NOT pre-gate input on the policy.
    // -----------------------------------------------------------------------------------

    [Fact]
    public void Verify_RoundTripsCompliantHashSuccessfully()
    {
        var hasher = new UIPasswordHasher();
        var hash = hasher.Hash("Password1");

        var result = hasher.Verify(hash, "Password1");

        Assert.Equal(PasswordVerificationResult.Success, result);
    }

    [Fact]
    public void Verify_NotPolicyGated_LegacyShortPasswordStillVerifies()
    {
        // Plan 15-02 contract: Verify is NOT policy-gated. A pre-policy "legacy" v0.9-v0.11
        // hash (created when the policy did not yet exist) must still authenticate even if
        // the plaintext would be rejected by Hash today.
        //
        // We simulate the legacy hash by going around our hasher's policy gate via a
        // sibling PasswordHasher<object> instance (same Identity V3 PBKDF2 format), then
        // assert our hasher's Verify accepts it. This proves the policy is enforced at
        // Hash only -- legacy operators with short passwords can still sign in.
        var legacyFramework = new PasswordHasher<object>();
        var legacyHash = legacyFramework.HashPassword(new object(), "short");

        var hasher = new UIPasswordHasher();
        var result = hasher.Verify(legacyHash, "short");

        Assert.Equal(PasswordVerificationResult.Success, result);
    }

    [Fact]
    public void Verify_NotPolicyGated_DoesNotPreCheckPlaintextLength()
    {
        // Companion to the legacy-hash fact: even with a wrong storedHash, Verify must NOT
        // throw ArgumentException on a short plaintext. The policy must NOT be enforced
        // on the Verify path. A wrong-but-well-formed Identity V3 hash + short plaintext
        // returns Failed; the load-bearing assertion is "no ArgumentException".
        var legacyFramework = new PasswordHasher<object>();
        var wrongHash = legacyFramework.HashPassword(new object(), "completely-different-password");

        var hasher = new UIPasswordHasher();
        var result = hasher.Verify(wrongHash, "short");

        // The point is: no throw. Failed is the expected framework outcome for a
        // non-matching candidate; the policy gate must not interpose on this path.
        Assert.Equal(PasswordVerificationResult.Failed, result);
    }
}
