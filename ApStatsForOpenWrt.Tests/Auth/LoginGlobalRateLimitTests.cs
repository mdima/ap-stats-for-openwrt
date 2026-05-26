using System.Net;
using ApStatsForOpenWrt.Services.Auth;
using ApStatsForOpenWrt.Services.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Auth;

/// <summary>
/// M-SEC-03 (plan 15-05) integration suite for the LoginGlobalPolicy distributed
/// brute-force ceiling (30 requests/minute server-wide, partition key "_global_").
///
/// <para>
/// The plan originally prescribed two stacked [EnableRateLimiting] attributes on
/// Login.razor (LoginPolicy per-IP + LoginGlobalPolicy global). EnableRateLimitingAttribute
/// is AllowMultiple=false in ASP.NET Core 10, so the production code enforces the
/// global ceiling via opts.GlobalLimiter (path-gated to /Login) instead. The 429
/// surface and ceiling values are identical to the attribute-chain design.
/// </para>
///
/// <para>
/// Each test issues N POST requests to /Login with distinct X-Forwarded-For values
/// so the per-IP LoginPolicy partition (5/IP/min) never trips — every request goes
/// to a fresh per-IP bucket. The only partition that can reject is the global
/// "_global_" partition gated at 30/min. Fact 1 (35 requests) MUST see at least one
/// 429 after request #30. Fact 2 (25 requests) MUST see no 429.
/// </para>
///
/// <para>
/// Forwarded-headers opt-in: Phase 16 plan 16-01 (M-SEC-05) HAS folded forwarded-headers
/// into conditional registration — production <c>app.UseForwardedHeaders()</c> only
/// registers when <c>ForwardedHeaders__KnownProxies</c> and/or
/// <c>ForwardedHeaders__KnownNetworks</c> is set. This factory opts in by setting
/// <c>ForwardedHeaders__KnownNetworks=0.0.0.0/0</c> in its constructor (BEFORE the host
/// builds) so the synthetic loopback sender is honored and the spoofed X-Forwarded-For
/// rewrites RemoteIpAddress; the prior env-var value is restored on dispose. The old
/// test-scoped <c>services.Configure&lt;ForwardedHeadersOptions&gt;</c> DI override is dead
/// under conditional registration and has been removed.
/// </para>
/// </summary>
[Collection("RateLimitSerial")]
[Trait("Category", "SecurityFix")]
public sealed class LoginGlobalRateLimitTests
{
    // Each test creates its OWN GlobalRateLimitFactory instance so the process-wide
    // "_global_" FixedWindow bucket is fresh per fact (the bucket is owned by the
    // RateLimiter middleware singleton inside that factory's host). An IClassFixture
    // would share the host (and the bucket) across all facts; once the first fact
    // exhausts the 30/min ceiling, subsequent facts would never see <30 budget
    // even though they only issue 25 requests.

    /// <summary>
    /// Test-only factory mirroring SessionRotationTests's ForwardedHeadersTrustingFactory
    /// (in-memory SQLite + keep-alive). Additionally trusts the entire IPv4 space as a
    /// forwarded-headers proxy so per-request X-Forwarded-For headers rewrite
    /// RemoteIpAddress and the LoginPolicy per-IP partition gets a fresh bucket per
    /// request. The global "_global_" partition is shared across all requests inside
    /// this factory instance — which is exactly what the LoginGlobalPolicy ceiling
    /// is supposed to cap.
    /// </summary>
    public sealed class GlobalRateLimitFactory
        : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly string _dataSource = $"LoginGlobalRL-{Guid.NewGuid():N}";
        private Microsoft.Data.Sqlite.SqliteConnection? _keepAlive;

        // M-SEC-05 / plan 16-01 Task 3 — the production ForwardedHeaders registration is now
        // env-var-gated (was unconditional in v0.11). This test needs X-Forwarded-For honored
        // so each request lands in a distinct /Login per-IP rate-limit partition (only the
        // shared "_global_" ceiling should reject). Set the env var BEFORE the host builds
        // (first CreateClient) and restore on dispose. 0.0.0.0/0 trusts the synthetic loopback
        // test peer — behaviorally identical to the old DI override this replaces.
        private readonly string? _priorKnownNetworks =
            Environment.GetEnvironmentVariable("ForwardedHeaders__KnownNetworks");

        public GlobalRateLimitFactory()
        {
            Environment.SetEnvironmentVariable("ForwardedHeaders__KnownNetworks", "0.0.0.0/0");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // Swap production SQLite for in-memory (SmokeFactoryFixture pattern).
                var dbDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IDbContextFactory<ApStatsForOpenWrt.Data.DatabaseContext>));
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
            Environment.SetEnvironmentVariable("ForwardedHeaders__KnownNetworks", _priorKnownNetworks);
            _keepAlive?.Dispose();
            base.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static HttpClient CreateClient(GlobalRateLimitFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,   // each request stands alone; no shared cookie state
        });

    /// <summary>
    /// Seeds the UI password so /Login is reachable (the gate is engaged) before the
    /// request burst. Without a seeded password, /Login may not behave the same way
    /// (and the wizard-redirect middleware may intercept GET /).
    /// </summary>
    private static async Task SeedPasswordAsync(GlobalRateLimitFactory factory, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IUIPasswordHasher>();
        await settings.SetUIPasswordHashAsync(hasher.Hash("Seed-Password-1"), ct);
    }

    // ── Fact 1: 35 requests from distinct IPs → at least one 429 ───────────────

    [Fact]
    public async Task ThirtyFivePostsFromDistinctIps_AtLeastOneReturns429()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new GlobalRateLimitFactory();
        await factory.InitializeAsync();
        try
        {
            await SeedPasswordAsync(factory, ct);
            var client = CreateClient(factory);
            int statusFor429 = 0;
            for (int i = 1; i <= 35; i++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/Login")
                {
                    // Minimal body — we don't need a valid antiforgery token. The rate-limit
                    // middleware runs BEFORE the antiforgery validation, so a 429 will fire
                    // ahead of any 400 from a missing token once the budget is exhausted.
                    // Before exhaustion, requests will be 400/200/etc., none of which are 429.
                    Content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("form.Password", "AnyPassword1!"),
                    }),
                };
                req.Headers.Add("X-Forwarded-For", $"10.0.0.{i}");
                using var resp = await client.SendAsync(req, ct);
                if ((int)resp.StatusCode == 429 || resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    statusFor429++;
                }
            }

            Assert.True(
                statusFor429 >= 1,
                $"expected at least one HTTP 429 across 35 distinct-IP POSTs to /Login (LoginGlobalPolicy ceiling = 30/min); got {statusFor429}.");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── Fact 2: 25 requests from distinct IPs → no 429 ─────────────────────────

    [Fact]
    public async Task TwentyFivePostsFromDistinctIps_NeverReturn429()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new GlobalRateLimitFactory();
        await factory.InitializeAsync();
        try
        {
            await SeedPasswordAsync(factory, ct);
            var client = CreateClient(factory);
            int statusFor429 = 0;
            for (int i = 100; i < 125; i++)   // distinct IP range from Fact 1
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/Login")
                {
                    Content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("form.Password", "AnyPassword1!"),
                    }),
                };
                req.Headers.Add("X-Forwarded-For", $"10.1.0.{i}");
                using var resp = await client.SendAsync(req, ct);
                if ((int)resp.StatusCode == 429 || resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    statusFor429++;
                }
            }

            // The ceiling is at 30; 25 requests must NEVER produce a 429.
            Assert.Equal(0, statusFor429);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}

/// <summary>
/// Serial-execution collection definition for rate-limit tests so the process-wide
/// "_global_" FixedWindow bucket doesn't bleed across concurrently-executing facts.
/// xUnit v3 honors collection serialization without an explicit DisableParallelization
/// flag (one fact at a time within a collection).
/// </summary>
[CollectionDefinition("RateLimitSerial")]
public sealed class RateLimitSerialCollection { }
