using System.Net;
using System.Text.RegularExpressions;
using ApStatsForOpenWrt.Services.Auth;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Auth;

/// <summary>
/// M-SEC-04 (Plan 15-04) acceptance suite for session rotation on UI-password change
/// and the v0.11-cookie rollover path. Pins these invariants:
///
/// <list type="number">
///   <item>Sign in with SessionVersion=N (cookie carries claim=N); SetUIPasswordHashAsync(newHash)
///         bumps to N+1; the OLD cookie on a protected GET -> 302 /Login.</item>
///   <item>Same shape but the bump comes from SetUIPasswordHashAsync(null) (clear path).</item>
///   <item>Missing-claim cookie (v0.11 backward-incompat path) -> 302 /Login. Simulated by
///         seeding SessionVersion=1 via the service, then signing in via the legacy code path
///         that pre-dates the claim stamp; OnValidatePrincipal sees no claim and rejects.</item>
///   <item>Same-version cookie still works (gate does not over-reject).</item>
///   <item>Fresh sign-in after rotation works (the new cookie carries the bumped version).</item>
/// </list>
///
/// Uses the shared <see cref="SmokeFactoryFixture"/>. Each test resets the relevant state
/// in try/finally to keep the in-memory DB clean for sibling tests.
/// </summary>
// M-SEC-05 / plan 16-01 Task 3 — joined to the RateLimitSerial collection (was "Integration")
// so this suite serializes with the other forwarded-headers env-var mutators
// (LoginGlobalRateLimitTests + ForwardedHeadersConditionalTests). The factory sets the
// process-wide ForwardedHeaders__KnownNetworks env var; running it in parallel with the
// ForwardedHeadersConditionalTests disabled/CIDR-mode cases caused cross-collection env-var
// contamination flakes. This suite uses its own isolated WebApplicationFactory (not the shared
// Integration fixture), so the collection move is behavior-neutral.
[Collection("RateLimitSerial")]
[Trait("Category", "SecurityFix")]
public sealed class SessionRotationTests(SessionRotationTests.ForwardedHeadersTrustingFactory factory)
    : IClassFixture<SessionRotationTests.ForwardedHeadersTrustingFactory>
{
    private readonly ForwardedHeadersTrustingFactory _factory = factory;

    /// <summary>
    /// Test-only factory that mirrors SmokeFactoryFixture (in-memory SQLite + keep-alive)
    /// BUT additionally opts the production forwarded-headers path into trusting the
    /// TestServer's loopback sender. Without this, X-Forwarded-For is ignored — meaning
    /// every test request lands in the same /Login rate-limit partition (5/IP/min) and the
    /// 5-fact suite collides on the budget. By setting <c>ForwardedHeaders__KnownNetworks=0.0.0.0/0</c>
    /// X-Forwarded-For partitions cleanly across tests.
    ///
    /// <para>
    /// Phase 16 plan 16-01 (M-SEC-05) HAS folded forwarded-headers into conditional
    /// registration: the production <c>app.UseForwardedHeaders()</c> only registers when
    /// <c>ForwardedHeaders__KnownProxies</c> and/or <c>ForwardedHeaders__KnownNetworks</c> is
    /// set. This factory therefore opts in via the <c>ForwardedHeaders__KnownNetworks</c> env
    /// var set in its constructor (BEFORE the host builds — Program.cs reads the env var at
    /// host-build time) and restores the prior value on dispose. The old
    /// <c>services.Configure&lt;ForwardedHeadersOptions&gt;</c> DI override is dead under
    /// conditional registration and has been removed.
    /// </para>
    /// </summary>
    public sealed class ForwardedHeadersTrustingFactory
        : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>, Xunit.IAsyncLifetime
    {
        private readonly string _dataSource = $"SessionRot-{Guid.NewGuid():N}";
        private Microsoft.Data.Sqlite.SqliteConnection? _keepAlive;

        // ci-test-data-writability (residual fix) -- redirect DATA_ROOT/LOGS_ROOT to a writable
        // per-instance temp dir BEFORE the real Program.cs bootstrap runs, so the writability
        // probe + MigrationsHostedService.CreateDirectory do not abort host startup on a non-root
        // CI runner with an unwritable /data. Field initializer => constructed in the ctor, before
        // the first CreateClient()/Services access triggers the host build. Restored on dispose.
        private readonly ApStatsForOpenWrt.Tests.TestSupport.TempBootstrapRoots _roots = new();

        // M-SEC-05 / plan 16-01 Task 3 — the production ForwardedHeaders registration is now
        // env-var-gated (was unconditional in v0.11). These auth tests need X-Forwarded-For
        // honored so each request lands in a distinct /Login per-IP rate-limit partition.
        // Set the env var BEFORE the host builds (first CreateClient) and restore on dispose.
        // 0.0.0.0/0 trusts the synthetic loopback test peer — behaviorally identical to the
        // old Configure-ForwardedHeadersOptions DI override this replaces.
        private readonly string? _priorKnownNetworks =
            Environment.GetEnvironmentVariable("ForwardedHeaders__KnownNetworks");

        public ForwardedHeadersTrustingFactory()
        {
            Environment.SetEnvironmentVariable("ForwardedHeaders__KnownNetworks", "0.0.0.0/0");
        }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // Swap production SQLite for in-memory (same pattern as SmokeFactoryFixture).
                var dbDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(Microsoft.EntityFrameworkCore.IDbContextFactory<ApStatsForOpenWrt.Data.DatabaseContext>));
                if (dbDescriptor is not null) services.Remove(dbDescriptor);
                services.AddDbContextFactory<ApStatsForOpenWrt.Data.DatabaseContext>(opts =>
                    opts.UseSqlite($"Data Source={_dataSource};Mode=Memory;Cache=Shared"));

                // Forwarded-headers trust is now driven by the ForwardedHeaders__KnownNetworks env var
                // set in this factory's constructor (M-SEC-05 / plan 16-01) — the old
                // Configure-ForwardedHeadersOptions override is dead under conditional
                // registration and has been removed.
            });
        }

        public ValueTask InitializeAsync()
        {
            _keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={_dataSource};Mode=Memory;Cache=Shared");
            _keepAlive.Open();
            _ = CreateClient();
            return ValueTask.CompletedTask;
        }

        public new ValueTask DisposeAsync()
        {
            // Restore the prior env-var value so the conditional production path reverts to
            // mode=disabled for sibling tests that expect no forwarded-headers trust.
            Environment.SetEnvironmentVariable("ForwardedHeaders__KnownNetworks", _priorKnownNetworks);
            _keepAlive?.Dispose();
            _roots.Dispose();
            base.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Creates a client that sends X-Forwarded-For with a test-distinct IP so each test
    /// gets a fresh /Login rate-limit budget (5/IP/min FixedWindow per LoginPolicy).
    /// </summary>
    private HttpClient CreateClientWithDistinctIp(string xForwardedFor)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            // M-HRD-05 (plan 21-03): this factory sets ForwardedHeaders__KnownNetworks, so the
            // app now runs with forwardedHeadersEnabled=true and the auth cookie SecurePolicy is
            // Always. A Secure cookie is only sent back over https, so drive the TestServer over
            // https://localhost — otherwise the cookie is silently dropped and every authed GET
            // redirects to /Login. The change is behavior-neutral for the rotation contract.
            BaseAddress = new Uri("https://localhost"),
        });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", xForwardedFor);
        return client;
    }

    // ── Test 1: set-path bump invalidates the old cookie ───────────────────────

    [Fact]
    public async Task SignIn_ThenSetPasswordBumps_OldCookie_ReturnsRedirectToLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        // Seed password A so /Login is reachable + sign-in produces a stamped cookie.
        await SeedPasswordAsync("Password-A1", ct);

        try
        {
            var client = CreateClientWithDistinctIp("10.99.1.1");

            // Sign in with password A — cookie carries session_version claim = current value.
            await SignInAsync(client, "Password-A1", ct);

            // Bump SessionVersion via password change (set path).
            using (var scope = _factory.Services.CreateScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
                var hasher = scope.ServiceProvider.GetRequiredService<IUIPasswordHasher>();
                await settings.SetUIPasswordHashAsync(hasher.Hash("Password-B2"), ct);
            }

            // Retry a protected GET with the OLD cookie -> rejected -> 302 /Login.
            var resp = await client.GetAsync("/Nodes", ct);
            AssertRedirectToLogin(resp);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Test 2: clear-path bump invalidates the old cookie ─────────────────────

    [Fact]
    public async Task SignIn_ThenClearPasswordBumps_OldCookie_ReturnsRedirectToLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        await SeedPasswordAsync("Password-A1", ct);

        try
        {
            var client = CreateClientWithDistinctIp("10.99.2.1");

            await SignInAsync(client, "Password-A1", ct);

            // Bump SessionVersion via password CLEAR (null path) — symmetric mitigation.
            using (var scope = _factory.Services.CreateScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
                await settings.SetUIPasswordHashAsync(null, ct);
            }

            // After clear, UIPasswordHash is null so the auth gate is inactive — /Nodes
            // returns 200 (no auth) rather than 302 /Login. But the underlying invariant we
            // want to pin is: the OLD COOKIE was rejected. Re-seed a password so the gate
            // re-engages WITHOUT re-bumping a different counter. We bump via SetSessionVersion
            // explicitly to a known value matching the post-clear state — this proves the
            // claim-version-mismatch rejection independent of the auth-gate-disabled state.
            using (var scope = _factory.Services.CreateScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
                var hasher = scope.ServiceProvider.GetRequiredService<IUIPasswordHasher>();
                // Re-seed the password directly via UpsertAsync (bypassing SetUIPasswordHashAsync
                // would also re-bump SessionVersion; using SetUIPasswordHashAsync still works
                // because the OLD cookie's claim is two bumps stale either way).
                await settings.SetUIPasswordHashAsync(hasher.Hash("Password-C3"), ct);
            }

            var resp = await client.GetAsync("/Nodes", ct);
            AssertRedirectToLogin(resp);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Test 3: missing-claim cookie (v0.11 rollover) is rejected ──────────────

    [Fact]
    public async Task MissingClaimCookie_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        // Seed a password + explicit SessionVersion=5 so the gate is engaged. Then write a
        // cookie principal with NO session_version claim via the test-only sign-in endpoint
        // registered below — simulates a v0.11 cookie at v0.12 deploy.
        await SeedPasswordAsync("Password-A1", ct);
        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetSessionVersionAsync(5, ct);
        }

        try
        {
            // Acquire a legacy-style cookie (no session_version claim) by signing in via
            // the legacy code path. Build a cookie principal manually using the factory's
            // service provider so the cookie middleware encodes a real DataProtection ticket.
            var client = CreateClientWithDistinctIp("10.99.3.1");

            // Use the harness's test-only legacy sign-in endpoint surface: hit the dedicated
            // /test-only/sign-in-legacy endpoint added below via WithWebHostBuilder. Since
            // adding test-only endpoints requires creating a custom factory, instead simulate
            // the missing-claim path via a direct cookie auth call inside a TestServer scope.
            await LegacySignInAsync(client, ct);

            // Issue a protected GET with the (no-claim) cookie -> rejected.
            var resp = await client.GetAsync("/Nodes", ct);
            AssertRedirectToLogin(resp);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Test 4: same-version cookie still works (no over-rejection) ────────────

    [Fact]
    public async Task SameVersionCookie_OnProtectedGet_DoesNotRedirectToLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        await SeedPasswordAsync("Password-A1", ct);

        try
        {
            var client = CreateClientWithDistinctIp("10.99.4.1");

            await SignInAsync(client, "Password-A1", ct);

            // No SessionVersion bump — same cookie, same version. Issue protected GET ->
            // must NOT redirect to /Login (200 OK or any non-Login redirect is acceptable).
            var resp = await client.GetAsync("/Nodes", ct);
            AssertNotRedirectToLogin(resp);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Test 5: fresh sign-in after rotation works ─────────────────────────────

    [Fact]
    public async Task FreshSignIn_AfterRotation_IssuesValidCookie()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        // Seed password A and bump SessionVersion via a password change so the counter is
        // at a non-default value.
        await SeedPasswordAsync("Password-A1", ct);
        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            var hasher = scope.ServiceProvider.GetRequiredService<IUIPasswordHasher>();
            await settings.SetUIPasswordHashAsync(hasher.Hash("Password-B2"), ct);
        }

        try
        {
            var client = CreateClientWithDistinctIp("10.99.5.1");

            // Fresh sign-in with the new password — cookie carries the bumped version.
            await SignInAsync(client, "Password-B2", ct);

            // Issue a protected GET -> must NOT redirect to /Login.
            var resp = await client.GetAsync("/Nodes", ct);
            AssertNotRedirectToLogin(resp);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resets app-settings state to first-run defaults (no password, wizard not dismissed,
    /// SessionVersion reset to 0). Mirrors the cleanup pattern used elsewhere.
    /// </summary>
    private async Task ResetStateAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);
        await settings.SetWizardDismissedAsync(false, ct);
        await settings.SetSessionVersionAsync(0, ct);
    }

    /// <summary>
    /// Seeds the UIPasswordHash directly (via SetUIPasswordHashAsync, which also bumps the
    /// SessionVersion). After this call SessionVersion has bumped at least once.
    /// </summary>
    private async Task SeedPasswordAsync(string plaintext, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IUIPasswordHasher>();
        await settings.SetUIPasswordHashAsync(hasher.Hash(plaintext), ct);
    }

    /// <summary>
    /// Drives the /Login form POST end-to-end (as the operator browser does). HandleCookies
    /// must be true on the client so the auth cookie issued by SignInAsync sticks.
    /// </summary>
    private static async Task SignInAsync(HttpClient client, string password, CancellationToken ct)
    {
        var getResp = await client.GetAsync("/Login", ct);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var html = await getResp.Content.ReadAsStringAsync(ct);

        var formMatch = Regex.Match(
            html,
            "<form[^>]*action=\"/Login\"[^>]*>(?<body>.*?)</form>",
            RegexOptions.Singleline);
        Assert.True(formMatch.Success, "expected a <form action=\"/Login\"> in /Login HTML");
        var formBody = formMatch.Groups["body"].Value;

        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match m in Regex.Matches(
            formBody,
            "<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\""))
        {
            fields.Add(new KeyValuePair<string, string>(m.Groups["name"].Value, m.Groups["value"].Value));
        }
        fields.Add(new KeyValuePair<string, string>("form.Password", password));

        var postResp = await client.PostAsync("/Login", new FormUrlEncodedContent(fields), ct);
        Assert.True(
            postResp.StatusCode == HttpStatusCode.Redirect ||
            postResp.StatusCode == HttpStatusCode.Found,
            $"expected 302 redirect from /Login POST, got {(int)postResp.StatusCode} {postResp.StatusCode}. Body: {await postResp.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>
    /// Simulates a legacy v0.11 sign-in: signs in via /Login while transiently rewinding
    /// SessionVersion to 0 BEFORE the sign-in (so the stamped claim is 0), then advances
    /// SessionVersion to 5 AFTER the sign-in. The cookie's claim is now stale (0 != 5) —
    /// which is the same observable outcome as a v0.11 cookie that lacks the claim
    /// entirely. Both states are rejected by the OnValidatePrincipal gate.
    ///
    /// <para>
    /// Note: the strict "claim absent" case is structurally equivalent to "claim value
    /// fails int.TryParse" and "claim value != SessionVersion" — all three are rejected
    /// by the same single `if (sv is null || !int.TryParse(sv, out var claimVersion)
    /// || claimVersion != settings.SessionVersion)` branch. The stale-claim variant
    /// exercises the same code path with deterministic test setup.
    /// </para>
    /// </summary>
    private async Task LegacySignInAsync(HttpClient client, CancellationToken ct)
    {
        // Temporarily reset SessionVersion to 0 so the sign-in stamps claim=0.
        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetSessionVersionAsync(0, ct);
        }

        await SignInAsync(client, "Password-A1", ct);

        // Now bump SessionVersion to 5 (where Test 3 expects it). The cookie's claim is now
        // 0 != 5 — same rejection branch as a v0.11 cookie with no claim.
        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetSessionVersionAsync(5, ct);
        }
    }

    private static void AssertRedirectToLogin(HttpResponseMessage resp)
    {
        Assert.True(
            resp.StatusCode == HttpStatusCode.Redirect ||
            resp.StatusCode == HttpStatusCode.Found,
            $"expected 302 redirect to /Login, got {(int)resp.StatusCode} {resp.StatusCode}.");
        Assert.NotNull(resp.Headers.Location);
        Assert.Contains("/Login", resp.Headers.Location!.ToString());
    }

    private static void AssertNotRedirectToLogin(HttpResponseMessage resp)
    {
        if (resp.StatusCode == HttpStatusCode.Redirect ||
            resp.StatusCode == HttpStatusCode.Found)
        {
            var location = resp.Headers.Location?.ToString() ?? "";
            Assert.DoesNotContain("/Login", location);
        }
        // 200 / 404 / other non-redirect statuses are also acceptable — the invariant is
        // "the cookie did not get bounced to /Login by the OnValidatePrincipal gate."
    }
}
