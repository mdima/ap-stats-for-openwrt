using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Settings;

namespace ApStatsForOpenWrt.Services.Onboarding;

/// <summary>
/// <b>GET / auto-redirect to the wizard on first run (v0.11, unchanged).</b>
/// Fires a 302 redirect to <c>/setup/password</c> when ALL three conditions hold:
///
/// <list type="number">
///   <item>Zero <c>WiFiNode</c> rows exist (<see cref="INodeRepo.AnyAsync"/> == false)</item>
///   <item><c>UIPasswordHash</c> is null or empty (<see cref="IAppSettingsService"/> snapshot)</item>
///   <item><c>WizardDismissed</c> is false (<see cref="IAppSettingsService"/> snapshot)</item>
/// </list>
///
/// <para>
/// Placed BEFORE <c>app.UseAuthentication()</c> so the anonymous first-run GET / is
/// handled before the auth pipeline. The auth state is irrelevant to this branch (a
/// fresh install has no password and no operator), so it does not need auth-awareness.
/// </para>
///
/// <para>
/// The <c>/setup/*</c> friendly-404 anti-takeover gate (M-SEC-02) lives in a SEPARATE
/// middleware — <see cref="WizardSetupGateMiddleware"/> — registered AFTER
/// <c>app.UseAuthentication()</c> so it can distinguish the just-signed-in operator
/// (who must be allowed to traverse the remaining wizard steps) from an anonymous
/// attacker (who must still get the 404). See debug session
/// <c>wizard-lockout-regression</c> for the split rationale.
/// </para>
///
/// <para>
/// Static assets are already handled by <c>app.UseStaticFiles()</c> — they never reach
/// this middleware.
/// </para>
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
        var isGet = context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase);
        var path = context.Request.Path;

        if (isGet && path == "/")
        {
            // v0.11 behavior UNCHANGED — auto-redirect first-run operator to the wizard.
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
