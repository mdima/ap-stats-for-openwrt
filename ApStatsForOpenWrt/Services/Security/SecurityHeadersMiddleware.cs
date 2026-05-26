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
        SetHeaders(ctx);
        await next(ctx);
    }

    // internal so tests can call directly with a synthetic HttpContext if needed.
    internal static void SetHeaders(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        // TryAdd-equivalent: only set if absent so downstream components / explicit
        // overrides (e.g., a future per-endpoint policy) take precedence.
        if (!h.ContainsKey("X-Content-Type-Options")) h.Append("X-Content-Type-Options", XContentTypeOptionsValue);
        if (!h.ContainsKey("X-Frame-Options"))        h.Append("X-Frame-Options", XFrameOptionsValue);
        if (!h.ContainsKey("Referrer-Policy"))        h.Append("Referrer-Policy", ReferrerPolicyValue);
        if (!h.ContainsKey("Permissions-Policy"))     h.Append("Permissions-Policy", PermissionsPolicyValue);
        if (!h.ContainsKey("Content-Security-Policy")) h.Append("Content-Security-Policy", ContentSecurityPolicyValue);
        // M-SEC-07 deliberate omissions (HTTP-on-LAN compat):
        //   - Strict-Transport-Security: NOT emitted under any code path.
        //   - CSP upgrade-insecure-requests: NOT included in the CSP value above.
    }
}
