using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Settings;

namespace ApStatsForOpenWrt.Services.Onboarding;

/// <summary>
/// <b>GET /setup/* AUTH-AWARE friendly-404 anti-takeover gate (M-SEC-02).</b>
///
/// <para>
/// Serves the pinned friendly HTML 404 body for <c>GET /setup/*</c> when the system is
/// genuinely complete (<c>wizardClosed = hasPassword AND anyNodes</c>) AND the caller is
/// NOT authenticated. "Complete" means a password is set <em>and</em> at least one AP
/// (WiFiNode) exists — neither alone closes this visible gate. The earlier OR semantics
/// fired too early (the moment a password was set), so the AND form was adopted per the
/// <c>wizard-gate-and-semantics</c> operator decision. Since adding an AP requires first
/// setting a password + signing in, <c>hasPassword &amp;&amp; anyNodes</c> is effectively
/// equivalent to <c>anyNodes</c> in all reachable states; the explicit two-term form is
/// retained for clarity/defense.
/// </para>
///
/// <para>
/// The authenticated operator is allowed through so they can complete the 3-step
/// onboarding wizard — setting the password is step 1, which flips
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
/// Anti-takeover preserved: an ANONYMOUS caller still gets the friendly 404 once the system
/// is genuinely complete (password AND AP). In the password-only intermediate window
/// (password set, no AP yet) the visible gate is OFF, and the response then depends on each
/// <c>/setup</c> page's OWN authorization — it is NOT uniformly blocked:
/// <list type="bullet">
///   <item><description><c>/setup/nodes</c> and <c>/setup/wlans</c> are NOT
///   <c>[AllowAnonymous]</c>, so the ConditionalAuth fallback policy 302-redirects an
///   anonymous caller to <c>/Login</c>.</description></item>
///   <item><description><c>/setup/password</c> carries <c>[AllowAnonymous]</c> (it must be
///   reachable on FIRST run, before any password exists), which EXEMPTS it from that
///   fallback — so an anonymous <c>GET /setup/password</c> renders <c>200</c> even with a
///   password already set. That is a DELIBERATE, tested decision (commit <c>19cefee</c>,
///   debug session <c>wizard-gate-and-semantics</c>; regression-locked by
///   <c>WizardRedirectMiddlewareTests.GetSetupPassword_WhenPasswordOnly_*</c>): viewing the
///   wizard form is not a takeover vector. The takeover boundary is the M-SEC-01 POST gate on
///   <c>/setup/submit-password</c> (Program.cs), which refuses an anonymous re-POST the moment
///   <c>hasPassword</c> — that gate is independent of this middleware and unchanged.</description></item>
/// </list>
/// So the friendly "setup complete" 404 is shown ONLY when the wizard is genuinely finished;
/// the password-form GET staying anonymously reachable in the interim is accepted by design
/// (confirmed in UAT 16-7 surface B, 2026-05-30 — POST correctly 404'd, GET intentionally 200).
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
            // Auth-aware anti-takeover gate. wizardClosed = hasPassword AND anyNodes —
            // the system is "complete" only when a password is set AND at least one AP
            // exists. The authenticated operator is allowed through (they are mid-onboarding);
            // anonymous callers get the friendly 404 only once the system is genuinely complete.
            // In the password-only window (no AP yet) the gate is OFF and the request falls
            // through to the ConditionalAuth fallback (302 /Login).
            var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
            if (!isAuthenticated)
            {
                var anyNodes = await nodeRepo.AnyAsync(context.RequestAborted);
                var hasPassword = !string.IsNullOrEmpty(settings.UIPasswordHash);
                if (anyNodes && hasPassword)
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
