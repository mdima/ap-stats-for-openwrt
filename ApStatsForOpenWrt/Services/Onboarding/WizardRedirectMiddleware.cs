using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Settings;

namespace ApStatsForOpenWrt.Services.Onboarding;

/// <summary>
/// Two interception paths on GET requests:
///
/// <para>
/// (1) <b>GET / auto-redirect to the wizard on first run (v0.11, unchanged).</b>
/// Fires a 302 redirect to <c>/setup/password</c> when ALL three conditions hold:
/// </para>
/// <list type="number">
///   <item>Zero <c>WiFiNode</c> rows exist (<see cref="INodeRepo.AnyAsync"/> == false)</item>
///   <item><c>UIPasswordHash</c> is null or empty (<see cref="IAppSettingsService"/> snapshot)</item>
///   <item><c>WizardDismissed</c> is false (<see cref="IAppSettingsService"/> snapshot)</item>
/// </list>
///
/// <para>
/// (2) <b>GET /setup/* friendly 404 once a real commitment exists (M-SEC-02, Option B
/// resumability).</b> Serves the pinned friendly HTML 404 body when
/// <c>wizardClosed = anyNodes OR hasPassword</c>. <c>WizardDismissed</c> is deliberately
/// NOT part of this derivation — dismissed-only state must keep the wizard reachable so
/// the operator can resume it via direct URL until a real commitment lands.
/// </para>
///
/// <para>
/// Placed BEFORE <c>app.UseAuthentication()</c> so it fires on every GET regardless of auth
/// state. Static assets are already handled by <c>app.UseStaticFiles()</c> — they never
/// reach this middleware.
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
        else if (isGet && path.StartsWithSegments("/setup"))
        {
            // M-SEC-02 — Option B resumability. wizardClosed = anyNodes OR hasPassword.
            // WizardDismissed is NOT in the closure derivation (operator can resume the
            // wizard via direct URL until a real commitment exists).
            var anyNodes = await nodeRepo.AnyAsync(context.RequestAborted);
            var hasPassword = !string.IsNullOrEmpty(settings.UIPasswordHash);
            if (anyNodes || hasPassword)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(WizardClosedHtml, context.RequestAborted);
                return;
            }
        }

        await next(context);
    }

    // M-SEC-02 / CONTEXT.md Decision 3 — pinned VERBATIM friendly 404 body.
    // Built via string-concatenation of literal segments so each line is grep-locatable;
    // downstream M-SEC-11 README docs reference the visible text.
    private const string WizardClosedHtml =
        "<!DOCTYPE html>\n" +
        "<html><head><title>Setup complete</title></head><body>\n" +
        "<h1>Setup complete</h1>\n" +
        "<p>This setup wizard has already been completed.</p>\n" +
        "<p><a href=\"/Login\">Sign in</a> to continue.</p>\n" +
        "</body></html>\n";
}
