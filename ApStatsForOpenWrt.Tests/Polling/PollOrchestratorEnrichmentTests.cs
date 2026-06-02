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
        TimeProvider? timeProvider = null,
        IForceEnrichmentService? forceEnrichment = null)
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
            timeProvider ?? TimeProvider.System,
            forceEnrichment ?? new ForceEnrichmentService());
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

    [Fact]
    public async Task Toggle_RandomMac_PersistsAcrossPollCycle()
    {
        // DASH-23 (Phase 10 Plan 10-04 Task 3) — operator override of IsRandomMac via
        // IStationRepo.SetIsRandomMacAsync survives a subsequent PollOrchestrator cycle.
        // Proves end-to-end that Plan 10-01's UPDATE-branch user-controlled-fields exclusion
        // (ORCH-02 + ORCH-03) does NOT recompute or clobber the operator's choice.
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: DateTimeOffset.UtcNow);
        // MAC byte 0 = 0x02 → U/L bit set → IsRandomMac=true on INSERT.
        var mac = $"02:11:22:33:44:{Random.Shared.Next(0, 255):X2}";

        var orch1 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = mac, Bssid = bssid, RSS = -50, LastPollDate = DateTime.UtcNow }));
        await orch1.RunCycleAsync(ct);

        // Baseline — INSERT branch flagged it true.
        await using (var verify1 = _fixture.CreateContext())
        {
            var row = await verify1.WLANStations.AsNoTracking().FirstAsync(s => s.MacAddress == mac, ct);
            Assert.True(row.IsRandomMac);
        }

        // Operator toggles via the Stations/Detail.razor surface — IStationRepo.SetIsRandomMacAsync.
        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var stationRepo = new StationRepo(dpFactory);
        await stationRepo.SetIsRandomMacAsync(mac, false, ct);

        // Cycle 2 — orchestrator re-upserts the same MAC. UPDATE branch must preserve the override.
        var orch2 = BuildOrchestrator(ProbeForNode(nodeId, bssid,
            new WLANStation { MacAddress = mac, Bssid = bssid, RSS = -45, LastPollDate = DateTime.UtcNow }));
        await orch2.RunCycleAsync(ct);

        await using var verify2 = _fixture.CreateContext();
        var after = await verify2.WLANStations.AsNoTracking().FirstAsync(s => s.MacAddress == mac, ct);
        Assert.False(after.IsRandomMac);    // operator's choice preserved across the cycle
        Assert.Equal(-45, after.RSS);       // telemetry was updated normally
    }

    [Fact]
    public async Task EnrichmentFactory_Receives_NodeHostname_AsPeerIp_VerbatimFromOrchestrator()
    {
        // QUICK 260510-kjt regression: locks the upstream half of the
        // SubnetSweepProbe-receives-DNS-hostname bug. The composition root
        // (Program.cs enrichmentFactory) is responsible for hostname->IPv4
        // resolution AFTER ConnectAsync, which depends on this contract:
        // the orchestrator hands node.Hostname unchanged into the factory's
        // third positional arg (peerIp). If this contract ever drifts, the
        // composition-root resolution step has nothing to bind against.
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: null);

        // Override the seeded GUID hostname with an explicit DNS-style name
        // (matches the operator-reported defect: "wifi-ap1", "wifi-ap2").
        const string dnsHostname = "wifi-ap-test-quick260510kjt";
        await using (var dbWrite = _fixture.CreateContext())
        {
            await dbWrite.WiFiNodes.Where(n => n.Id == nodeId)
                .ExecuteUpdateAsync(u => u.SetProperty(n => n.Hostname, dnsHostname), ct);
        }

        string? capturedPeerIp = null;
        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> capturingFactory =
            (n, ips, peerIp, c) =>
            {
                capturedPeerIp = peerIp;
                return Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(
                    n.Id,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    new Dictionary<string, string?>(StringComparer.Ordinal)));
            };

        var mac = $"CA:01:02:03:04:{Random.Shared.Next(0, 255):X2}";
        var orch = BuildOrchestrator(
            ProbeForNode(nodeId, bssid,
                new WLANStation { MacAddress = mac, Bssid = bssid, LastPollDate = DateTime.UtcNow }),
            enrichmentFactory: capturingFactory);
        await orch.RunCycleAsync(ct);

        // Contract: orchestrator hands node.Hostname into the factory verbatim.
        // The composition-root factory in Program.cs then performs DNS resolution
        // (post-ConnectAsync) before invoking SubnetSweepProbe.PollAsync.
        Assert.Equal(dnsHostname, capturedPeerIp);
    }

    // ─── QUICK 260511-ma0: delete-then-rebid one-cycle hostname convergence ───────────────────
    // Three regression tests that pin the sweep-then-DNS sequencing introduced in the
    // enrichmentFactory closure (Program.cs:162-253). Test 1 is the end-to-end "operator UI
    // sees both fields in one cycle" outcome through the orchestrator. Tests 2 + 3 drive the
    // closure-body logic directly via EnrichmentFactoryClosure.BuildMergedDnsMapAsync — the
    // static helper extracted to keep the merge logic unit-testable without rebuilding a
    // ServiceProvider or instantiating SSH session DI plumbing.

    [Fact]
    public async Task RunCycle_DeleteThenRebid_PersistsIpAndHostname_InOneCycle()
    {
        // QUICK 260511-ma0 — Test 1: end-to-end convergence assertion. The factory script
        // returns a complete delta (sweep MAC→IP + dns IP→hostname both populated). The
        // orchestrator's merge step must persist BOTH IPAddress AND Hostname for the rebid
        // MAC after ONE RunCycleAsync invocation.
        var ct = TestContext.Current.CancellationToken;
        // LastEnrichmentRefreshAt = null → trigger fires unconditionally on first cycle
        // (cold-start arm of EnrichmentRefreshTrigger).
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: null);
        var mac = "AA:BB:CC:DD:EE:F0";
        var sweepIp = "192.168.42.10";
        var resolvedHostname = "rebid-laptop.lan";

        IReadOnlyList<string>? capturedIpsForReverseDns = null;
        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> factory =
            (n, ips, peerIp, c) =>
            {
                capturedIpsForReverseDns = ips;
                // Factory simulates the post-fix closure behavior: sweep discovered the IP,
                // and the factory's INTERNAL second DNS pass resolved its hostname. The
                // returned delta therefore carries BOTH (sweep MAC→IP AND dns IP→hostname).
                return Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(
                    n.Id,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [mac] = sweepIp,
                    },
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        [sweepIp] = resolvedHostname,
                    }));
            };

        // Probe shape: iwinfo reports the MAC but IPAddress=null (cache-cold post-delete).
        var orch = BuildOrchestrator(
            ProbeForNode(nodeId, bssid,
                new WLANStation { MacAddress = mac, Bssid = bssid, IPAddress = null, LastPollDate = DateTime.UtcNow }),
            enrichmentFactory: factory);
        await orch.RunCycleAsync(ct);

        // Sanity: orchestrator handed an EMPTY ipsForReverseDns list (the bug-trigger setup —
        // post-delete no existing row, probe IP=null → orchestrator has nothing to pre-supply).
        Assert.NotNull(capturedIpsForReverseDns);
        Assert.Empty(capturedIpsForReverseDns!);

        await using var verify = _fixture.CreateContext();
        var row = await verify.WLANStations.AsNoTracking().FirstAsync(s => s.MacAddress == mac, ct);
        Assert.Equal(sweepIp, row.IPAddress);             // ORCH-04 merge filled IP from sweepMap
        Assert.Equal(resolvedHostname, row.Hostname);     // ORCH-05 merge filled Hostname from ReverseDnsResolved
    }

    [Fact]
    public async Task EnrichmentFactoryClosure_BuildMergedDnsMap_SteadyState_NoSweepOnlyIps_IssuesOneDnsBatch()
    {
        // QUICK 260511-ma0 — Test 2 Sub-case A (steady-state): when sweepMap.Values is a
        // subset of ipsForReverseDns, the helper MUST NOT issue a second DNS PollAsync. The
        // counting FakeSshSession proves "exactly ONE RunCommandAsync issued" — i.e., one
        // batched-nslookup SSH round-trip for the caller-supplied IPs. No second-pass RTT.
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        // Stub batched-nslookup output for the FIRST (and only expected) DNS dispatch.
        Directory.CreateDirectory(fixtureDir);
        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-batched-steady.txt"),
            "==10.0.0.5==\nName:      host5.lan\nAddress 1: 10.0.0.5 host5.lan\n" +
            "==10.0.0.6==\nName:      host6.lan\nAddress 1: 10.0.0.6 host6.lan\n");

        var session = new FakeSshSession(fixtureDir);
        // The route-by-substring override matches the batched-loop signature. ALL nslookup
        // RunCommandAsync calls (first AND any unexpected second pass) would route here.
        session.CommandOverrides["nslookup -timeout=1 -retry=1"] = "nslookup-batched-steady";

        var dnsProbe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var node = new WiFiNode { Id = 1, Hostname = "test-ap", Username = "root", Password = "" };

        // sweepMap is a subset of ipsForReverseDns → sweepOnlyIps is empty → no second pass.
        var ipsForReverseDns = new[] { "10.0.0.5", "10.0.0.6" };
        var sweepMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AA:BB:CC:DD:EE:01"] = "10.0.0.5",     // already in ipsForReverseDns
        };

        var merged = await EnrichmentFactoryClosure.BuildMergedDnsMapAsync(
            dnsProbe, node, ipsForReverseDns, sweepMap, ct);

        // Steady-state invariant: exactly ONE batched-nslookup SSH command issued.
        Assert.Single(session.CommandsIssued);
        Assert.Contains("nslookup -timeout=1 -retry=1", session.CommandsIssued[0]);

        // Merged map carries the first-pass resolution verbatim.
        Assert.Equal("host5.lan", merged["10.0.0.5"]);
        Assert.Equal("host6.lan", merged["10.0.0.6"]);
    }

    [Fact]
    public async Task EnrichmentFactoryClosure_BuildMergedDnsMap_RebidScenario_IssuesSecondDnsPass_ForSweepOnlyIps()
    {
        // QUICK 260511-ma0 — Test 2 Sub-case B (rebid): when sweepMap.Values contains IPs
        // NOT in ipsForReverseDns, the helper MUST issue a SECOND DNS PollAsync against the
        // sweep-only IPs and merge dnsMap2 into dnsMap1. The returned merged map MUST contain
        // the sweep-discovered IP's resolved hostname.
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        Directory.CreateDirectory(fixtureDir);

        // First DNS pass receives an empty ipsForReverseDns (the rebid setup — orchestrator
        // had nothing to pre-supply). ReverseDnsProbe.PollAsync early-returns on empty list
        // WITHOUT issuing an SSH command — so we expect exactly ONE RunCommandAsync (the
        // second pass, against the sweep-only IP).
        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-batched-rebid.txt"),
            "==192.168.42.10==\nName:      rebid-laptop.lan\nAddress 1: 192.168.42.10 rebid-laptop.lan\n");

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["nslookup -timeout=1 -retry=1"] = "nslookup-batched-rebid";

        var dnsProbe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var node = new WiFiNode { Id = 1, Hostname = "test-ap", Username = "root", Password = "" };

        var ipsForReverseDns = Array.Empty<string>();    // bug-trigger setup: empty pre-supplied list
        var sweepMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AA:BB:CC:DD:EE:F0"] = "192.168.42.10",     // sweep-discovered → drives second pass
        };

        var merged = await EnrichmentFactoryClosure.BuildMergedDnsMapAsync(
            dnsProbe, node, ipsForReverseDns, sweepMap, ct);

        // Rebid invariant: the sweep-discovered IP's hostname appears in the merged map.
        Assert.True(merged.ContainsKey("192.168.42.10"), "sweep-only IP missing from merged map");
        Assert.Equal("rebid-laptop.lan", merged["192.168.42.10"]);

        // Empty first pass + non-empty second pass → exactly ONE batched-nslookup RunCommandAsync.
        // (PollAsync against empty list early-returns without an SSH call — see ReverseDnsProbe.cs:36-40.)
        Assert.Single(session.CommandsIssued);
        // Quick task 260515-rds: assert the new shape's per-IP redirect token. Pre-260515
        // the in-command sentinel `==192.168.42.10==` was the locator; now sentinels are
        // emitted at runtime by `for ... cat` and only the per-IP redirect target appears
        // in the command text.
        Assert.Contains("$TMPDIR/192.168.42.10", session.CommandsIssued[0]);
    }

    [Fact]
    public async Task EnrichmentFactoryClosure_BuildMergedDnsMap_SecondPassFails_PreservesFirstPassResultsAndDoesNotThrow()
    {
        // QUICK 260511-ma0 — Test 3: partial-failure preservation. When the FIRST DNS pass
        // succeeds and the SECOND DNS pass fails (linked-CTS-budget exhaustion → Failed/timeout),
        // the merged map MUST preserve dnsMap1 verbatim and the helper MUST NOT throw into
        // the caller. This matches the existing "DB-as-cache" philosophy at
        // PollOrchestrator.cs:237. The missing sweep-only-IP key is acceptable — ORCH-08
        // will retry on the next refresh tick via the existing-stations branch.
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        Directory.CreateDirectory(fixtureDir);

        // First DNS pass: parseable batched output for the known IP.
        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-batched-firstpass.txt"),
            "==10.0.0.5==\nName:      known-host.lan\nAddress 1: 10.0.0.5 known-host.lan\n");

        // Toggling session: the 2nd RunCommandAsync throws OperationCanceledException with
        // the host ct NOT cancelled — mirrors the linked-CTS-budget exhaustion shape that
        // ReverseDnsProbe surfaces as Failed(timeout) (ReverseDnsProbe.cs:62-69).
        var session = new TogglingFakeSshSession(fixtureDir, throwOnNthCall: 2);
        session.CommandOverrides["nslookup -timeout=1 -retry=1"] = "nslookup-batched-firstpass";

        var dnsProbe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var node = new WiFiNode { Id = 1, Hostname = "test-ap", Username = "root", Password = "" };

        var ipsForReverseDns = new[] { "10.0.0.5" };
        var sweepMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AA:BB:CC:DD:EE:F0"] = "192.168.42.10",     // sweep-only → drives second pass which will fail
        };

        // MUST NOT throw — partial-failure preservation is the contract.
        var merged = await EnrichmentFactoryClosure.BuildMergedDnsMapAsync(
            dnsProbe, node, ipsForReverseDns, sweepMap, ct);

        // dnsMap1 results preserved verbatim.
        Assert.Equal("known-host.lan", merged["10.0.0.5"]);
        // The sweep-only IP's entry is acceptable as null OR absent — both shapes leave
        // Hostname null at the orchestrator merge step, which is the documented next-tick-retry
        // path (ORCH-08 fires on the next refresh tick via the existing-stations branch).
        if (merged.TryGetValue("192.168.42.10", out var sweepHost))
        {
            Assert.Null(sweepHost);
        }
        // Two RunCommandAsync calls issued: first succeeded, second threw.
        Assert.Equal(2, session.CommandsIssued.Count);
    }

    // ── v0.11 ENRICH-01 (CONTEXT 14 §D-05/D-07) — Force-enrichment integration tests ──

    [Fact]
    public async Task ForceEnrichment_BypassesTimeBasedGate_AndEnrichesAllOnlineNodes()
    {
        // Seed two nodes; both have a recent LastEnrichmentRefreshAt so the time-based gate
        // would NOT fire naturally this cycle. With ForceEnrichment.Request() called BEFORE
        // RunCycleAsync, both nodes MUST have enrichmentFactory invoked.
        var ct = TestContext.Current.CancellationToken;
        var recent = DateTimeOffset.UtcNow;
        var (n1Id, _, n1Bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: recent);
        var (n2Id, _, n2Bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: recent);

        var enrichedNodeIds = new HashSet<int>();
        Task<EnrichmentDelta?> CapturingFactory(WiFiNode node, IReadOnlyList<string> _, string __, CancellationToken ___)
        {
            lock (enrichedNodeIds) { enrichedNodeIds.Add(node.Id); }
            return Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(
                node.Id,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string?>(StringComparer.Ordinal)));
        }

        // Probe-driver that recognizes BOTH seeded nodes so each yields a Success result.
        Task<NodeProbeResult> Probe(WiFiNode n, CancellationToken _)
        {
            string bssid = n.Id == n1Id ? n1Bssid : (n.Id == n2Id ? n2Bssid : "");
            if (string.IsNullOrEmpty(bssid))
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            var network = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { network },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null));
        }

        var forceEnrichment = new ForceEnrichmentService();
        forceEnrichment.Request();      // queue the flag BEFORE the cycle

        var orchestrator = BuildOrchestrator(
            probeAsync: Probe,
            enrichmentFactory: CapturingFactory,
            forceEnrichment: forceEnrichment);

        await orchestrator.RunCycleAsync(ct);

        Assert.Contains(n1Id, enrichedNodeIds);
        Assert.Contains(n2Id, enrichedNodeIds);
        // Flag consumed; subsequent cycle without Request() must NOT enrich (time-based gate
        // not eligible — LastEnrichmentRefreshAt was just bumped to ~now).
        enrichedNodeIds.Clear();
        await orchestrator.RunCycleAsync(ct);
        Assert.DoesNotContain(n1Id, enrichedNodeIds);
        Assert.DoesNotContain(n2Id, enrichedNodeIds);
    }

    [Fact]
    public async Task ForceEnrichment_RespectsPerNodeSubnetScanEnabled_OptOut()
    {
        // Seed two nodes: n1 with SubnetScanEnabled=true, n2 with SubnetScanEnabled=false.
        // Both have a recent LastEnrichmentRefreshAt. With ForceEnrichment.Request() called,
        // ONLY n1 is enriched. n2's operator opt-out is honored (CONTEXT 14 §D-05 planner
        // reading — "all online nodes the operator has not opted out of"; documented in
        // PLAN 14-02 truth #6).
        var ct = TestContext.Current.CancellationToken;
        var recent = DateTimeOffset.UtcNow;
        var (n1Id, _, n1Bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: recent, subnetScanEnabled: true);
        var (n2Id, _, n2Bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: recent, subnetScanEnabled: false);

        var enrichedNodeIds = new HashSet<int>();
        Task<EnrichmentDelta?> CapturingFactory(WiFiNode node, IReadOnlyList<string> _, string __, CancellationToken ___)
        {
            lock (enrichedNodeIds) { enrichedNodeIds.Add(node.Id); }
            return Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(
                node.Id,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string?>(StringComparer.Ordinal)));
        }

        Task<NodeProbeResult> Probe(WiFiNode n, CancellationToken _)
        {
            string bssid = n.Id == n1Id ? n1Bssid : (n.Id == n2Id ? n2Bssid : "");
            if (string.IsNullOrEmpty(bssid))
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            var network = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { network },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null));
        }

        var forceEnrichment = new ForceEnrichmentService();
        forceEnrichment.Request();

        var orchestrator = BuildOrchestrator(
            probeAsync: Probe,
            enrichmentFactory: CapturingFactory,
            forceEnrichment: forceEnrichment);

        await orchestrator.RunCycleAsync(ct);

        Assert.Contains(n1Id, enrichedNodeIds);
        Assert.DoesNotContain(n2Id, enrichedNodeIds);
    }

    [Fact]
    public async Task ForceEnrichment_CadenceFromCompletion_PreservedViaLastEnrichmentRefreshAt()
    {
        // After a force-flag cycle completes, the next *time-based* enrichment fires
        // RefreshIntervalSeconds after that completion. Seed one node with a stale
        // LastEnrichmentRefreshAt. Run a force-cycle. Verify node.LastEnrichmentRefreshAt
        // is advanced past the stale baseline — D-07 cadence-from-completion is preserved
        // via the existing UpdateLastEnrichmentRefreshAtAsync at PollOrchestrator.cs:284.
        var ct = TestContext.Current.CancellationToken;
        var stale = DateTimeOffset.UtcNow.AddHours(-2);
        var (nId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct, lastEnrichmentRefreshAt: stale);

        var forceEnrichment = new ForceEnrichmentService();
        forceEnrichment.Request();

        var fixedNow = DateTimeOffset.UtcNow;
        var orchestrator = BuildOrchestrator(
            probeAsync: ProbeForNode(nId, bssid),
            enrichmentFactory: (n, _, _, _) => Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(
                n.Id,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string?>(StringComparer.Ordinal))),
            forceEnrichment: forceEnrichment,
            timeProvider: new FakeTimeProvider(fixedNow));

        await orchestrator.RunCycleAsync(ct);

        await using var db = _fixture.CreateContext();
        var refreshed = await db.WiFiNodes.FirstAsync(n => n.Id == nId, ct);
        Assert.NotNull(refreshed.LastEnrichmentRefreshAt);
        Assert.True(refreshed.LastEnrichmentRefreshAt!.Value > stale,
            "LastEnrichmentRefreshAt should advance past the pre-force stale value");
        // The persisted value should match the fake clock (within 1 second of round-tripping).
        Assert.True((refreshed.LastEnrichmentRefreshAt.Value - fixedNow).Duration() < TimeSpan.FromSeconds(1),
            "LastEnrichmentRefreshAt should be ~fixedNow under the fake clock — D-07 cadence-from-completion.");
    }

    /// <summary>
    /// QUICK 260511-ma0 helper. Behaves like FakeSshSession except that the Nth
    /// <see cref="ISshSession.RunCommandAsync"/> call throws <see cref="OperationCanceledException"/>
    /// without the host CancellationToken being cancelled — mirrors the linked-CTS-budget
    /// exhaustion shape ReverseDnsProbe surfaces as <c>Failed(timeout)</c>.
    /// </summary>
    private sealed class TogglingFakeSshSession(string fixtureDir, int throwOnNthCall) : ApStatsForOpenWrt.Services.Polling.Ssh.ISshSession
    {
        private readonly FakeSshSession _inner = new(fixtureDir);
        private int _callCount;
        private readonly int _throwOnNthCall = throwOnNthCall;

        public List<string> CommandsIssued => _inner.CommandsIssued;
        public Dictionary<string, string> CommandOverrides => _inner.CommandOverrides;

        public Task ConnectAsync(WiFiNode node, string plaintextPassword, string? expectedFingerprint,
            Action<string>? onHostKeyAccepted, CancellationToken ct)
            => _inner.ConnectAsync(node, plaintextPassword, expectedFingerprint, onHostKeyAccepted, ct);

        public Task<string> RunCommandAsync(string command, CancellationToken ct)
        {
            _callCount++;
            if (_callCount == _throwOnNthCall)
            {
                _inner.CommandsIssued.Add(command);
                throw new OperationCanceledException("Simulated linked-CTS timeout on Nth call.");
            }
            return _inner.RunCommandAsync(command, ct);
        }

        public Task<(int ExitCode, string StdOut, string StdErr)> RunCommandWithExitCodeAsync(string command, CancellationToken ct)
            => _inner.RunCommandWithExitCodeAsync(command, ct);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
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
