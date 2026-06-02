namespace ApStatsForOpenWrt.Data.Repositories.History;

/// <summary>
/// Phase 19 M-HIST-05 (L-03 / L-07 / D-03) — read-side query service that folds the persisted
/// <c>KpiHistorySnapshots</c> into the fixed 5-min bucket grid the dashboard KPI stacked-bar chart
/// consumes. Owns ALL the bucketing logic (components are thin wrappers); the ≥80% line+branch
/// coverage gate (QUAL-01) lands on the implementation.
/// <para>
/// Lives under <c>ApStatsForOpenWrt.Data.Repositories.History</c> so it may legally inject
/// <c>IDbContextFactory&lt;DatabaseContext&gt;</c> (the ArchitectureTests namespace carve-out — see
/// <c>ArchitectureTests.DbContext_IsOnlyUsedByRepositories</c>).
/// </para>
/// </summary>
public interface IKpiHistoryQueryService
{
    /// <summary>
    /// Read the last 12 h of KPI snapshots and fold them into the full ≤144-bucket 5-min grid
    /// (last value per bucket, gaps where no snapshot, full axis anchored to the latest snapshot's
    /// <c>TimestampUtc</c>). Returns an empty-state DTO when no rows exist in the window (drives the
    /// "Collecting history…" placeholder, D-03).
    /// </summary>
    Task<KpiHistoryDto> GetLast12hAsync(CancellationToken ct);
}

/// <summary>
/// The KPI history read result: the ordered full-axis bucket set plus a zero-rows flag. Each
/// <see cref="KpiHistoryBucket"/> carries null counts where the bucket had no snapshot (a gap).
/// </summary>
/// <param name="Buckets">Ascending, full-axis ≤144 buckets (oldest first; last is the anchor slot).</param>
/// <param name="IsEmpty">True when zero snapshots fell in the 12 h window (D-03 placeholder state).</param>
public sealed record KpiHistoryDto(IReadOnlyList<KpiHistoryBucket> Buckets, bool IsEmpty);

/// <summary>
/// One 5-min bucket of KPI counts. Null counts == gap (no snapshot landed in this bucket, D-03).
/// When present, the triad is the LAST snapshot in the bucket (D-03 last-value rule) and stays
/// internally coherent (Phase 18 D-01 guarantees the persisted triad sums).
/// </summary>
/// <param name="BucketUtc">The bucket slot-start instant (UTC).</param>
/// <param name="OnlineCount">Online station count, or null for a gap.</param>
/// <param name="StaleCount">Stale (recently-offline) station count, or null for a gap.</param>
/// <param name="OfflineCount">Offline station count, or null for a gap.</param>
public sealed record KpiHistoryBucket(DateTime BucketUtc, int? OnlineCount, int? StaleCount, int? OfflineCount);
