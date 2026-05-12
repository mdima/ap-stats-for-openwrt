using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Settings;

namespace ApStatsForOpenWrt.Services.Onboarding;

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
    IAppSettingsService settings)
{
    // INodeRepo is transient — inject via InvokeAsync so ASP.NET Core resolves it
    // from the per-request DI scope rather than capturing it at construction time
    // (construction is effectively singleton for conventional middleware). Fixes CR-02.
    public async Task InvokeAsync(HttpContext context, INodeRepo nodeRepo)
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
