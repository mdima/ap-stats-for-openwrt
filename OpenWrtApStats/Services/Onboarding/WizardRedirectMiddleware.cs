using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Settings;

namespace OpenWrtApStats.Services.Onboarding;

/// <summary>
/// D-07: Fires a 302 redirect to /setup/password on GET / when ALL three conditions hold:
/// 1. Zero WiFiNode rows exist (INodeRepo.AnyAsync() == false)
/// 2. UIPasswordHash is null or empty (IAppSettingsService snapshot)
/// 3. WizardDismissed is false (IAppSettingsService snapshot)
///
/// Placed BEFORE app.UseAuthentication() so it fires on every GET / regardless of auth state.
/// Static assets are already handled by app.UseStaticFiles() — they never reach this middleware.
/// </summary>
internal sealed class WizardRedirectMiddleware(
    RequestDelegate next,
    INodeRepo nodeRepo,
    IAppSettingsService settings)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Only intercept GET /
        if (context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && context.Request.Path == "/")
        {
            // Check exemptions (belt + braces — static files short-circuit before us,
            // but explicit path check guards against any ordering change):
            // /setup/*, /Login, /_blazor, /lib/*, /_framework, /health are all ≠ "/"
            // so the path == "/" check above already excludes them.

            var anyNodes = await nodeRepo.AnyAsync(context.RequestAborted);
            var hash = settings.UIPasswordHash;
            var dismissed = settings.WizardDismissed;

            if (!anyNodes
                && string.IsNullOrEmpty(hash)
                && !dismissed)
            {
                context.Response.Redirect("/setup/password");
                return;
            }
        }

        await next(context);
    }
}
