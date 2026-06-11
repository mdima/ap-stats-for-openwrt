using Microsoft.Data.Sqlite;
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
    // 260517-stp — retry on SQLITE_CONSTRAINT_UNIQUE (Error 19) up to 2 times. The
    // cold-start race against PollOrchestrator's first cycle was: discovery reads
    // existing-rows snapshot (empty) -> orchestrator's tx commits the same WLANs ->
    // discovery's local merge attempts INSERT for rows that now exist -> SQLite
    // throws UNIQUE constraint failed. Recovery is to re-read existing (which now
    // includes the orchestrator's rows), re-run the merge (everything becomes an
    // UPDATE), re-save. The retry-loop also covers any benign concurrent UPSERT
    // from a future caller. SQLITE_BUSY (Error 5) is handled separately by the
    // connection-string-level busy_timeout = 30s set in Program.cs.
    public async Task UpsertManyAsync(int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default)
    {
        // Materialise once so we can re-iterate the same probe set across retries.
        var discoveredList = discovered as IReadOnlyList<WiFiNetwork> ?? discovered.ToList();

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await UpsertManyAsync(db, wiFiNodeId, discoveredList, ct);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex)
                when (attempt < maxAttempts && ex.InnerException is SqliteException sqlEx
                      && sqlEx.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT_UNIQUE */)
            {
                // Drop the tracked add+update changes; the next iteration opens a
                // fresh DbContext, reloads existing rows (which now include the
                // concurrently-inserted ones), and re-runs the merge.
                db.ChangeTracker.Clear();
            }
        }
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
    // WR-05 (21-REVIEW): MarkAbsentDisabledAsync is LIVE (called by WlanDiscoveryService) and the
    // repo-owned DeleteAbsentAsync overload retains dedicated test coverage, so neither is removed.
    // Latent-correctness note for a future v2 discovery caller: `!presentMacs.Contains(...)` inside
    // ExecuteUpdate/DeleteAsync translates to a SQL `(NOT) IN (@p0, @p1, ...)` — one parameter per
    // MAC. SQLite caps host parameters (~999 on older builds, ~32766 on modern ones), so a node
    // with a very large station set could blow the ceiling. v1 keeps the set small, but a v2
    // discovery path against a busy AP MUST batch presentMacs (chunk the IN-list) before calling.
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

    // ── M-HRD-10 (v1.0 hardening): batched dashboard read ───────────────────
    // Single AsNoTracking() pull over the whole table, grouped in-memory by WiFiNodeId —
    // replaces the dashboard's per-node GetByNodeAsync N+1 loop. Read-only; never injects a
    // scoped DbContext (PER-05 IDbContextFactory per-call pattern).
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<WiFiNetwork>>> GetAllGroupedByNodeAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await db.WiFiNetworks.AsNoTracking().ToListAsync(ct);
        return all.GroupBy(w => w.WiFiNodeId)
                  .ToDictionary(g => g.Key, g => (IReadOnlyList<WiFiNetwork>)g.ToList());
    }
}
