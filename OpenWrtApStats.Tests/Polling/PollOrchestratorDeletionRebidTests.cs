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
/// Phase 10 HUMAN-UAT regression — Bug 1 from p10-trigger-and-nslookup debug session.
///
/// Scenario: operator deletes an Online station via /Stations/{mac} → Delete (hard delete via
/// StationRepo.DeleteByMacAsync). The device remains associated to its AP and iwinfo re-reports
/// the MAC on the next cycle. The composite refresh trigger's newMacDetected arm
/// (PollOrchestrator.cs:208) should fire because the MAC is observed by the probe but NOT
/// present in the cycle's existingMacs query — driving LastEnrichmentRefreshAt to advance.
///
/// Live UAT showed LastEnrichmentRefreshAt stuck at the cold-start timestamp across 16+ cycles
/// post-deletion. This test isolates the orchestrator loop end-to-end (in-process, no SSH) to
/// determine whether the bug lives in the orchestrator's view of `existingMacs` or upstream.
/// </summary>
public sealed class PollOrchestratorDeletionRebidTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    [Fact]
    public async Task DeletedOnlineStation_ReObservedByProbe_AdvancesLastEnrichmentRefreshAt_OnNextCycle()
    {
        var ct = TestContext.Current.CancellationToken;

        // ── Setup: node + network + a single station already present (post-cold-start state) ──
        var bssid = "FE:DE:DE:DE:DE:01";
        var mac   = "AA:BB:CC:DD:EE:01";
        var coldStartTs = DateTimeOffset.UtcNow.AddMinutes(-1); // recent — well inside RefreshIntervalSeconds=3600

        int nodeId;
        int netId;
        await using (var db = _fixture.CreateContext())
        {
            var node = new WiFiNode
            {
                Hostname = "wifi-ap-delrebid",
                Username = "root",
                Password = "",
                LastEnrichmentRefreshAt = coldStartTs,
                SubnetScanEnabled = true,
            };
            db.WiFiNodes.Add(node);
            await db.SaveChangesAsync(ct);
            nodeId = node.Id;

            var net = new WiFiNetwork
            {
                WiFiNodeId = node.Id,
                SSID = "Net",
                MacAddress = bssid
            };
            db.WiFiNetworks.Add(net);
            await db.SaveChangesAsync(ct);
            netId = net.Id;

            var existingStation = new WLANStation
            {
                MacAddress  = mac,
                WiFiNetworkId = net.Id,
                Bssid       = null,             // [NotMapped] — irrelevant for persistence
                IPAddress   = "192.168.1.50",
                Hostname    = "before-delete",
                Status      = Status.Online,
                LastPollDate = DateTime.UtcNow,
            };
            db.WLANStations.Add(existingStation);
            await db.SaveChangesAsync(ct);
        }

        // Capture whether the enrichmentFactory was actually invoked, and the trigger's
        // ipsForReverseDns input — that tells us if the orchestrator decided to refresh.
        int factoryInvocationCount = 0;
        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> factory =
            (n, ips, peerIp, c) =>
            {
                factoryInvocationCount++;
                return Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(
                    n.Id,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    new Dictionary<string, string?>(StringComparer.Ordinal)));
            };

        // Probe stub returns the same station on every cycle (device stays associated).
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            var network = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            var stations = new[]
            {
                new WLANStation
                {
                    MacAddress  = mac,
                    Bssid       = bssid,
                    RSS         = -55,
                    LastPollDate = DateTime.UtcNow,
                }
            };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id, Networks: new[] { network }, Stations: stations, NewlyTrustedFingerprint: null));
        };

        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var stationRepo = new StationRepo(dpFactory);

        var fakeClock = new MutableTimeProvider(coldStartTs.AddSeconds(30));
        PollOrchestrator BuildOrch() => new PollOrchestrator(
            dpFactory,
            new NodeRepo(dpFactory),
            new NetworkRepo(dpFactory),
            stationRepo,
            probe,
            new PollNotifier(),
            NullLogger<PollOrchestrator>.Instance,
            new StubSettings(),
            factory,
            fakeClock);

        // ── Cycle 1 (steady-state, no change): trigger should NOT fire ──
        await BuildOrch().RunCycleAsync(ct);
        Assert.Equal(0, factoryInvocationCount);     // no MAC change, time interval not yet expired → no trigger

        await using (var verifyA = _fixture.CreateContext())
        {
            var n = await verifyA.WiFiNodes.AsNoTracking().FirstAsync(x => x.Id == nodeId, ct);
            Assert.Equal(coldStartTs, n.LastEnrichmentRefreshAt!.Value, TimeSpan.FromSeconds(1));
        }

        // ── Operator clicks Delete on /Stations/{mac} ──
        await stationRepo.DeleteByMacAsync(mac, ct);

        await using (var verifyDel = _fixture.CreateContext())
        {
            Assert.False(await verifyDel.WLANStations.AnyAsync(s => s.MacAddress == mac, ct),
                "Sanity: station should be hard-deleted before cycle 2 starts.");
        }

        // ── Cycle 2 (30s later, device still associated, iwinfo reports the MAC) ──
        //    Expected per design: newMacDetected = true → trigger fires → LastEnrichmentRefreshAt advances.
        fakeClock.Advance(TimeSpan.FromSeconds(30));
        await BuildOrch().RunCycleAsync(ct);

        // Decisive assertions ── these flip together; one failing tells us which arm broke.
        await using var verifyFinal = _fixture.CreateContext();
        var nodeAfter = await verifyFinal.WiFiNodes.AsNoTracking().FirstAsync(n => n.Id == nodeId, ct);

        Assert.Equal(1, factoryInvocationCount);     // trigger MUST have fired the factory exactly once
        Assert.NotEqual(coldStartTs, nodeAfter.LastEnrichmentRefreshAt!.Value);   // timestamp MUST advance
        Assert.True(nodeAfter.LastEnrichmentRefreshAt!.Value > coldStartTs,
            $"LastEnrichmentRefreshAt did not advance: before={coldStartTs:o}, after={nodeAfter.LastEnrichmentRefreshAt:o}");
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class StubSettings : IAppSettingsService
    {
        public int     PollingIntervalSeconds => 30;
        public string? UIPasswordHash         => null;
        public bool    WizardDismissed        => false;
        public int     RefreshIntervalSeconds => 3600;     // 1h — well above the cycle gap so time-arm stays cold
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
