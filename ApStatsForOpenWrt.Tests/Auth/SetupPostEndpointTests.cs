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
///   <item>/setup/submit-password commitment 404: refuses an anonymous POST once a real
///   commitment exists (UIPasswordHash set OR any WiFiNode row) - the M-SEC-01 takeover
///   boundary, deliberately kept on the hasPassword OR semantics.</item>
///   <item>/setup/skip anti-takeover 404 (AND semantics, wizard-gate-and-semantics): an
///   anonymous skip is refused ONLY once the system is genuinely complete (password set AND
///   any WiFiNode row). In the password-only or node-only window the anonymous skip succeeds
///   (302 /, WizardDismissed set).</item>
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
/// <para>
/// Action-aware /setup/submit-password ADVANCE (debug session wizard-resume-404-after-skip):
/// the "Skip password" button on step 1 POSTs <c>action=skip</c> — a no-op ADVANCE to
/// step 2 that writes no password. The commitment-404 gate must apply ONLY to a password
/// WRITE (<c>action=set</c>); the <c>action=skip</c> advance must never 404 once a password
/// exists, so a returning operator can complete the wizard. The M-SEC-01 boundary is
/// preserved: an ANONYMOUS <c>action=set</c> with a password already set still 404s (no
/// overwrite/takeover).
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

    // ── /setup/skip AND semantics (wizard-gate-and-semantics) ───────────────────
    // Anonymous skip is refused ONLY when the system is genuinely complete (password AND
    // AP). In the single-condition windows the anonymous skip succeeds; the complete-state
    // 404 is the preserved anti-takeover regression lock.

    [Fact]
    public async Task Skip_AnonymousCaller_WhenPasswordOnly_Succeeds_AndDismissesWizard()
    {
        // AND semantics: password set, NO AP yet -> skip gate OFF. The client never
        // authenticates; the password hash is written directly to the DB to model the
        // password-only intermediate window seen by an anonymous caller. Under the new AND
        // contract this must SUCCEED (302 /, WizardDismissed set), NOT 404. (Under the old
        // OR contract this returned 404.)
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Token must be harvested BEFORE the password is set (afterwards the wizard page
        // GET no longer serves a token form in the password-only window - it 302s /Login).
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

            Assert.True(
                resp.StatusCode == HttpStatusCode.Redirect ||
                resp.StatusCode == HttpStatusCode.Found,
                $"anonymous password-only skip should 302 to /, got {(int)resp.StatusCode} {resp.StatusCode}.");
            Assert.NotNull(resp.Headers.Location);
            Assert.Equal("/", resp.Headers.Location!.ToString());

            using var scope = _factory.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            Assert.True(settings.WizardDismissed,
                "anonymous password-only skip must set WizardDismissed = true.");
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    [Fact]
    public async Task Skip_AnonymousCaller_WhenNodeOnly_Succeeds_AndDismissesWizard()
    {
        // AND semantics: an AP exists but NO password (unreachable in practice) -> skip gate
        // OFF (AND false). Anonymous skip must SUCCEED, NOT 404. (Under the old OR contract
        // this returned 404.) With no password the wizard page is reachable to harvest a token.
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

            Assert.True(
                resp.StatusCode == HttpStatusCode.Redirect ||
                resp.StatusCode == HttpStatusCode.Found,
                $"anonymous node-only skip should 302 to /, got {(int)resp.StatusCode} {resp.StatusCode}.");
            Assert.NotNull(resp.Headers.Location);
            Assert.Equal("/", resp.Headers.Location!.ToString());
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
    public async Task Skip_AnonymousCaller_WhenComplete_PasswordAndNode_Returns404()
    {
        // (b) Anti-takeover regression lock for the PRESERVED behavior under AND. The client
        // never authenticates; a password hash AND a WiFiNode are written directly to the DB
        // to model a genuinely COMPLETE system seen by an anonymous caller. This MUST 404.
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Harvest a token from the fresh state before committing (post-commit the GET 404s).
        var token = await AcquireAntiforgeryTokenFromSetupPasswordAsync(client, ct);

        int seededId;
        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetUIPasswordHashAsync("AQAAA-fake-hash", ct);

            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeRepo>();
            var node = new WiFiNode
            {
                Hostname = "ap-complete.local",
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

    // ── Auth-aware submit-password ADVANCE (wizard-resume-404-after-skip RED→GREEN) ──
    // The "Skip password" button on wizard step 1 (Password.razor: <button name="action"
    // value="skip">) POSTs /setup/submit-password with action=skip — a no-op ADVANCE to
    // step 2 (/setup/nodes) that writes NO password. A returning operator who set a
    // password in a prior step 1, then skipped the wizard, lands back on step 1 and clicks
    // "Skip password" to advance. The commitment-404 gate (anyNodes || hasPassword) ran
    // BEFORE the action branch, so it 404'd the harmless action=skip advance the moment a
    // password existed — blocking wizard completion. The gate must apply ONLY to a password
    // WRITE (action=set), never to the action=skip advance, while keeping the M-SEC-01
    // anonymous-takeover boundary intact (anonymous action=set with a password already set
    // must STILL 404 — no overwrite). See the M-SEC-01 lock test below.

    [Fact]
    public async Task SubmitPassword_AuthenticatedOperator_ActionSkip_WhenPasswordSet_Advances_NotFound404()
    {
        // RED against the pre-fix gate: the operator completes step 1 (submit-password
        // action=set), which sets hasPassword AND signs them in. On a resumed step 1 they
        // click "Skip password" → POST /setup/submit-password with action=skip. The pre-fix
        // gate (anyNodes || hasPassword) runs before the action branch and 404s the advance.
        // After the fix the action=skip advance must 302 → /setup/nodes (NOT 404), letting
        // the returning operator complete the wizard.
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Step 1: set the password the way the wizard does — sets hasPassword AND issues
        // the auth cookie the client carries forward.
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
            // Resume: GET /setup/password as the authenticated operator (password-only
            // window → AND gate OFF → [AllowAnonymous] page renders 200 with a fresh token).
            var resumeToken = await AcquireAntiforgeryTokenFromAuthenticatedStepAsync(
                client, "/setup/password", ct);

            // Click "Skip password" — action=skip is a pure ADVANCE, writes no password.
            var resp = await PostFormAsync(client, "/setup/submit-password", resumeToken, new[]
            {
                new KeyValuePair<string, string>("action", "skip"),
            }, ct);

            Assert.True(
                resp.StatusCode == HttpStatusCode.Redirect ||
                resp.StatusCode == HttpStatusCode.Found,
                $"authenticated action=skip advance should 302 to /setup/nodes, got "
                + $"{(int)resp.StatusCode} {resp.StatusCode}.");
            Assert.NotNull(resp.Headers.Location);
            Assert.Equal("/setup/nodes", resp.Headers.Location!.ToString());

            // The advance must NOT have written/altered the password (still set, unchanged).
            using var scope = _factory.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            Assert.False(string.IsNullOrEmpty(settings.UIPasswordHash),
                "action=skip advance must not clear the existing password.");
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    [Fact]
    public async Task SubmitPassword_Anonymous_ActionSet_WhenPasswordAlreadySet_Returns404_MSec01Preserved()
    {
        // M-SEC-01 anti-takeover lock (must HOLD before and after the wizard-resume fix):
        // an ANONYMOUS caller attempting to WRITE a new password (action=set) once one
        // already exists must STILL receive 404 — no overwrite/takeover. The wizard-resume
        // fix only unblocks the action=skip ADVANCE; the action=set WRITE stays on the
        // hasPassword || anyNodes M-SEC-01 boundary.
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Harvest a token from the fresh state BEFORE committing (post-commit the GET 404s
        // for the anonymous caller). The client never authenticates.
        var token = await AcquireAntiforgeryTokenFromSetupPasswordAsync(client, ct);

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
                new KeyValuePair<string, string>("Password", "Different1"),
                new KeyValuePair<string, string>("ConfirmPassword", "Different1"),
            }, ct);

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

            // The original password must be untouched (no overwrite).
            using var scope = _factory.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            Assert.Equal("AQAAA-fake-hash", settings.UIPasswordHash);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Auth-aware submit-password WRITE (wizard-resume-pw-reset-404 RED→GREEN) ────
    // The returning operator who set a password in a prior step 1, then skipped the
    // wizard, may resume and RE-SET the password on step 1 (action=set). The pre-fix
    // gate (anyNodes || hasPassword) was NOT auth-aware, so it 404'd the AUTHENTICATED
    // operator's legitimate re-set the moment a password existed — dead-ending the
    // wizard at step 1. The gate must apply ONLY to ANONYMOUS callers once a commitment
    // exists (M-SEC-01 anti-takeover, covered by the lock test above). The authenticated
    // operator may set/re-set the password and advance to /setup/nodes. The handler
    // re-issues the auth cookie with the bumped SessionVersion (M-SEC-04), so the
    // re-set does NOT self-logout.

    [Fact]
    public async Task SubmitPassword_AuthenticatedOperator_ActionSet_WhenPasswordAlreadySet_Advances_NotFound404()
    {
        // RED against the pre-fix auth-blind gate: the operator completes step 1
        // (action=set), which sets hasPassword AND signs them in. On a resumed step 1
        // they RE-SET the password (action=set, a NEW valid password). The pre-fix gate
        // (anyNodes || hasPassword) runs with no auth check and 404s the re-set. After
        // the fix the authenticated re-set must 302 → /setup/nodes, write the new hash,
        // and re-issue the cookie (no self-logout).
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // Step 1: set the password the way the wizard does — sets hasPassword AND issues
        // the auth cookie the client carries forward.
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

        // Capture the hash set by step 1 so we can prove the re-set actually overwrote it.
        string? hashAfterStep1;
        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            hashAfterStep1 = settings.UIPasswordHash;
        }
        Assert.False(string.IsNullOrEmpty(hashAfterStep1),
            "step 1 must have written a password hash.");

        try
        {
            // Resume: GET /setup/password as the authenticated operator (password-only
            // window → AND gate OFF → [AllowAnonymous] page renders 200 with a fresh token).
            var resumeToken = await AcquireAntiforgeryTokenFromAuthenticatedStepAsync(
                client, "/setup/password", ct);

            // RE-SET the password (action=set) with a DIFFERENT valid value.
            var resp = await PostFormAsync(client, "/setup/submit-password", resumeToken, new[]
            {
                new KeyValuePair<string, string>("action", "set"),
                new KeyValuePair<string, string>("Password", "Different1"),
                new KeyValuePair<string, string>("ConfirmPassword", "Different1"),
            }, ct);

            Assert.True(
                resp.StatusCode == HttpStatusCode.Redirect ||
                resp.StatusCode == HttpStatusCode.Found,
                $"authenticated action=set re-set should 302 to /setup/nodes, got "
                + $"{(int)resp.StatusCode} {resp.StatusCode}.");
            Assert.NotNull(resp.Headers.Location);
            Assert.Equal("/setup/nodes", resp.Headers.Location!.ToString());

            // The handler must re-issue the auth cookie (M-SEC-04 — no self-logout). A
            // Set-Cookie carrying the app auth cookie (Program.cs: o.Cookie.Name =
            // "ApStatsForOpenWrt.Auth") must be present on the re-set response so the
            // re-issued SessionVersion claim validates on the next request.
            Assert.Contains(
                resp.Headers.TryGetValues("Set-Cookie", out var setCookies) ? setCookies : Array.Empty<string>(),
                c => c.Contains("ApStatsForOpenWrt.Auth", StringComparison.OrdinalIgnoreCase));

            // The new hash must have replaced the step-1 hash (real overwrite by the operator).
            using var scope = _factory.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            Assert.False(string.IsNullOrEmpty(settings.UIPasswordHash),
                "the re-set must leave a password hash.");
            Assert.NotEqual(hashAfterStep1, settings.UIPasswordHash);
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
