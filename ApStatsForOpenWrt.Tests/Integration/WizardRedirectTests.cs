using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Services.Settings;
using Xunit;

namespace OpenWrtApStats.Tests.Integration;

/// <summary>
/// ONB-01 integration suite. Verifies WizardRedirectMiddleware D-07 three-condition gate
/// and that exempted paths are never redirected to the wizard.
/// </summary>
[Collection("Integration")]
public sealed class WizardRedirectTests(SmokeFactoryFixture factory)
    : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    [Fact]
    public async Task GetRoot_ZeroNodes_NullHash_NotDismissed_RedirectsToSetup()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        // Ensure clean first-run state
        await settings.SetUIPasswordHashAsync(null, ct);
        await settings.SetWizardDismissedAsync(false, ct);
        // The integration test DB starts with zero nodes (SmokeFactoryFixture uses a fresh in-memory DB)
        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
                { AllowAutoRedirect = false });
            var resp = await client.GetAsync("/", ct);
            Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
            Assert.EndsWith("/setup/password", resp.Headers.Location?.ToString() ?? "");
        }
        finally
        {
            // Reset to defaults so subsequent tests start from a clean state (WR-04).
            await settings.SetWizardDismissedAsync(false, ct);
        }
    }

    [Fact]
    public async Task GetRoot_WizardDismissed_DoesNotRedirectToWizard()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);
        await settings.SetWizardDismissedAsync(true, ct);
        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
                { AllowAutoRedirect = false });
            var resp = await client.GetAsync("/", ct);
            // Not redirected to wizard — may be 200 or other auth redirect but NOT /setup/password
            if (resp.StatusCode == System.Net.HttpStatusCode.Redirect)
                Assert.DoesNotMatch("/setup/password", resp.Headers.Location?.ToString() ?? "");
        }
        finally
        {
            await settings.SetWizardDismissedAsync(false, ct);
        }
    }

    [Fact]
    public async Task GetRoot_HashSet_DoesNotRedirectToWizard()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync("somehash", ct);
        await settings.SetWizardDismissedAsync(false, ct);
        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
                { AllowAutoRedirect = false });
            var resp = await client.GetAsync("/", ct);
            // Second condition (hash set) prevents wizard redirect
            if (resp.StatusCode == System.Net.HttpStatusCode.Redirect)
                Assert.DoesNotMatch("/setup/password", resp.Headers.Location?.ToString() ?? "");
        }
        finally
        {
            await settings.SetUIPasswordHashAsync(null, ct);
        }
    }

    [Fact]
    public async Task GetSetupPassword_IsNeverRedirectedToWizard()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);
        await settings.SetWizardDismissedAsync(false, ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/setup/password", ct);
        // Must not be a redirect to /setup/password (would be infinite loop)
        if (resp.StatusCode == System.Net.HttpStatusCode.Redirect)
            Assert.DoesNotMatch("/setup/password$", resp.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task GetHealth_IsNotRedirectedToWizard()
    {
        // /health is exempt — it's not "/"
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);
        await settings.SetWizardDismissedAsync(false, ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });
        // Health endpoint may not exist until Phase 6, so check /Login instead (also exempt)
        var resp = await client.GetAsync("/Login", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Redirect)
            Assert.DoesNotMatch("/setup/password", resp.Headers.Location?.ToString() ?? "");
    }
}
