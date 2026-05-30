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
        // Verbatim CSP per REQUIREMENTS.md line 47.
        const string expected =
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
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
    public async Task CspIncludesWsAndWssInConnectSrc_BlazorCompat()
    {
        var ct = TestContext.Current.CancellationToken;
        using var resp = await _factory.CreateClient().GetAsync("/", ct);
        var csp = GetHeader(resp, "Content-Security-Policy");
        Assert.NotNull(csp);
        // ws:/wss: in connect-src permits the Blazor InteractiveServer SignalR upgrade.
        Assert.Contains("connect-src 'self' ws: wss:", csp!);
    }
}
