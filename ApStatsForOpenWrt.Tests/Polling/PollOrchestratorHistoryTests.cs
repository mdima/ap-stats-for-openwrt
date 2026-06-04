using Microsoft.Data.Sqlite;
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
/// Phase 18 Plan 03 (M-HIST-01 / M-HIST-02) orchestrator integration tests. Closes the
/// highest-correctness-risk surface of the phase: the D-01 StaleCount sense inversion, the D-04
/// pre-dedup RSSI write-set shape, the single-clock invariant, the unconditional 12 h prune, and
/// disable-gating (inserts stop but prune still runs). Modeled on the in-memory-SQLite + fake
/// probe harness in <see cref="PollOrchestratorTests"/>, but with a PER-TEST fresh database
/// (<see cref="IAsyncLifetime"/>, not an <c>IClassFixture</c>) so the one-row-per-cycle
/// <see cref="KpiHistorySnapshot"/> assertions are not polluted by sibling tests' cycles.
/// <para>
/// Per CLAUDE.md the history writes/prune are orchestrator-only, inside the per-cycle coordinator
/// transaction; this suite running green (no <c>SqliteException 'database is locked'</c>) is the
/// single-writer guarantee (T-18-08). Run via the .NET 10 MTP class filter:
/// <c>dotnet test ApStatsForOpenWrt.Tests --nologo -- --filter-class "*PollOrchestratorHistory*"</c>.
/// </para>
/// </summary>
public sealed class PollOrchestratorHistoryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<DatabaseContext> _options = null!;

    public async ValueTask InitializeAsync()
    {
        // Per-test fresh shared-cache :memory: DB so KpiHistorySnapshot row counts are isolated.
        var dataSource = $"HistTests-{Guid.NewGuid():N}";
        _connection = new SqliteConnection($"Data Source={dataSource};Mode=Memory;Cache=Shared");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(_connection).Options;
        await using var seed = new DatabaseContext(_options);
        await seed.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }

    private DatabaseContext CreateContext() => new(_options);

    /// <summary>
    /// Builds a probe-emitted station POCO faithful to the production parser
    /// (<c>OpenWrtOutputParser</c> stamps <c>Status = Status.Online</c> on every associated
    /// station it emits — see OpenWrtOutputParser.cs:154/:283). The orchestrator's D-04 RSSI
    /// capture filters on this Online status over the pre-dedup set, so the test fakes must mirror
    /// it; a default-constructed WLANStation is <see cref="Status.Offline"/> and would be skipped.
    /// </summary>
    private static WLANStation OnlineProbe(string mac, string bssid, int? rss) => new()
    {
        MacAddress = mac,
        Bssid = bssid,
        RSS = rss,
        Status = Status.Online,
        LastPollDate = DateTime.UtcNow,
    };

    private PollOrchestrator BuildOrchestrator(
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync,
        bool historyEnabled = true,
        int pollingIntervalSeconds = 30,
        IHistoryRepo? historyRepo = null)
    {
        var dpFactory = new TestDbContextFactory(_options);
        return new PollOrchestrator(
            dpFactory,
            new NodeRepo(dpFactory),
            new NetworkRepo(dpFactory),
            new StationRepo(dpFactory),
            historyRepo ?? new HistoryRepo(dpFactory),
            probeAsync,
            new PollNotifier(),
            NullLogger<PollOrchestrator>.Instance,
            new HistoryStubSettings(pollingIntervalSeconds, historyEnabled),
            (_, _, _, _) => Task.FromResult<EnrichmentDelta?>(null),
            TimeProvider.System,
            new ForceEnrichmentService());
    }

    private async Task<(int nodeId, int netId, string bssid)> SeedNodeWithOneNetworkAsync(CancellationToken ct)
    {
        var bssid = $"FF:00:00:00:{Random.Shared.Next(0, 255):X2}:{Random.Shared.Next(0, 255):X2}";
        await using var db = CreateContext();
        var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = "" };
        db.WiFiNodes.Add(node);
        await db.SaveChangesAsync(ct);
        var net = new WiFiNetwork { WiFiNodeId = node.Id, SSID = "Net", MacAddress = bssid };
        db.WiFiNetworks.Add(net);
        await db.SaveChangesAsync(ct);
        return (node.Id, net.Id, bssid);
    }

    // ── Fact 1: write-set shape (one KPI snapshot + one RSSI sample per (MAC,AP,WLAN)) ──────────

    [Fact]
    public async Task HistoryEnabled_OneCycle_WritesOneKpiSnapshot_AndOneRssiSamplePerOnlineStation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct);

        var mac1 = $"AA:01:00:00:00:{Random.Shared.Next(0, 255):X2}";
        var mac2 = $"AA:02:00:00:00:{Random.Shared.Next(0, 255):X2}";
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            if (n.Id != nodeId) return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: new[]
                {
                    OnlineProbe(mac1, bssid, -50),
                    OnlineProbe(mac2, bssid, -60),
                },
                NewlyTrustedFingerprint: null));
        };

        await BuildOrchestrator(probe).RunCycleAsync(ct);

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.KpiHistorySnapshots.CountAsync(ct));
        var samples = await verify.RssiHistorySamples.AsNoTracking().ToListAsync(ct);
        Assert.Equal(2, samples.Count);
        Assert.Contains(samples, s => s.StationMac == mac1 && s.Rssi == -50 && s.NodeId == nodeId && s.NetworkId == netId);
        Assert.Contains(samples, s => s.StationMac == mac2 && s.Rssi == -60 && s.NodeId == nodeId && s.NetworkId == netId);
    }

    [Fact]
    public async Task DualWlanStation_ProducesTwoRssiSamples_PreDedupWriteSet()
    {
        // D-04: a station reported on two WLANs in the same cycle yields TWO samples (pre-dedup),
        // even though the post-dedup WLANStation table collapses it to one row.
        var ct = TestContext.Current.CancellationToken;
        int nodeId, net1Id, net2Id;
        var bssid1 = $"FF:01:00:00:00:{Random.Shared.Next(0, 255):X2}";
        var bssid2 = $"FF:02:00:00:00:{Random.Shared.Next(0, 255):X2}";
        await using (var seed = CreateContext())
        {
            var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = "" };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;
            var n1 = new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssid1, SSID = "net-1" };
            var n2 = new WiFiNetwork { WiFiNodeId = nodeId, MacAddress = bssid2, SSID = "net-2" };
            seed.WiFiNetworks.AddRange(n1, n2);
            await seed.SaveChangesAsync(ct);
            net1Id = n1.Id; net2Id = n2.Id;
        }

        var sharedMac = $"AA:CC:DD:EE:FF:{Random.Shared.Next(0, 255):X2}";
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            if (n.Id != nodeId) return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[]
                {
                    new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid1, SSID = "net-1" },
                    new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid2, SSID = "net-2" },
                },
                Stations: new[]
                {
                    OnlineProbe(sharedMac, bssid1, -55),
                    OnlineProbe(sharedMac, bssid2, -40),
                },
                NewlyTrustedFingerprint: null));
        };

        await BuildOrchestrator(probe).RunCycleAsync(ct);

        await using var verify = CreateContext();
        var samples = await verify.RssiHistorySamples.AsNoTracking().Where(s => s.StationMac == sharedMac).ToListAsync(ct);
        Assert.Equal(2, samples.Count);
        Assert.Contains(samples, s => s.NetworkId == net1Id && s.Rssi == -55);
        Assert.Contains(samples, s => s.NetworkId == net2Id && s.Rssi == -40);
        // Post-dedup WLANStation collapses to one row — proves the samples are PRE-dedup.
        Assert.Equal(1, await verify.WLANStations.CountAsync(s => s.MacAddress == sharedMac, ct));
    }

    // ── Fact 2: null-RSS online station excluded from RssiHistorySample ─────────────────────────

    [Fact]
    public async Task OnlineStation_WithNullRss_ExcludedFromRssiSamples()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct);

        var withRss = $"BA:01:00:00:00:{Random.Shared.Next(0, 255):X2}";
        var noRss   = $"BA:02:00:00:00:{Random.Shared.Next(0, 255):X2}";
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            if (n.Id != nodeId) return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: new[]
                {
                    OnlineProbe(withRss, bssid, -50),
                    OnlineProbe(noRss,   bssid, null),   // associated (Online) but no RSS — excluded by the RSS filter
                },
                NewlyTrustedFingerprint: null));
        };

        await BuildOrchestrator(probe).RunCycleAsync(ct);

        await using var verify = CreateContext();
        var samples = await verify.RssiHistorySamples.AsNoTracking().ToListAsync(ct);
        Assert.Single(samples);
        Assert.Equal(withRss, samples[0].StationMac);
        Assert.DoesNotContain(samples, s => s.StationMac == noRss);
    }

    // ── Fact 3: count triad — the stale-sense pin + sum invariant ───────────────────────────────

    [Fact]
    public async Task CountTriad_FreshOffline_IsStale_OldOffline_IsOffline_AndSumIsConsistent()
    {
        // staleCutoff = now - 2x interval. With a 30s interval the boundary is 60s.
        //   * an Online station                          → OnlineCount
        //   * a fresh-offline station (LastPollDate just inside the window, >= staleCutoff) → StaleCount
        //   * an old-offline station (LastPollDate older than the window, < staleCutoff)    → OfflineCount
        // The fresh-offline station MUST NOT be reported by the probe (else it flips Online).
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct);

        var onlineMac      = $"C0:01:00:00:00:{Random.Shared.Next(0, 255):X2}";
        var freshOfflineMac = $"C0:02:00:00:00:{Random.Shared.Next(0, 255):X2}";
        var oldOfflineMac   = $"C0:03:00:00:00:{Random.Shared.Next(0, 255):X2}";

        await using (var seed = CreateContext())
        {
            // Fresh-offline: 30s ago (1x interval) — inside the 60s stale window ⇒ Stale (>= staleCutoff).
            seed.WLANStations.Add(new WLANStation
            {
                MacAddress = freshOfflineMac, WiFiNetworkId = netId, Status = Status.Offline,
                LastPollDate = DateTime.UtcNow.AddSeconds(-30), RSS = -70,
            });
            // Old-offline: 5 minutes ago — well outside the window ⇒ Offline (< staleCutoff).
            seed.WLANStations.Add(new WLANStation
            {
                MacAddress = oldOfflineMac, WiFiNetworkId = netId, Status = Status.Offline,
                LastPollDate = DateTime.UtcNow.AddMinutes(-5), RSS = -75,
            });
            await seed.SaveChangesAsync(ct);
        }

        // Probe reports ONLY the online station — the two offline rows stay Offline after mark-all-offline.
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            if (n.Id != nodeId) return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: new[] { OnlineProbe(onlineMac, bssid, -50) },
                NewlyTrustedFingerprint: null));
        };

        await BuildOrchestrator(probe, pollingIntervalSeconds: 30).RunCycleAsync(ct);

        await using var verify = CreateContext();
        var snap = await verify.KpiHistorySnapshots.AsNoTracking().SingleAsync(ct);
        Assert.Equal(1, snap.OnlineCount);
        Assert.Equal(1, snap.StaleCount);   // fresh-offline lands here — the sense pin (>= staleCutoff)
        Assert.Equal(1, snap.OfflineCount); // old-offline lands here   — (< staleCutoff)

        // Sum invariant: triad == count of (Online ∪ Offline) stations (Polling excluded).
        var onlineOrOffline = await verify.WLANStations
            .CountAsync(s => s.Status == Status.Online || s.Status == Status.Offline, ct);
        Assert.Equal(onlineOrOffline, snap.OnlineCount + snap.StaleCount + snap.OfflineCount);
    }

    // ── Fact 4: single-clock — snapshot timestamp equals the cycle's station LastPollDate ───────

    [Fact]
    public async Task SnapshotTimestamp_EqualsCycleClock_UsedForOnlineStationLastPollDate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct);
        var mac = $"D0:01:00:00:00:{Random.Shared.Next(0, 255):X2}";

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            if (n.Id != nodeId) return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: new[] { OnlineProbe(mac, bssid, -50) },
                NewlyTrustedFingerprint: null));
        };

        await BuildOrchestrator(probe).RunCycleAsync(ct);

        await using var verify = CreateContext();
        var snap = await verify.KpiHistorySnapshots.AsNoTracking().SingleAsync(ct);
        var sample = await verify.RssiHistorySamples.AsNoTracking().SingleAsync(ct);
        // The node's LastPollDate was stamped with the cycle's `now` (the single clock); the
        // snapshot and the sample use the SAME `now` — assert exact equality (not approximate).
        var node = await verify.WiFiNodes.AsNoTracking().SingleAsync(n => n.Id == nodeId, ct);
        Assert.Equal(node.LastPollDate, snap.TimestampUtc);
        Assert.Equal(snap.TimestampUtc, sample.TimestampUtc);
    }

    // ── Fact 5: prune — rows older than 12 h are deleted; current-cycle rows remain ─────────────

    [Fact]
    public async Task Prune_RemovesRowsOlderThan12h_KeepsCurrentCycleRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct);
        var mac = $"E0:01:00:00:00:{Random.Shared.Next(0, 255):X2}";

        // Seed one stale (13h-old) row in each table.
        var stale = DateTime.UtcNow.AddHours(-13);
        await using (var seed = CreateContext())
        {
            seed.KpiHistorySnapshots.Add(new KpiHistorySnapshot { TimestampUtc = stale, OnlineCount = 1, StaleCount = 0, OfflineCount = 0 });
            seed.RssiHistorySamples.Add(new RssiHistorySample { TimestampUtc = stale, StationMac = "ZZ:99:99:99:99:99", NodeId = nodeId, NetworkId = netId, Rssi = -99 });
            await seed.SaveChangesAsync(ct);
        }

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            if (n.Id != nodeId) return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: new[] { OnlineProbe(mac, bssid, -50) },
                NewlyTrustedFingerprint: null));
        };

        await BuildOrchestrator(probe).RunCycleAsync(ct);

        await using var verify = CreateContext();
        // The 13h-old rows are gone; only the current cycle's rows remain.
        Assert.Equal(0, await verify.KpiHistorySnapshots.CountAsync(k => k.TimestampUtc == stale, ct));
        Assert.Equal(0, await verify.RssiHistorySamples.CountAsync(r => r.TimestampUtc == stale, ct));
        Assert.Equal(1, await verify.KpiHistorySnapshots.CountAsync(ct));   // only this cycle's snapshot
        Assert.Equal(1, await verify.RssiHistorySamples.CountAsync(r => r.StationMac == mac, ct));
    }

    // ── Fact 6: disable-gating — no new rows inserted, but prune still runs (non-destructive) ───

    [Fact]
    public async Task HistoryDisabled_InsertsZeroNewRows_ButPruneStillRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, netId, bssid) = await SeedNodeWithOneNetworkAsync(ct);
        var mac = $"F0:01:00:00:00:{Random.Shared.Next(0, 255):X2}";

        var stale = DateTime.UtcNow.AddHours(-13);
        await using (var seed = CreateContext())
        {
            seed.KpiHistorySnapshots.Add(new KpiHistorySnapshot { TimestampUtc = stale, OnlineCount = 1, StaleCount = 0, OfflineCount = 0 });
            seed.RssiHistorySamples.Add(new RssiHistorySample { TimestampUtc = stale, StationMac = "ZZ:88:88:88:88:88", NodeId = nodeId, NetworkId = netId, Rssi = -88 });
            await seed.SaveChangesAsync(ct);
        }

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            if (n.Id != nodeId) return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: new[] { OnlineProbe(mac, bssid, -50) },
                NewlyTrustedFingerprint: null));
        };

        await BuildOrchestrator(probe, historyEnabled: false).RunCycleAsync(ct);

        await using var verify = CreateContext();
        // D-05: with history disabled, ZERO new rows inserted — both tables are now empty
        // (the only rows were the 13h-old seeds, which the unconditional prune removed).
        Assert.Equal(0, await verify.KpiHistorySnapshots.CountAsync(ct));
        Assert.Equal(0, await verify.RssiHistorySamples.CountAsync(ct));
        // D-06: confirm the stale rows were specifically pruned (non-destructive disable).
        Assert.Equal(0, await verify.KpiHistorySnapshots.CountAsync(k => k.TimestampUtc == stale, ct));
        Assert.Equal(0, await verify.RssiHistorySamples.CountAsync(r => r.TimestampUtc == stale, ct));
    }

    // ── Fact 7: atomicity — a throw after the history write rolls back the inserts ──────────────

    [Fact]
    public async Task HistoryWrite_RollsBack_WhenCycleThrowsAfterWrite()
    {
        // A failing history repo writes the rows then throws — the catch block runs RollbackAsync,
        // so no history rows persist (D-05 atomicity: the writes are inside the coordinator tx).
        var ct = TestContext.Current.CancellationToken;
        var (nodeId, _, bssid) = await SeedNodeWithOneNetworkAsync(ct);
        var mac = $"AB:01:00:00:00:{Random.Shared.Next(0, 255):X2}";

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            if (n.Id != nodeId) return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = bssid, SSID = "Net" } },
                Stations: new[] { OnlineProbe(mac, bssid, -50) },
                NewlyTrustedFingerprint: null));
        };

        var orch = BuildOrchestrator(probe, historyRepo: new WriteThenThrowHistoryRepo());

        await Assert.ThrowsAsync<InvalidOperationException>(() => orch.RunCycleAsync(ct));

        await using var verify = CreateContext();
        Assert.Equal(0, await verify.KpiHistorySnapshots.CountAsync(ct));
        Assert.Equal(0, await verify.RssiHistorySamples.CountAsync(ct));
    }

    /// <summary>
    /// Writes the history rows (so they exist in the change tracker / pending in the tx) then throws,
    /// forcing the orchestrator into its catch + RollbackAsync path. Proves the inserts roll back
    /// atomically with the cycle (D-05 coordinator-transaction invariant).
    /// </summary>
    private sealed class WriteThenThrowHistoryRepo : IHistoryRepo
    {
        public async Task WriteCycleAsync(DatabaseContext db, KpiHistorySnapshot snapshot, IEnumerable<RssiHistorySample> samples, CancellationToken ct)
        {
            db.KpiHistorySnapshots.Add(snapshot);
            db.RssiHistorySamples.AddRange(samples);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("simulated post-write failure (atomicity test)");
        }

        public Task PruneAsync(DatabaseContext db, DateTime cutoff, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Orchestrator-test IAppSettingsService stub with a controllable HistoryEnabled.</summary>
    private sealed class HistoryStubSettings(int pollingIntervalSeconds, bool historyEnabled) : IAppSettingsService
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
        public bool UpdateCheckEnabled => true;
        public bool HistoryEnabled => historyEnabled;
        public Task SetHistoryEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public string? LatestKnownVersion => null;
        public string? LatestReleaseUrl => null;
        public DateTime? LastCheckedAtUtc => null;
        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
