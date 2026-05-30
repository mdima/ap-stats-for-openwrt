using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// SEC-06 + Pitfall 3 + Pitfall 4 integration suite. Drives the M-SEC-03 login
/// brute-force limiter (per-IP 5/min + global 30/min) end-to-end.
///
/// <para>
/// <b>POST-only contract (debug login-429-counts-page-views, 2026-05-30).</b> The
/// brute-force limiter counts submitted ATTEMPTS (POST /Login), never page VIEWS
/// (GET /Login) or any other method. A legitimate operator who merely views/reloads
/// the login page (e.g. the sign-out → /Login flow) must NEVER be locked out, so GET
/// /Login is free regardless of frequency. Both budgets live in
/// <c>opts.GlobalLimiter</c> (Program.cs), gated on
/// <c>Request.Method == "POST" &amp;&amp; Path.StartsWithSegments("/Login")</c>; the per-IP
/// 5/min trip is exercised by <see cref="ApStatsForOpenWrt.Tests.Auth.LoginGlobalRateLimitTests"/>
/// in the RateLimitSerial collection (which needs the X-Forwarded-For partition trick).
/// </para>
/// </summary>
[Collection("Integration")]
public sealed class RateLimitTests(SmokeFactoryFixture factory) : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    [Fact]
    public async Task BlazorAndStaticFiles_NotRateLimited()
    {
        // Pitfall 3: rate limit is on POST /Login only — /lib/* static assets must not 429.
        var ct = TestContext.Current.CancellationToken;
        var client = _factory.CreateClient();

        for (int i = 0; i < 20; i++)
        {
            var resp = await client.GetAsync("/lib/adminlte/css/adminlte.min.css", ct);
            Assert.NotEqual(System.Net.HttpStatusCode.TooManyRequests, resp.StatusCode);
        }
    }

    [Fact]
    public async Task GetLoginPageViews_AreFree_NeverReturn429()
    {
        // login-429-counts-page-views — RED→GREEN regression lock for the operator's
        // primary symptom ("set a password, sign out, /Login returns 429 on the first try").
        //
        // BEFORE the POST-only fix, the [EnableRateLimiting("LoginPolicy")] attribute on
        // Login.razor + the path-only (no method check) GlobalLimiter gate counted GET page
        // views against the per-IP 5/min budget, so the 6th GET /Login → 429. This test issues
        // far more than 5 GETs from the same synthetic TestServer peer IP and asserts NONE of
        // them 429. Page views — including the sign-out → /Login redirect chain and any reloads
        // — must be free no matter how many.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync("AQAAA-someHash", ct);

        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            for (int i = 0; i < 12; i++)
            {
                var resp = await client.GetAsync("/Login", ct);
                Assert.NotEqual(System.Net.HttpStatusCode.TooManyRequests, resp.StatusCode);
            }
        }
        finally
        {
            await settings.SetUIPasswordHashAsync(null, ct);
        }
    }
}
