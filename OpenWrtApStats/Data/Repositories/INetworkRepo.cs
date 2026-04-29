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
}
