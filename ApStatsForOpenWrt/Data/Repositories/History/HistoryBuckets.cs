using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Data.Repositories.History;

/// <summary>
/// Phase 19 M-HIST-05/06/07 (L-02 / D-03) — shared read-side 5-minute bucketing helper. Owns the
/// fixed-grid construction every history surface (KPI bar, RSSI sparkline, RSSI detail) folds into:
/// a sequence of <see cref="BucketMinutes"/>-minute slots anchored to the latest snapshot's
/// <c>TimestampUtc</c> (the polling clock, Phase 18 D-06), walking back over the requested span, so
/// the full axis is always present with gaps where no data lands (D-03).
/// <para>
/// SQLite stores <c>DateTime</c> as <c>Kind=Unspecified</c> (RESEARCH Pitfall 3); this helper
/// centralizes the <see cref="System.DateTime.SpecifyKind(System.DateTime, System.DateTimeKind)"/>
/// to <c>Utc</c> so all three read paths share one correct treatment before any bucket subtraction.
/// </para>
/// <para>
/// Bucket size is a single <see cref="BucketMinutes"/> constant (L-02 — tunable to 4 minutes later,
/// read-side only, no schema change, no migration). A 12 h span at 5 minutes yields ≤144 buckets.
/// </para>
/// </summary>
internal static class HistoryBuckets
{
    /// <summary>Read-side bucket width in minutes (L-02). Ship 5; tunable to 4 during QUAL-02 UAT.</summary>
    internal const int BucketMinutes = 5;

    /// <summary>The full KPI / detail window span (D-03 fixed 12 h axis).</summary>
    internal static readonly TimeSpan FullWindow = TimeSpan.FromHours(12);

    /// <summary>The adaptive sparkline window span (L-05 ~1 h ≈ 12 buckets at 5 min).</summary>
    internal static readonly TimeSpan SparklineWindow = TimeSpan.FromHours(1);

    /// <summary>Force a DB-read <c>DateTime</c> to UTC kind before any bucket math (Pitfall 3).</summary>
    internal static DateTime AsUtc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);

    /// <summary>
    /// Round <paramref name="t"/> DOWN to the start of its <see cref="BucketMinutes"/>-minute slot.
    /// Input is treated as UTC (caller passes an already-<see cref="AsUtc"/>'d value or a UtcNow).
    /// </summary>
    internal static DateTime FloorToBucket(DateTime t)
    {
        var utc = DateTime.SpecifyKind(t, DateTimeKind.Utc);
        var floorTicks = utc.Ticks - (utc.Ticks % TimeSpan.FromMinutes(BucketMinutes).Ticks);
        return new DateTime(floorTicks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Build the ordered (ascending) full-axis bucket grid. The grid ends at the bucket containing
    /// <paramref name="anchorUtc"/> (rounded down) and walks back <paramref name="span"/>. Bucket
    /// timestamps are the slot-start instants. Always returns at least one bucket.
    /// </summary>
    internal static IReadOnlyList<DateTime> BuildGrid(DateTime anchorUtc, TimeSpan span)
    {
        var lastBucket = FloorToBucket(anchorUtc);
        var step = TimeSpan.FromMinutes(BucketMinutes);
        // Number of buckets spanning [anchor-span .. anchor], inclusive of both ends' slots.
        var count = (int)(span.Ticks / step.Ticks) + 1;
        if (count < 1) count = 1;

        var grid = new DateTime[count];
        // Fill ascending: grid[0] is the oldest slot, grid[^1] is the anchor slot.
        for (var i = 0; i < count; i++)
            grid[i] = lastBucket - TimeSpan.FromMinutes((count - 1 - i) * (long)BucketMinutes);
        return grid;
    }

    /// <summary>
    /// Resolve the zero-based grid index for a given <paramref name="timestampUtc"/> against a grid
    /// built from <paramref name="anchorUtc"/> / <paramref name="span"/>. Returns <c>-1</c> when the
    /// timestamp falls outside the grid (older than the first slot or newer than the anchor slot).
    /// The timestamp is treated as UTC kind (Pitfall 3).
    /// </summary>
    internal static int BucketIndex(DateTime timestampUtc, DateTime anchorUtc, TimeSpan span)
    {
        var floored = FloorToBucket(timestampUtc);
        var lastBucket = FloorToBucket(anchorUtc);
        var count = (int)(span.Ticks / TimeSpan.FromMinutes(BucketMinutes).Ticks) + 1;
        if (count < 1) count = 1;
        var firstBucket = lastBucket - TimeSpan.FromMinutes((count - 1) * (long)BucketMinutes);

        if (floored < firstBucket || floored > lastBucket) return -1;
        var minutesFromStart = (floored - firstBucket).TotalMinutes;
        return (int)(minutesFromStart / BucketMinutes);
    }

    /// <summary>
    /// Fold a windowed, time-ordered row set into one slot per grid bucket, keeping the LAST row
    /// (max <c>TimestampUtc</c>) per bucket (D-03 last-value rule). Empty buckets are
    /// <c>default(T)?</c> (gap). The caller supplies a <paramref name="timestampSelector"/> reading
    /// each row's raw DB timestamp (SpecifyKind is applied here).
    /// </summary>
    /// <returns>An array aligned 1:1 with <see cref="BuildGrid"/>; element is <c>null</c> for a gap.</returns>
    internal static T?[] FoldLastValue<T>(
        IEnumerable<T> rows,
        Func<T, DateTime> timestampSelector,
        DateTime anchorUtc,
        TimeSpan span)
        where T : class
    {
        var grid = BuildGrid(anchorUtc, span);
        var slots = new T?[grid.Count];
        var slotStamp = new DateTime?[grid.Count];

        foreach (var row in rows)
        {
            var ts = AsUtc(timestampSelector(row));
            var idx = BucketIndex(ts, anchorUtc, span);
            if (idx < 0) continue;
            // Last-value: keep the row with the greatest timestamp in the slot.
            if (slotStamp[idx] is null || ts >= slotStamp[idx]!.Value)
            {
                slots[idx] = row;
                slotStamp[idx] = ts;
            }
        }

        return slots;
    }
}
