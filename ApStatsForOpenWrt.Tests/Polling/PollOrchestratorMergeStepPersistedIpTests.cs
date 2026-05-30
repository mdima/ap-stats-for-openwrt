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
/// Phase 10 HUMAN-UAT third-order regression for debug session <c>p10-trigger-and-nslookup</c>.
/// Lives in its own class (with its own <see cref="IClassFixture{SqliteInMemoryFixture}"/>) so
/// the fresh per-class fixture guarantees DB isolation from
/// <see cref="PollOrchestratorHostnameBackfillTests"/> and
/// <see cref="PollOrchestratorDeletionRebidTests"/>: all three classes stub
/// <c>Func&lt;WiFiNode, ...&gt; probe</c> as per-test closures that return the same MACs for every
/// node, so sharing a fixture would let one class's persisted rows pollute the other class's
/// <c>existingMacs</c> computation.
///
/// Background: <see cref="PollOrchestratorHostnameBackfillTests"/> closed the second-order bug
/// (commit <c>87bf42f</c>) so the orchestrator now correctly aggregates persisted IPs into
/// <c>ipsForDns</c>. A subsequent runtime observation surfaced a THIRD-order silent drop in the
/// merge step at <c>PollOrchestrator.cs</c> lines ~247-271: when neither <c>ip neigh</c>
/// (<see cref="NodePoller"/>:128) nor the arping sweep (<c>delta.SweepMacToIp</c>) gave the probe
/// POCO an <c>IPAddress</c> for a given MAC, but the persisted row already carried the IP, the
/// dnsMap lookup (keyed on <c>s.IPAddress</c>) silently missed and the resolved FQDN was thrown
/// away — even though <c>delta.ReverseDnsResolved</c> contained it.
///
/// Runtime evidence (operator deployment 2026-05-11): 5 consecutive enrichment cycles each
/// resolved 13-14 IPs via the <see cref="ReverseDnsProbe"/> line-86 LogInformation, yet DB
/// hostname count stayed at 0/41 across all 5 cycles. The dns probe was doing its job; the merge
/// step had no way to apply the result for stations whose probe POCO <c>IPAddress</c> was null.
///
/// Fix: in the merge step (after the sweep-IP fallback), add a persisted-IP fallback that
/// hydrates <c>s.IPAddress</c> from <c>existingByMacForNode[mac].IPAddress</c> when both upstream
/// sources are null. Then the dnsMap lookup hits and the FQDN reaches the row.
///
/// RED pre-fix: <c>s.IPAddress</c> stays null → dnsMap lookup misses → Hostname stays null on
/// the probe POCO → <c>UpsertCycleAsync</c> coalesces with the existing (null) hostname → DB
/// row's Hostname stays null.
/// GREEN post-fix: persisted-IP fallback fills <c>s.IPAddress</c> → dnsMap lookup hits → Hostname
/// reaches the probe POCO → <c>UpsertCycleAsync</c> writes it through to the DB row.
/// </summary>
public sealed class PollOrchestratorMergeStepPersistedIpTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    [Fact]
    public async Task RebidViaNewMacArm_BackfillsHostnamesEvenWhenIpNeighAndSweepAreEmpty()
    {
        var ct = TestContext.Current.CancellationToken;

        var bssid        = "FE:DE:DE:DE:DE:03";
        var existingMac  = "AA:BB:CC:DD:EE:43";       // IP-having, Hostname=null station already in DB
        var newMac       = "11:22:33:44:55:9A";       // brand-new MAC arriving in cycle 2 — fires new-MAC arm
        var existingIp   = "192.168.1.43";
        var expectedFqdn = "study-printer.lan";

        // coldStart is "recent" — well INSIDE RefreshIntervalSeconds=3600. So the time-based arm
        // CANNOT fire; only path to enrichment is the new-MAC arm. This is exactly the production
        // state where the operator observed the merge-step bug.
        var coldStartTs = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using (var db = _fixture.CreateContext())
        {
            var node = new WiFiNode
            {
                Hostname = "wifi-ap-merge-step-persisted-ip",
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

        // Factory: SweepMacToIp is EMPTY (simulating arping returning no hits — operator's
        // observed steady-state where the sweep is silent). ReverseDnsResolved DOES contain a
        // hostname for the persisted IP, because the orchestrator's line-218-224 branch puts
        // the persisted IP into ipsForReverseDns (Hostname is null AND IPAddress is non-null).
        List<string>? capturedIps = null;
        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> factory =
            (n, ips, peerIp, c) =>
            {
                capturedIps = ips.ToList();

                // Sweep returned nothing — empty dict. Mirrors operator runtime state (no
                // `Subnet-sweep pass on node ...: found N MAC→IP mappings` lines in their logs).
                var sweep = new Dictionary<string, string>(StringComparer.Ordinal);

                // Reverse-DNS: orchestrator handed us the persisted IP via the line-218-224
                // branch; we resolve it (mirrors operator's 13/15 successful nslookup runtime).
                var dns = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var ip in ips)
                {
                    if (ip == existingIp) dns[ip] = expectedFqdn;
                }

                return Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(n.Id, sweep, dns));
            };

        // Probe stub: cycle 1 reports only the existing MAC (steady-state). Cycle 2 reports the
        // existing MAC PLUS a new MAC — that arrival fires newMacDetected.
        //
        // IMPORTANT: the probe sets IPAddress = null on r.Stations (mirrors `ip neigh show`
        // returning empty on this AP — the operator's runtime condition that triggers the bug).
        bool newMacObserved = false;
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            var network = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            var stations = new List<WLANStation>
            {
                new WLANStation
                {
                    MacAddress = existingMac, Bssid = bssid, RSS = -55,
                    IPAddress = null,        // ← ip-neigh empty for this MAC on this cycle
                    LastPollDate = DateTime.UtcNow,
                },
            };
            if (newMacObserved)
            {
                stations.Add(new WLANStation
                {
                    MacAddress = newMac, Bssid = bssid, RSS = -60,
                    IPAddress = null,
                    LastPollDate = DateTime.UtcNow,
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
        await BuildOrch().RunCycleAsync(ct);
        Assert.Null(capturedIps);

        // Cycle 2: new MAC arrives, fires newMacDetected → trigger fires → factory MUST be
        // called with the existing station's persisted IP in its ipsForReverseDns input (the
        // line-218-224 existing-stations branch contributes it because Hostname IS NULL ∧
        // IPAddress is non-null).
        newMacObserved = true;
        fakeClock.Advance(TimeSpan.FromSeconds(30));
        await BuildOrch().RunCycleAsync(ct);

        // Sanity: the orchestrator did pass the persisted IP into the factory (this is the
        // 87bf42f contract — sibling regression in PollOrchestratorHostnameBackfillTests).
        Assert.NotNull(capturedIps);
        Assert.Contains(existingIp, capturedIps!);

        // Decisive assertion — the merge step at PollOrchestrator.cs ~247-271 MUST hydrate
        // s.IPAddress from the persisted row (since neither ip-neigh nor sweep filled it)
        // BEFORE the dnsMap lookup, so the resolved FQDN reaches s.Hostname and survives into
        // UpsertCycleAsync. Pre-fix code without the persisted-IP fallback structurally cannot
        // satisfy this assertion: s.IPAddress stays null, dnsMap lookup misses, Hostname stays
        // null on the probe POCO, UpsertCycleAsync coalesces with the existing (null) row.
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
