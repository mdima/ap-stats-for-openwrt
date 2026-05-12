using OpenWrtApStats.Data.Models;

namespace OpenWrtApStats.Data.Repositories;

/// <summary>
/// Repository contract for <see cref="WLANStation"/>. PER-02 binds <see cref="WLANStation.MacAddress"/>
/// as the stable cross-WLAN identity — the cycle-shaped <c>UpsertCycleAsync</c> reconciles a
/// hopping station to a single row. The operator-assigned <see cref="WLANStation.Name"/> is
/// preserved across cycles (DASH-14 forward-compat).
/// </summary>
public interface IStationRepo
{
    // ── CRUD (repo-owned only) ──────────────────────────────────────────────
    Task<IReadOnlyList<WLANStation>> GetAllAsync(CancellationToken ct = default);
    Task<WLANStation?> GetByMacAsync(string macAddress, CancellationToken ct = default);
    Task SetNameAsync(string macAddress, string? name, CancellationToken ct = default);

    /// <summary>
    /// Phase 10 DASH-22 / DASH-23 — operator override of the U/L-bit-derived random-MAC flag.
    /// Single-property <c>ExecuteUpdateAsync</c> (CR-02-safe). Repo-owned; runs outside any
    /// coordinator transaction (Stations/Detail.razor toggle). Idempotent on missing MAC.
    /// User-controlled-fields convention (Plan 10-01 ORCH-02): IsRandomMac is in the UPDATE-branch
    /// exclusion list of <c>UpsertCycleAsync</c> — only this method may write the column post-INSERT.
    /// </summary>
    Task SetIsRandomMacAsync(string macAddress, bool isRandomMac, CancellationToken ct = default);

    /// <summary>
    /// Phase 8 Plan 08-07 D-06 — read-only enumeration of every distinct <see cref="WLANStation.MacAddress"/>
    /// currently in the database. Used by <c>EventDetectionService</c> exactly once on its first
    /// <c>PollNotifier.Cycled</c> event to seed the in-memory <c>HashSet&lt;string&gt;</c> of "known" MACs
    /// (the closest faithful approximation to "MAC never recorded in DB" without a <c>FirstSeenAt</c>
    /// column — Phase 8 hard-blocked from schema changes).
    /// <para>
    /// Read-only: implemented as <c>AsNoTracking().Select(s =&gt; s.MacAddress).Distinct().ToListAsync</c>.
    /// NEVER calls <c>SaveChangesAsync</c>, <c>ExecuteUpdate</c>, or <c>ExecuteDelete</c>. The
    /// orchestrator-only-writes invariant (CLAUDE.md, PER-04) is preserved because this is a READ.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> GetAllMacsAsync(CancellationToken ct = default);

    /// <summary>
    /// Phase 10 ORCH-01 — read-only enumeration of MAC addresses currently persisted on a given
    /// node's WLANs. Used by PollOrchestrator to detect "newMacDetectedThisCycle" when joining
    /// against the Phase 2 NodePoller's freshly-observed station MACs.
    /// <para>
    /// Read-only: <c>AsNoTracking().Where(...).Select(s => s.MacAddress).Distinct().ToListAsync</c>.
    /// NEVER calls SaveChangesAsync. Orchestrator-only-writes invariant preserved.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> GetMacsByNodeAsync(int nodeId, CancellationToken ct = default);

    /// <summary>
    /// D-14 (Phase 5) operator-initiated delete (DASH-16). Single-statement
    /// <c>ExecuteDeleteAsync</c> (CR-02-safe — never <c>db.WLANStations.Remove(entity)</c>
    /// after a Find which would re-track the row, RESEARCH §Pitfall 5). Repo-owned only;
    /// never composed inside the coordinator transaction (operator-initiated and runs
    /// outside any cycle, so by definition not concurrent with the orchestrator's writer).
    /// Idempotent — calling on a non-existent MAC is a no-op (no exception).
    /// Race-with-cycle (CONTEXT D-14): if a <c>PollNotifier.Cycled</c> upsert recreates
    /// the row between modal-confirm and this call, the operator's delete still removes
    /// the just-recreated row; the modal copy explicitly warns about reconnect-reappear.
    /// </summary>
    Task DeleteByMacAsync(string macAddress, CancellationToken ct = default);

    // ── Cycle-shaped (D-07) ─────────────────────────────────────────────────
    Task MarkAllOfflineAsync(CancellationToken ct = default);
    Task MarkAllOfflineAsync(DatabaseContext db, CancellationToken ct = default);

    /// <summary>MAC-keyed upsert (PER-02 — a station can hop WLANs; MAC is the stable identity).
    /// Sets Status=Online, updates LastPollDate, mutates WiFiNetworkId / IPAddress / RSS / etc.</summary>
    Task UpsertCycleAsync(IEnumerable<WLANStation> live, CancellationToken ct = default);
    Task UpsertCycleAsync(DatabaseContext db, IEnumerable<WLANStation> live, CancellationToken ct = default);
}
