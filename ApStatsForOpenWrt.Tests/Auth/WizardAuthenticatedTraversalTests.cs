using System.Net;
using System.Text.RegularExpressions;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.Integration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Auth;

/// <summary>
/// Regression lock for the wizard-lockout-regression (debug session
/// <c>wizard-lockout-regression</c>): the M-SEC-02 <c>/setup/*</c> 404 gate must be
/// AUTH-AWARE. Setting the password is wizard step 1; it flips
/// <c>hasPassword == true</c> and signs the operator in. The just-signed-in operator
/// MUST be able to traverse the remaining wizard steps (<c>/setup/nodes</c> step 2,
/// <c>/setup/wlans</c> step 3). Only ANONYMOUS callers get the friendly 404 once the
/// system is committed (anti-takeover, M-SEC-02 intent — covered by the existing
/// <see cref="WizardRedirectMiddlewareTests"/> anonymous suite, which is intentionally
/// retained unchanged).
///
/// <para>
/// This is the authenticated-operator coverage that was missing (which is why CI
/// stayed green while the lockout shipped). The round-trip follows the
/// <see cref="SetupPostEndpointTests"/> harness pattern: <c>HandleCookies=true</c>
/// client carries the <c>SignInAsync</c> auth cookie across the subsequent GET.
/// </para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "SecurityFix")]
public sealed class WizardAuthenticatedTraversalTests(SmokeFactoryFixture factory)
    : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    [Fact]
    public async Task AuthenticatedOperator_AfterSubmitPassword_CanReachSetupNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        // HandleCookies=true: the auth cookie issued by SignInAsync inside
        // /setup/submit-password is retained and replayed on the follow-up GET.
        // AllowAutoRedirect=false: we drive the 302 -> GET /setup/nodes ourselves so
        // we can assert the GET's own status (NOT the redirect's).
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Acquire an antiforgery token from the fresh-state wizard step 1.
        var token = await AcquireAntiforgeryTokenFromSetupPasswordAsync(client, ct);

        try
        {
            // Step 1: POST a policy-valid password + matching confirm. This sets the
            // hash, bumps SessionVersion, SignInAsync issues the auth cookie, and
            // redirects to /setup/nodes (step 2).
            var post = await PostFormAsync(client, "/setup/submit-password", token, new[]
            {
                new KeyValuePair<string, string>("action", "set"),
                new KeyValuePair<string, string>("Password", "Password1"),
                new KeyValuePair<string, string>("ConfirmPassword", "Password1"),
            }, ct);

            Assert.True(
                post.StatusCode == HttpStatusCode.Redirect ||
                post.StatusCode == HttpStatusCode.Found,
                $"step 1 should 302 to /setup/nodes; got {(int)post.StatusCode} {post.StatusCode}.");
            Assert.NotNull(post.Headers.Location);
            Assert.Contains("/setup/nodes", post.Headers.Location!.ToString());

            // Step 2: the now-authenticated operator follows the redirect. hasPassword
            // is true, but the operator is authenticated, so the auth-aware gate MUST
            // let them through (NOT the friendly "Setup complete" 404).
            var nodes = await client.GetAsync("/setup/nodes", ct);

            Assert.NotEqual(HttpStatusCode.NotFound, nodes.StatusCode);
            Assert.Equal(HttpStatusCode.OK, nodes.StatusCode);

            var body = await nodes.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain("This setup wizard has already been completed.", body);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    [Fact]
    public async Task AuthenticatedOperator_AfterSubmitPassword_CanReachSetupWlans()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var token = await AcquireAntiforgeryTokenFromSetupPasswordAsync(client, ct);

        try
        {
            var post = await PostFormAsync(client, "/setup/submit-password", token, new[]
            {
                new KeyValuePair<string, string>("action", "set"),
                new KeyValuePair<string, string>("Password", "Password1"),
                new KeyValuePair<string, string>("ConfirmPassword", "Password1"),
            }, ct);

            Assert.True(
                post.StatusCode == HttpStatusCode.Redirect ||
                post.StatusCode == HttpStatusCode.Found,
                $"step 1 should 302; got {(int)post.StatusCode} {post.StatusCode}.");

            // Step 3 (final wizard page) must also be reachable for the authenticated operator.
            var wlans = await client.GetAsync("/setup/wlans", ct);

            Assert.NotEqual(HttpStatusCode.NotFound, wlans.StatusCode);
            Assert.Equal(HttpStatusCode.OK, wlans.StatusCode);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Helpers (mirrors SetupPostEndpointTests harness) ───────────────────────

    private async Task ResetStateAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);
        await settings.SetWizardDismissedAsync(false, ct);
    }

    private static async Task<string> AcquireAntiforgeryTokenFromSetupPasswordAsync(
        HttpClient client, CancellationToken ct)
    {
        var resp = await client.GetAsync("/setup/password", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync(ct);

        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"");
        if (!match.Success)
        {
            match = Regex.Match(
                html,
                "value=\"(?<value>[^\"]+)\"[^>]*name=\"__RequestVerificationToken\"");
        }
        Assert.True(match.Success,
            "expected a __RequestVerificationToken hidden input in /setup/password HTML");
        return match.Groups["value"].Value;
    }

    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string url,
        string antiforgeryToken,
        IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken ct)
    {
        var withToken = new List<KeyValuePair<string, string>>(fields)
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", antiforgeryToken),
        };
        return client.PostAsync(url, new FormUrlEncodedContent(withToken), ct);
    }
}
