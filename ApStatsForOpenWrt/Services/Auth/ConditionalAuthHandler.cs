using Microsoft.AspNetCore.Authorization;
using ApStatsForOpenWrt.Services.Settings;

namespace ApStatsForOpenWrt.Services.Auth;

/// <summary>
/// D-01 conditional auth gate. Reads <see cref="IAppSettingsService.UIPasswordHash"/> per
/// request via <see cref="System.Threading.Volatile.Read"/> over an immutable snapshot
/// (see <see cref="AppSettingsService"/>). Null/empty → gate disabled (Succeed); non-empty
/// AND user authenticated → Succeed; non-empty AND anonymous → silent (cookie middleware
/// then redirects to LoginPath). Pitfall 1 binding: NEVER cache the hash value.
/// </summary>
internal sealed class ConditionalAuthHandler(IAppSettingsService settings)
    : AuthorizationHandler<ConditionalAuthRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ConditionalAuthRequirement requirement)
    {
        var hash = settings.UIPasswordHash;        // single Volatile.Read; lock-free; per-request
        if (string.IsNullOrEmpty(hash))
        {
            context.Succeed(requirement);          // gate disabled (D-01)
        }
        else if (context.User.Identity?.IsAuthenticated == true)
        {
            context.Succeed(requirement);
        }
        // else: do nothing → cookie middleware redirects to LoginPath
        return Task.CompletedTask;
    }
}
