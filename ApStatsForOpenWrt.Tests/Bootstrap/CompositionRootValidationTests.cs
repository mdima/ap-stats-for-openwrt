using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Bootstrap;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Bootstrap;

/// <summary>
/// GAP-02-A regression: the production composition root MUST pass eager service-provider
/// validation (<see cref="ServiceProviderOptions.ValidateOnBuild"/> +
/// <see cref="ServiceProviderOptions.ValidateScopes"/>). <c>WebApplicationBuilder</c> enables
/// both options in Development, so a single bad registration aborts <c>app.Build()</c> at startup.
/// <para>
/// Plan 02-03 originally added <c>AddTransient&lt;NodePoller&gt;()</c> to the polling subsystem
/// registrations. <see cref="NodePoller"/>'s primary-ctor takes a runtime <see cref="WiFiNode"/>
/// positional arg that is not (and must not be) a DI service. The dead registration triggered
/// validation failure: "Unable to resolve service for type 'WiFiNode' while attempting to
/// activate 'NodePoller'." <see cref="NodePoller"/> is constructed per-cycle via
/// <c>ActivatorUtilities.CreateInstance</c> in the probe factory, which does NOT require the
/// type to be registered.
/// </para>
/// <para>
/// This test mirrors Program.cs's polling-subsystem registrations and asserts that
/// <c>BuildServiceProvider</c> with <c>ValidateOnBuild=true</c> succeeds. If a future change
/// re-introduces <c>AddTransient&lt;NodePoller&gt;()</c> (or any equivalent registration whose
/// constructor parameters cannot be resolved from the container), this test fails before the
/// dashboard ever boots — closing the test-coverage gap that let GAP-02-A slip past Plan 02-03's
/// 95-test suite.
/// </para>
/// <para>
/// Drift discipline: when Program.cs adds new Polling-subsystem registrations, mirror them here.
/// Phase 1's bootstrap services (<see cref="MigrationsHostedService"/>,
/// <see cref="DataProtectionHealthHostedService"/>, <see cref="AppSettingsBootstrapHostedService"/>)
/// are intentionally OUT of scope here — their DI shape is exercised by their own tests and by
/// <see cref="HostedServiceRegistrationOrderTests"/>. This test is focused: it covers exactly
/// the Polling subsystem block that GAP-02-A landed in.
/// </para>
/// </summary>
public sealed class CompositionRootValidationTests
{
    [Fact]
    public void PollingSubsystem_ServiceProvider_BuildsCleanly_WithValidateOnBuild()
    {
        // Mirror Program.cs registrations needed to construct the Polling subsystem.
        // Use SQLite in-memory to avoid touching /data — registration-shape only.
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContextFactory<DatabaseContext>(o => o.UseSqlite("Data Source=:memory:"));
        services.AddDataProtection();   // RealSshSession's IDataProtectionProvider dep is the only DP consumer in the polling graph

        services.AddTransient<INodeRepo, NodeRepo>();
        services.AddTransient<INetworkRepo, NetworkRepo>();
        services.AddTransient<IStationRepo, StationRepo>();
        // Phase 18 M-HIST-01 — history write seam; PollOrchestrator's primary ctor now takes
        // IHistoryRepo, so its DI graph requires this registration to pass ValidateOnBuild=true
        // (mirrors Program.cs:160).
        services.AddTransient<IHistoryRepo, HistoryRepo>();
        services.AddTransient<IAppSettingsRepo, AppSettingsRepo>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();

        // 5b — the exact registrations from Program.cs (sans dead AddTransient<NodePoller>).
        services.AddSingleton<OpenWrtAdapterCache>();
        services.AddSingleton<PollNotifier>();
        // v0.11 ENRICH-01 / CONTEXT 14 §D-05 — force-enrichment singleton (Plan 14-02). PollOrchestrator
        // primary-ctor takes IForceEnrichmentService, so its DI graph requires this registration to
        // pass ValidateOnBuild=true.
        services.AddSingleton<ApStatsForOpenWrt.Services.Enrichment.IForceEnrichmentService,
                              ApStatsForOpenWrt.Services.Enrichment.ForceEnrichmentService>();
        // Phase 10 ORCH-07 — TimeProvider + Phase 9 probes (additive registrations from Program.cs).
        services.AddSingleton(TimeProvider.System);
        services.AddTransient<ApStatsForOpenWrt.Services.Polling.SubnetSweepProbe>();
        services.AddTransient<ApStatsForOpenWrt.Services.Polling.ReverseDnsProbe>();
        services.AddSingleton<PollOrchestrator>();
        services.AddTransient<ISshSession, RealSshSession>();
        // NOTE: NodePoller is intentionally NOT registered (GAP-02-A). The probe factory below
        // constructs it per-cycle via ActivatorUtilities — NodePoller does not need to be a DI
        // service. Re-introducing AddTransient<NodePoller>() here MUST fail this test.
        services.AddSingleton<Func<WiFiNode, CancellationToken, Task<NodeProbeResult>>>(sp => (node, ct) =>
        {
            var poller = ActivatorUtilities.CreateInstance<NodePoller>(sp, node);
            return poller.PollAsync(ct);
        });
        // Phase 10 — enrichmentFactory delegate (validation-only stub returning null; the production
        // factory body lives in Program.cs and is exercised by integration tests).
        services.AddSingleton<Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<ApStatsForOpenWrt.Services.Polling.EnrichmentDelta?>>>(sp =>
            (n, ips, peerIp, ct) => Task.FromResult<ApStatsForOpenWrt.Services.Polling.EnrichmentDelta?>(null));
        services.AddSingleton<Func<CancellationToken, Task>>(sp => ct =>
            sp.GetRequiredService<PollOrchestrator>().RunCycleAsync(ct));

        // Phase 6 D-01 — PollingHostedService now takes ICircuitGate for the cold-start kick.
        // Use AlwaysLiveGate as the validation-only stub (this test asserts registration shape,
        // not gating semantics). Production registers CircuitTrackerGate (see Program.cs §6).
        services.AddSingleton<ApStatsForOpenWrt.Services.Gating.ICircuitGate,
                              ApStatsForOpenWrt.Services.Gating.AlwaysLiveGate>();

        services.AddHostedService<PollingHostedService>();

        // The eager-validation pass: this is what WebApplicationBuilder runs in Development.
        // If any registered service has constructor parameters the container can't resolve,
        // BuildServiceProvider throws an AggregateException listing every offender.
        var options = new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        };

        // No exception expected. If GAP-02-A regresses (or any future polling registration adds
        // a service whose constructor parameters aren't resolvable), this throws AggregateException
        // with one InvalidOperationException per bad descriptor.
        using var provider = services.BuildServiceProvider(options);

        // Sanity-check the probe factory: it must produce a working NodePoller via ActivatorUtilities
        // even though NodePoller is not registered. This guards against accidentally "fixing"
        // GAP-02-A by registering NodePoller (which would re-trigger validation failure).
        var probeFactory = provider.GetRequiredService<Func<WiFiNode, CancellationToken, Task<NodeProbeResult>>>();
        Assert.NotNull(probeFactory);

        // And confirm NodePoller is genuinely absent from the descriptor list — drift guard.
        var nodePollerDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(NodePoller));
        Assert.Null(nodePollerDescriptor);
    }

    [Fact]
    public void NodePoller_DirectlyRegisteredAsTransient_FailsValidation_AsBaseline()
    {
        // Baseline test: prove that the failure mode GAP-02-A originally triggered is real.
        // If this assertion ever stops throwing (e.g., NodePoller's ctor changes shape so all
        // params become DI-resolvable), the regression guard above can be relaxed — but until
        // then, this is the documented anti-pattern that must NOT appear in Program.cs.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<DatabaseContext>(o => o.UseSqlite("Data Source=:memory:"));
        services.AddDataProtection();
        services.AddTransient<INodeRepo, NodeRepo>();
        services.AddSingleton<OpenWrtAdapterCache>();
        services.AddTransient<ISshSession, RealSshSession>();

        // The bug: NodePoller takes WiFiNode in its primary-ctor; WiFiNode is not a DI service.
        services.AddTransient<NodePoller>();

        var options = new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true };

        var ex = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(options));
        Assert.Contains(ex.InnerExceptions, inner =>
            inner is InvalidOperationException &&
            inner.Message.Contains("WiFiNode") &&
            inner.Message.Contains("NodePoller"));
    }
}
