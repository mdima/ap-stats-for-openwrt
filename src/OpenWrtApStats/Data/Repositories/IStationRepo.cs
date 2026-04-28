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

    // ── Cycle-shaped (D-07) ─────────────────────────────────────────────────
    Task MarkAllOfflineAsync(CancellationToken ct = default);
    Task MarkAllOfflineAsync(DatabaseContext db, CancellationToken ct = default);

    /// <summary>MAC-keyed upsert (PER-02 — a station can hop WLANs; MAC is the stable identity).
    /// Sets Status=Online, updates LastPollDate, mutates WiFiNetworkId / IPAddress / RSS / etc.</summary>
    Task UpsertCycleAsync(IEnumerable<WLANStation> live, CancellationToken ct = default);
    Task UpsertCycleAsync(DatabaseContext db, IEnumerable<WLANStation> live, CancellationToken ct = default);
}
