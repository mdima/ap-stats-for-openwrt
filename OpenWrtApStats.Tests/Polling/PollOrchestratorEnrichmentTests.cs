using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Settings;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

/// <summary>
/// Phase 10 ORCH-02..07 integration suite. Drives PollOrchestrator.RunCycleAsync end-to-end through
/// the SqliteInMemoryFixture + TestDbContextFactory, using a stub enrichmentFactory that returns
/// a configurable EnrichmentDelta. Asserts the post-cycle DB state for:
///   - ORCH-02: Name preserved across cycles (operator-assigned, UPDATE-branch exclusion)
///   - ORCH-03: IsRandomMac computed on INSERT only; preserved across UPDATE cycles
///   - ORCH-04: DB-as-cache cascade for IPAddress (probe.IPAddress=null reuses persisted)
///   - ORCH-05: DB-as-cache cascade for Hostname (probe.Hostname=null reuses persisted)
///   - ORCH-06: Online → Offline preserves last-known IPAddress + Hostname
///   - ORCH-07: LastEnrichmentRefreshAt updated within the coordinator transaction on success
/// </summary>
public sealed class PollOrchestratorEnrichmentTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private async Task<(int nodeId, int netId, string netBssid)> SeedNodeWithOneNetworkAsync(CancellationToken ct,
        DateTimeOffset? lastEnrichmentRefreshAt = null,
        bool subnetScanEnabled = true)
    {
        var bssid = $"FE:00:00:00:{Random.Shared.Next(0, 255):X2}:{Random.Shared.Next(0, 255):X2}";
        await using var db = _fixture.CreateContext();
        var node = new WiFiNode
        {
            Hostname = $"orch10-{Guid.NewGuid():N}",
            Username = "root",
            Password = "",
            LastEnrichmentRefreshAt = lastEnrichmentRefreshAt,
            SubnetScanEnabled = subnetScanEnabled,
        };
        db.WiFiNodes.Add(node);
        await db.SaveChangesAsync(ct);

        var net = new WiFiNetwork
        {
            WiFiNodeId = node.Id,
            SSID = "Net",
            MacAddress = bssid
        };
        db.WiFiNetworks.Add(net);
        await db.SaveChangesAsync(ct);

        return (node.Id, net.Id, bssid);
    }

    private PollOrchestrator BuildOrchestrator(
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync,
        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>>? enrichmentFactory = null,
        TimeProvider? timeProvider = null)
    {
        var dpFactory = new TestDbContextFactory(_fixture.Options);
        return new PollOrchestrator(
            dpFactory,
            new NodeRepo(dpFactory),
            new NetworkRepo(dpFactory),
            new StationRepo(dpFactory),
            probeAsync,
            new PollNotifier(),
            NullLogger<PollOrchestrator>.Instance,
            new StubSettings(),
            enrichmentFactory ?? ((_, _, _, _) => Task.FromResult<EnrichmentDelta?>(null)),
            timeProvider ?? TimeProvider.System);
    }

    private static Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> ProbeForNode(int nodeId, string bssid, params WLANStation[] stations) =>
        (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            var network = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { network },
                Stations: stations,
                NewlyTrustedFingerprint: null));
        };

    [Fact]
    public async Task UpsertCycle_DoesNotOverwrite_OperatorAssignedName_AcrossCycles()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: DateTimeOffset.UtcNow);
        var mac = $"BA:01:02:03:04:{Random.Shared.Next(0, 255):X2}";

        // Cycle 1: probe inserts station with no Name.
        var orch1 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = mac, Bssid = bssid, RSS = -50, LastPollDate = DateTime.UtcNow }));
        await orch1.RunCycleAsync(ct);

        // Operator assigns a Name out-of-band.
        await using (var dbWrite = _fixture.CreateContext())
        {
            await dbWrite.WLANStations.Where(s => s.MacAddress == mac)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Name, "OperatorAssigned"), ct);
        }

        // Cycle 2: probe re-observes the same station; UPDATE branch must NOT touch Name.
        var orch2 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = mac, Bssid = bssid, RSS = -45, LastPollDate = DateTime.UtcNow }));
        await orch2.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var row = await verify.WLANStations.FirstAsync(s => s.MacAddress == mac, ct);
        Assert.Equal("OperatorAssigned", row.Name);
        Assert.Equal(-45, row.RSS);    // telemetry was updated
    }

    [Fact]
    public async Task UpsertCycle_ComputesIsRandomMac_OnFirstInsertOnly_ThenPreservesAcrossUpdates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: DateTimeOffset.UtcNow);
        // MAC byte 0 = 0xBA → U/L bit (0x02) is set → IsRandomMac = true on INSERT.
        var randomMac = $"BA:11:22:33:44:{Random.Shared.Next(0, 255):X2}";
        // MAC byte 0 = 0xFC → U/L bit (0x02) is also set; pick 0x00 to get IsRandomMac = false.
        var stableMac = $"00:11:22:33:44:{Random.Shared.Next(0, 255):X2}";

        var orch1 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = randomMac, Bssid = bssid, LastPollDate = DateTime.UtcNow },
            new WLANStation { MacAddress = stableMac, Bssid = bssid, LastPollDate = DateTime.UtcNow }));
        await orch1.RunCycleAsync(ct);

        await using (var verify1 = _fixture.CreateContext())
        {
            var random = await verify1.WLANStations.FirstAsync(s => s.MacAddress == randomMac, ct);
            var stable = await verify1.WLANStations.FirstAsync(s => s.MacAddress == stableMac, ct);
            Assert.True(random.IsRandomMac);
            Assert.False(stable.IsRandomMac);
        }

        // Operator overrides the random flag (e.g., this is actually a stable Proxmox VM MAC).
        await using (var dbWrite = _fixture.CreateContext())
        {
            await dbWrite.WLANStations.Where(s => s.MacAddress == randomMac)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsRandomMac, false), ct);
        }

        // Cycle 2: re-observe both → UPDATE branch must preserve IsRandomMac (no recomputation).
        var orch2 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = randomMac, Bssid = bssid, RSS = -55, LastPollDate = DateTime.UtcNow },
            new WLANStation { MacAddress = stableMac, Bssid = bssid, RSS = -55, LastPollDate = DateTime.UtcNow }));
        await orch2.RunCycleAsync(ct);

        await using var verify2 = _fixture.CreateContext();
        var randomAfter = await verify2.WLANStations.FirstAsync(s => s.MacAddress == randomMac, ct);
        var stableAfter = await verify2.WLANStations.FirstAsync(s => s.MacAddress == stableMac, ct);
        Assert.False(randomAfter.IsRandomMac);    // operator override preserved
        Assert.False(stableAfter.IsRandomMac);    // stable MAC stays stable
    }

    [Fact]
    public async Task UpsertCycle_ReusesPersistedIp_When_ProbeIpIsNull_DbAsCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: DateTimeOffset.UtcNow);
        var mac = $"BC:01:02:03:04:{Random.Shared.Next(0, 255):X2}";

        // Cycle 1: probe reports an IP.
        var orch1 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = mac, Bssid = bssid, IPAddress = "10.99.0.50", LastPollDate = DateTime.UtcNow }));
        await orch1.RunCycleAsync(ct);

        // Cycle 2: probe reports NO IP (neigh empty, sweep didn't fire). DB-as-cache must reuse 10.99.0.50.
        var orch2 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = mac, Bssid = bssid, IPAddress = null, LastPollDate = DateTime.UtcNow }));
        await orch2.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var row = await verify.WLANStations.FirstAsync(s => s.MacAddress == mac, ct);
        Assert.Equal("10.99.0.50", row.IPAddress);
    }

    [Fact]
    public async Task UpsertCycle_ReusesPersistedHostname_When_ProbeHostnameIsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: DateTimeOffset.UtcNow);
        var mac = $"BD:01:02:03:04:{Random.Shared.Next(0, 255):X2}";

        // Cycle 1: probe reports a hostname.
        var orch1 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = mac, Bssid = bssid, IPAddress = "10.99.0.51", Hostname = "laptop.local", LastPollDate = DateTime.UtcNow }));
        await orch1.RunCycleAsync(ct);

        // Cycle 2: probe reports NO hostname (no nslookup ran or it returned NXDOMAIN). Persisted Hostname must be preserved.
        var orch2 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = mac, Bssid = bssid, IPAddress = "10.99.0.51", Hostname = null, LastPollDate = DateTime.UtcNow }));
        await orch2.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var row = await verify.WLANStations.FirstAsync(s => s.MacAddress == mac, ct);
        Assert.Equal("laptop.local", row.Hostname);
    }

    [Fact]
    public async Task RunCycle_OnlineToOffline_PreservesLastKnownIpAndHostname()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: DateTimeOffset.UtcNow);
        var mac = $"BE:01:02:03:04:{Random.Shared.Next(0, 255):X2}";

        // Cycle 1: station observed Online with IP + Hostname.
        var orch1 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = mac, Bssid = bssid, IPAddress = "10.99.0.60", Hostname = "phone.local", LastPollDate = DateTime.UtcNow }));
        await orch1.RunCycleAsync(ct);

        // Cycle 2: station NOT observed → mark-offline path runs; the station should flip to Offline
        // but its IPAddress + Hostname must be preserved (ORCH-06 by construction — MarkAllOfflineAsync
        // only sets Status).
        var orch2 = BuildOrchestrator(ProbeForNode(nodeId, bssid /* no stations */));
        await orch2.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var row = await verify.WLANStations.FirstAsync(s => s.MacAddress == mac, ct);
        Assert.Equal(Status.Offline, row.Status);
        Assert.Equal("10.99.0.60", row.IPAddress);
        Assert.Equal("phone.local", row.Hostname);
    }

    [Fact]
    public async Task RunCycle_AfterSuccessfulRefreshTick_UpdatesLastEnrichmentRefreshAt_WithinTransaction()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seed with LastEnrichmentRefreshAt = null → trigger fires.
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: null);
        var mac = $"BF:01:02:03:04:{Random.Shared.Next(0, 255):X2}";

        // Stub enrichmentFactory returns a non-null delta — orchestrator must persist LastEnrichmentRefreshAt.
        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> factory =
            (n, ips, peerIp, c) => Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(
                n.Id,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string?>(StringComparer.Ordinal)));

        var fixedNow = DateTimeOffset.UtcNow;
        var fakeClock = new FakeTimeProvider(fixedNow);

        var orch = BuildOrchestrator(
            ProbeForNode(nodeId, bssid,
                new WLANStation { MacAddress = mac, Bssid = bssid, LastPollDate = DateTime.UtcNow }),
            enrichmentFactory: factory,
            timeProvider: fakeClock);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var node = await verify.WiFiNodes.FirstAsync(n => n.Id == nodeId, ct);
        Assert.NotNull(node.LastEnrichmentRefreshAt);
        // The persisted value should match the fake clock (within 1 second due to DateTimeOffset round-tripping).
        Assert.True((node.LastEnrichmentRefreshAt!.Value - fixedNow).Duration() < TimeSpan.FromSeconds(1));
    }

    private sealed class FakeTimeProvider(DateTimeOffset fixedUtcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => fixedUtcNow;
    }

    private sealed class StubSettings : IAppSettingsService
    {
        public int     PollingIntervalSeconds => 30;
        public string? UIPasswordHash         => null;
        public bool    WizardDismissed        => false;
        public int     RefreshIntervalSeconds => 3600;
        public IReadOnlyList<string> LocalDomainSuffixes => Array.Empty<string>();
        public event Action<IAppSettingsService>? Changed { add { } remove { } }

        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
