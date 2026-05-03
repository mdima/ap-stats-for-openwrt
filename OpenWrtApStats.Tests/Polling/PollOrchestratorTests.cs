using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Polling.Exceptions;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

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
        ILogger<PollOrchestrator>? logger = null)
    {
        var dpFactory = new TestDbContextFactory(_fixture.Options);
        return new PollOrchestrator(
            dpFactory,
            new NodeRepo(dpFactory),
            new NetworkRepo(dpFactory),
            new StationRepo(dpFactory),
            probeAsync,
            new PollNotifier(),
            logger ?? NullLogger<PollOrchestrator>.Instance);
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
            NullLogger<PollOrchestrator>.Instance);

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
                var node = new WiFiNode { Hostname = $"par-{Guid.NewGuid():N}", Username = "root", Password = "" };
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
}
