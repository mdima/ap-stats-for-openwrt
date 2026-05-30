using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Enrichment;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Polling.Exceptions;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Polling;

/// <summary>
/// PollOrchestrator coordinator-transaction tests. Closes:
/// SC1 (one-cycle / one-transaction / mark-Offline-then-upsert),
/// CR-03 (D-08 dedup last-wins-on-RSS),
/// D-02 (first-connect TOFU persisted INSIDE coordinator tx),
/// D-03 (mismatch keeps node Offline; no fingerprint write),
/// D-04 (per-node failure does not roll back survivors),
/// POLL-13 (missing IP — station stays Online with null IP),
/// SC2 / B1 LOCK (fan-out across many cycles produces no SqliteException 'database is locked'),
/// B2 / POLL-03 (per-cycle fan-out runs nodes concurrently).
/// </summary>
public sealed class PollOrchestratorTests(SqliteInMemoryFixture fixture) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    /// <summary>Captured-entries logger for log-assertion tests (D-02, D-03, D-08 templates).</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IEnumerable<string> Messages => Entries.Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, exception), exception));
    }

    private PollOrchestrator BuildOrchestrator(
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync,
        ILogger<PollOrchestrator>? logger = null,
        int pollingIntervalSeconds = 30)
    {
        var dpFactory = new TestDbContextFactory(_fixture.Options);
        return new PollOrchestrator(
            dpFactory,
            new NodeRepo(dpFactory),
            new NetworkRepo(dpFactory),
            new StationRepo(dpFactory),
            probeAsync,
            new PollNotifier(),
            logger ?? NullLogger<PollOrchestrator>.Instance,
            new StubSettings(pollingIntervalSeconds),
            (_, _, _, _) => Task.FromResult<EnrichmentDelta?>(null),     // Phase 10 default: no enrichment this cycle
            TimeProvider.System,
            new ForceEnrichmentService());                                // 14-02 ENRICH-01: no force-flag in default test path
    }

    private async Task<(int nodeId, int netId, string netBssid)> SeedNodeWithOneNetworkAsync(CancellationToken ct, string? hostKeyFingerprint = null)
    {
        var bssid = $"FF:00:00:00:{Random.Shared.Next(0, 255):X2}:{Random.Shared.Next(0, 255):X2}";
        await using var db = _fixture.CreateContext();
        var node = new WiFiNode
        {
            Hostname = $"node-{Guid.NewGuid():N}",
            Username = "root",
            Password = "",
            HostKeyFingerprint = hostKeyFingerprint
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

    [Fact]
    public async Task SC1_OneCycle_OneTransaction_MarkAllOfflineThenUpsertOnline()
    {
        var ct = TestContext.Current.CancellationToken;

        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct);

        var staleMac = $"BB:01:02:03:04:{Random.Shared.Next(0, 255):X2}";
        await using (var seed = _fixture.CreateContext())
        {
            seed.WLANStations.Add(new WLANStation
            {
                MacAddress = staleMac,
                WiFiNetworkId = netId,
                Status = Status.Online,
                LastPollDate = DateTime.UtcNow.AddMinutes(-5)
            });
            await seed.SaveChangesAsync(ct);
        }

        var freshMac = $"AA:01:02:03:04:{Random.Shared.Next(0, 255):X2}";
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            // IClassFixture-shared DB — only this test's node should produce live data.
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));

            var network = new WiFiNetwork
            {
                WiFiNodeId = n.Id,
                MacAddress = bssid,
                SSID = "Net"
            };
            var station = new WLANStation
            {
                MacAddress = freshMac,
                Bssid = bssid,
                RSS = -50,
                LastPollDate = DateTime.UtcNow
            };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { network },
                Stations: new[] { station },
                NewlyTrustedFingerprint: null));
        };

        var orch = BuildOrchestrator(probeAsync);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var node2 = await verify.WiFiNodes.FirstAsync(n => n.Id == nodeId, ct);
        Assert.Equal(Status.Online, node2.Status);
        Assert.True(node2.LastPollDate > DateTime.UtcNow.AddSeconds(-30));

        var stale = await verify.WLANStations.FirstAsync(s => s.MacAddress == staleMac, ct);
        Assert.Equal(Status.Offline, stale.Status);

        var fresh = await verify.WLANStations.FirstAsync(s => s.MacAddress == freshMac, ct);
        Assert.Equal(Status.Online, fresh.Status);
        Assert.Equal(netId, fresh.WiFiNetworkId);
    }

    [Fact]
    public async Task D02_FirstConnectTOFU_PersistsFingerprintInsideCoordinatorTransaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct, hostKeyFingerprint: null);

        // Test-unique fingerprint so the log assertion can isolate THIS test's TOFU event
        // from any other test's parallel TOFU activity in the IClassFixture-shared DB.
        var capturedFp = $"FAKE-FP-{Guid.NewGuid():N}";
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: capturedFp));
        };

        var capLogger = new CapturingLogger<PollOrchestrator>();
        var orch = BuildOrchestrator(probeAsync, capLogger);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var node = await verify.WiFiNodes.FirstAsync(n => n.Id == nodeId, ct);
        Assert.Equal(capturedFp, node.HostKeyFingerprint);

        // Exactly one Information log line matching the D-02 TOFU template for THIS node's fingerprint.
        // (IClassFixture is shared across tests; filter on the test-unique fingerprint to isolate.)
        var trustLogs = capLogger.Entries
            .Where(e => e.Level == LogLevel.Information
                     && e.Message.Contains("trusting host key", StringComparison.Ordinal)
                     && e.Message.Contains(capturedFp, StringComparison.Ordinal))
            .ToList();
        Assert.Single(trustLogs);
    }

    [Fact]
    public async Task D03_HostKeyMismatch_NodeStaysOffline_NoFingerprintWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        // Test-unique trusted fingerprint isolates this test's mismatch warning in the
        // shared-fixture log capture from any parallel test producing similar lines.
        var trustedFp = $"ABC-{Guid.NewGuid():N}";
        var actualFp = $"XYZ-{Guid.NewGuid():N}";
        var (nodeId, _, _) = await SeedNodeWithOneNetworkAsync(ct, hostKeyFingerprint: trustedFp);

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            throw new HostKeyMismatchException(trustedFp, actualFp);
        };

        var capLogger = new CapturingLogger<PollOrchestrator>();
        var orch = BuildOrchestrator(probeAsync, capLogger);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var node = await verify.WiFiNodes.FirstAsync(n => n.Id == nodeId, ct);
        Assert.Equal(trustedFp, node.HostKeyFingerprint);     // unchanged
        Assert.Equal(Status.Offline, node.Status);            // mark-all-Offline + no upsert pass

        var warnLogs = capLogger.Entries
            .Where(e => e.Level == LogLevel.Warning
                     && e.Message.Contains("host key changed", StringComparison.Ordinal)
                     && e.Message.Contains(trustedFp, StringComparison.Ordinal)
                     && e.Message.Contains(actualFp, StringComparison.Ordinal))
            .ToList();
        Assert.Single(warnLogs);
    }

    [Fact]
    public async Task CR03_DuplicateMacAcrossWlans_DedupedByHighestRSS()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed one node with TWO networks (different BSSIDs).
        int nodeId, net1Id, net2Id;
        var bssid1 = $"FF:01:00:00:00:{Random.Shared.Next(0, 255):X2}";
        var bssid2 = $"FF:02:00:00:00:{Random.Shared.Next(0, 255):X2}";
        await using (var seed = _fixture.CreateContext())
        {
            var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = "" };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;

            var n1 = new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssid1, SSID = "net-1" };
            var n2 = new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssid2, SSID = "net-2" };
            seed.WiFiNetworks.AddRange(n1, n2);
            await seed.SaveChangesAsync(ct);
            net1Id = n1.Id;
            net2Id = n2.Id;
        }

        var sharedMac = $"AA:CC:DD:EE:FF:{Random.Shared.Next(0, 255):X2}";

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            var nets = new[]
            {
                new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid1, SSID = "net-1" },
                new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid2, SSID = "net-2" }
            };
            var stations = new[]
            {
                new WLANStation { MacAddress = sharedMac, Bssid = bssid1, RSS = -55, LastPollDate = DateTime.UtcNow },
                new WLANStation { MacAddress = sharedMac, Bssid = bssid2, RSS = -40, LastPollDate = DateTime.UtcNow }
            };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: nets,
                Stations: stations,
                NewlyTrustedFingerprint: null));
        };

        var capLogger = new CapturingLogger<PollOrchestrator>();
        var orch = BuildOrchestrator(probeAsync, capLogger);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var rows = await verify.WLANStations.AsNoTracking()
            .Where(s => s.MacAddress == sharedMac).ToListAsync(ct);
        var row = Assert.Single(rows);
        Assert.Equal(net2Id, row.WiFiNetworkId);     // higher RSS -> bssid2 -> net2
        Assert.Equal(-40, row.RSS);

        // D-08 dedup log line emitted (Information level) — filter by THIS test's MAC.
        var dedupLogs = capLogger.Entries
            .Where(e => e.Level == LogLevel.Information
                     && e.Message.Contains("reported on", StringComparison.Ordinal)
                     && e.Message.Contains("WLANs in cycle", StringComparison.Ordinal)
                     && e.Message.Contains(sharedMac, StringComparison.Ordinal))
            .ToList();
        Assert.Single(dedupLogs);
    }

    [Fact]
    public async Task MissingIP_StationStaysOnline()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct);
        var mac = $"BB:11:22:33:44:{Random.Shared.Next(0, 255):X2}";

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            var net = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            var station = new WLANStation
            {
                MacAddress = mac,
                Bssid = bssid,
                IPAddress = null,           // POLL-13 — ip neigh missed this client
                RSS = -60,
                LastPollDate = DateTime.UtcNow
            };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { net },
                Stations: new[] { station },
                NewlyTrustedFingerprint: null));
        };

        var orch = BuildOrchestrator(probeAsync);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var row = await verify.WLANStations.AsNoTracking().FirstAsync(s => s.MacAddress == mac, ct);
        Assert.Null(row.IPAddress);
        Assert.Equal(Status.Online, row.Status);
    }

    [Fact]
    public async Task PerNodeFailure_OtherNodesStillCommitted()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed two nodes, each with their own network.
        int goodNodeId, goodNetId, badNodeId;
        var goodBssid = $"FF:AA:01:00:00:{Random.Shared.Next(0, 255):X2}";
        var badBssid = $"FF:AA:02:00:00:{Random.Shared.Next(0, 255):X2}";
        await using (var seed = _fixture.CreateContext())
        {
            var good = new WiFiNode { Hostname = $"good-{Guid.NewGuid():N}", Username = "root", Password = "" };
            var bad = new WiFiNode { Hostname = $"bad-{Guid.NewGuid():N}", Username = "root", Password = "" };
            seed.WiFiNodes.AddRange(good, bad);
            await seed.SaveChangesAsync(ct);
            goodNodeId = good.Id;
            badNodeId = bad.Id;

            var goodNet = new WiFiNetwork { WiFiNodeId = goodNodeId, MacAddress = goodBssid, SSID = "good-net" };
            var badNet = new WiFiNetwork { WiFiNodeId = badNodeId, MacAddress = badBssid, SSID = "bad-net" };
            seed.WiFiNetworks.AddRange(goodNet, badNet);
            await seed.SaveChangesAsync(ct);
            goodNetId = goodNet.Id;
        }

        var goodMac = $"CC:11:22:33:44:{Random.Shared.Next(0, 255):X2}";
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id == badNodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "auth-failed"));
            if (n.Id != goodNodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));

            var net = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = goodBssid, SSID = "good-net" };
            var station = new WLANStation
            {
                MacAddress = goodMac,
                Bssid = goodBssid,
                RSS = -45,
                LastPollDate = DateTime.UtcNow
            };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { net },
                Stations: new[] { station },
                NewlyTrustedFingerprint: null));
        };

        var orch = BuildOrchestrator(probeAsync);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var goodNode = await verify.WiFiNodes.FirstAsync(n => n.Id == goodNodeId, ct);
        Assert.Equal(Status.Online, goodNode.Status);

        var badNode = await verify.WiFiNodes.FirstAsync(n => n.Id == badNodeId, ct);
        Assert.Equal(Status.Offline, badNode.Status);     // mark-all-Offline ran; no upsert pass for failed node

        var goodStation = await verify.WLANStations.FirstAsync(s => s.MacAddress == goodMac, ct);
        Assert.Equal(Status.Online, goodStation.Status);
        Assert.Equal(goodNetId, goodStation.WiFiNetworkId);
    }

    // Phase 7 LIVE-UAT row 5.2 escalation: operator reports stations stay Stale despite
    // re-association. Probe path is healthy per logs (29 stations, 0 failed). MACs are
    // uppercase, no duplicates. Two new tests pin down whether the orchestrator/repo
    // pair correctly recovers a previously-stale station and handles AP roaming.

    [Fact]
    public async Task StaleStation_Recovers_ToOnline_WhenSeenAgainNextCycle()
    {
        // Pre-state: a station X exists with Status=Offline + LastPollDate=10 minutes ago
        // (the in-DB representation of "stale"). Probe reports the SAME MAC under the same
        // WiFiNetworkId. Expected post-state: Status=Online and LastPollDate within seconds
        // of UtcNow. Failure here would prove a bug in StationRepo.UpsertCycleAsync that
        // explains the LIVE-UAT symptom.
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct);
        var staleMac = $"CC:01:02:03:04:{Random.Shared.Next(0, 255):X2}";

        await using (var seed = _fixture.CreateContext())
        {
            seed.WLANStations.Add(new WLANStation
            {
                MacAddress = staleMac,
                WiFiNetworkId = netId,
                Status = Status.Offline,                                  // already marked offline (post mark-all-offline)
                LastPollDate = DateTime.UtcNow.AddMinutes(-10),           // long enough ago that IsStale would be true on the dashboard
            });
            await seed.SaveChangesAsync(ct);
        }

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));

            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: new[] { new WLANStation
                {
                    MacAddress = staleMac,                                // SAME MAC as the offline pre-state row
                    Bssid = bssid,
                    RSS = -55,
                    LastPollDate = DateTime.UtcNow,
                }},
                NewlyTrustedFingerprint: null));
        };

        var orch = BuildOrchestrator(probeAsync);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var recovered = await verify.WLANStations.FirstAsync(s => s.MacAddress == staleMac, ct);
        Assert.Equal(Status.Online, recovered.Status);
        Assert.True(recovered.LastPollDate > DateTime.UtcNow.AddSeconds(-30),
            $"LastPollDate must be recent after re-association. Was: {recovered.LastPollDate:o}");
        Assert.Equal(netId, recovered.WiFiNetworkId);

        // No duplicate row got created — exactly one row for this MAC.
        var dupCount = await verify.WLANStations.CountAsync(s => s.MacAddress == staleMac, ct);
        Assert.Equal(1, dupCount);
    }

    [Fact]
    public async Task RoamingStation_UpdatesWiFiNetworkId_AndStaysOnline_AcrossAPSwitch()
    {
        // Operator clarification: a station can switch APs autonomously while keeping the
        // same MAC. Test scenario: station X is associated to AP1 on cycle 1, then in cycle 2
        // it's reported by AP2 and NOT by AP1. Expected: the row's WiFiNetworkId updates to
        // AP2's network and Status stays Online. Failure here would mean the dashboard
        // would render stations under the wrong AP / SSID after roaming, or worse, drop them.
        var ct = TestContext.Current.CancellationToken;
        var (nodeId1, netId1, bssid1) = await SeedNodeWithOneNetworkAsync(ct);
        var (nodeId2, netId2, bssid2) = await SeedNodeWithOneNetworkAsync(ct);
        var roamMac = $"DD:01:02:03:04:{Random.Shared.Next(0, 255):X2}";

        // Cycle 1: AP1 sees the station. AP2 sees nothing.
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeCycle1 = (n, _) =>
        {
            if (n.Id == nodeId1)
            {
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                    NodeId: n.Id,
                    Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid1, SSID = "Net" } },
                    Stations: new[] { new WLANStation { MacAddress = roamMac, Bssid = bssid1, RSS = -45, LastPollDate = DateTime.UtcNow } },
                    NewlyTrustedFingerprint: null));
            }
            if (n.Id == nodeId2)
            {
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                    NodeId: n.Id,
                    Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid2, SSID = "Net" } },
                    Stations: Array.Empty<WLANStation>(),
                    NewlyTrustedFingerprint: null));
            }
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
        };

        var orch1 = BuildOrchestrator(probeCycle1);
        await orch1.RunCycleAsync(ct);

        await using (var verify1 = _fixture.CreateContext())
        {
            var afterCycle1 = await verify1.WLANStations.FirstAsync(s => s.MacAddress == roamMac, ct);
            Assert.Equal(Status.Online, afterCycle1.Status);
            Assert.Equal(netId1, afterCycle1.WiFiNetworkId);
        }

        // Cycle 2: station roamed. AP2 sees it. AP1 does not.
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeCycle2 = (n, _) =>
        {
            if (n.Id == nodeId1)
            {
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                    NodeId: n.Id,
                    Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid1, SSID = "Net" } },
                    Stations: Array.Empty<WLANStation>(),
                    NewlyTrustedFingerprint: null));
            }
            if (n.Id == nodeId2)
            {
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                    NodeId: n.Id,
                    Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid2, SSID = "Net" } },
                    Stations: new[] { new WLANStation { MacAddress = roamMac, Bssid = bssid2, RSS = -60, LastPollDate = DateTime.UtcNow } },
                    NewlyTrustedFingerprint: null));
            }
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
        };

        var orch2 = BuildOrchestrator(probeCycle2);
        await orch2.RunCycleAsync(ct);

        await using var verify2 = _fixture.CreateContext();
        var roamed = await verify2.WLANStations.FirstAsync(s => s.MacAddress == roamMac, ct);
        Assert.Equal(Status.Online, roamed.Status);
        Assert.Equal(netId2, roamed.WiFiNetworkId);   // FK switched to AP2
        Assert.Equal(1, await verify2.WLANStations.CountAsync(s => s.MacAddress == roamMac, ct));
    }

    [Fact]
    public async Task SC2_FanOut_NoSqliteLock()
    {
        // B1 LOCK / SC2 — across N=20 cycles with >=3 parallel probes returning Success per cycle,
        // assert NO Microsoft.Data.Sqlite.SqliteException with "database is locked".
        var ct = TestContext.Current.CancellationToken;

        // Seed 3 nodes, each with one network.
        var nodeBssids = new List<(int NodeId, int NetId, string Bssid)>();
        await using (var seed = _fixture.CreateContext())
        {
            for (int i = 0; i < 3; i++)
            {
                var node = new WiFiNode { Hostname = $"fan-{Guid.NewGuid():N}", Username = "root", Password = "" };
                seed.WiFiNodes.Add(node);
                await seed.SaveChangesAsync(ct);
                var bssid = $"FE:00:{i:X2}:{Random.Shared.Next(0, 255):X2}:{Random.Shared.Next(0, 255):X2}:{Random.Shared.Next(0, 255):X2}";
                var net = new WiFiNetwork { WiFiNodeId = node.Id, MacAddress = bssid, SSID = $"fan-net-{i}" };
                seed.WiFiNetworks.Add(net);
                await seed.SaveChangesAsync(ct);
                nodeBssids.Add((node.Id, net.Id, bssid));
            }
        }

        var bssidByNode = nodeBssids.ToDictionary(t => t.NodeId, t => t.Bssid);

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = async (n, cct) =>
        {
            // IClassFixture-shared DB — only this test's seeded nodes return Success;
            // other tests' nodes return Failed (no-op for this assertion).
            if (!bssidByNode.TryGetValue(n.Id, out var bssid))
                return new NodeProbeResult.Failed(n.Id, "not-this-test");

            // Encourage interleaving so SQLite single-writer pressure is exercised.
            await Task.Delay(Random.Shared.Next(5, 25), cct);
            var net = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "fan-net" };
            var station = new WLANStation
            {
                MacAddress = $"DA:{n.Id & 0xFF:X2}:00:00:00:{Random.Shared.Next(0, 255):X2}",
                Bssid = bssid,
                RSS = -50,
                LastPollDate = DateTime.UtcNow
            };
            return new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { net },
                Stations: new[] { station },
                NewlyTrustedFingerprint: null);
        };

        var orch = BuildOrchestrator(probeAsync);

        for (int cycle = 0; cycle < 20; cycle++)
        {
            // If RunCycleAsync ever throws SqliteException 'database is locked', the test fails immediately.
            await orch.RunCycleAsync(ct);
        }

        // If we got here without exception, B1 LOCK / SC2 holds.
        Assert.True(true);
    }

    [Fact]
    public async Task GenericException_DuringProbe_TranslatedToFailedAndLogged()
    {
        // Covers RunNodeProbeAsync's generic catch (Exception ex) path: a non-HostKeyMismatch,
        // non-OperationCanceled exception logs Warning + returns NodeProbeResult.Failed("probe-failed").
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, _) = await SeedNodeWithOneNetworkAsync(ct);

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            throw new InvalidOperationException("synthetic-probe-failure");
        };

        var capLogger = new CapturingLogger<PollOrchestrator>();
        var orch = BuildOrchestrator(probeAsync, capLogger);
        await orch.RunCycleAsync(ct);

        // Node stays Offline (mark-all-Offline ran; probe failed → no upsert pass).
        await using var verify = _fixture.CreateContext();
        var node = await verify.WiFiNodes.FirstAsync(n => n.Id == nodeId, ct);
        Assert.Equal(Status.Offline, node.Status);

        // Warning log emitted from the generic catch.
        var probeFailLogs = capLogger.Entries
            .Where(e => e.Level == LogLevel.Warning
                     && e.Message.Contains("probe failed (top-level catch)", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(probeFailLogs);
    }

    // ── D-09 LastSuccessfulCycleUtc regression facts (Plan 06-02 Task 1) ──────
    // The /health writer in Plan 06-03 consumes this property. The contract:
    //  * fresh instance returns DateTime.MinValue (kind=Utc) — emits null in JSON.
    //  * after a successful RunCycleAsync (commit succeeded), the property reflects
    //    the same `now` the orchestrator embedded in the cycle's writes.
    //  * if the cycle throws before tx.CommitAsync, the property is NOT updated
    //    (write-after-commit invariant — mirrors the post-commit TOFU audit log).

    [Fact]
    public void LastSuccessfulCycleUtc_OnFreshInstance_IsDefault()
    {
        // D-09 cold-boot semantic — null/MinValue means "no successful cycle yet" (plan 06-03's
        // /health writer maps this to lastPollAge=null in the JSON response).
        var orch = BuildOrchestrator((n, _) => Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "stub")));

        Assert.Equal(default(DateTime), orch.LastSuccessfulCycleUtc);
        Assert.Equal(DateTimeKind.Utc, orch.LastSuccessfulCycleUtc.Kind);
    }

    [Fact]
    public async Task RunCycleAsync_AfterSuccessfulCommit_UpdatesLastSuccessfulCycleUtc()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct);

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            // IClassFixture-shared DB — only this test's node should produce live data.
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            var net = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { net },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null));
        };

        var orch = BuildOrchestrator(probeAsync);
        var before = orch.LastSuccessfulCycleUtc;
        var startedAt = DateTime.UtcNow;

        await orch.RunCycleAsync(ct);

        var after = orch.LastSuccessfulCycleUtc;
        Assert.NotEqual(before, after);
        Assert.Equal(DateTimeKind.Utc, after.Kind);
        Assert.True(after >= startedAt.AddSeconds(-1), $"after={after:O} should be >= startedAt-1s ({startedAt:O})");
        Assert.True(after <= DateTime.UtcNow.AddSeconds(1), $"after={after:O} should be <= UtcNow+1s");
    }

    [Fact]
    public async Task RunCycleAsync_OnFailure_DoesNotUpdateLastSuccessfulCycleUtc()
    {
        // D-09 write-after-commit invariant: if anything in the cycle throws before tx.CommitAsync
        // (and the catch block runs RollbackAsync), _ticks must NOT be advanced. We force a failure
        // by injecting a stub network repo whose UpsertManyAsync throws.
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct);

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            var net = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { net },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null));
        };

        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var orch = new PollOrchestrator(
            dpFactory,
            new NodeRepo(dpFactory),
            new ThrowingNetworkRepo(),
            new StationRepo(dpFactory),
            probeAsync,
            new PollNotifier(),
            NullLogger<PollOrchestrator>.Instance,
            new StubSettings(30),
            (_, _, _, _) => Task.FromResult<EnrichmentDelta?>(null),
            TimeProvider.System,
            new ForceEnrichmentService());

        var before = orch.LastSuccessfulCycleUtc;

        await Assert.ThrowsAsync<InvalidOperationException>(() => orch.RunCycleAsync(ct));

        Assert.Equal(before, orch.LastSuccessfulCycleUtc);   // _ticks stayed at the pre-cycle value
    }

    /// <summary>
    /// Test double for <see cref="INetworkRepo"/> that throws on the cycle-shaped UpsertManyAsync.
    /// Used by <see cref="RunCycleAsync_OnFailure_DoesNotUpdateLastSuccessfulCycleUtc"/> to force the
    /// orchestrator into the catch + RollbackAsync path BEFORE tx.CommitAsync runs. All other methods
    /// throw NotImplementedException — the orchestrator only invokes UpsertManyAsync on this seam
    /// during the failing cycle.
    /// </summary>
    private sealed class ThrowingNetworkRepo : INetworkRepo
    {
        public Task UpsertManyAsync(DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated UpsertManyAsync failure (Plan 06-02 D-09 regression test)");

        public Task UpsertManyAsync(int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<WiFiNetwork>> GetByNodeAsync(int wiFiNodeId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<WiFiNetwork?> GetByIdAsync(int id, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task MarkAbsentDisabledAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task DeleteAbsentAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task DeleteAbsentAsync(DatabaseContext db, int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> AnyEnabledAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task FanOut_Parallelism_RunsNodesConcurrently()
    {
        // B2 / POLL-03 LOCK — within one cycle, the 3 probeAsync invocations must enter
        // concurrently. Recorded entry-tick spread across the 3 probes < 50ms; total cycle
        // wall-clock < 500ms (sequential 3 × 200ms would be >= 600ms).
        var ct = TestContext.Current.CancellationToken;

        var nodeBssids = new List<(int NodeId, string Bssid)>();
        await using (var seed = _fixture.CreateContext())
        {
            for (int i = 0; i < 3; i++)
            {
                // Phase 10: seed LastEnrichmentRefreshAt = recent so the orchestrator's enrichment
                // trigger does NOT fire for these nodes — this test asserts probe fan-out concurrency
                // (POLL-03), not the enrichment cycle. Without this, ShouldRefresh returns true
                // (cold-start) and the orchestrator runs its enrichment per-node loop, blowing past
                // the 500ms wall-clock budget.
                var node = new WiFiNode { Hostname = $"par-{Guid.NewGuid():N}", Username = "root", Password = "", LastEnrichmentRefreshAt = DateTimeOffset.UtcNow };
                seed.WiFiNodes.Add(node);
                await seed.SaveChangesAsync(ct);
                var bssid = $"FD:00:{i:X2}:{Random.Shared.Next(0, 255):X2}:{Random.Shared.Next(0, 255):X2}:{Random.Shared.Next(0, 255):X2}";
                seed.WiFiNetworks.Add(new WiFiNetwork { WiFiNodeId = node.Id, MacAddress = bssid, SSID = $"par-net-{i}" });
                await seed.SaveChangesAsync(ct);
                nodeBssids.Add((node.Id, bssid));
            }
        }
        var bssidByNode = nodeBssids.ToDictionary(t => t.NodeId, t => t.Bssid);

        var entryTicks = new System.Collections.Concurrent.ConcurrentBag<long>();
        var sw = Stopwatch.StartNew();

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = async (n, cct) =>
        {
            // IClassFixture-shared DB — entry-tick measurement applies only to THIS test's 3 nodes.
            // Other tests' nodes (if any persist in the DB) return Failed without delaying.
            if (!bssidByNode.TryGetValue(n.Id, out var bssid))
                return new NodeProbeResult.Failed(n.Id, "not-this-test");

            entryTicks.Add(sw.ElapsedTicks);
            await Task.Delay(200, cct);
            var net = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "par-net" };
            return new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { net },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null);
        };

        var orch = BuildOrchestrator(probeAsync);

        var cycleSw = Stopwatch.StartNew();
        await orch.RunCycleAsync(ct);
        cycleSw.Stop();

        var ticks = entryTicks.OrderBy(t => t).ToList();
        Assert.Equal(3, ticks.Count);
        var spreadMs = TimeSpan.FromTicks(ticks[^1] - ticks[0]).TotalMilliseconds;
        Assert.True(spreadMs < 50, $"Probe entry spread should be < 50ms (concurrent fan-out); actual={spreadMs:F1}ms");
        Assert.True(cycleSw.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Total cycle wall-clock should be < 500ms (concurrent); actual={cycleSw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task MissedStation_PreservesTelemetry_DuringStaleWindow_FirstMissedCycle()
    {
        // Stale window: 1 missed cycle, elapsed < 2x interval. Telemetry MUST be preserved
        // (operator's mental model: badge = Stale, cells still show last-known signal/bitrate).
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct);
        var mac = $"AA:01:02:03:04:{Random.Shared.Next(0, 255):X2}";

        await using (var seed = _fixture.CreateContext())
        {
            seed.WLANStations.Add(new WLANStation
            {
                MacAddress = mac,
                WiFiNetworkId = netId,
                Status = Status.Offline,                                  // already-offline pre-state
                LastPollDate = DateTime.UtcNow.AddSeconds(-30),           // 1x interval ago — Stale window (< 2x = 60s)
                RSS = -55,
                TxBitrate = 72.0,
                RxBitrate = 65.0,
            });
            await seed.SaveChangesAsync(ct);
        }

        // Probe returns NO stations for this node (the missing station scenario).
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
            Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null));

        var orch = BuildOrchestrator(probeAsync, pollingIntervalSeconds: 30);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var row = await verify.WLANStations.FirstAsync(s => s.MacAddress == mac, ct);
        Assert.Equal(Status.Offline, row.Status);
        Assert.Equal(-55, row.RSS);                                       // PRESERVED
        Assert.Equal(72.0, row.TxBitrate);                                // PRESERVED
        Assert.Equal(65.0, row.RxBitrate);                                // PRESERVED
    }

    [Fact]
    public async Task MissedStation_ClearsTelemetry_AfterTwoMissedCycles_ButPreservesIdentity()
    {
        // Offline window: elapsed >= 2x. Telemetry MUST be cleared (badge = Offline, cells "—").
        // Identity columns (MAC, Hostname, Name, WiFiNetworkId) MUST be preserved.
        // Use 3x rather than exactly 2x to avoid boundary-condition ambiguity around the strict-less-than cutoff.
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct);
        var mac = $"AB:01:02:03:04:{Random.Shared.Next(0, 255):X2}";
        var seededHostname = "device-stale";
        var seededName     = "Operator-Tag";

        await using (var seed = _fixture.CreateContext())
        {
            seed.WLANStations.Add(new WLANStation
            {
                MacAddress = mac,
                WiFiNetworkId = netId,
                Hostname = seededHostname,
                Name = seededName,
                Status = Status.Offline,
                LastPollDate = DateTime.UtcNow.AddSeconds(-90),           // 3x interval ago — Offline window (>= 2x = 60s)
                RSS = -60,
                TxBitrate = 54.0,
                RxBitrate = 48.0,
            });
            await seed.SaveChangesAsync(ct);
        }

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
            Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null));

        var orch = BuildOrchestrator(probeAsync, pollingIntervalSeconds: 30);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var row = await verify.WLANStations.FirstAsync(s => s.MacAddress == mac, ct);
        // Telemetry CLEARED.
        Assert.Equal(Status.Offline, row.Status);
        Assert.Null(row.RSS);
        Assert.Equal(0.0, row.TxBitrate);
        Assert.Equal(0.0, row.RxBitrate);
        // Identity PRESERVED.
        Assert.Equal(mac, row.MacAddress);
        Assert.Equal(seededHostname, row.Hostname);
        Assert.Equal(seededName, row.Name);
        Assert.Equal(netId, row.WiFiNetworkId);
    }

    [Fact]
    public async Task RecoveringStation_RehydratesTelemetry_FromProbe_AfterClearedState()
    {
        // Regression pin: a station in cleared state (post-Offline-window) must rehydrate telemetry
        // when probed again. Guards against UpsertCycleAsync skipping rows whose RSS is null.
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct);
        var mac = $"AC:01:02:03:04:{Random.Shared.Next(0, 255):X2}";

        await using (var seed = _fixture.CreateContext())
        {
            seed.WLANStations.Add(new WLANStation
            {
                MacAddress = mac,
                WiFiNetworkId = netId,
                Status = Status.Offline,                                  // already cleared
                LastPollDate = DateTime.UtcNow.AddSeconds(-150),          // 5x interval ago (well past cutoff)
                RSS = null,
                TxBitrate = 0.0,
                RxBitrate = 0.0,
            });
            await seed.SaveChangesAsync(ct);
        }

        // Probe returns the station with FRESH telemetry.
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
            Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: new[] { new WLANStation
                {
                    MacAddress = mac,
                    Bssid = bssid,
                    RSS = -55,
                    TxBitrate = 72.0,
                    RxBitrate = 65.0,
                    LastPollDate = DateTime.UtcNow,
                }},
                NewlyTrustedFingerprint: null));

        var orch = BuildOrchestrator(probeAsync, pollingIntervalSeconds: 30);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var row = await verify.WLANStations.FirstAsync(s => s.MacAddress == mac, ct);
        Assert.Equal(Status.Online, row.Status);
        Assert.Equal(-55, row.RSS);
        Assert.Equal(72.0, row.TxBitrate);
        Assert.Equal(65.0, row.RxBitrate);
        Assert.Equal(netId, row.WiFiNetworkId);
    }

    // ── Quick-260506-005 / D-XX: per-cycle hard-delete of absent WiFiNetworks ──
    // Closes the BSSID-rotation accumulation defect (operator-confirmed: AP2 had 5x
    // DesertStorm rows on channel 11, one per historically-observed BSSID). The
    // PollOrchestrator now calls INetworkRepo.DeleteAbsentAsync per Success result,
    // AFTER UpsertManyAsync and BEFORE step 5's BSSID-resolution, inside the same
    // coordinator transaction (D-04). Per-success-only protects against transient
    // SSH failures wiping a node's WLAN set.

    [Fact]
    public async Task RunCycleAsync_DeletesAbsent_WiFiNetworks_ForSuccessfullyProbedNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seed one node with three networks; only two of them will be reported in the probe.
        int nodeId;
        var bssidA = $"FF:0A:00:00:00:{Random.Shared.Next(0, 255):X2}";
        var bssidB = $"FF:0A:00:01:00:{Random.Shared.Next(0, 255):X2}";
        var bssidC = $"FF:0A:00:02:00:{Random.Shared.Next(0, 255):X2}";
        await using (var seed = _fixture.CreateContext())
        {
            var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = "" };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;

            seed.WiFiNetworks.AddRange(
                new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssidA, SSID = "DesertStorm" },
                new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssidB, SSID = "DesertStorm" },
                new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssidC, SSID = "DesertStorm" });
            await seed.SaveChangesAsync(ct);
        }

        // Probe reports A and B; C has rotated away.
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[]
                {
                    new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssidA, SSID = "DesertStorm" },
                    new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssidB, SSID = "DesertStorm" },
                },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null));
        };

        var orch = BuildOrchestrator(probeAsync);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var rows = await verify.WiFiNetworks.AsNoTracking()
            .Where(n => n.WiFiNodeId == nodeId)
            .ToListAsync(ct);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.MacAddress == bssidA);
        Assert.Contains(rows, r => r.MacAddress == bssidB);
        Assert.DoesNotContain(rows, r => r.MacAddress == bssidC);
    }

    [Fact]
    public async Task RunCycleAsync_DoesNotDeleteAbsent_ForFailedProbeNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seed a node with two networks; configure the probe to FAIL for that node.
        int nodeId;
        var bssidA = $"FF:0B:00:00:00:{Random.Shared.Next(0, 255):X2}";
        var bssidB = $"FF:0B:00:01:00:{Random.Shared.Next(0, 255):X2}";
        await using (var seed = _fixture.CreateContext())
        {
            var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = "" };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;

            seed.WiFiNetworks.AddRange(
                new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssidA, SSID = "Net" },
                new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssidB, SSID = "Net" });
            await seed.SaveChangesAsync(ct);
        }

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            // ALL nodes return Failed for this test — including this test's own node.
            // The DeleteAbsentAsync pass MUST NOT run for failed nodes (transient outage protection).
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "auth-failed"));
        };

        var orch = BuildOrchestrator(probeAsync);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        var rows = await verify.WiFiNetworks.AsNoTracking()
            .Where(n => n.WiFiNodeId == nodeId)
            .ToListAsync(ct);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.MacAddress == bssidA);
        Assert.Contains(rows, r => r.MacAddress == bssidB);
    }

    [Fact]
    public async Task RunCycleAsync_StationOnDeletedNetwork_GetsReResolved_OrNulled()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seed one node with two networks (A, B), and two stations:
        //   stationX -> WiFiNetworkId points at A's row
        //   stationY -> WiFiNetworkId points at B's row
        // Probe reports ONLY A (so B's network is hard-deleted by step 4b).
        // Probe reports stationX on bssidA in the cycle, but does NOT report stationY at all.
        // Expected:
        //   stationX: WiFiNetworkId still == A's network id (re-resolved successfully on the same cycle)
        //   stationY: row exists, WiFiNetworkId == null (cascade SetNull from B's delete; not reported -> not re-resolved)
        int nodeId, netAId, netBId;
        var bssidA = $"FF:0C:00:00:00:{Random.Shared.Next(0, 255):X2}";
        var bssidB = $"FF:0C:00:01:00:{Random.Shared.Next(0, 255):X2}";
        var stationXMac = $"AA:0C:00:01:00:{Random.Shared.Next(0, 255):X2}";
        var stationYMac = $"AA:0C:00:02:00:{Random.Shared.Next(0, 255):X2}";
        await using (var seed = _fixture.CreateContext())
        {
            var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = "" };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;

            var netA = new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssidA, SSID = "Net" };
            var netB = new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssidB, SSID = "Net" };
            seed.WiFiNetworks.AddRange(netA, netB);
            await seed.SaveChangesAsync(ct);
            netAId = netA.Id;
            netBId = netB.Id;

            seed.WLANStations.AddRange(
                new WLANStation
                {
                    MacAddress = stationXMac,
                    WiFiNetworkId = netAId,
                    Status = Status.Online,
                    LastPollDate = DateTime.UtcNow.AddMinutes(-1),
                },
                new WLANStation
                {
                    MacAddress = stationYMac,
                    WiFiNetworkId = netBId,
                    Status = Status.Online,
                    LastPollDate = DateTime.UtcNow.AddMinutes(-1),
                });
            await seed.SaveChangesAsync(ct);
        }

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[]
                {
                    new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssidA, SSID = "Net" },
                    // bssidB intentionally absent -> step 4b deletes netB
                },
                Stations: new[]
                {
                    // stationX is reported on bssidA -> orchestrator re-resolves WiFiNetworkId via bssidLookup
                    new WLANStation { MacAddress = stationXMac, Bssid = bssidA, RSS = -50, LastPollDate = DateTime.UtcNow },
                    // stationY intentionally not reported -> WiFiNetworkId stays null after cascade
                },
                NewlyTrustedFingerprint: null));
        };

        var orch = BuildOrchestrator(probeAsync);
        await orch.RunCycleAsync(ct);

        await using var verify = _fixture.CreateContext();
        // netB was deleted; netA survives.
        Assert.False(await verify.WiFiNetworks.AsNoTracking().AnyAsync(n => n.Id == netBId, ct));
        Assert.True (await verify.WiFiNetworks.AsNoTracking().AnyAsync(n => n.Id == netAId, ct));

        // stationX: still pointing at netA, re-resolved on this cycle.
        var sx = await verify.WLANStations.AsNoTracking().FirstAsync(s => s.MacAddress == stationXMac, ct);
        Assert.Equal(netAId, sx.WiFiNetworkId);

        // stationY: row still exists but FK was nulled by DeleteBehavior.SetNull cascade.
        var sy = await verify.WLANStations.AsNoTracking().FirstAsync(s => s.MacAddress == stationYMac, ct);
        Assert.Null(sy.WiFiNetworkId);
    }

    // ── v0.11 Plan 14-01 / POLL-01 — CycleStarted event placement contract (CONTEXT 14 §D-02 / §D-03) ──
    // The orchestrator MUST raise CycleStarted at the TOP of RunCycleAsync, BEFORE the BeginTransactionAsync
    // try{} body, so that:
    //   (a) on the happy path, CycleStarted fires before Cycled (event-order contract); and
    //   (b) on a failure path that rolls back the coordinator transaction, CycleStarted still fires
    //       (so PollCountdown holds at "Polling…" until the next successful Cycled — D-03 passive failure
    //       signal). The orchestrator's existing try/catch/finally is NOT extended (D-03).

    [Fact]
    public async Task CycleStarted_FiresBefore_Cycled_OnHappyPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct);

        var notifier = new PollNotifier();
        var events = new List<string>();
        var cycleStartedCount = 0;
        var cycledCount = 0;
        notifier.CycleStarted += () =>
        {
            events.Add("CycleStarted");
            cycleStartedCount++;
            return Task.CompletedTask;
        };
        notifier.Cycled += _ =>
        {
            events.Add("Cycled");
            cycledCount++;
            return Task.CompletedTask;
        };

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            // IClassFixture-shared DB — only this test's node should produce live data.
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            var net = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { net },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null));
        };

        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var orch = new PollOrchestrator(
            dpFactory,
            new NodeRepo(dpFactory),
            new NetworkRepo(dpFactory),
            new StationRepo(dpFactory),
            probeAsync,
            notifier,
            NullLogger<PollOrchestrator>.Instance,
            new StubSettings(30),
            (_, _, _, _) => Task.FromResult<EnrichmentDelta?>(null),
            TimeProvider.System,
            new ForceEnrichmentService());

        await orch.RunCycleAsync(ct);

        // CycleStarted must precede Cycled in the captured order list. The shared-fixture DB
        // means other tests' nodes also pass through this same orchestrator instance's cycle,
        // but only THIS orchestrator instance's notifier is wired — and there is exactly ONE
        // cycle per RunCycleAsync invocation, so the event-count is 1 for each.
        Assert.Equal(new[] { "CycleStarted", "Cycled" }, events);
        Assert.Equal(1, cycleStartedCount);
        Assert.Equal(1, cycledCount);
    }

    [Fact]
    public async Task CycleStarted_FiresEvenWhenCycleFails_BeforeRollback_NoCycledRaised()
    {
        // D-03 placement contract: CycleStarted fires at the TOP of RunCycleAsync, BEFORE
        // BeginTransactionAsync. We force a rollback path by injecting a ThrowingNetworkRepo
        // whose UpsertManyAsync throws — the orchestrator's outer catch then RollbackAsync's
        // and rethrows. CycleStarted has already fired by the time the exception propagates;
        // Cycled never fires (the raise sits after tx.CommitAsync, inside the try{} block).
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct);

        var notifier = new PollNotifier();
        var cycleStartedCount = 0;
        var cycledCount = 0;
        notifier.CycleStarted += () => { cycleStartedCount++; return Task.CompletedTask; };
        notifier.Cycled       += _ => { cycledCount++; return Task.CompletedTask; };

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            var net = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { net },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null));
        };

        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var orch = new PollOrchestrator(
            dpFactory,
            new NodeRepo(dpFactory),
            new ThrowingNetworkRepo(),     // forces InvalidOperationException out of UpsertManyAsync
            new StationRepo(dpFactory),
            probeAsync,
            notifier,
            NullLogger<PollOrchestrator>.Instance,
            new StubSettings(30),
            (_, _, _, _) => Task.FromResult<EnrichmentDelta?>(null),
            TimeProvider.System,
            new ForceEnrichmentService());

        await Assert.ThrowsAsync<InvalidOperationException>(() => orch.RunCycleAsync(ct));

        Assert.Equal(1, cycleStartedCount);
        Assert.Equal(0, cycledCount);
    }

    // ── Quick task 260516-pce — PollSnapshot.CycleEndUtc producer-side contract ──
    // Operator-visible symptom (v0.11 QUAL-02 LIVE-UAT surface (a), 2026-05-16): the
    // PollCountdown badge restarts at 25s after Cycled instead of the full 30s
    // PollingIntervalSeconds. Root cause: PollOrchestrator.RunCycleAsync captured
    // `var now = DateTime.UtcNow` early in the cycle (just after the SSH fan-out,
    // before enrichment + station upserts + tx.CommitAsync) and reused the same
    // stale `now` for BOTH `_ticks` (LastSuccessfulCycleUtc) AND
    // `PollSnapshot.CycleEndUtc`. Operator's clarification: "the timer should
    // restore after the polling AND the enrichment operation" — i.e., the
    // end-of-cycle wall clock must be captured downstream of the per-node
    // `enrichmentFactory` awaits, NOT upstream.
    //
    // This test pins the producer-side contract by injecting a deliberate post-line-125
    // delay via a slow enrichmentFactory. With the bug shape, CycleEndUtc ≈ run-start;
    // with the fix, CycleEndUtc lags run-start by at least the injected delay.

    [Fact]
    public async Task CycleEndUtc_ReflectsActualCycleEnd_NotStartOfCycleSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct);

        var notifier = new PollNotifier();
        DateTime? capturedCycleEndUtc = null;
        notifier.Cycled += s =>
        {
            capturedCycleEndUtc = s.CycleEndUtc;
            return Task.CompletedTask;
        };

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync = (n, _) =>
        {
            // Shared-fixture DB: only this test's node should produce live data.
            // Other tests' nodes return Failed so they skip the enrichment loop and
            // do NOT trigger the slow factory below.
            if (n.Id != nodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            var net = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" };
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { net },
                Stations: Array.Empty<WLANStation>(),
                NewlyTrustedFingerprint: null));
        };

        // Inject a deliberate delay strictly between the line-125 'now' capture and
        // the post-commit snapshot construction. The enrichmentFactory is awaited
        // inside the Phase 10 ORCH-01..08 block (PollOrchestrator.cs line ~308),
        // which runs after step 4's per-node network upsert flush AND before
        // tx.CommitAsync. Fresh seeded WiFiNode has SubnetScanEnabled=true and
        // LastEnrichmentRefreshAt=null, so EnrichmentRefreshTrigger.ShouldRefresh
        // returns true on first cycle and the factory IS invoked for this node.
        const int delayMs = 200;
        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> slowEnrich =
            async (_, _, _, innerCt) =>
            {
                await Task.Delay(delayMs, innerCt);
                return null;
            };

        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var orch = new PollOrchestrator(
            dpFactory,
            new NodeRepo(dpFactory),
            new NetworkRepo(dpFactory),
            new StationRepo(dpFactory),
            probeAsync,
            notifier,
            NullLogger<PollOrchestrator>.Instance,
            new StubSettings(30),
            slowEnrich,
            TimeProvider.System,
            new ForceEnrichmentService());

        var lastSuccessBefore = orch.LastSuccessfulCycleUtc;
        await orch.RunCycleAsync(ct);
        var afterRun = DateTime.UtcNow;
        var lastSuccessAfter = orch.LastSuccessfulCycleUtc;

        Assert.NotNull(capturedCycleEndUtc);
        var cycleEndUtc = capturedCycleEndUtc!.Value;

        // PRODUCER CONTRACT #1 (260516-pce / PollSnapshot.CycleEndUtc):
        // CycleEndUtc must be captured AT THE END of the cycle (after enrichment +
        // commit), not at the start. The cleanest discriminator is the gap between
        // the snapshot timestamp and the moment RunCycleAsync returns:
        //
        //   - Fixed code: cycleEndUtc captured at line ~487 (post-tx.CommitAsync),
        //     snapshot raised at line ~505, RunCycleAsync returns shortly after.
        //     Gap is tens of milliseconds at most.
        //   - Bug shape: cycleEndUtc was the stale `now` from line ~125 — captured
        //     BEFORE the 200ms enrichment await on line ~308. So afterRun - cycleEndUtc
        //     is at least delayMs (≈ 200ms) plus the post-enrichment work.
        //
        // We assert the gap is below half the injected delay — strictly within the
        // band achievable only when CycleEndUtc is sampled at end-of-cycle.
        var gapToAfterRun = (afterRun - cycleEndUtc).TotalMilliseconds;
        Assert.True(
            gapToAfterRun < delayMs / 2,
            $"afterRun - PollSnapshot.CycleEndUtc = {gapToAfterRun:F0}ms — " +
            $"expected < {delayMs / 2}ms (a larger gap means CycleEndUtc was captured before the {delayMs}ms enrichment delay, " +
            $"i.e. at start-of-cycle line ~125 instead of end-of-cycle line ~487 — the v0.11 LIVE-UAT surface-(a) bug shape).");

        // CycleEndUtc must not come from the future relative to RunCycleAsync's return.
        Assert.True(
            cycleEndUtc <= afterRun,
            $"PollSnapshot.CycleEndUtc {cycleEndUtc:O} is after RunCycleAsync returned ({afterRun:O}).");

        // PRODUCER CONTRACT #2 (260516-pce / _ticks LastSuccessfulCycleUtc):
        // The unified post-commit timestamp must also be reflected in the
        // /health-facing LastSuccessfulCycleUtc. After the fix the two values are
        // written from the same `cycleEndUtc` capture, so they must agree exactly —
        // this pins the "single source of truth for end-of-cycle wall clock"
        // contract across both consumer surfaces.
        Assert.NotEqual(lastSuccessBefore, lastSuccessAfter);
        Assert.Equal(cycleEndUtc, lastSuccessAfter);
    }

    /// <summary>Minimal IAppSettingsService stub for orchestrator tests. Only PollingIntervalSeconds is exercised.</summary>
    private sealed class StubSettings(int pollingIntervalSeconds) : IAppSettingsService
    {
        public int     PollingIntervalSeconds => pollingIntervalSeconds;
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
