using System.Net.Http;
using ApStatsForOpenWrt.Services.Security;
using ApStatsForOpenWrt.Tests.Integration;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Security;

/// <summary>
/// M-SEC-07 (plan 16-05) acceptance suite for <see cref="SecurityHeadersMiddleware"/>.
/// Drives the production Program.cs pipeline end-to-end via <see cref="SmokeFactoryFixture"/>
/// and pins the verbatim header values on <c>GET /</c>, <c>GET /health</c>, and a static
/// asset path — proving the middleware sits BEFORE <c>UseStaticFiles</c>.
///
/// <para>
/// Two deliberate charter exclusions are asserted by ABSENCE:
/// <list type="bullet">
///   <item><description>NO <c>Strict-Transport-Security</c> (HTTP-on-LAN compat).</description></item>
///   <item><description>CSP does NOT contain <c>upgrade-insecure-requests</c> (same reason).</description></item>
/// </list>
/// </para>
///
/// <para>
/// CSP <c>connect-src 'self' ws: wss:</c> is asserted so Blazor InteractiveServer's
/// SignalR <c>/_blazor</c> connection still negotiates under the policy.
/// </para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "SecurityFix")]
public sealed class SecurityHeadersResponseTests(SmokeFactoryFixture factory) : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    // A static asset proven to return 200 by AdminLteAssetSmokeTests — served by
    // app.UseStaticFiles(), which the middleware must sit in front of.
    private const string StaticAssetPath = "/lib/adminlte/css/adminlte.min.css";

    private static readonly string[] AllHeaderNames =
    [
        "X-Content-Type-Options",
        "X-Frame-Options",
        "Referrer-Policy",
        "Permissions-Policy",
        "Content-Security-Policy",
    ];

    // Headers may land on either the response headers or content headers collection
    // depending on how the framework partitions them — check both.
    private static string? GetHeader(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var v1)) return string.Join(", ", v1);
        if (resp.Content.Headers.TryGetValues(name, out var v2)) return string.Join(", ", v2);
        return null;
    }

    private static bool HasHeader(HttpResponseMessage resp, string name) =>
        resp.Headers.Contains(name) || resp.Content.Headers.Contains(name);

    // ── Case 1 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllFiveHeadersPresent_OnGetRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);

        foreach (var name in AllHeaderNames)
        {
            Assert.True(HasHeader(resp, name), $"expected header '{name}' on GET /");
        }
    }

    // ── Case 2 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task XContentTypeOptions_VerbatimValue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);
        Assert.Equal(SecurityHeadersMiddleware.XContentTypeOptionsValue,
            GetHeader(resp, "X-Content-Type-Options"));
    }

    // ── Case 3 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task XFrameOptions_VerbatimValue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);
        Assert.Equal(SecurityHeadersMiddleware.XFrameOptionsValue,
            GetHeader(resp, "X-Frame-Options"));
    }

    // ── Case 4 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReferrerPolicy_VerbatimValue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);
        Assert.Equal(SecurityHeadersMiddleware.ReferrerPolicyValue,
            GetHeader(resp, "Referrer-Policy"));
    }

    // ── Case 5 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PermissionsPolicy_VerbatimValue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);
        // Verbatim per CONTEXT.md Decision 3 — Assert.Equal, NOT Assert.Contains.
        Assert.Equal("geolocation=(), camera=(), microphone=(), interest-cohort=()",
            GetHeader(resp, "Permissions-Policy"));
        Assert.Equal(SecurityHeadersMiddleware.PermissionsPolicyValue,
            GetHeader(resp, "Permissions-Policy"));
    }

    // ── Case 6 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CSP_VerbatimValue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);
        // Verbatim CSP — M-HRD-04 (plan 21-03) hardened it: script-src drops 'unsafe-inline'
        // for a sha256- hash of the single App.razor inline boot script (pinned by
        // CspScriptHashTests); connect-src tightens to 'self' (no ws:/wss: literals); style-src
        // KEEPS 'unsafe-inline'. The base value (no Csp__ConnectSrc env var set in this fixture)
        // matches the middleware const exactly.
        const string expected =
            "default-src 'self'; script-src 'self' 'sha256-N+XgxVzS7xHbt2VJ63Smp/xmf8x7iq10qnQZ1+ljDiY='; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        Assert.Equal(expected, GetHeader(resp, "Content-Security-Policy"));
        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicyValue,
            GetHeader(resp, "Content-Security-Policy"));
    }

    // ── Case 7 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoStrictTransportSecurity_OnGetRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);
        // HTTP-on-LAN compat — the header is NEVER emitted regardless of scheme.
        Assert.False(HasHeader(resp, "Strict-Transport-Security"),
            "Strict-Transport-Security must NOT be emitted (HTTP-on-LAN compat).");
    }

    // ── Case 8 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoUpgradeInsecureRequests_InCsp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);
        var csp = GetHeader(resp, "Content-Security-Policy");
        Assert.NotNull(csp);
        Assert.DoesNotContain("upgrade-insecure-requests", csp!);
    }

    // ── Case 9 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HeadersPresentOnHealthEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        // M-SEC-07 contract: headers apply universally, including the plain
        // MapHealthChecks /health endpoint.
        using var resp = await _factory.CreateClient().GetAsync("/health", ct);

        foreach (var name in AllHeaderNames)
        {
            Assert.True(HasHeader(resp, name), $"expected header '{name}' on GET /health");
        }
    }

    // ── Case 10 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task HeadersPresentOnStaticAsset()
    {
        var ct = TestContext.Current.CancellationToken;
        // Proves the pipeline placement BEFORE UseStaticFiles is correct — the
        // static-file terminal middleware short-circuits, but headers are already set.
        using var resp = await _factory.CreateClient().GetAsync(StaticAssetPath, ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        foreach (var name in AllHeaderNames)
        {
            Assert.True(HasHeader(resp, name), $"expected header '{name}' on GET {StaticAssetPath}");
        }
    }

    // ── Case 11 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CspConnectSrcIsSelfOnly_NoWsWildcards_BlazorCompat()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);
        var csp = GetHeader(resp, "Content-Security-Policy");
        Assert.NotNull(csp);
        // M-HRD-04 (plan 21-03): connect-src tightened from 'self' ws: wss: to 'self'.
        // Same-origin ws:// is covered by 'self' in modern CSP, so the plain-HTTP LAN
        // Blazor InteractiveServer SignalR circuit still negotiates (proven by the Plan 04
        // Playwright smoke test). The ws:/wss: WILDCARD literals are gone.
        Assert.Contains("connect-src 'self';", csp!);
        Assert.DoesNotContain("connect-src 'self' ws: wss:", csp!);
    }

    // ── Case 12 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CspScriptSrc_DropsUnsafeInline_ForSha256Hash()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);
        var csp = GetHeader(resp, "Content-Security-Policy");
        Assert.NotNull(csp);
        // script-src carries the sha256- hash and no longer allows arbitrary inline scripts.
        Assert.Contains("script-src 'self' 'sha256-", csp!);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp!);
        // style-src DELIBERATELY keeps 'unsafe-inline' (AdminLTE/Bootstrap inline <style>).
        Assert.Contains("style-src 'self' 'unsafe-inline'", csp!);
    }
}
