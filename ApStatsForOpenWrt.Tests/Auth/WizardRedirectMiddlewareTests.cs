using System.Net;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.Integration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Auth;

/// <summary>
/// M-SEC-02 (Plan 15-03) acceptance suite for the AUTH-AWARE
/// <see cref="Services.Onboarding.WizardSetupGateMiddleware"/> friendly-404 anti-takeover
/// gate, under the AND commitment semantics adopted in debug session
/// <c>wizard-gate-and-semantics</c>.
///
/// <para>
/// The gate fires the pinned friendly 404 for an ANONYMOUS <c>GET /setup/*</c> ONLY when
/// the system is genuinely complete — a password is set AND at least one AP (WiFiNode)
/// exists. Neither alone closes the visible gate (the earlier OR semantics fired too
/// early, the moment a password was set). In the password-only intermediate window the
/// gate is OFF, so the anonymous request falls through to the ConditionalAuth fallback
/// (302 /Login) rather than a misleading "setup complete" 404 — anonymous access stays
/// blocked, only the response shape changes.
/// </para>
///
/// <list type="number">
///   <item>Fresh state (no password, no AP): GET /setup/password returns 200 (wizard reachable).</item>
///   <item>WizardDismissed-only state: GET /setup/password STILL returns 200 (Option B resumability).</item>
///   <item>Password-only (no AP): GET /setup/* is NOT the friendly 404 — it falls through to
///   the ConditionalAuth 302 /Login (anonymous still blocked, no "complete" body).</item>
///   <item>Node-only (no password, unreachable in practice): GET /setup/* is NOT the friendly
///   404 (AND false).</item>
///   <item>Complete (password AND AP): GET /setup/* returns 404 with the pinned friendly HTML
///   body — regression lock on the preserved anti-takeover, theory across the three wizard URLs.</item>
///   <item>Regression: WizardDismissed=true still suppresses the GET / auto-redirect
///   (v0.11 behavior preserved).</item>
/// </list>
/// </summary>
[Collection("Integration")]
[Trait("Category", "SecurityFix")]
public sealed class WizardRedirectMiddlewareTests(SmokeFactoryFixture factory)
    : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    [Fact]
    public async Task GetSetupPassword_FreshState_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var resp = await client.GetAsync("/setup/password", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetSetupPassword_WhenWizardDismissedOnly_StillReturns200()
    {
        // Option B resumability: WizardDismissed=true alone, no nodes, no password,
        // must keep the wizard reachable.
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetWizardDismissedAsync(true, ct);
        }

        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            var resp = await client.GetAsync("/setup/password", ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    [Fact]
    public async Task GetSetupPassword_WhenPasswordOnly_DoesNotServeFriendly404_FallsThroughToLogin()
    {
        // AND semantics (wizard-gate-and-semantics): password set, NO AP yet -> the visible
        // anti-takeover gate is OFF (AND false). An anonymous GET /setup/* must NOT receive the
        // misleading "setup complete" 404. The load-bearing invariant is "not the friendly 404";
        // the post-gate response shape then depends on each page's own authorization:
        //   - /setup/password carries [AllowAnonymous] (it must work on first run), so it stays
        //     reachable (200) even with a password set. Anonymous access to the WIZARD is not a
        //     takeover vector; the M-SEC-01 boundary is the submit-password POST gate, which is
        //     unchanged and still refuses an anonymous re-POST once hasPassword.
        //   - a wizard page WITHOUT [AllowAnonymous] (e.g. /setup/nodes) falls through to the
        //     ConditionalAuth fallback once a password exists -> 302 /Login (asserted below).
        // Either way the misleading "complete" 404 must NOT appear in the password-only window.
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetUIPasswordHashAsync("AQAAA-fake-hash", ct);
        }

        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            // (1) AllowAnonymous wizard page: NOT the friendly 404, no "complete" body.
            var pwResp = await client.GetAsync("/setup/password", ct);
            Assert.NotEqual(HttpStatusCode.NotFound, pwResp.StatusCode);
            var pwBody = await pwResp.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain("This setup wizard has already been completed.", pwBody);

            // (2) Protected wizard page: the ConditionalAuth fallback engages once a password
            //     is set -> 302 /Login (NOT the friendly 404). This is the matrix's documented
            //     GET fall-through for a non-AllowAnonymous /setup/* URL.
            var nodesResp = await client.GetAsync("/setup/nodes", ct);
            Assert.NotEqual(HttpStatusCode.NotFound, nodesResp.StatusCode);
            Assert.True(
                nodesResp.StatusCode == HttpStatusCode.Redirect ||
                nodesResp.StatusCode == HttpStatusCode.Found,
                $"expected a 302 to /Login for the protected /setup/nodes, got {(int)nodesResp.StatusCode} {nodesResp.StatusCode}.");
            Assert.Contains("/Login", nodesResp.Headers.Location?.ToString() ?? "");
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    [Fact]
    public async Task GetSetupPassword_WhenNodeOnly_DoesNotServeFriendly404()
    {
        // AND semantics: an AP exists but NO password (unreachable in practice, since adding
        // an AP requires first setting a password + signing in) → gate OFF (AND false). The
        // anonymous GET must NOT receive the friendly anti-takeover 404. With no password the
        // ConditionalAuth fallback is not armed, so the wizard page itself is reachable (200).
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

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
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            var resp = await client.GetAsync("/setup/password", ct);

            Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain("This setup wizard has already been completed.", body);
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeRepo>();
            await nodeRepo.DeleteAsync(seededId, ct);
            await ResetStateAsync(ct);
        }
    }

    [Theory]
    [InlineData("/setup/password")]
    [InlineData("/setup/nodes")]
    [InlineData("/setup/wlans")]
    public async Task GetWizardUrl_WhenComplete_PasswordAndNode_Returns404WithPinnedBody(string url)
    {
        // Regression lock on the PRESERVED anti-takeover. Under AND the friendly 404 fires
        // only when the system is genuinely complete: a password is set AND at least one AP
        // exists. An anonymous caller in that state gets the pinned friendly 404 body.
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

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
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            var resp = await client.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync(ct);
            Assert.Contains("Setup complete", body);
            Assert.Contains("This setup wizard has already been completed.", body);
            Assert.Contains("/Login", body);
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
    public async Task GetRoot_WhenWizardDismissed_DoesNotAutoRedirectToWizard()
    {
        // Regression — preserves v0.11 behavior for GET /. WizardDismissed=true
        // suppresses the auto-redirect (operator already chose not to onboard).
        var ct = TestContext.Current.CancellationToken;
        await ResetStateAsync(ct);

        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetWizardDismissedAsync(true, ct);
        }

        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            var resp = await client.GetAsync("/", ct);

            // The dashboard might 200 directly or 302 elsewhere (e.g. /Login) depending on
            // auth state — the key invariant is: it MUST NOT redirect to /setup/password.
            if (resp.StatusCode == HttpStatusCode.Redirect ||
                resp.StatusCode == HttpStatusCode.Found)
            {
                Assert.DoesNotContain("/setup/password",
                    resp.Headers.Location?.ToString() ?? "");
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task ResetStateAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);
        await settings.SetWizardDismissedAsync(false, ct);
    }
}
