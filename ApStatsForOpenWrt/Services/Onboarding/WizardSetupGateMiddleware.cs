using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Settings;

namespace ApStatsForOpenWrt.Services.Onboarding;

/// <summary>
/// <b>GET /setup/* AUTH-AWARE friendly-404 anti-takeover gate (M-SEC-02).</b>
///
/// <para>
/// Serves the pinned friendly HTML 404 body for <c>GET /setup/*</c> when the system is
/// committed (<c>wizardClosed = anyNodes OR hasPassword</c>) AND the caller is NOT
/// authenticated. The authenticated operator is allowed through so they can complete
/// the 3-step onboarding wizard — setting the password is step 1, which flips
/// <c>hasPassword == true</c> and signs the operator in, so an auth-blind gate locked
/// the operator out of steps 2 (<c>/setup/nodes</c>) and 3 (<c>/setup/wlans</c>). See
/// debug session <c>wizard-lockout-regression</c>.
/// </para>
///
/// <para>
/// <c>WizardDismissed</c> is deliberately NOT part of the closure derivation —
/// dismissed-only state must keep the wizard reachable so the operator can resume it
/// via direct URL until a real commitment lands (Option B resumability).
/// </para>
///
/// <para>
/// Anti-takeover preserved: an ANONYMOUS caller still gets the 404 once the system is
/// committed. The independent M-SEC-01 POST gate on <c>/setup/submit-password</c> and
/// <c>/setup/skip</c> (Program.cs) is untouched by this middleware.
/// </para>
///
/// <para>
/// Placed AFTER <c>app.UseAuthentication()</c> so <c>context.User</c> reflects the
/// validated auth cookie. Static assets are handled by <c>app.UseStaticFiles()</c> /
/// <c>app.MapStaticAssets()</c> and never reach this middleware; the <c>path ==
/// "/setup/*"</c> guard also excludes <c>/health</c>, <c>/Login</c>, <c>/_blazor</c>,
/// <c>/_framework</c>, <c>/lib/*</c>, and <c>/</c>.
/// </para>
/// </summary>
internal sealed class WizardSetupGateMiddleware(
    RequestDelegate next,
    IAppSettingsService settings)
{
    // INodeRepo is transient — inject via InvokeAsync so ASP.NET Core resolves it
    // from the per-request DI scope rather than capturing it at construction time.
    public async Task InvokeAsync(HttpContext context, INodeRepo nodeRepo)
    {
        var isGet = context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase);
        var path = context.Request.Path;

        if (isGet && path.StartsWithSegments("/setup"))
        {
            // Auth-aware anti-takeover gate. wizardClosed = anyNodes OR hasPassword.
            // The authenticated operator is allowed through (they are mid-onboarding);
            // anonymous callers get the friendly 404 once the system is committed.
            var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
            if (!isAuthenticated)
            {
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
