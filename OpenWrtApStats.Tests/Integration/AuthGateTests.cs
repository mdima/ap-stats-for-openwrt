using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Services.Settings;
using Xunit;

namespace OpenWrtApStats.Tests.Integration;

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
}
