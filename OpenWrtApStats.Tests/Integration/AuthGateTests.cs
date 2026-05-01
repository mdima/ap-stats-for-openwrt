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
    public async Task Anonymous_WhenHashNull_RootIs200()
    {
        // Fresh in-memory DB has UIPasswordHash null. /  must be reachable anonymously.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);   // ensure null

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/", ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
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
    public async Task AnonymousAfterClear_RootIs200_NoStaleCacheing()
    {
        // Pitfall 1 — set, then clear, then anonymous request must succeed.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync("AQAAA-temp", ct);
        await settings.SetUIPasswordHashAsync(null, ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/", ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
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
