using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.Integration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Auth;

/// <summary>
/// M-SEC-01 (Plan 15-03) acceptance suite for the three MapPost lambdas
/// (/setup/submit-password, /setup/skip, /Logout):
///
/// <list type="number">
///   <item>Antiforgery rejection on each endpoint when no token is supplied -> 400.</item>
///   <item>Commitment 404 on /setup/submit-password and /setup/skip once a real
///   commitment exists (UIPasswordHash set OR any WiFiNode row).</item>
///   <item>Option B resumability: WizardDismissed=true alone does NOT close the POST
///   endpoints (operator can still re-submit the wizard until a real commitment lands).</item>
///   <item>Regression: first-run flow still succeeds end-to-end.</item>
/// </list>
///
/// Uses the shared <see cref="SmokeFactoryFixture"/> with the in-memory SQLite database.
/// Each test resets the relevant state in try/finally to keep the fixture's shared DB
/// state clean for sibling tests.
/// </summary>
[Collection("Integration")]
[Trait("Category", "SecurityFix")]
public sealed class SetupPostEndpointTests(SmokeFactoryFixture factory)
    : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    // ── Antiforgery rejection: no token -> 400 ─────────────────────────────────

    [Fact]
    public async Task SubmitPassword_WithoutAntiforgeryToken_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var resp = await client.PostAsync(
            "/setup/submit-password",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("action", "set"),
                new KeyValuePair<string, string>("Password", "Password1"),
                new KeyValuePair<string, string>("ConfirmPassword", "Password1"),
            }),
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Skip_WithoutAntiforgeryToken_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var resp = await client.PostAsync(
            "/setup/skip",
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()),
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutAntiforgeryToken_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var resp = await client.PostAsync(
            "/Logout",
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()),
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Commitment 404: real commitment exists -> 404 even with valid token ─────

    [Fact]
    public async Task SubmitPassword_WithValidToken_WhenPasswordAlreadySet_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Acquire a token from a fresh-state GET BEFORE we commit the password (otherwise
        // the wizard middleware will already serve 404 and we cannot grab a token).
        var token = await AcquireAntiforgeryTokenFromSetupPasswordAsync(client, ct);

        // Now commit the password (this triggers the wizard-closed gate).
        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetUIPasswordHashAsync("AQAAA-fake-hash", ct);
        }

        try
        {
            var resp = await PostFormAsync(client, "/setup/submit-password", token, new[]
            {
                new KeyValuePair<string, string>("action", "set"),
                new KeyValuePair<string, string>("Password", "Password1"),
                new KeyValuePair<string, string>("ConfirmPassword", "Password1"),
            }, ct);

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    [Fact]
    public async Task SubmitPassword_WithValidToken_WhenAnyNodeExists_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Acquire a token from a fresh-state GET before seeding the node (after seeding,
        // the wizard 404 gate fires on GET /setup/password too).
        var token = await AcquireAntiforgeryTokenFromSetupPasswordAsync(client, ct);

        // Seed a WiFiNode so anyNodes == true.
        int seededId;
        using (var scope = _factory.Services.CreateScope())
        {
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeRepo>();
            var node = new WiFiNode
            {
                Hostname = "ap-test.local",
                Username = "root",
                Password = "encrypted-blob",
            };
            await nodeRepo.AddAsync(node, ct);
            seededId = node.Id;
        }

        try
        {
            var resp = await PostFormAsync(client, "/setup/submit-password", token, new[]
            {
                new KeyValuePair<string, string>("action", "set"),
                new KeyValuePair<string, string>("Password", "Password1"),
                new KeyValuePair<string, string>("ConfirmPassword", "Password1"),
            }, ct);

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeRepo>();
            await nodeRepo.DeleteAsync(seededId, ct);
            await ResetStateAsync(ct);
        }
    }

    [Fact]
    public async Task Skip_WithValidToken_WhenPasswordAlreadySet_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var token = await AcquireAntiforgeryTokenFromSetupPasswordAsync(client, ct);

        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetUIPasswordHashAsync("AQAAA-fake-hash", ct);
        }

        try
        {
            var resp = await PostFormAsync(client, "/setup/skip", token,
                Array.Empty<KeyValuePair<string, string>>(), ct);

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Option B resumability: WizardDismissed alone does NOT close the endpoints ──

    [Fact]
    public async Task SubmitPassword_WithValidToken_WhenWizardDismissedOnly_Succeeds()
    {
        // Critical Option B test: WizardDismissed=true alone, with no nodes and no
        // password, must NOT close the POST endpoint. Operator must be able to resume
        // the wizard via direct URL.
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var token = await AcquireAntiforgeryTokenFromSetupPasswordAsync(client, ct);

        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetWizardDismissedAsync(true, ct);
        }

        try
        {
            var resp = await PostFormAsync(client, "/setup/submit-password", token, new[]
            {
                new KeyValuePair<string, string>("action", "set"),
                new KeyValuePair<string, string>("Password", "Password1"),
                new KeyValuePair<string, string>("ConfirmPassword", "Password1"),
            }, ct);

            // Successful first-run sign-in lands as a 302 redirect to /setup/nodes.
            Assert.True(
                resp.StatusCode == HttpStatusCode.Redirect ||
                resp.StatusCode == HttpStatusCode.Found,
                $"expected 302 redirect, got {(int)resp.StatusCode} {resp.StatusCode}.");
            Assert.NotNull(resp.Headers.Location);
            Assert.Contains("/setup/nodes", resp.Headers.Location!.ToString());
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Regression: first-run flow still works end-to-end ──────────────────────

    [Fact]
    public async Task SubmitPassword_WithValidToken_FreshState_Succeeds()
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
            var resp = await PostFormAsync(client, "/setup/submit-password", token, new[]
            {
                new KeyValuePair<string, string>("action", "set"),
                new KeyValuePair<string, string>("Password", "Password1"),
                new KeyValuePair<string, string>("ConfirmPassword", "Password1"),
            }, ct);

            Assert.True(
                resp.StatusCode == HttpStatusCode.Redirect ||
                resp.StatusCode == HttpStatusCode.Found,
                $"expected 302 redirect, got {(int)resp.StatusCode} {resp.StatusCode}.");
            Assert.NotNull(resp.Headers.Location);
            Assert.Contains("/setup/nodes", resp.Headers.Location!.ToString());
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resets app-settings state to first-run defaults (no password, wizard not dismissed).
    /// Mirrors the cleanup pattern used by <see cref="Integration.AuthGateTests"/>.
    /// </summary>
    private async Task ResetStateAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);
        await settings.SetWizardDismissedAsync(false, ct);
    }

    /// <summary>
    /// GETs /setup/password in the fresh state to harvest the antiforgery cookie + form
    /// token. The caller's HttpClient (with <c>HandleCookies=true</c>) automatically
    /// retains the .AspNetCore.Antiforgery.* cookie; we return the form token for the
    /// caller to attach to the subsequent POST as <c>__RequestVerificationToken</c>.
    /// </summary>
    private static async Task<string> AcquireAntiforgeryTokenFromSetupPasswordAsync(
        HttpClient client, CancellationToken ct)
    {
        var resp = await client.GetAsync("/setup/password", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync(ct);

        // The page renders <AntiforgeryToken /> which emits a hidden input with name
        // __RequestVerificationToken inside the form. Find it and extract the value.
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"");
        if (!match.Success)
        {
            // Fall back to the alternate ordering (value before name).
            match = Regex.Match(
                html,
                "value=\"(?<value>[^\"]+)\"[^>]*name=\"__RequestVerificationToken\"");
        }
        Assert.True(match.Success,
            "expected a __RequestVerificationToken hidden input in /setup/password HTML");
        return match.Groups["value"].Value;
    }

    /// <summary>
    /// POSTs a form to <paramref name="url"/> attaching <paramref name="antiforgeryToken"/>
    /// as the <c>__RequestVerificationToken</c> field. The matching antiforgery cookie is
    /// carried automatically by the HttpClient (<c>HandleCookies=true</c>).
    /// </summary>
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
