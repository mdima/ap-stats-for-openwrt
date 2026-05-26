using Microsoft.AspNetCore.Http;

namespace ApStatsForOpenWrt.Services.Security;

/// <summary>
/// M-SEC-07 / plan 16-05 — baseline security response headers middleware. Emits the
/// 5 charter-pinned headers on every response. Registered immediately BEFORE
/// <c>app.UseStaticFiles()</c> in Program.cs so static-asset responses also carry
/// the headers.
/// <para>
/// Two deliberate exclusions per CONTEXT.md Decision 3 + REQUIREMENTS.md:
/// <list type="bullet">
///   <item><description>NO <c>Strict-Transport-Security</c> — HTTP-on-LAN deployment compat.</description></item>
///   <item><description>NO CSP <c>upgrade-insecure-requests</c> — same reason.</description></item>
/// </list>
/// </para>
/// <para>
/// <c>'unsafe-inline'</c> stays in <c>script-src</c> + <c>style-src</c> for v0.12
/// because <c>App.razor</c> carries an inline boot script and AdminLTE/Bootstrap
/// ships inline <c>&lt;style&gt;</c> blocks. v0.13 polish candidate to externalize.
/// </para>
/// </summary>
public sealed class SecurityHeadersMiddleware : IMiddleware
{
    // Pinned per REQUIREMENTS.md line 47 / CONTEXT.md Decision 3.
    internal const string XContentTypeOptionsValue = "nosniff";
    internal const string XFrameOptionsValue = "DENY";
    internal const string ReferrerPolicyValue = "same-origin";
    internal const string PermissionsPolicyValue =
        "geolocation=(), camera=(), microphone=(), interest-cohort=()";
    internal const string ContentSecurityPolicyValue =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        // Register the header write on the OnStarting callback (fired just before the
        // first response byte is flushed) rather than setting them on the inbound leg.
        // This is the most defensive shape: it guarantees the charter-pinned values win
        // even when a DOWNSTREAM component (e.g. ASP.NET Core's UseAntiforgery, which on
        // .NET 10 appends `Content-Security-Policy: frame-ancestors 'self'`) would
        // otherwise Append() a second, conflicting CSP value after this middleware ran.
        // The indexer assignment in SetHeaders REPLACES rather than Appends, so the
        // emitted CSP is exactly the verbatim charter value (frame-ancestors 'none').
        ctx.Response.OnStarting(static state =>
        {
            SetHeaders((HttpContext)state);
            return Task.CompletedTask;
        }, ctx);

        await next(ctx);
    }

    // internal so tests can call directly with a synthetic HttpContext if needed.
    internal static void SetHeaders(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        // Indexer assignment = REPLACE semantics (sets the header to exactly this single
        // value, overwriting any value a downstream component appended). The charter
        // contract requires these EXACT values on every response (T-16-05-01 clickjacking
        // mitigation pins frame-ancestors 'none'); a downstream Append() of a second CSP
        // value (e.g. UseAntiforgery's `frame-ancestors 'self'`) would both break the
        // verbatim-value contract and weaken the clickjacking gate.
        h["X-Content-Type-Options"] = XContentTypeOptionsValue;
        h["X-Frame-Options"] = XFrameOptionsValue;
        h["Referrer-Policy"] = ReferrerPolicyValue;
        h["Permissions-Policy"] = PermissionsPolicyValue;
        h["Content-Security-Policy"] = ContentSecurityPolicyValue;
        // M-SEC-07 deliberate omissions (HTTP-on-LAN compat):
        //   - Strict-Transport-Security: NOT emitted under any code path.
        //   - CSP upgrade-insecure-requests: NOT included in the CSP value above.
    }
}
