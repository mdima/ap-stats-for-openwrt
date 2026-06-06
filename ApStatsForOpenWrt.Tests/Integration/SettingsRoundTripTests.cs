using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// SET-02 enable/disable + SET-03 bulk + SET-04 propagation. End-to-end through the real
/// IAppSettingsService snapshot + ConditionalAuthHandler chain — no stubs at the
/// service layer. Verifies Pitfall 1 propagation regression: setter → service snapshot →
/// next request observes the change without a host restart.
/// </summary>
[Collection("Integration")]
public sealed class SettingsRoundTripTests(SmokeFactoryFixture factory) : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    [Fact]
    public async Task SetPassword_FromNull_NextRequest_RedirectsToLogin()
    {
        // Pitfall 1 — set the hash, then immediately verify a fresh request observes the
        // gate change. ConditionalAuthHandler reads IAppSettingsService.UIPasswordHash per
        // request via Volatile.Read over the Interlocked.Exchange'd snapshot, so the new
        // value must be visible without restart.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync(null, ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Pre-condition: anonymous → not auth-blocked (200 or 302 to /setup/password, NOT /Login).
        // Phase 4: WizardRedirectMiddleware may redirect GET / to /setup/password in first-run state.
        var pre = await client.GetAsync("/", ct);
        if (pre.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.Found)
            Assert.DoesNotContain("/Login", pre.Headers.Location?.ToString() ?? "");
        else
            Assert.Equal(System.Net.HttpStatusCode.OK, pre.StatusCode);

        try
        {
            // Action: set the password directly via the service (the same path
            // /Settings Card 2's HandleSetPasswordAsync invokes).
            await settings.SetUIPasswordHashAsync("AQAAA-set-from-null", ct);

            // Post-condition: anonymous → 302 to /Login.
            var post = await client.GetAsync("/", ct);
            Assert.True(post.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.Found,
                $"Expected redirect to /Login but got {post.StatusCode}");
            Assert.NotNull(post.Headers.Location);
            Assert.Contains("/Login", post.Headers.Location!.ToString());
        }
        finally
        {
            await settings.SetUIPasswordHashAsync(null, ct);   // cleanup for sibling tests
        }
    }

    [Fact]
    public async Task ClearPassword_FromNonNull_NextRequest_Returns200Anonymous()
    {
        // Mirror of the previous test: the operator can disable the gate at runtime by
        // clearing the hash. This is the SET-02 "Clear password" → ConfirmationModal →
        // SetUIPasswordHashAsync(null) path.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync("AQAAA-temp", ct);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Pre-condition: anonymous → 302 to /Login.
        var pre = await client.GetAsync("/", ct);
        Assert.True(pre.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.Found,
            $"Expected redirect with hash set but got {pre.StatusCode}");

        // Action: clear the password.
        await settings.SetUIPasswordHashAsync(null, ct);

        // Post-condition: anonymous → not auth-blocked (200 or 302 to /setup/password, NOT /Login).
        // Phase 4: WizardRedirectMiddleware may redirect GET / to /setup/password in first-run state.
        var post = await client.GetAsync("/", ct);
        if (post.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.Found)
            Assert.DoesNotContain("/Login", post.Headers.Location?.ToString() ?? "");
        else
            Assert.Equal(System.Net.HttpStatusCode.OK, post.StatusCode);
    }

    [Fact]
    public async Task ClearHostKeyFingerprintAsync_NullsTargetFingerprint_PreservesPassword()
    {
        // CR-02-safe regression — NodeRepoClearFingerprintsTests covers the SQLite
        // ExecuteUpdateAsync semantics; this test verifies the wiring through the real INodeRepo
        // singleton resolved from the WebApplicationFactory composition root. It exercises exactly
        // what the per-node Re-trust button on /Nodes/{id} invokes end-to-end.
        // M-HRD-03 / v1.0 (plan 21-02): the obsolete all-rows bulk-clear path was removed; only the
        // per-node ClearHostKeyFingerprintAsync re-trust surface remains.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var nodes = scope.ServiceProvider.GetRequiredService<INodeRepo>();

        var n1 = new WiFiNode { Hostname = $"clr-{Guid.NewGuid():N}", Username = "root", Password = "P1", HostKeyFingerprint = "FP1" };
        var n2 = new WiFiNode { Hostname = $"clr-{Guid.NewGuid():N}", Username = "root", Password = "P2", HostKeyFingerprint = "FP2" };
        await nodes.AddAsync(n1, ct);
        await nodes.AddAsync(n2, ct);

        await nodes.ClearHostKeyFingerprintAsync(n1.Id, ct);

        var f1 = await nodes.GetByIdAsync(n1.Id, ct);
        var f2 = await nodes.GetByIdAsync(n2.Id, ct);
        Assert.NotNull(f1);
        Assert.NotNull(f2);
        Assert.Null(f1!.HostKeyFingerprint);
        // Only the targeted row is cleared; the other node's fingerprint is untouched.
        Assert.Equal("FP2", f2!.HostKeyFingerprint);
        // CR-02-safe: Password column preserved across the update.
        Assert.Equal("P1", f1.Password);
        Assert.Equal("P2", f2.Password);
    }

    [Fact]
    public async Task SetPollingInterval_PropagatesToService_WithoutRestart()
    {
        // SET-04 contract: changes take effect without container restart. The test verifies
        // that immediately after the setter returns, the service's snapshot exposes the new value.
        // Phase 2's PollingHostedService observes the same snapshot at the top of its next tick
        // (proven by PollingHostedServiceTests in Phase 2 baseline).
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

        await settings.SetPollingIntervalSecondsAsync(5, ct);
        Assert.Equal(5, settings.PollingIntervalSeconds);

        await settings.SetPollingIntervalSecondsAsync(10, ct);
        Assert.Equal(10, settings.PollingIntervalSeconds);

        await settings.SetPollingIntervalSecondsAsync(30, ct);
        Assert.Equal(30, settings.PollingIntervalSeconds);
    }
}
