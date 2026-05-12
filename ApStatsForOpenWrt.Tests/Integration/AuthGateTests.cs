using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// SEC-05 + Pitfall 1 integration suite. Drives the auth gate end-to-end via
/// WebApplicationFactory&lt;Program&gt;. Per D-01 the gate engages only when
/// IAppSettingsService.UIPasswordHash is non-empty.
/// </summary>
[Collection("Integration")]
public sealed class AuthGateTests(SmokeFactoryFixture factory) : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    [Fact]
    public async Task Anonymous_WhenHashNull_RootIsNotAuthBlocked()
    {
        // SEC-05: when UIPasswordHash is null the auth gate is inactive.
        // Phase 4 note: GET / may return 302 to /setup/password (wizard redirect) in first-run
        // state (zero nodes, null hash, WizardDismissed=false). This is NOT an auth block.
        // The invariant is: the redirect MUST NOT be to /Login.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);   // ensure null

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/", ct);
        // Auth gate inactive: must not redirect to /Login.
        if (resp.StatusCode == System.Net.HttpStatusCode.Redirect ||
            resp.StatusCode == System.Net.HttpStatusCode.Found)
        {
            var location = resp.Headers.Location?.ToString() ?? "";
            Assert.DoesNotContain("/Login", location);
        }
        else
        {
            // 200 is also acceptable (e.g. when wizard is dismissed or nodes exist).
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        }
    }

    [Fact]
    public async Task RedirectsToLogin_WhenHashSet_AndNoCookie()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync("AQAAA-fake-hash", ct);

        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var resp = await client.GetAsync("/", ct);
            // Cookie middleware redirects to LoginPath when the fallback policy fails.
            Assert.True(resp.StatusCode == System.Net.HttpStatusCode.Redirect ||
                        resp.StatusCode == System.Net.HttpStatusCode.Found);
            Assert.NotNull(resp.Headers.Location);
            Assert.Contains("/Login", resp.Headers.Location!.ToString());
        }
        finally
        {
            // Cleanup so sibling tests see null again.
            await settings.SetUIPasswordHashAsync(null, ct);
        }
    }

    [Fact]
    public async Task AnonymousAfterClear_RootIsNotAuthBlocked_NoStaleCacheing()
    {
        // Pitfall 1 — set, then clear, then anonymous request must not be auth-blocked.
        // Phase 4 note: GET / may return 302 to /setup/password (wizard redirect) when hash
        // reverts to null and zero nodes exist. This is NOT an auth block.
        // The invariant is: the redirect MUST NOT be to /Login.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync("AQAAA-temp", ct);
        await settings.SetUIPasswordHashAsync(null, ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/", ct);
        // Auth gate cleared: must not redirect to /Login.
        if (resp.StatusCode == System.Net.HttpStatusCode.Redirect ||
            resp.StatusCode == System.Net.HttpStatusCode.Found)
        {
            var location = resp.Headers.Location?.ToString() ?? "";
            Assert.DoesNotContain("/Login", location);
        }
        else
        {
            // 200 is also acceptable (e.g. when wizard is dismissed or nodes exist).
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        }
    }

    [Fact]
    public async Task LoginPage_IsReachable_WhenAnonymous()
    {
        // /Login is [AllowAnonymous]; reachable regardless of UIPasswordHash state.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync("AQAAA-real", ct);

        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var resp = await client.GetAsync("/Login", ct);
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            await settings.SetUIPasswordHashAsync(null, ct);
        }
    }

    // Phase 7 LIVE-UAT row 3.3: in-browser POST to /Login failed with "A valid antiforgery
    // token was not provided" because the form had TWO __RequestVerificationToken hidden
    // inputs — one auto-emitted by <EditForm method="post"> and one from a redundant explicit
    // <AntiforgeryToken /> sibling. Browsers submit both; Request.Form coalesces them into a
    // comma-separated StringValues; validation fails. The two tests below guard against
    // re-introducing the duplicate.

    [Fact]
    public async Task LoginPage_GET_EmitsExactlyOneAntiforgeryToken_AndSetsCookie()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync("AQAAA-real", ct);

        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var resp = await client.GetAsync("/Login", ct);
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

            var html = await resp.Content.ReadAsStringAsync(ct);

            var setCookies = resp.Headers.TryGetValues("Set-Cookie", out var values)
                ? string.Join("; ", values)
                : "";
            Assert.Contains(".AspNetCore.Antiforgery", setCookies);

            // The /Login form MUST contain EXACTLY ONE __RequestVerificationToken hidden input.
            // <EditForm method="post"> auto-injects one; a redundant explicit <AntiforgeryToken />
            // sibling would produce a second one and break validation when the browser POSTs.
            var formMatch = System.Text.RegularExpressions.Regex.Match(
                html,
                "<form[^>]*action=\"/Login\"[^>]*>.*?</form>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.True(formMatch.Success, "expected a <form action=\"/Login\"> in /Login HTML");
            var tokenCount = System.Text.RegularExpressions.Regex.Matches(
                formMatch.Value,
                "name=\"__RequestVerificationToken\"").Count;
            Assert.Equal(1, tokenCount);
        }
        finally
        {
            await settings.SetUIPasswordHashAsync(null, ct);
        }
    }

    [Fact]
    public async Task LoginPage_POST_FaithfullyReplaysFormFields_RoundTripsAntiforgery_AndSignsIn()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        var hasher = scope.ServiceProvider.GetRequiredService<ApStatsForOpenWrt.Services.Auth.IUIPasswordHasher>();
        var hash = hasher.Hash("operator-pw");
        await settings.SetUIPasswordHashAsync(hash, ct);

        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

            // GET /Login → harvest the form so we replay it exactly the way a browser would.
            var getResp = await client.GetAsync("/Login", ct);
            Assert.Equal(System.Net.HttpStatusCode.OK, getResp.StatusCode);
            var html = await getResp.Content.ReadAsStringAsync(ct);

            var formMatch = System.Text.RegularExpressions.Regex.Match(
                html,
                "<form[^>]*action=\"/Login\"[^>]*>(?<body>.*?)</form>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.True(formMatch.Success, "expected a <form action=\"/Login\"> in /Login HTML");
            var formBody = formMatch.Groups["body"].Value;

            // Replay every hidden input — duplicate tokens (if any) included, exactly like a browser.
            var fields = new List<KeyValuePair<string, string>>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                formBody,
                "<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\""))
            {
                fields.Add(new KeyValuePair<string, string>(m.Groups["name"].Value, m.Groups["value"].Value));
            }
            // Plus the password input the operator types.
            fields.Add(new KeyValuePair<string, string>("form.Password", "operator-pw"));

            var postResp = await client.PostAsync("/Login", new System.Net.Http.FormUrlEncodedContent(fields), ct);

            // Sign-in flow ends with a 302 redirect to "/", NOT a 400 antiforgery error.
            Assert.True(
                postResp.StatusCode == System.Net.HttpStatusCode.Redirect ||
                postResp.StatusCode == System.Net.HttpStatusCode.Found,
                $"expected 302 redirect from /Login POST, got {(int)postResp.StatusCode} {postResp.StatusCode}. Body: {await postResp.Content.ReadAsStringAsync(ct)}");
        }
        finally
        {
            await settings.SetUIPasswordHashAsync(null, ct);
        }
    }
}
