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
/// M-SEC-02 (Plan 15-03) acceptance suite for the extended
/// <see cref="Services.Onboarding.WizardRedirectMiddleware"/>:
///
/// <list type="number">
///   <item>Fresh state: GET /setup/password returns 200 (wizard reachable).</item>
///   <item>WizardDismissed-only state: GET /setup/password STILL returns 200
///   (Option B resumability).</item>
///   <item>Password committed: GET /setup/* returns 404 with the pinned friendly HTML body.</item>
///   <item>Any node exists: GET /setup/* returns 404 with the pinned body.</item>
///   <item>Theory across the three wizard URLs (/setup/password, /setup/nodes, /setup/wlans).</item>
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
    public async Task GetSetupPassword_WhenPasswordSet_Returns404WithPinnedBody()
    {
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

            var resp = await client.GetAsync("/setup/password", ct);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync(ct);
            Assert.Contains("Setup complete", body);
            Assert.Contains("This setup wizard has already been completed.", body);
            Assert.Contains("/Login", body);
        }
        finally
        {
            await ResetStateAsync(ct);
        }
    }

    [Fact]
    public async Task GetSetupPassword_WhenAnyNodeExists_Returns404WithPinnedBody()
    {
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
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync(ct);
            Assert.Contains("Setup complete", body);
            Assert.Contains("This setup wizard has already been completed.", body);
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
    public async Task GetWizardUrl_WhenWizardClosed_Returns404WithPinnedBody(string url)
    {
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

            var resp = await client.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync(ct);
            Assert.Contains("Setup complete", body);
        }
        finally
        {
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
