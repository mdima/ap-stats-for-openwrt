using OpenWrtApStats.Data.Models;

namespace OpenWrtApStats.Data.Repositories;

/// <summary>
/// Repository contract for <see cref="WiFiNetwork"/>. Read paths are per-node; the
/// cycle-shaped <c>UpsertManyAsync</c> reconciles a node's discovered WLAN set
/// idempotently (PER-03) and is exposed in both repo-owned and caller-owned flavors
/// (D-07). Removed WLANs are NOT deleted here — Phase 3 (WLAN-03) marks them
/// disabled-on-absence; Phase 1 ships only the upsert path.
/// </summary>
public interface INetworkRepo
{
    // ── CRUD (repo-owned only) ──────────────────────────────────────────────
    Task<IReadOnlyList<WiFiNetwork>> GetByNodeAsync(int wiFiNodeId, CancellationToken ct = default);
    Task<WiFiNetwork?> GetByIdAsync(int id, CancellationToken ct = default);
    Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default);

    // ── Cycle-shaped (D-07) ─────────────────────────────────────────────────
    /// <summary>Idempotent upsert keyed on (WiFiNodeId, MacAddress) per PER-03.
    /// Updates fields on existing rows; inserts new ones; preserves existing Enabled flag
    /// across re-discoveries (WLAN-03 in Phase 3).</summary>
    Task UpsertManyAsync(int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default);
    Task UpsertManyAsync(DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default);

    // ── D-15 Phase 3: discovery-only absent-WLAN handling (Pitfall 7) ──────
    /// <summary>
    /// D-15 / RESEARCH §Pitfall 7: discovery-only path. Sets <c>Enabled = false</c> on every
    /// <see cref="WiFiNetwork"/> that belongs to <paramref name="wiFiNodeId"/> AND whose
    /// <c>MacAddress</c> is NOT in <paramref name="presentMacs"/>. Implemented via
    /// <c>ExecuteUpdateAsync</c> — never <c>db.WiFiNetworks.Update(...)</c> (CR-02-safe).
    /// <para>
    /// The Phase 2 <c>PollOrchestrator</c> does NOT call this method — poll-cycle absence
    /// semantics are owned by discovery, not by the polling loop. <c>UpsertManyAsync</c>'s
    /// contract stays unchanged (present rows update; absent rows untouched).
    /// </para>
    /// <para>
    /// <paramref name="presentMacs"/> MUST be uppercased upstream (Phase 2 D-07 / parser).
    /// </para>
    /// </summary>
    Task MarkAbsentDisabledAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default);

    // ── DASH-10 Phase 3: dashboard "all WLANs disabled" detection ──────────
    /// <summary>
    /// DASH-10 binding: returns <c>true</c> if any <see cref="WiFiNetwork"/> across all nodes
    /// has <c>Enabled = true</c>. Single <c>AsNoTracking().AnyAsync(n => n.Enabled)</c> —
    /// avoids the N+1-via-GetByNodeAsync pattern when the dashboard renders the empty state.
    /// </summary>
    Task<bool> AnyEnabledAsync(CancellationToken ct = default);
}
