using Microsoft.AspNetCore.Identity;

namespace ApStatsForOpenWrt.Services.Auth;

/// <summary>
/// <see cref="IUIPasswordHasher"/> implementation. The generic-type slot of
/// <see cref="PasswordHasher{TUser}"/> is unused for V3 hashes (the user parameter is
/// only consumed by older format variants); <see cref="Placeholder"/> is a marker type.
///
/// <para>
/// M-SEC-03 password policy: <see cref="Hash"/> enforces a minimum-entropy floor
/// (>=8 chars + upper + lower + digit-or-symbol) at the trust boundary BEFORE delegating
/// to the underlying <see cref="PasswordHasher{TUser}"/>. Policy violations throw
/// <see cref="ArgumentException"/> (charter line 181 verbatim; CONTEXT.md decision
/// "Password-policy exception type"). The three call sites
/// (<c>Login.razor</c> rehash path / <c>Settings/Index.razor</c> password set /
/// <c>Program.cs</c> <c>/setup/submit-password</c> MapPost lambda) catch
/// <see cref="ArgumentException"/> and surface <see cref="Exception.Message"/> verbatim
/// in the banner. The catch sites land in plan 15-05.
/// </para>
///
/// <para>
/// <see cref="Verify"/> is intentionally NOT policy-gated. Legacy v0.9-v0.11 passwords
/// that predate the policy must still authenticate; policy enforcement is hash-side only.
/// </para>
/// </summary>
public sealed class UIPasswordHasher : IUIPasswordHasher
{
    private sealed class Placeholder;
    private readonly PasswordHasher<Placeholder> _hasher = new();
    private static readonly Placeholder _user = new();

    /// <summary>
    /// M-SEC-03 — pinned operator-facing copy. The three caller sites
    /// (<c>Login.razor</c> / <c>Settings/Index.razor</c> / <c>Program.cs</c>
    /// <c>/setup/submit-password</c> MapPost lambda) surface
    /// <see cref="Exception.Message"/> verbatim. Changing this string requires
    /// updating the bUnit gates in plan 15-05 AND the
    /// <c>UIPasswordHasherPolicyTests</c> verbatim-message facts.
    ///
    /// <para>
    /// Source of truth: <c>.planning/REQUIREMENTS.md</c> line 16 / charter line 559 /
    /// <c>.planning/phases/15-auth-surface-hardening/15-CONTEXT.md</c>.
    /// </para>
    /// </summary>
    private const string PolicyMessage =
        "Password must be at least 8 characters and include upper, lower, and a digit or symbol.";

    public string Hash(string plaintext)
    {
        if (!IsPolicyCompliant(plaintext))
            throw new ArgumentException(PolicyMessage, nameof(plaintext));
        return _hasher.HashPassword(_user, plaintext);
    }

    // M-SEC-03 — Verify is intentionally NOT policy-gated; legacy passwords created before
    // v0.12 must still authenticate. Policy enforcement is hash-side only.
    public PasswordVerificationResult Verify(string storedHash, string providedPassword)
        => _hasher.VerifyHashedPassword(_user, storedHash, providedPassword);

    /// <summary>
    /// Four-part M-SEC-03 policy check: length >= 8 AND has upper AND has lower AND
    /// (has digit OR has symbol). Symbol = any non-alphanumeric character (whitespace
    /// included; harmless and matches operator expectations). Null and empty
    /// short-circuit via the length check.
    /// </summary>
    private static bool IsPolicyCompliant(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 8)
            return false;

        var hasUpper = false;
        var hasLower = false;
        var hasDigit = false;
        var hasSymbol = false;

        foreach (var c in s)
        {
            if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsLower(c)) hasLower = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else hasSymbol = true; // anything non-alphanumeric counts as a symbol
        }

        return hasUpper && hasLower && (hasDigit || hasSymbol);
    }
}
