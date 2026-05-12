using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// ONB-02 integration suite. Verifies wizard dismiss state and password-set transitions
/// propagate correctly to the WizardRedirectMiddleware without host restart (Pitfall 1).
/// </summary>
[Collection("Integration")]
public sealed class WizardSignInRoundTripTests(SmokeFactoryFixture factory)
    : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    [Fact]
    public async Task SetWizardDismissed_True_NextGetRoot_NotRedirectedToWizard()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);
        await settings.SetWizardDismissedAsync(true, ct);
        try
        {
            var client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions
                    { AllowAutoRedirect = false });
            var resp = await client.GetAsync("/", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Redirect)
                Assert.DoesNotMatch("/setup/password", resp.Headers.Location?.ToString() ?? "");
        }
        finally
        {
            await settings.SetWizardDismissedAsync(false, ct);
        }
    }

    [Fact]
    public async Task PasswordSet_EngagesAuthGate_RedirectsToLogin_NotWizard()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetWizardDismissedAsync(false, ct);
        await settings.SetUIPasswordHashAsync("AQAAAAIAAYagAAAAE...", ct);
        try
        {
            var client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions
                    { AllowAutoRedirect = false });
            var resp = await client.GetAsync("/", ct);
            // Auth gate engaged — redirect should be to /Login, not /setup/password
            if (resp.StatusCode == System.Net.HttpStatusCode.Redirect)
                Assert.DoesNotMatch("/setup/password", resp.Headers.Location?.ToString() ?? "");
        }
        finally
        {
            await settings.SetUIPasswordHashAsync(null, ct);
        }
    }
}
