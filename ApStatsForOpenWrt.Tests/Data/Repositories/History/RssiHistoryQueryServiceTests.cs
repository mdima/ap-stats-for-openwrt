using ApStatsForOpenWrt.Data.Repositories.History;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Data.Repositories.History;

/// <summary>
/// Phase 19 M-HIST-06 / M-HIST-07 (L-03) — unit coverage of the RSSI read-side bucketing for both
/// shapes against in-memory SQLite. Sparkline: latest-cycle→MAX per bucket, continuous line across a
/// roam, gap at an offline bucket, per-MAC map keyed correctly, MAC-with-no-samples absent from the
/// map. Detail: one series per (NodeId, NetworkId), last-value-per-bucket, label = AP hostname + SSID,
/// no cross-series aggregation, zero-rows empty state.
/// <para>Run: <c>dotnet test ApStatsForOpenWrt.Tests -- --filter-class "*RssiHistoryQueryServiceTests*"</c>.</para>
/// </summary>
public sealed class RssiHistoryQueryServiceTests : IAsyncLifetime
{
    private readonly SqliteInMemoryFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private RssiHistoryQueryService BuildService() =>
        new(new TestDbContextFactory(_fixture.Options));

    private const string MacA = "AA:AA:AA:AA:AA:AA";
    private const string MacB = "BB:BB:BB:BB:BB:BB";

    // ---- Sparkline ---------------------------------------------------------

    [Fact]
    public async Task GetSparklineSeriesAsync_EmptyMacSet_ReturnsEmptyMap()
    {
        var ct = TestContext.Current.CancellationToken;
        var map = await BuildService().GetSparklineSeriesAsync([], ct);
        Assert.Empty(map);
    }

    [Fact]
    public async Task GetSparklineSeriesAsync_MacWithNoSamples_AbsentFromMap()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        int nodeId, netId;
        await using (var db = _fixture.CreateContext())
        {
            (nodeId, netId) = await HistorySeed.SeedNodeAndNetworkAsync(db, "ap1", "wifi", ct);
            await HistorySeed.SeedRssiAsync(db, anchor, MacA, nodeId, netId, -55, ct);
        }

        // Ask for MacA (has data) and MacB (none) — MacB must be absent.
        var map = await BuildService().GetSparklineSeriesAsync([MacA, MacB], ct);

        Assert.True(map.ContainsKey(MacA));
        Assert.False(map.ContainsKey(MacB));
    }

    [Fact]
    public async Task GetSparklineSeriesAsync_LatestCycleThenMaxAcrossRoam_OneContinuousLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        var bucketStart = FloorBucket(anchor);
        int n1, w1, n2, w2;
        await using (var db = _fixture.CreateContext())
        {
            (n1, w1) = await HistorySeed.SeedNodeAndNetworkAsync(db, "ap1", "wifi", ct);
            (n2, w2) = await HistorySeed.SeedNodeAndNetworkAsync(db, "ap2", "wifi", ct);
            // Same MAC, same bucket, roaming between two (AP, WLAN): an earlier cycle and a latest
            // cycle. The latest cycle has two rows (a dual-band/roam moment); MAX(Rssi) wins.
            await HistorySeed.SeedRssiAsync(db, bucketStart.AddSeconds(10), MacA, n1, w1, -80, ct);   // earlier cycle
            await HistorySeed.SeedRssiAsync(db, bucketStart.AddMinutes(2), MacA, n1, w1, -70, ct);    // latest cycle, row 1
            await HistorySeed.SeedRssiAsync(db, bucketStart.AddMinutes(2), MacA, n2, w2, -60, ct);    // latest cycle, row 2 (roam)
        }

        var map = await BuildService().GetSparklineSeriesAsync([MacA], ct);
        var series = map[MacA];
        var lastPoint = series.Points[^1];

        // Latest cycle = the +2min rows; MAX across (-70, -60) = -60. The earlier -80 is ignored.
        Assert.Equal(-60, lastPoint.Rssi);
    }

    [Fact]
    public async Task GetSparklineSeriesAsync_OfflineBucket_IsGap()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        int nodeId, netId;
        await using (var db = _fixture.CreateContext())
        {
            (nodeId, netId) = await HistorySeed.SeedNodeAndNetworkAsync(db, "ap1", "wifi", ct);
            // One sample ~30 min ago and one at the anchor — buckets between are gaps (null).
            await HistorySeed.SeedRssiAsync(db, anchor.AddMinutes(-30), MacA, nodeId, netId, -50, ct);
            await HistorySeed.SeedRssiAsync(db, anchor, MacA, nodeId, netId, -52, ct);
        }

        var map = await BuildService().GetSparklineSeriesAsync([MacA], ct);
        var pts = map[MacA].Points;

        // A point strictly between the two seeded buckets must be a gap.
        var gap = pts.SkipLast(1).First(p => p.BucketUtc > pts[^1].BucketUtc.AddMinutes(-25)
                                             && p.BucketUtc < pts[^1].BucketUtc.AddMinutes(-5));
        Assert.Null(gap.Rssi);
        // Anchor bucket has data.
        Assert.Equal(-52, pts[^1].Rssi);
    }

    [Fact]
    public async Task GetSparklineSeriesAsync_TwoMacs_KeyedCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        int nodeId, netId;
        await using (var db = _fixture.CreateContext())
        {
            (nodeId, netId) = await HistorySeed.SeedNodeAndNetworkAsync(db, "ap1", "wifi", ct);
            await HistorySeed.SeedRssiAsync(db, anchor, MacA, nodeId, netId, -45, ct);
            await HistorySeed.SeedRssiAsync(db, anchor, MacB, nodeId, netId, -65, ct);
        }

        var map = await BuildService().GetSparklineSeriesAsync([MacA, MacB], ct);

        Assert.Equal(2, map.Count);
        Assert.Equal(-45, map[MacA].Points[^1].Rssi);
        Assert.Equal(-65, map[MacB].Points[^1].Rssi);
    }

    [Fact]
    public async Task GetSparklineSeriesAsync_LatestSampleOlderThanWindow_StillPopulates()
    {
        // Regression (QUAL-02 / Bug 2): a UtcNow-based read cutoff blanked the sparkline whenever
        // the newest sample was older than the wall-clock window (e.g. a visitor-gated polling
        // pause), even though the KPI/detail charts still rendered. The read must anchor to the
        // latest sample (the polling clock, D-06), so a 5 h-old series still populates the spark.
        var ct = TestContext.Current.CancellationToken;
        var stale = DateTime.UtcNow.AddHours(-5);   // newest sample 5 h old — outside a wall-clock 1 h window
        int nodeId, netId;
        await using (var db = _fixture.CreateContext())
        {
            (nodeId, netId) = await HistorySeed.SeedNodeAndNetworkAsync(db, "ap1", "wifi", ct);
            await HistorySeed.SeedRssiAsync(db, stale.AddMinutes(-30), MacA, nodeId, netId, -58, ct);
            await HistorySeed.SeedRssiAsync(db, stale, MacA, nodeId, netId, -50, ct);
        }

        var map = await BuildService().GetSparklineSeriesAsync([MacA], ct);

        Assert.True(map.ContainsKey(MacA));
        // Grid anchored to the latest (5 h-old) sample; its last bucket carries that sample's value.
        Assert.Equal(-50, map[MacA].Points[^1].Rssi);
    }

    // ---- Detail ------------------------------------------------------------

    [Fact]
    public async Task GetDetailSeriesAsync_ZeroRows_ReportsEmptyState()
    {
        var ct = TestContext.Current.CancellationToken;
        var dto = await BuildService().GetDetailSeriesAsync(MacA, ct);

        Assert.True(dto.IsEmpty);
        Assert.Empty(dto.Series);
        Assert.Empty(dto.BucketsUtc);
    }

    [Fact]
    public async Task GetDetailSeriesAsync_OneSeriesPerNodeNetwork_NoCrossSeriesAggregation()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        int n1, w1, n2, w2;
        await using (var db = _fixture.CreateContext())
        {
            (n1, w1) = await HistorySeed.SeedNodeAndNetworkAsync(db, "alpha", "ssid-a", ct);
            (n2, w2) = await HistorySeed.SeedNodeAndNetworkAsync(db, "beta", "ssid-b", ct);
            // Same MAC seen on two distinct (NodeId, NetworkId) — must produce TWO series, not merged.
            await HistorySeed.SeedRssiAsync(db, anchor, MacA, n1, w1, -55, ct);
            await HistorySeed.SeedRssiAsync(db, anchor, MacA, n2, w2, -66, ct);
        }

        var dto = await BuildService().GetDetailSeriesAsync(MacA, ct);

        Assert.False(dto.IsEmpty);
        Assert.Equal(2, dto.Series.Count);
        // Each series last-value at the anchor bucket equals its own RSSI (no aggregation).
        var sA = dto.Series.Single(s => s.NodeId == n1 && s.NetworkId == w1);
        var sB = dto.Series.Single(s => s.NodeId == n2 && s.NetworkId == w2);
        Assert.Equal(-55, sA.Points[^1]);
        Assert.Equal(-66, sB.Points[^1]);
        // Points align 1:1 with the shared axis.
        Assert.Equal(dto.BucketsUtc.Count, sA.Points.Count);
    }

    [Fact]
    public async Task GetDetailSeriesAsync_Label_IsApHostnamePlusSsid()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        int nodeId, netId;
        await using (var db = _fixture.CreateContext())
        {
            (nodeId, netId) = await HistorySeed.SeedNodeAndNetworkAsync(db, "kitchen-ap", "HomeNet", ct);
            await HistorySeed.SeedRssiAsync(db, anchor, MacA, nodeId, netId, -50, ct);
        }

        var dto = await BuildService().GetDetailSeriesAsync(MacA, ct);
        var label = dto.Series.Single().Label;

        Assert.Contains("kitchen-ap", label);
        Assert.Contains("HomeNet", label);
    }

    [Fact]
    public async Task GetDetailSeriesAsync_LastValuePerBucket_WinsWithinBucket()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        var bucketStart = FloorBucket(anchor);
        int nodeId, netId;
        await using (var db = _fixture.CreateContext())
        {
            (nodeId, netId) = await HistorySeed.SeedNodeAndNetworkAsync(db, "ap1", "wifi", ct);
            await HistorySeed.SeedRssiAsync(db, bucketStart.AddSeconds(10), MacA, nodeId, netId, -80, ct);  // earlier
            await HistorySeed.SeedRssiAsync(db, bucketStart.AddMinutes(2), MacA, nodeId, netId, -40, ct);   // latest
        }

        var dto = await BuildService().GetDetailSeriesAsync(MacA, ct);

        Assert.Equal(-40, dto.Series.Single().Points[^1]);   // last value wins, not -80
    }

    [Fact]
    public async Task GetDetailSeriesAsync_GapBucket_IsNullPoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        int nodeId, netId;
        await using (var db = _fixture.CreateContext())
        {
            (nodeId, netId) = await HistorySeed.SeedNodeAndNetworkAsync(db, "ap1", "wifi", ct);
            await HistorySeed.SeedRssiAsync(db, anchor.AddMinutes(-30), MacA, nodeId, netId, -50, ct);
            await HistorySeed.SeedRssiAsync(db, anchor, MacA, nodeId, netId, -52, ct);
        }

        var dto = await BuildService().GetDetailSeriesAsync(MacA, ct);
        var series = dto.Series.Single();
        // A bucket strictly between the two has a null point.
        var gapIndex = Enumerable.Range(0, dto.BucketsUtc.Count)
            .Last(i => dto.BucketsUtc[i] > dto.BucketsUtc[^1].AddMinutes(-25)
                       && dto.BucketsUtc[i] < dto.BucketsUtc[^1].AddMinutes(-5));
        Assert.Null(series.Points[gapIndex]);
        Assert.Equal(-52, series.Points[^1]);
    }

    [Fact]
    public async Task GetDetailSeriesAsync_ZeroRowsButKpiSnapshot_AnchorsToKpiClock_NotEmpty()
    {
        // Bug 1/2 (QUAL-02 follow-up): a station with NO RSSI samples in the window but an active
        // poll clock (a KPI snapshot exists) must show empty axes up to the current poll time, NOT
        // the permanent "Collecting history…" placeholder. The grid anchors to MAX(KpiHistorySnapshots
        // .TimestampUtc); Series is empty (no samples for this MAC); IsEmpty is false (history exists).
        var ct = TestContext.Current.CancellationToken;
        var kpiClock = DateTime.UtcNow.AddMinutes(-1);
        await using (var db = _fixture.CreateContext())
        {
            // Seed only a KPI snapshot — NO RSSI rows for MacA.
            await HistorySeed.SeedKpiAsync(db, kpiClock, 3, 1, 0, ct);
        }

        var dto = await BuildService().GetDetailSeriesAsync(MacA, ct);

        Assert.False(dto.IsEmpty);
        Assert.Empty(dto.Series);
        Assert.NotEmpty(dto.BucketsUtc);
        // Last grid bucket = floor of the KPI snapshot timestamp (the polling clock).
        Assert.Equal(FloorBucket(kpiClock), dto.BucketsUtc[^1]);
    }

    [Fact]
    public async Task GetDetailSeriesAsync_StationRowsOlderThanKpiClock_TrailingNullsToAnchor()
    {
        // Bug 1/2 (disconnect case): a disconnected station's chart must slide forward with trailing
        // null gaps anchored to the polling clock (the latest KPI snapshot) instead of freezing at its
        // last sample. The station's RSSI rows sit ~30 min before the KPI clock; the grid ends at the
        // floored KPI-clock bucket, the series carries data in the older bucket and null at the anchor.
        var ct = TestContext.Current.CancellationToken;
        var kpiClock = DateTime.UtcNow.AddMinutes(-1);
        var stationTs = kpiClock.AddMinutes(-30);   // last sample 30 min before the poll clock
        int nodeId, netId;
        await using (var db = _fixture.CreateContext())
        {
            (nodeId, netId) = await HistorySeed.SeedNodeAndNetworkAsync(db, "ap1", "wifi", ct);
            await HistorySeed.SeedRssiAsync(db, stationTs, MacA, nodeId, netId, -57, ct);
            // Later KPI snapshot — the poll clock has advanced past the station's last sample.
            await HistorySeed.SeedKpiAsync(db, kpiClock, 2, 0, 1, ct);
        }

        var dto = await BuildService().GetDetailSeriesAsync(MacA, ct);

        Assert.False(dto.IsEmpty);
        // Grid anchored to the KPI clock, NOT the station's last sample.
        Assert.Equal(FloorBucket(kpiClock), dto.BucketsUtc[^1]);
        var series = dto.Series.Single();
        // Anchor bucket is a trailing null gap (station disconnected before the poll clock).
        Assert.Null(series.Points[^1]);
        // An earlier bucket (the station's last sample bucket) carries the seeded RSSI.
        var stationBucket = FloorBucket(stationTs);
        var idx = Enumerable.Range(0, dto.BucketsUtc.Count).Single(i => dto.BucketsUtc[i] == stationBucket);
        Assert.Equal(-57, series.Points[idx]);
    }

    [Fact]
    public async Task GetDetailSeriesAsync_NoKpiSnapshotAndNoRows_ReportsEmptyState()
    {
        // The truly-empty contract: with NEITHER a KPI snapshot NOR any station row, the placeholder
        // is truthful — IsEmpty true, empty Series, empty BucketsUtc (no axes to draw).
        var ct = TestContext.Current.CancellationToken;
        var dto = await BuildService().GetDetailSeriesAsync(MacA, ct);

        Assert.True(dto.IsEmpty);
        Assert.Empty(dto.Series);
        Assert.Empty(dto.BucketsUtc);
    }

    private static DateTime FloorBucket(DateTime t) =>
        new(t.Ticks - t.Ticks % TimeSpan.FromMinutes(HistorySeed.BucketMinutes).Ticks, DateTimeKind.Utc);
}
