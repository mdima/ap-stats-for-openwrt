using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Data.Repositories;

/// <summary>
/// <see cref="INetworkRepo"/> implementation. PER-05 IDbContextFactory injection only;
/// dual-overload pattern (D-07) routes repo-owned through caller-owned. Re-discovery
/// preserves the operator-toggled <c>Enabled</c> flag (WLAN-03 forward-compat) and
/// does NOT delete WLANs that disappeared from the latest probe.
/// Phase 2 D-07 / CR-01 sister-site closure: case-strict BSSID dedup — callers MUST
/// uppercase upstream (parser does, per <c>OpenWrtOutputParser</c> Plan 02-02).
/// </summary>
public sealed class NetworkRepo(IDbContextFactory<DatabaseContext> factory) : INetworkRepo
{
    public async Task<IReadOnlyList<WiFiNetwork>> GetByNodeAsync(int wiFiNodeId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WiFiNetworks.AsNoTracking()
            .Where(n => n.WiFiNodeId == wiFiNodeId)
            .ToListAsync(ct);
    }

    public async Task<WiFiNetwork?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WiFiNetworks.FindAsync([id], ct);
    }

    public async Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WiFiNetworks
            .Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Enabled, enabled), ct);
    }

    // ── Repo-owned: opens its own context, commits ──────────────────────────
    public async Task UpsertManyAsync(int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await UpsertManyAsync(db, wiFiNodeId, discovered, ct);
        await db.SaveChangesAsync(ct);
    }

    // ── Caller-owned: NO SaveChangesAsync — orchestrator owns the transaction ──
    public async Task UpsertManyAsync(DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default)
    {
        var discoveredList = discovered.ToList();

        // Load only this node's existing rows so the (NodeId, MAC) match is local.
        var existing = await db.WiFiNetworks
            .Where(n => n.WiFiNodeId == wiFiNodeId)
            .ToListAsync(ct);
        var existingByMac = existing.ToDictionary(n => n.MacAddress, StringComparer.Ordinal);          // D-07 / CR-01 sister-site closure

        foreach (var probe in discoveredList)
        {
            if (existingByMac.TryGetValue(probe.MacAddress, out var existingRow))
            {
                // Update fields in place; preserve `Enabled` toggle across re-discoveries (WLAN-03).
                existingRow.SSID      = probe.SSID;
                existingRow.Channel   = probe.Channel;
                existingRow.Width     = probe.Width;
                existingRow.TxPower   = probe.TxPower;
                existingRow.IfaceName = probe.IfaceName;   // v0.11 D-10 — IfaceName persisted; parser-populated.
                // existingRow.Enabled is intentionally NOT overwritten — operator toggles persist.
            }
            else
            {
                probe.WiFiNodeId = wiFiNodeId;
                // probe.IfaceName already populated by parser (OpenWrtOutputParser line 119 / 224);
                // it now lands on the INSERT via EF's tracker. v0.11 D-10.
                db.WiFiNetworks.Add(probe);
            }
        }

        // Note: WLANs that disappeared from re-discovery are NOT deleted here per WLAN-03.
        // Phase 3 will mark them Enabled=false on absence. Phase 1 only ships the upsert path.
    }

    // ── D-15 Phase 3: absent-WLAN handling for discovery's transaction ──────
    public async Task MarkAbsentDisabledAsync(
        int wiFiNodeId,
        IReadOnlyCollection<string> presentMacs,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WiFiNetworks
            .Where(n => n.WiFiNodeId == wiFiNodeId && !presentMacs.Contains(n.MacAddress))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Enabled, false), ct);
    }

    // ── Repo-owned: opens its own context, commits ──────────────────────────
    public async Task DeleteAbsentAsync(
        int wiFiNodeId,
        IReadOnlyCollection<string> presentMacs,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await DeleteAbsentAsync(db, wiFiNodeId, presentMacs, ct);
        // ExecuteDeleteAsync persists immediately; SaveChangesAsync is a no-op for tracked-entity changes here
        // but call it for symmetry with sibling repo-owned methods.
        await db.SaveChangesAsync(ct);
    }

    // ── Caller-owned: NO SaveChangesAsync — orchestrator owns the transaction ──
    public Task DeleteAbsentAsync(
        DatabaseContext db,
        int wiFiNodeId,
        IReadOnlyCollection<string> presentMacs,
        CancellationToken ct = default) =>
        db.WiFiNetworks
            .Where(n => n.WiFiNodeId == wiFiNodeId && !presentMacs.Contains(n.MacAddress))
            .ExecuteDeleteAsync(ct);

    // ── DASH-10 Phase 3: dashboard empty-state detection ────────────────────
    public async Task<bool> AnyEnabledAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WiFiNetworks.AsNoTracking().AnyAsync(n => n.Enabled, ct);
    }
}
