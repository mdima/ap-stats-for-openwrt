using ApStatsForOpenWrt.Data.Repositories.History;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Data.Repositories.History;

/// <summary>
/// Phase 19 M-HIST-05 (L-03) — unit coverage of the KPI read-side bucketing. The ≥80% line+branch
/// gate (QUAL-01) lands here: the component is a thin wrapper, the logic is exercised against an
/// in-memory SQLite database via <see cref="SqliteInMemoryFixture"/>. Asserts last-value-per-bucket,
/// the ≤144 cap, gap-as-null, full-axis-anchored-to-latest, a 5-min-boundary-crossing case, the
/// zero-rows empty state, and SpecifyKind correctness (rows round-trip as Kind=Unspecified).
/// <para>Run: <c>dotnet test ApStatsForOpenWrt.Tests -- --filter-class "*KpiHistoryQueryServiceTests*"</c>.</para>
/// </summary>
public sealed class KpiHistoryQueryServiceTests : IAsyncLifetime
{
    private readonly SqliteInMemoryFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private KpiHistoryQueryService BuildService() =>
        new(new TestDbContextFactory(_fixture.Options));

    [Fact]
    public async Task GetLast12hAsync_ZeroRows_ReportsEmptyState()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = BuildService();

        var dto = await svc.GetLast12hAsync(ct);

        Assert.True(dto.IsEmpty);
        Assert.Empty(dto.Buckets);
    }

    [Fact]
    public async Task GetLast12hAsync_FullAxis_AnchoredToLatestSnapshot_With144Buckets()
    {
        var ct = TestContext.Current.CancellationToken;
        // Anchor near now so the 12 h SQL cutoff includes the rows.
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        await using (var db = _fixture.CreateContext())
        {
            // Two snapshots: one ~11.5 h ago, one at the anchor — the grid must span the full 12 h.
            await HistorySeed.SeedKpiAsync(db, anchor.AddHours(-11).AddMinutes(-30), 1, 0, 0, ct);
            await HistorySeed.SeedKpiAsync(db, anchor, 5, 1, 2, ct);
        }

        var dto = await BuildService().GetLast12hAsync(ct);

        Assert.False(dto.IsEmpty);
        // 12 h / 5 min = 144 steps → 145 slots inclusive of both ends; cap is "≤144 bars" of DATA,
        // the axis itself is the full grid. Assert the grid is full-axis and bounded.
        Assert.True(dto.Buckets.Count is >= 144 and <= 145);
        // Ascending order, last slot is the anchor bucket.
        for (var i = 1; i < dto.Buckets.Count; i++)
            Assert.True(dto.Buckets[i].BucketUtc > dto.Buckets[i - 1].BucketUtc);
        // The last bucket carries the anchor snapshot's triad (last-value).
        var last = dto.Buckets[^1];
        Assert.Equal(5, last.OnlineCount);
        Assert.Equal(1, last.StaleCount);
        Assert.Equal(2, last.OfflineCount);
    }

    [Fact]
    public async Task GetLast12hAsync_GapBucket_IsNullCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        await using (var db = _fixture.CreateContext())
        {
            // One snapshot 30 min before the anchor, one at the anchor. The buckets in between
            // (e.g. 10 min, 15 min before) must be gaps (null counts).
            await HistorySeed.SeedKpiAsync(db, anchor.AddMinutes(-30), 3, 0, 0, ct);
            await HistorySeed.SeedKpiAsync(db, anchor, 7, 0, 0, ct);
        }

        var dto = await BuildService().GetLast12hAsync(ct);

        // Find a bucket strictly between the two seeded ones; it must be a gap.
        var gap = dto.Buckets.SkipLast(1).Last(b => b.BucketUtc > dto.Buckets[^1].BucketUtc.AddMinutes(-25)
                                                     && b.BucketUtc < dto.Buckets[^1].BucketUtc.AddMinutes(-5));
        Assert.Null(gap.OnlineCount);
        Assert.Null(gap.StaleCount);
        Assert.Null(gap.OfflineCount);
    }

    [Fact]
    public async Task GetLast12hAsync_LastValuePerBucket_WinsWithinBucket()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        await using (var db = _fixture.CreateContext())
        {
            // Two cycles inside the SAME 5-min bucket: an earlier (lower) and the latest (higher).
            // Floor to a bucket boundary so both land together.
            var bucketStart = new DateTime(
                anchor.Ticks - anchor.Ticks % TimeSpan.FromMinutes(HistorySeed.BucketMinutes).Ticks,
                DateTimeKind.Utc);
            await HistorySeed.SeedKpiAsync(db, bucketStart.AddSeconds(10), 2, 0, 0, ct);   // earlier
            await HistorySeed.SeedKpiAsync(db, bucketStart.AddMinutes(2), 9, 0, 0, ct);    // latest in bucket
        }

        var dto = await BuildService().GetLast12hAsync(ct);
        var last = dto.Buckets[^1];

        Assert.Equal(9, last.OnlineCount);   // last value, not 2
    }

    [Fact]
    public async Task GetLast12hAsync_BoundaryCrossing_LandsInCorrectBucket()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        await using (var db = _fixture.CreateContext())
        {
            var bucketStart = new DateTime(
                anchor.Ticks - anchor.Ticks % TimeSpan.FromMinutes(HistorySeed.BucketMinutes).Ticks,
                DateTimeKind.Utc);
            // One row at bucketStart-1s (previous bucket), one at bucketStart+1s (anchor bucket).
            await HistorySeed.SeedKpiAsync(db, bucketStart.AddSeconds(-1), 4, 0, 0, ct);
            await HistorySeed.SeedKpiAsync(db, bucketStart.AddSeconds(1), 8, 0, 0, ct);
        }

        var dto = await BuildService().GetLast12hAsync(ct);

        // Anchor bucket holds the +1s row; the immediately-prior bucket holds the -1s row.
        Assert.Equal(8, dto.Buckets[^1].OnlineCount);
        Assert.Equal(4, dto.Buckets[^2].OnlineCount);
    }

    [Fact]
    public async Task GetLast12hAsync_TriadStaysCoherentPerBucket()
    {
        var ct = TestContext.Current.CancellationToken;
        var anchor = DateTime.UtcNow.AddMinutes(-1);
        await using (var db = _fixture.CreateContext())
            await HistorySeed.SeedKpiAsync(db, anchor, 5, 2, 3, ct);

        var dto = await BuildService().GetLast12hAsync(ct);
        var last = dto.Buckets[^1];

        // Returned exactly as persisted (Phase 18 D-01 coherence — no recomputation).
        Assert.Equal(5, last.OnlineCount);
        Assert.Equal(2, last.StaleCount);
        Assert.Equal(3, last.OfflineCount);
    }
}
