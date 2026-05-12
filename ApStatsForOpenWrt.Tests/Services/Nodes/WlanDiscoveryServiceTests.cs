using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Nodes;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Polling.Exceptions;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Services.Nodes;

/// <summary>
/// WLAN-01..04 discovery orchestration tests. Verifies:
/// - Success: networks persisted via UpsertManyAsync; absent rows disabled via MarkAbsentDisabledAsync (D-15)
/// - Within-cycle-reentry: Busy outcome (D-14)
/// - HostKeyMismatchException caught: HostKeyMismatch outcome
/// - NodeNotFound: returns NodeNotFound outcome (no probe call)
/// </summary>
public sealed class WlanDiscoveryServiceTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private (IWlanDiscoveryService svc, INodeRepo nodes, INetworkRepo nets) Build(
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe)
    {
        var factory = new TestDbContextFactory(_fixture.Options);
        var nodes = new NodeRepo(factory);
        var nets = new NetworkRepo(factory);
        var svc = new WlanDiscoveryService(probe, nodes, nets, NullLogger<WlanDiscoveryService>.Instance);
        return (svc, nodes, nets);
    }

    [Fact]
    public async Task Success_PersistsNetworks_AndDisablesAbsent()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed: a node with two existing WLANs, both Enabled=true.
        var seedFactory = new TestDbContextFactory(_fixture.Options);
        var seedNodes = new NodeRepo(seedFactory);
        var seedNets = new NetworkRepo(seedFactory);
        var node = new WiFiNode
        {
            Hostname = $"discover-success-{Guid.NewGuid():N}",
            Username = "root",
            Password = ""
        };
        await seedNodes.AddAsync(node, ct);
        await seedNets.UpsertManyAsync(node.Id, new[]
        {
            new WiFiNetwork { MacAddress = "11:22:33:00:00:01", SSID = "old-1", Enabled = true },
            new WiFiNetwork { MacAddress = "11:22:33:00:00:02", SSID = "old-2", Enabled = true },
        }, ct);

        // Probe returns only :01 — :02 must be disabled-on-absence.
        var (svc, _, nets) = Build((n, _) => Task.FromResult<NodeProbeResult>(
            new NodeProbeResult.Success(
                n.Id,
                Networks: new List<WiFiNetwork>
                {
                    new() { MacAddress = "11:22:33:00:00:01", SSID = "still-here" },
                },
                Stations: new List<WLANStation>(),
                NewlyTrustedFingerprint: null)));

        var outcome = await svc.DiscoverAsync(node.Id, ct);

        Assert.IsType<DiscoveryOutcome.Success>(outcome);
        var s = (DiscoveryOutcome.Success)outcome;
        Assert.Equal(1, s.DiscoveredCount);
        Assert.Equal(1, s.DisabledOnAbsence);
        Assert.Contains("Discovery complete", outcome.UserMessage);
        Assert.Contains("1 WLANs are now visible", outcome.UserMessage);
        Assert.Contains("1 previously-discovered WLANs were marked disabled", outcome.UserMessage);

        var rows = await nets.GetByNodeAsync(node.Id, ct);
        Assert.Equal(2, rows.Count);   // none deleted; absent row preserved & disabled
        Assert.True(rows.Single(r => r.MacAddress == "11:22:33:00:00:01").Enabled);
        Assert.False(rows.Single(r => r.MacAddress == "11:22:33:00:00:02").Enabled);   // absent → Enabled=false
    }

    [Fact]
    public async Task NodeNotFound_DoesNotCallProbe()
    {
        var ct = TestContext.Current.CancellationToken;
        var probeCalled = false;
        var (svc, _, _) = Build((n, _) =>
        {
            probeCalled = true;
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "x"));
        });

        var outcome = await svc.DiscoverAsync(int.MaxValue, ct);

        Assert.IsType<DiscoveryOutcome.NodeNotFound>(outcome);
        Assert.False(probeCalled);
    }

    [Fact]
    public async Task WithinCycleReentry_MapsToBusy()
    {
        // D-14: rely on Phase 2's per-node SemaphoreSlim B2 LOCK; within-cycle reentry → Busy.
        var ct = TestContext.Current.CancellationToken;
        var seedFactory = new TestDbContextFactory(_fixture.Options);
        var seedNodes = new NodeRepo(seedFactory);
        var node = new WiFiNode
        {
            Hostname = $"discover-busy-{Guid.NewGuid():N}",
            Username = "root",
            Password = ""
        };
        await seedNodes.AddAsync(node, ct);

        var (svc, _, _) = Build((n, _) => Task.FromResult<NodeProbeResult>(
            new NodeProbeResult.Failed(n.Id, "within-cycle-reentry")));

        var outcome = await svc.DiscoverAsync(node.Id, ct);

        Assert.IsType<DiscoveryOutcome.Busy>(outcome);
        Assert.Equal(OutcomeSeverity.Info, outcome.Severity);
        Assert.Contains("Node is busy with the regular polling cycle", outcome.UserMessage);
    }

    [Fact]
    public async Task HostKeyMismatchException_CaughtByService_MapsToHostKeyMismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var seedFactory = new TestDbContextFactory(_fixture.Options);
        var seedNodes = new NodeRepo(seedFactory);
        var node = new WiFiNode
        {
            Hostname = $"discover-hkm-{Guid.NewGuid():N}",
            Username = "root",
            Password = ""
        };
        await seedNodes.AddAsync(node, ct);

        var (svc, _, _) = Build((n, _) => throw new HostKeyMismatchException("a", "b"));

        var outcome = await svc.DiscoverAsync(node.Id, ct);

        Assert.IsType<DiscoveryOutcome.HostKeyMismatch>(outcome);
        Assert.Contains("Host key changed since first trust", outcome.UserMessage);
    }

    [Fact]
    public async Task HostKeyMismatchReasonToken_AlsoMapsToHostKeyMismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var seedFactory = new TestDbContextFactory(_fixture.Options);
        var seedNodes = new NodeRepo(seedFactory);
        var node = new WiFiNode
        {
            Hostname = $"discover-hkm2-{Guid.NewGuid():N}",
            Username = "root",
            Password = ""
        };
        await seedNodes.AddAsync(node, ct);

        var (svc, _, _) = Build((n, _) => Task.FromResult<NodeProbeResult>(
            new NodeProbeResult.Failed(n.Id, "host-key-mismatch")));

        var outcome = await svc.DiscoverAsync(node.Id, ct);

        Assert.IsType<DiscoveryOutcome.HostKeyMismatch>(outcome);
    }
}
