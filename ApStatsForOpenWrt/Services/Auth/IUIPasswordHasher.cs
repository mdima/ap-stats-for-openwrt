using Microsoft.AspNetCore.Identity;

namespace OpenWrtApStats.Services.Auth;

/// <summary>
/// SEC-04 hasher seam over <see cref="PasswordHasher{TUser}"/>. Identity V3 PBKDF2-HMAC-SHA512
/// 100,000 iterations / 256-bit subkey / 128-bit salt — framework default. Pitfall 8: callers
/// MUST handle <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> by re-hashing
/// with <see cref="Hash"/> and persisting the new hash via
/// <see cref="OpenWrtApStats.Services.Settings.IAppSettingsService.SetUIPasswordHashAsync"/>.
/// </summary>
public interface IUIPasswordHasher
{
    string Hash(string plaintext);
    PasswordVerificationResult Verify(string storedHash, string providedPassword);
}
