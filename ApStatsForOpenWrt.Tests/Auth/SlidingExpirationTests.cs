using System.Net;
using System.Text.RegularExpressions;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Services.Auth;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Auth;

/// <summary>
/// M-SEC-04 (Plan 15-04) sliding-expiration invariant.
///
/// <para>
/// Cookie lifetime configuration (ExpireTimeSpan = 7 days, SlidingExpiration = true) is
/// UNCHANGED in v0.12 per the CONTEXT.md mid-flight decision — rotation-on-password-change
/// is the cookie-compromise mitigation, NOT a shortened TTL. This test pins the contract
/// so a future regression that drops sliding expiration or shrinks the 7-day window will
/// trip a red light immediately.
/// </para>
///
/// <para>
/// Strategy selected at execution time (plan 15-04 punted between two): <b>behavioral
/// end-to-end via FakeTimeProvider</b>. Cookie auth's expiration attribute is encoded
/// inside the encrypted DataProtection ticket, NOT the HTTP-level Set-Cookie Expires
/// attribute (the production cookie is a session cookie at the HTTP level — no Expires
/// attribute is emitted). Parsing Set-Cookie therefore would not actually observe the
/// 7-day TTL. Behavioral assertion (cookie still valid at T+6d sliding window, rejected
/// at T+15d past renewal) IS the observable invariant from the test harness, and is also
/// what an operator actually experiences.
/// </para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "SecurityFix")]
public sealed class SlidingExpirationTests
{
    [Fact]
    public async Task Cookie_StillValid_AfterSixDays_WithSlidingExpiration()
    {
        // At T0 sign in; advance to T0+6d (within ExpireTimeSpan=7d window); cookie still
        // valid -> protected GET does NOT redirect to /Login. The sliding refresh on this
        // GET extends the cookie's expiry forward by ~7 days; advance to T0+13d (BEFORE the
        // new expiry at T0+13d) and a second GET still works. Advance past the renewed
        // expiry (T0+13d) — well past it (T0+15d, 2 days beyond the renewal) — and a third
        // GET MUST redirect to /Login because the cookie ticket is now genuinely expired.
        var ct = TestContext.Current.CancellationToken;

        var startUtc = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(startUtc);

        await using var factory = new FakeClockFactory(fake);
        // Eager bootstrap.
        _ = factory.CreateClient();

        // Seed password on the same factory instance.
        using (var scope = factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            var hasher = scope.ServiceProvider.GetRequiredService<IUIPasswordHasher>();
            await settings.SetUIPasswordHashAsync(hasher.Hash("Password-A1"), ct);
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.99.6.1");

        // Sign in at T0.
        await SignInAsync(client, "Password-A1", ct);

        // Advance to T0 + 6 days (within the original 7-day expiry).
        fake.Advance(TimeSpan.FromDays(6));

        // Protected GET — cookie is still valid; the sliding-expiration refresh fires
        // because elapsed/expire >= 0.5, extending the cookie expiry to ~T0+13d.
        var resp1 = await client.GetAsync("/Nodes", ct);
        AssertNotRedirectToLogin(resp1);

        // Advance to T0 + 6d + 6d = T0 + 12d (within the post-refresh expiry of ~T0+13d).
        fake.Advance(TimeSpan.FromDays(6));
        var resp2 = await client.GetAsync("/Nodes", ct);
        AssertNotRedirectToLogin(resp2);

        // Advance to T0 + 12d + 3d = T0 + 15d (PAST the post-refresh expiry at ~T0+13d
        // even allowing for the second sliding refresh — third refresh would have pushed
        // it to ~T0+19d, but we are now well past the ENTIRE 7-day window from the second
        // refresh too if we go far enough). Use a comfortable margin past the canonical
        // window to make the assertion robust.
        fake.Advance(TimeSpan.FromDays(15));
        var resp3 = await client.GetAsync("/Nodes", ct);
        AssertRedirectToLogin(resp3);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the /Login form POST end-to-end. Mirrors AuthGateTests's harvester so the
    /// cookie middleware encodes a real DataProtection ticket the FakeTimeProvider can later
    /// arbitrate against.
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
    }

    /// <summary>
    /// WebApplicationFactory that injects a <see cref="FakeTimeProvider"/> singleton so
    /// the cookie auth pipeline's TimeProvider (wired via
    /// <c>CookieAuthTimeProviderConfigurator</c>) reads test-controlled time.
    /// Trusts 0.0.0.0/0 as a proxy so X-Forwarded-For partitions the LoginPolicy bucket
    /// distinctly from sibling tests in the Integration collection.
    /// </summary>
    private sealed class FakeClockFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _dataSource = $"SlideExp-{Guid.NewGuid():N}";
        private readonly FakeTimeProvider _fake;
        private Microsoft.Data.Sqlite.SqliteConnection? _keepAlive;

        public FakeClockFactory(FakeTimeProvider fake)
        {
            _fake = fake;
            _keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={_dataSource};Mode=Memory;Cache=Shared");
            _keepAlive.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IDbContextFactory<DatabaseContext>));
                if (dbDescriptor is not null) services.Remove(dbDescriptor);
                services.AddDbContextFactory<DatabaseContext>(opts =>
                    opts.UseSqlite($"Data Source={_dataSource};Mode=Memory;Cache=Shared"));

                // Swap TimeProvider.System for FakeTimeProvider so the cookie auth pipeline
                // (via CookieAuthTimeProviderConfigurator) reads test-controlled time.
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_fake);

                // Trust all proxies so X-Forwarded-For drives the LoginPolicy partition.
                services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(opts =>
                {
                    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                    opts.KnownIPNetworks.Clear();
                    opts.KnownProxies.Clear();
                    opts.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.Parse("0.0.0.0"), 0));
                });
            });
        }

        public new async ValueTask DisposeAsync()
        {
            _keepAlive?.Dispose();
            _keepAlive = null;
            await base.DisposeAsync();
        }
    }
}
