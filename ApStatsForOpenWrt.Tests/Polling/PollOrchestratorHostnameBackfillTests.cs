using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Enrichment;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Polling;

/// <summary>
/// Phase 10 HUMAN-UAT regression — Bug 2 root cause from debug session
/// <c>p10-trigger-and-nslookup</c>. Lives in its own class (with its own
/// <see cref="IClassFixture{SqliteInMemoryFixture}"/>) so the fresh per-class fixture
/// guarantees DB isolation from <see cref="PollOrchestratorDeletionRebidTests"/>: both
/// classes stub <c>Func&lt;WiFiNode, ...&gt; probe</c> as a per-test closure that returns
/// the same MAC for every node, so sharing a fixture would let one class's persisted rows
/// pollute the other class's <c>existingMacs</c> computation.
///
/// Pre-condition: a node has at least one persisted station with <c>IPAddress</c> populated by a
/// prior sweep and <c>Hostname IS NULL</c> (the spec-correct "DNS chain not yet ready" state per
/// ORCH-08 retry-on-next-tick). <c>LastEnrichmentRefreshAt</c> is recent (NOT null, NOT expired),
/// so the time-based arm cannot fire this cycle — the only path to enrichment is the new-MAC arm.
///
/// Scenario: a NEW device associates and is observed by iwinfo, firing <c>newMacDetected</c>. The
/// orchestrator MUST include the pre-existing IP-having station's <c>IPAddress</c> in the
/// ipsForReverseDns list it passes to the enrichmentFactory (the
/// <c>PollOrchestrator.cs:248-254</c> "existing-stations retry-on-next-tick" branch). When the
/// factory returns a hostname for that IP, the orchestrator MUST persist it on the pre-existing
/// row.
///
/// Pre-fix (fast-path present): when no time-based arm was eligible, <c>existingStationsByNode</c>
/// was zeroed to empty lists, so the line-248-254 branch contributed nothing to ipsForDns. iwinfo
/// never reports IPs on r.Stations either, so ipsForDns ended up empty and the factory was called
/// with NO IPs. The hostname was never resolved, never merged, never persisted.
///
/// Post-fix (full-fetch always): the line-248-254 branch sees the persisted (MAC, IP,
/// Hostname=null) row and contributes the IP. The factory resolves it, the merge fills both IP and
/// Hostname on the probe-reported station POCO, and <c>UpsertCycleAsync</c> writes Hostname back
/// to the row keyed by MAC.
/// </summary>
public sealed class PollOrchestratorHostnameBackfillTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    [Fact]
    public async Task RebidViaNewMacArm_BackfillsHostnamesForExistingIpRows()
    {
        var ct = TestContext.Current.CancellationToken;

        var bssid        = "FE:DE:DE:DE:DE:02";
        var existingMac  = "AA:BB:CC:DD:EE:42";       // IP-having, Hostname=null station already in DB
        var newMac       = "11:22:33:44:55:99";       // brand-new MAC arriving in cycle 2 — fires new-MAC arm
        var existingIp   = "192.168.1.42";
        var expectedFqdn = "kitchen-light.lan";

        // coldStart is "recent" — well INSIDE RefreshIntervalSeconds=3600. So the time-based arm
        // CANNOT fire; the only way the trigger can fire is the new-MAC arm. This is exactly the
        // production state where the fast-path used to short-circuit.
        var coldStartTs = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using (var db = _fixture.CreateContext())
        {
            var node = new WiFiNode
            {
                Hostname = "wifi-ap-rebid-hostname-backfill",
                Username = "root",
                Password = "",
                LastEnrichmentRefreshAt = coldStartTs,
                SubnetScanEnabled = true,
            };
            db.WiFiNodes.Add(node);
            await db.SaveChangesAsync(ct);

            var net = new WiFiNetwork
            {
                WiFiNodeId = node.Id,
                SSID       = "Net",
                MacAddress = bssid,
            };
            db.WiFiNetworks.Add(net);
            await db.SaveChangesAsync(ct);

            db.WLANStations.Add(new WLANStation
            {
                MacAddress    = existingMac,
                WiFiNetworkId = net.Id,
                Bssid         = null,
                IPAddress     = existingIp,
                Hostname      = null,                 // ← the bug condition: IP populated, hostname null
                Status        = Status.Online,
                LastPollDate  = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        // Capture the ipsForReverseDns the orchestrator hands the factory — this is the empirical
        // proof that the fast-path was killing the input list.
        List<string>? capturedIps = null;
        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> factory =
            (n, ips, peerIp, c) =>
            {
                capturedIps = ips.ToList();

                // Sweep portion (simulated): always reports the existing MAC's IP so the merge
                // step at PollOrchestrator.cs:277-287 can fill it on the probe POCO. In production
                // this is SubnetSweepProbe.PollAsync's arping pass.
                var sweep = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [existingMac] = existingIp,
                };

                // Reverse-DNS portion: ONLY resolves IPs the orchestrator asked about. This
                // mirrors ReverseDnsProbe.PollAsync's behavior (it iterates the input `ips` list).
                // With the fast-path present, `ips` is empty, so the result map is empty.
                var dns = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var ip in ips)
                {
                    if (ip == existingIp) dns[ip] = expectedFqdn;
                }

                return Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(n.Id, sweep, dns));
            };

        // Probe stub: cycle 1 reports only the existing MAC (steady-state). Cycle 2 reports the
        // existing MAC PLUS a new MAC — that arrival fires newMacDetected.
        bool newMacObserved = false;
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            var network = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            var stations = new List<WLANStation>
            {
                new WLANStation { MacAddress = existingMac, Bssid = bssid, RSS = -55, LastPollDate = DateTime.UtcNow },
            };
            if (newMacObserved)
            {
                stations.Add(new WLANStation
                {
                    MacAddress = newMac, Bssid = bssid, RSS = -60, LastPollDate = DateTime.UtcNow,
                });
            }
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id, Networks: new[] { network }, Stations: stations.ToArray(), NewlyTrustedFingerprint: null));
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
            fakeClock,
            new ForceEnrichmentService());

        // Cycle 1: steady-state, no new MAC, time-arm not eligible → trigger does NOT fire.
        // capturedIps stays null. The existing station's Hostname stays null.
        await BuildOrch().RunCycleAsync(ct);
        Assert.Null(capturedIps);

        // Cycle 2: new MAC arrives, fires newMacDetected → trigger fires → factory MUST be called
        // with the existing station's IP in its ipsForReverseDns input.
        newMacObserved = true;
        fakeClock.Advance(TimeSpan.FromSeconds(30));
        await BuildOrch().RunCycleAsync(ct);

        // Decisive assertion #1 — empirical proof that the orchestrator surfaces the persisted
        // IP-having row's IP to the reverse-DNS probe. This is the assertion the fast-path
        // structurally cannot satisfy: with the fast-path present, capturedIps would be empty.
        Assert.NotNull(capturedIps);
        Assert.Contains(existingIp, capturedIps!);

        // Decisive assertion #2 — the resolved hostname lands on the pre-existing row in the DB.
        await using var verify = _fixture.CreateContext();
        var stationAfter = await verify.WLANStations.AsNoTracking()
            .FirstAsync(s => s.MacAddress == existingMac, ct);
        Assert.Equal(expectedFqdn, stationAfter.Hostname);
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
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        // Phase 17 Plan 17-01 M-UPD-02 — minimal mutable update-check stub members.
        public bool UpdateCheckEnabled { get; private set; } = true;
        public bool HistoryEnabled => true;
        public Task SetHistoryEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public string? LatestKnownVersion { get; private set; }
        public string? LatestReleaseUrl { get; private set; }
        public DateTime? LastCheckedAtUtc { get; private set; }
        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) { UpdateCheckEnabled = enabled; return Task.CompletedTask; }
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) { LatestKnownVersion = version; return Task.CompletedTask; }
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) { LatestReleaseUrl = url; return Task.CompletedTask; }
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) { LastCheckedAtUtc = checkedAtUtc; return Task.CompletedTask; }
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default) { LatestKnownVersion = version; LatestReleaseUrl = url; LastCheckedAtUtc = checkedAtUtc; return Task.CompletedTask; }
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
