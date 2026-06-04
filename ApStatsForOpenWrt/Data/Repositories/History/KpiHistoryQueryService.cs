using ApStatsForOpenWrt.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace ApStatsForOpenWrt.Data.Repositories.History;

/// <summary>
/// Phase 19 M-HIST-05 — windowed-read + in-memory fold implementation of
/// <see cref="IKpiHistoryQueryService"/> (RESEARCH Pattern 4). Reads the 12 h window via
/// <c>IDbContextFactory&lt;DatabaseContext&gt;</c> (legal here — repo sub-namespace) and folds into
/// the shared <see cref="HistoryBuckets"/> grid, last-value-per-bucket, full-axis-with-gaps,
/// anchored to the latest snapshot's <c>TimestampUtc</c> (the polling clock, Phase 18 D-06).
/// </summary>
public sealed class KpiHistoryQueryService(IDbContextFactory<DatabaseContext> factory) : IKpiHistoryQueryService
{
    public async Task<KpiHistoryDto> GetLast12hAsync(CancellationToken ct)
    {
        await using var db = factory.CreateDbContext();

        // Window the read to the last 12 h. The cutoff uses UtcNow to bound the SQL read cheaply;
        // the bucket GRID is anchored to the latest row's TimestampUtc below (D-06), so a stale DB
        // (after downtime) still produces a coherent full axis ending at the last real snapshot.
        var cutoff = DateTime.UtcNow - HistoryBuckets.FullWindow;
        var rows = await db.KpiHistorySnapshots
            .AsNoTracking()
            .Where(s => s.TimestampUtc >= cutoff)
            .OrderBy(s => s.TimestampUtc)
            .ToListAsync(ct);

        if (rows.Count == 0)
            return new KpiHistoryDto([], IsEmpty: true);

        // Anchor the grid to the polling clock (latest snapshot), not wall-clock UtcNow (D-06 / OQ2).
        var anchor = HistoryBuckets.AsUtc(rows[^1].TimestampUtc);

        var folded = HistoryBuckets.FoldLastValue(
            rows,
            r => r.TimestampUtc,
            anchor,
            HistoryBuckets.FullWindow);

        var grid = HistoryBuckets.BuildGrid(anchor, HistoryBuckets.FullWindow);
        var buckets = new KpiHistoryBucket[grid.Count];
        for (var i = 0; i < grid.Count; i++)
        {
            var snap = folded[i];
            buckets[i] = snap is null
                ? new KpiHistoryBucket(grid[i], null, null, null)
                : new KpiHistoryBucket(grid[i], snap.OnlineCount, snap.StaleCount, snap.OfflineCount);
        }

        return new KpiHistoryDto(buckets, IsEmpty: false);
    }
}
