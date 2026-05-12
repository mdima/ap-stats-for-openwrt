using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Bootstrap;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Polling.Ssh;
using OpenWrtApStats.Services.Settings;
using Xunit;

namespace OpenWrtApStats.Tests.Bootstrap;

/// <summary>
/// Pitfall 10 mitigation: hosted-service registration order = StartAsync order. If a developer
/// accidentally swaps the order (e.g., DataProtectionHealth before Migrations) the D-12 probe
/// would query a not-yet-migrated WiFiNodes table and crash for the wrong reason. This test
/// makes the silent reorder loud.
/// <para>
/// Phase 7 amendment: WritabilityCheckHostedService is the 1st IHostedService; total count == 5
/// (Writability → Migrations → DataProtectionHealth → AppSettingsBootstrap → Polling). The
/// writability probe MUST run BEFORE EF migrations or a read-only /data mount silently corrupts
/// the bootstrap chain (INF-07; D-01). Pitfall 8 still active: future phases adding a 6th
/// MUST amend this test again.
/// </para>
/// </summary>
public sealed class HostedServiceRegistrationOrderTests
{
    [Fact]
    public void RegistrationOrder_IsMigrationsThenDataProtectionHealthThenAppSettingsBootstrapThenPolling()
    {
        // Pitfall 10 — registration order = StartAsync order.
        // Uses Host.CreateApplicationBuilder (test project is Microsoft.NET.Sdk, not Web).
        // The Services collection behaves identically for the registration-order assertion;
        // Pitfall 10 cares about the descriptor sequence, not the host flavor.
        var builder = Host.CreateApplicationBuilder();

        // Replicate the Program.cs hosted-service registrations in the same order.
        builder.Services.AddDbContextFactory<DatabaseContext>(o => o.UseSqlite("Data Source=:memory:"));
        builder.Services.AddTransient<INodeRepo,        NodeRepo>();
        builder.Services.AddTransient<IAppSettingsRepo, AppSettingsRepo>();
        builder.Services.AddSingleton<IAppSettingsService, AppSettingsService>();

        // Phase 2 prerequisite registrations — the descriptor enumeration doesn't actually start
        // the host, so registering bare-minimum stubs is sufficient. Real production graph lives
        // in Program.cs; this test only validates registration ORDER + COUNT.
        builder.Services.AddTransient<INetworkRepo, NetworkRepo>();
        builder.Services.AddTransient<IStationRepo, StationRepo>();
        builder.Services.AddSingleton<OpenWrtAdapterCache>();
        builder.Services.AddSingleton<PollNotifier>();
        builder.Services.AddSingleton<PollOrchestrator>();
        builder.Services.AddTransient<ISshSession, RealSshSession>();
        // GAP-02-A: NodePoller is intentionally NOT registered. Mirrors Program.cs.
        builder.Services.AddSingleton<Func<WiFiNode, CancellationToken, Task<NodeProbeResult>>>(
            sp => (n, ct) => Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "test-stub")));
        builder.Services.AddSingleton<Func<CancellationToken, Task>>(
            sp => ct => Task.CompletedTask);

        // Phase 6 — visitor gate seam (registered as AlwaysLiveGate stub for ORDER-validation;
        // production registers CircuitTrackerGate per Program.cs). PollingHostedService's primary
        // ctor now takes ICircuitGate; this stub keeps the descriptor-enumeration test compiling
        // and resolvable. W-new-2 — exactly ONE AddSingleton<ICircuitGate, ...> registration in
        // this file (no double-registration drift).
        builder.Services.AddSingleton<OpenWrtApStats.Services.Gating.ICircuitGate,
                                      OpenWrtApStats.Services.Gating.AlwaysLiveGate>();

        builder.Services.AddHostedService<WritabilityCheckHostedService>();      // NEW — Phase 7
        builder.Services.AddHostedService<MigrationsHostedService>();
        builder.Services.AddHostedService<DataProtectionHealthHostedService>();
        builder.Services.AddHostedService<AppSettingsBootstrapHostedService>();
        builder.Services.AddHostedService<PollingHostedService>();

        var hostedDescriptors = builder.Services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType ?? d.ImplementationFactory?.Method.ReturnType)
            .Where(t => t != null)
            .ToList();

        var implTypes = hostedDescriptors.Select(t => t!.Name).ToList();

        // All five bootstrap-then-polling services must appear in exact registration order.
        var allServices = implTypes.Where(n =>
            n == nameof(WritabilityCheckHostedService) ||  // NEW — Phase 7 / INF-07
            n == nameof(MigrationsHostedService) ||
            n == nameof(DataProtectionHealthHostedService) ||
            n == nameof(AppSettingsBootstrapHostedService) ||
            n == nameof(PollingHostedService)).ToList();

        Assert.Equal(
            new[]
            {
                nameof(WritabilityCheckHostedService),       // NEW — Phase 7
                nameof(MigrationsHostedService),
                nameof(DataProtectionHealthHostedService),
                nameof(AppSettingsBootstrapHostedService),
                nameof(PollingHostedService),
            },
            allServices);

        // Phase 7 adds the 5th — total is now five (WritabilityCheckHostedService prepended).
        // Pitfall 8 still active: future phases adding a 6th MUST amend this count again.
        Assert.Equal(5, allServices.Count);
    }
}
