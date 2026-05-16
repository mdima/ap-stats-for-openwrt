using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Enrichment;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Polling;

/// <summary>
/// AUDIT-04 reference fixture (CONTEXT 14 §D-18). Deterministic multi-node multi-WLAN scenario.
/// Asserts ≥90% first-cycle enrichment coverage. Fixed-seed Random so successive runs against
/// the same code produce identical pass/fail outcomes. The fixture's enrichmentFactory simulates
/// a 95% upstream success rate; the orchestrator's behaviour is what the assertion measures.
/// <para>
/// The fixture is tagged <c>[Trait("Category", "Audit")]</c> so it is excluded from the
/// per-commit fast suite (<c>--filter-not-trait "Category=Smoke"</c> does NOT exclude Audit,
/// but the default <c>dotnet test</c> invocation in CI uses an explicit filter; the convention
/// is <c>--filter "Category=Audit"</c> for the audit-only run).
/// </para>
/// </summary>
[Trait("Category", "Audit")]
public sealed class FirstCycleEnrichmentCoverageFixture(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    [Fact]
    public async Task FirstCycleEnrichmentCoverage_AtLeast90Percent_OnReferenceFixture()
    {
        var ct = TestContext.Current.CancellationToken;
        var rng = new Random(14_03_42);    // fixed seed for determinism (CONTEXT 14 §D-18)

        // Seed: 3 nodes × 2 WLANs each × 10 stations per WLAN = 60 stations.
        var seededNodeIds = new List<int>();
        var bssidByNetworkId = new Dictionary<int, string>();
        await using (var db = _fixture.CreateContext())
        {
            for (int n = 0; n < 3; n++)
            {
                var node = new WiFiNode
                {
                    Hostname = $"audit-node-{n}",
                    Username = "root",
                    Password = "",
                    SubnetScanEnabled = true,
                    LastEnrichmentRefreshAt = null, // cold start -> trigger fires unconditionally
                };
                db.WiFiNodes.Add(node);
                await db.SaveChangesAsync(ct);
                seededNodeIds.Add(node.Id);

                for (int w = 0; w < 2; w++)
                {
                    var net = new WiFiNetwork
                    {
                        WiFiNodeId = node.Id,
                        SSID = $"net-{n}-{w}",
                        MacAddress = $"FE:00:00:{n:X2}:{w:X2}:00"
                    };
                    db.WiFiNetworks.Add(net);
                }
                await db.SaveChangesAsync(ct);
            }

            // Capture BSSIDs keyed by network ID so the probe and factory key consistently.
            foreach (var net in await db.WiFiNetworks.AsNoTracking().ToListAsync(ct))
            {
                bssidByNetworkId[net.Id] = net.MacAddress;
            }
        }

        // probeAsync returns 10 stations per WLAN with deterministic MACs derived from node/network ids.
        // The same MAC formation is used by the enrichmentFactory below so SweepMacToIp keys match.
        Task<NodeProbeResult> Probe(WiFiNode node, CancellationToken _)
        {
            using var dbInner = _fixture.CreateContext();
            var networks = dbInner.WiFiNetworks.AsNoTracking().Where(x => x.WiFiNodeId == node.Id).ToList();
            var stations = new List<WLANStation>();
            var probeNetworks = new List<WiFiNetwork>();
            foreach (var net in networks)
            {
                // Pass the existing network (with its persisted Id) through the probe result; the
                // orchestrator upserts by (NodeId, MacAddress) and the BSSID lookup hits the same row.
                probeNetworks.Add(new WiFiNetwork { WiFiNodeId = node.Id, MacAddress = net.MacAddress, SSID = net.SSID });
                for (int s = 0; s < 10; s++)
                {
                    var mac = $"AA:{node.Id:X2}:{net.Id:X2}:00:00:{s:X2}";
                    stations.Add(new WLANStation
                    {
                        MacAddress = mac,
                        Bssid = net.MacAddress,
                        Status = Status.Online,
                        IPAddress = null,        // pre-enrichment
                        Hostname = null,         // pre-enrichment
                        LastPollDate = DateTime.UtcNow,
                        RSS = -55,
                    });
                }
            }
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: node.Id,
                Networks: probeNetworks,
                Stations: stations,
                NewlyTrustedFingerprint: null));
        }

        // enrichmentFactory: deterministically resolve ~95% of stations' IPs + hostnames; leave the
        // remainder null. The fixture is measuring the ORCHESTRATOR'S behaviour against a known-good
        // upstream — if first-cycle coverage drops below 90% here, the racing condition CONTEXT 14
        // §AUDIT alludes to is implicated.
        Task<EnrichmentDelta?> Factory(WiFiNode node, IReadOnlyList<string> ips, string peer, CancellationToken _)
        {
            var sweep = new Dictionary<string, string>(StringComparer.Ordinal);
            var dns = new Dictionary<string, string?>(StringComparer.Ordinal);

            using var dbInner = _fixture.CreateContext();
            var networks = dbInner.WiFiNetworks.AsNoTracking().Where(x => x.WiFiNodeId == node.Id).ToList();
            int slotIndex = 0;
            foreach (var net in networks)
            {
                for (int s = 0; s < 10; s++)
                {
                    var rnd = rng.NextDouble();
                    if (rnd < 0.95)
                    {
                        var mac = $"AA:{node.Id:X2}:{net.Id:X2}:00:00:{s:X2}";
                        var fakeIp = $"192.168.{node.Id}.{100 + slotIndex}";
                        sweep[mac] = fakeIp;
                        dns[fakeIp] = $"host-{node.Id}-{slotIndex}.test";
                    }
                    slotIndex++;
                }
            }
            return Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(
                NodeId: node.Id,
                SweepMacToIp: sweep,
                ReverseDnsResolved: dns));
        }

        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var orchestrator = new PollOrchestrator(
            dpFactory,
            new NodeRepo(dpFactory),
            new NetworkRepo(dpFactory),
            new StationRepo(dpFactory),
            Probe,
            new PollNotifier(),
            NullLogger<PollOrchestrator>.Instance,
            new StubSettings(),
            Factory,
            TimeProvider.System,
            new ForceEnrichmentService());

        // Run the FIRST cycle.
        await orchestrator.RunCycleAsync(ct);

        // Count enriched stations in the DB (post-first-cycle). A station is "enriched" iff its
        // IPAddress OR Hostname is populated.
        await using var assertDb = _fixture.CreateContext();
        var auditMacs = await assertDb.WLANStations
            .Where(s => s.MacAddress.StartsWith("AA:"))
            .Select(s => new { s.MacAddress, s.IPAddress, s.Hostname })
            .ToListAsync(ct);
        var total = auditMacs.Count;
        var withIpOrHostname = auditMacs.Count(s => !string.IsNullOrEmpty(s.IPAddress) || !string.IsNullOrEmpty(s.Hostname));

        var coverage = total == 0 ? 0.0 : (double)withIpOrHostname / total;
        Assert.True(coverage >= 0.90,
            $"AUDIT-04: first-cycle enrichment coverage was {coverage:P1}, expected >=90%. Observed total={total}, enriched={withIpOrHostname}.");
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
