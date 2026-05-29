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
/// <para>
/// Auth-aware /setup/skip (debug session skip-setup-404-auth-blind, sibling of
/// wizard-lockout-regression): after the operator sets a password in step 1 the
/// commitment <c>hasPassword</c> is true AND the operator is signed in, so an auth-blind
/// gate 404'd the authenticated operator's legitimate "Skip for now" on wizard steps 2/3.
/// The gate must apply only to ANONYMOUS callers once committed; the authenticated
/// operator may skip from any step. The anonymous anti-takeover 404 is preserved.
/// </para>
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

    // ── Anti-takeover lock (regression): ANONYMOUS skip on a committed system 404s ──

    [Fact]
    public async Task Skip_WithValidToken_WhenPasswordAlreadySet_Returns404()
    {
        // (b) Anti-takeover regression lock for the PRESERVED behavior. The client never
        // authenticates — the password hash is written directly to the DB to model a
        // committed system seen by an anonymous caller. This must 404 both before and
        // after the auth-aware fix (skip-setup-404-auth-blind).
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

    [Fact]
    public async Task Skip_AnonymousCaller_WhenAnyNodeExists_Returns404()
    {
        // (b) Anti-takeover regression lock — committed via a real WiFiNode rather than a
        // password. Anonymous + committed must still 404 after the auth-aware fix.
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var token = await AcquireAntiforgeryTokenFromSetupPasswordAsync(client, ct);

        int seededId;
        using (var scope = _factory.Services.CreateScope())
        {
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeRepo>();
            var node = new WiFiNode
            {
                Hostname = "ap-anon.local",
                Username = "root",
                Password = "encrypted-blob",
            };
            await nodeRepo.AddAsync(node, ct);
            seededId = node.Id;
        }

        try
        {
            var resp = await PostFormAsync(client, "/setup/skip", token,
                Array.Empty<KeyValuePair<string, string>>(), ct);

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

    // ── Auth-aware skip (skip-setup-404-auth-blind RED→GREEN regression) ─────────

    [Fact]
    public async Task Skip_AuthenticatedOperator_WhenPasswordSet_Redirects_AndDismissesWizard()
    {
        // (a) Real scenario: the operator completes step 1 (submit-password), which sets
        // UIPasswordHash AND signs them in via the auth cookie. They are now on step 2/3
        // (/setup/nodes, /setup/wlans) where SetupLayout renders the "Skip for now" POST
        // form. The authenticated operator's skip MUST 302 -> "/" and set WizardDismissed,
        // NOT 404. This FAILS RED against the auth-blind gate (returns 404 because
        // hasPassword == true) and passes GREEN after the auth-aware fix.
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Step 1: complete submit-password the way the wizard does — this is what sets
        // hasPassword AND issues the auth cookie that the client carries forward.
        var step1Token = await AcquireAntiforgeryTokenFromSetupPasswordAsync(client, ct);
        var step1 = await PostFormAsync(client, "/setup/submit-password", step1Token, new[]
        {
            new KeyValuePair<string, string>("action", "set"),
            new KeyValuePair<string, string>("Password", "Password1"),
            new KeyValuePair<string, string>("ConfirmPassword", "Password1"),
        }, ct);

        Assert.True(
            step1.StatusCode == HttpStatusCode.Redirect ||
            step1.StatusCode == HttpStatusCode.Found,
            $"step 1 submit-password should 302 to /setup/nodes, got {(int)step1.StatusCode}.");

        try
        {
            // Now AUTHENTICATED (cookie carried) and committed (hasPassword == true).
            // GET /setup/nodes (step 2) is reachable for the authenticated operator and
            // renders the SetupLayout skip form with a fresh antiforgery token.
            var skipToken = await AcquireAntiforgeryTokenFromAuthenticatedStepAsync(
                client, "/setup/nodes", ct);

            var resp = await PostFormAsync(client, "/setup/skip", skipToken,
                Array.Empty<KeyValuePair<string, string>>(), ct);

            Assert.True(
                resp.StatusCode == HttpStatusCode.Redirect ||
                resp.StatusCode == HttpStatusCode.Found,
                $"authenticated skip should 302 to /, got {(int)resp.StatusCode} {resp.StatusCode}.");
            Assert.NotNull(resp.Headers.Location);
            Assert.Equal("/", resp.Headers.Location!.ToString());

            // WizardDismissed must be flipped true by the skip handler.
            using var scope = _factory.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            Assert.True(settings.WizardDismissed,
                "authenticated skip must set WizardDismissed = true.");
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
        return ExtractAntiforgeryToken(html);
    }

    /// <summary>
    /// GETs an authenticated wizard step (e.g. <c>/setup/nodes</c>) to harvest a fresh
    /// antiforgery cookie + form token AFTER step 1 has signed the operator in. The auth
    /// cookie issued by submit-password is carried by the same <c>HandleCookies=true</c>
    /// client, so the WizardSetupGateMiddleware lets the authenticated operator through
    /// (200) and SetupLayout renders the "Skip for now" form with a token.
    /// </summary>
    private static async Task<string> AcquireAntiforgeryTokenFromAuthenticatedStepAsync(
        HttpClient client, string stepUrl, CancellationToken ct)
    {
        var resp = await client.GetAsync(stepUrl, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync(ct);
        return ExtractAntiforgeryToken(html);
    }

    /// <summary>
    /// Extracts the <c>__RequestVerificationToken</c> hidden-input value emitted by
    /// <c>&lt;AntiforgeryToken /&gt;</c> from a rendered setup page's HTML.
    /// </summary>
    private static string ExtractAntiforgeryToken(string html)
    {
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
            "expected a __RequestVerificationToken hidden input in the setup page HTML");
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
