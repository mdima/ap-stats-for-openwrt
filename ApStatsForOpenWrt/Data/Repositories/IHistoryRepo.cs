using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Data.Repositories;

/// <summary>
/// Phase 18 M-HIST-01 / M-HIST-02. Write seam for the history tables. Both methods take the
/// caller-owned <see cref="DatabaseContext"/> (the orchestrator's <c>db</c>/<c>tx</c>) so the
/// inserts and prune participate in the per-cycle coordinator transaction and the single-writer
/// invariant is preserved (CLAUDE.md / PER-04). Taking <c>db</c> as a method parameter (rather
/// than holding a <see cref="DatabaseContext"/> field) keeps
/// <c>ArchitectureTests.DbContext_IsOnlyUsedByRepositories</c> green for consumers, mirroring the
/// <see cref="IStationRepo.UpsertCycleAsync(DatabaseContext, System.Collections.Generic.IEnumerable{WLANStation}, System.Threading.CancellationToken)"/>
/// caller-owned idiom.
/// </summary>
public interface IHistoryRepo
{
    /// <summary>
    /// Inserts exactly one <see cref="KpiHistorySnapshot"/> and N <see cref="RssiHistorySample"/>
    /// rows on the caller-owned <paramref name="db"/>, then <c>SaveChangesAsync</c> (captured by
    /// the orchestrator's open transaction). Gated upstream on <c>HistoryEnabled</c> (D-05) —
    /// this method does not consult settings.
    /// </summary>
    Task WriteCycleAsync(DatabaseContext db, KpiHistorySnapshot snapshot, IEnumerable<RssiHistorySample> samples, CancellationToken ct);

    /// <summary>
    /// Set-based <c>ExecuteDeleteAsync</c> of all rows older than <paramref name="cutoff"/> from
    /// both history tables on the caller-owned <paramref name="db"/> (D-06). Runs UNCONDITIONALLY
    /// (NOT gated on <c>HistoryEnabled</c>) so disabling history is non-destructive. Participates
    /// in the orchestrator's open transaction.
    /// </summary>
    Task PruneAsync(DatabaseContext db, DateTime cutoff, CancellationToken ct);
}
