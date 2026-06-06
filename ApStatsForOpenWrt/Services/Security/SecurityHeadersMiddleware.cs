using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ApStatsForOpenWrt.Services.Security;

/// <summary>
/// M-HRD-04 (plan 21-03) — the runtime-assembled CSP injected into
/// <see cref="SecurityHeadersMiddleware"/>. The <c>script-src</c> <c>sha256-</c> hash stays in
/// the compile-time <see cref="SecurityHeadersMiddleware.ContentSecurityPolicyValue"/> const
/// (so <c>CspScriptHashTests</c> can pin it). The only runtime-variable part is the
/// <c>connect-src</c> directive, which APPENDS validated <c>Csp__ConnectSrc</c> origins to
/// <c>'self'</c>. The final string is built ONCE at startup (after fail-loud validation in
/// Program.cs) and passed here so <see cref="SecurityHeadersMiddleware.SetHeaders"/> stays a
/// pure REPLACE (Open Question 1 — build-once-inject recommendation).
/// </summary>
public sealed class SecurityHeadersOptions
{
    /// <summary>
    /// The fully-assembled Content-Security-Policy header value emitted on every response.
    /// Defaults to the compile-time const (no <c>Csp__ConnectSrc</c> origins appended).
    /// </summary>
    public string ContentSecurityPolicy { get; set; } =
        SecurityHeadersMiddleware.ContentSecurityPolicyValue;
}

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
/// M-HRD-04 (plan 21-03) HARDENED <c>script-src</c>: <c>'unsafe-inline'</c> is DROPPED in
/// favor of a <c>sha256-</c> hash of the SINGLE inline boot <c>&lt;script&gt;</c> in
/// <c>App.razor</c> (the anti-FOUC theme/sidebar pre-paint). The hash is LF-normalized so it
/// is identical on a Windows checkout and the Linux container build (repo-root
/// <c>.gitattributes</c> <c>*.razor eol=lf</c>, RESEARCH Pitfall 1); <c>CspScriptHashTests</c>
/// recomputes it from source and fails the build on any un-re-pinned boot-script edit.
/// </para>
/// <para>
/// <c>style-src</c> DELIBERATELY KEEPS <c>'unsafe-inline'</c> — AdminLTE/Bootstrap ship inline
/// <c>&lt;style&gt;</c> blocks that have no stable hash. <c>connect-src</c> tightens from
/// <c>'self' ws: wss:</c> to <c>'self'</c> (same-origin <c>ws://</c> is covered by <c>'self'</c>
/// in modern CSP); proxied deployments append their WebSocket origin via the fail-loud
/// <c>Csp__ConnectSrc</c> env knob, which is assembled into the emitted value via
/// <see cref="SecurityHeadersOptions"/> at startup.
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

    // M-HRD-04 (plan 21-03): script-src carries the sha256- hash of the App.razor inline boot
    // script (NO 'unsafe-inline'); style-src KEEPS 'unsafe-inline'; connect-src is 'self' (no
    // ws:/wss: wildcards — Csp__ConnectSrc appends proxy origins at startup). The hash below is
    // LF-normalized and PINNED by CspScriptHashTests — if the App.razor boot script changes,
    // that test fails and prints the fresh hash to paste here.
    internal const string ContentSecurityPolicyValue =
        "default-src 'self'; script-src 'self' 'sha256-T5YSl+2aUjhEmzNd2Iq7VZTu7E+XPoHJndyYSmqCng8='; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    // M-HRD-04: the assembled CSP (const + any Csp__ConnectSrc origins) injected at startup.
    // Falls back to the const when the middleware is resolved without options registered (e.g.
    // a synthetic-context unit test) so SetHeaders always has a value.
    private readonly string _csp;

    public SecurityHeadersMiddleware(IOptions<SecurityHeadersOptions>? options = null)
    {
        _csp = options?.Value.ContentSecurityPolicy ?? ContentSecurityPolicyValue;
    }

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
        // Capture the instance-assembled CSP (const + any Csp__ConnectSrc origins) into the
        // OnStarting state tuple so the static callback stays allocation-disciplined and emits
        // the runtime-configured value without re-resolving options.
        var csp = _csp;
        ctx.Response.OnStarting(static state =>
        {
            var (httpCtx, cspValue) = ((HttpContext, string))state;
            SetHeaders(httpCtx, cspValue);
            return Task.CompletedTask;
        }, (ctx, csp));

        await next(ctx);
    }

    // internal so tests can call directly with a synthetic HttpContext if needed.
    // Overload uses the compile-time const (no Csp__ConnectSrc origins) — for synthetic-context
    // callers that don't go through the DI-injected instance.
    internal static void SetHeaders(HttpContext ctx) => SetHeaders(ctx, ContentSecurityPolicyValue);

    internal static void SetHeaders(HttpContext ctx, string contentSecurityPolicy)
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
        h["Content-Security-Policy"] = contentSecurityPolicy;
        // M-SEC-07 deliberate omissions (HTTP-on-LAN compat):
        //   - Strict-Transport-Security: NOT emitted under any code path.
        //   - CSP upgrade-insecure-requests: NOT included in the CSP value above.
    }
}
