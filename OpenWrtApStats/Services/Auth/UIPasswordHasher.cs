using Microsoft.AspNetCore.Identity;

namespace OpenWrtApStats.Services.Auth;

/// <summary>
/// <see cref="IUIPasswordHasher"/> implementation. The generic-type slot of
/// <see cref="PasswordHasher{TUser}"/> is unused for V3 hashes (the user parameter is
/// only consumed by older format variants); <see cref="Placeholder"/> is a marker type.
/// </summary>
public sealed class UIPasswordHasher : IUIPasswordHasher
{
    private sealed class Placeholder;
    private readonly PasswordHasher<Placeholder> _hasher = new();
    private static readonly Placeholder _user = new();

    public string Hash(string plaintext) => _hasher.HashPassword(_user, plaintext);
    public PasswordVerificationResult Verify(string storedHash, string providedPassword)
        => _hasher.VerifyHashedPassword(_user, storedHash, providedPassword);
}
