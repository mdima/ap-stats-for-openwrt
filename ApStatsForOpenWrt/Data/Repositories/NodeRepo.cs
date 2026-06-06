using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling;

namespace ApStatsForOpenWrt.Data.Repositories;

/// <summary>
/// <see cref="INodeRepo"/> implementation. Per PER-05, NEVER constructor-inject a scoped
/// <see cref="DatabaseContext"/>; always go through <see cref="IDbContextFactory{TContext}"/>.
/// The dual-overload pattern (D-07) routes repo-owned overloads through caller-owned
/// implementations: repo-owned opens its own context + commits; caller-owned does NOT call
/// <c>SaveChangesAsync</c> (the orchestrator owns the transaction).
/// <para>
/// M-HRD-09 (v1.0 / plan 21-05): <see cref="DeleteAsync"/> additionally tears down the deleted
/// node's process-scoped polling-plane state — the static <c>NodePoller._perNodeGate</c>
/// semaphore (via <see cref="NodePoller.RemoveNodeGate"/>) and the
/// <see cref="OpenWrtAdapterCache"/> adapter-binding entry. This is the v1 layering choice
/// from 21-RESEARCH Open Question 2 (option a): a direct call to two in-memory cleanups
/// (one static, one injected singleton). It adds NO DB write — the cleanup runs only AFTER
/// the existing <c>SaveChangesAsync</c>, so the orchestrator-only-writes rule and the
/// captive-dependency / PER-04 invariants are unchanged (<see cref="OpenWrtAdapterCache"/>
/// is a plain singleton, not a <see cref="DatabaseContext"/>, so it does not trip
/// <c>ArchitectureTests.DbContext_IsOnlyUsedByRepositories</c>). The cache is OPTIONAL so
/// existing direct-construction test call sites (<c>new NodeRepo(factory)</c>) keep compiling;
/// DI always resolves the registered singleton.
/// </para>
/// </summary>
public sealed class NodeRepo(IDbContextFactory<DatabaseContext> factory, OpenWrtAdapterCache? adapterCache = null) : INodeRepo
{
    // ── CRUD ────────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WiFiNodes.AsNoTracking().ToListAsync(ct);
    }

    public async Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WiFiNodes.FindAsync([id], ct);
    }

    public async Task AddAsync(WiFiNode node, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.WiFiNodes.Add(node);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.WiFiNodes.FindAsync([id], ct);
        if (entity is null) return;
        db.WiFiNodes.Remove(entity);
        await db.SaveChangesAsync(ct);

        // M-HRD-09 (v1.0 / plan 21-05): after the row deletion commits, tear down the deleted
        // node's process-scoped polling-plane state so a reused id cannot inherit a stale gate or
        // adapter binding, and the per-node semaphore is not leaked. IN-MEMORY ONLY — no new DB
        // write (the cleanup deliberately runs AFTER SaveChangesAsync). Covering DeleteAsync covers
        // both delete UI paths (Setup/Nodes.razor + Nodes/Index.razor). adapterCache is null only in
        // legacy direct-construction unit tests that do not exercise the cache (harmless skip);
        // DI always supplies the registered singleton.
        NodePoller.RemoveNodeGate(id);
        adapterCache?.ClearForNode(id);
    }

    // ── D-09 CR-02 closure (Phase 3): split metadata + password updates ─────
    // PATTERN: single-property ExecuteUpdateAsync. NEVER db.WiFiNodes.Update(entity)
    // — that would re-track the whole row and clobber un-set columns from defaults.
    public async Task UpdateMetadataAsync(int id, string hostname, string username, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WiFiNodes
            .Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Hostname, hostname)
                .SetProperty(n => n.Username, username), ct);
    }

    // Phase 10 SET-03 / SET-04 — per-node enrichment scan settings (D-09). Single-statement
    // ExecuteUpdateAsync writing ONLY SubnetScanEnabled + SubnetScanRanges; never touches
    // Password, HostKeyFingerprint, LastEnrichmentRefreshAt, Hostname, or Username (CR-02-safe).
    public async Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WiFiNodes
            .Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.SubnetScanEnabled, subnetScanEnabled)
                .SetProperty(n => n.SubnetScanRanges, subnetScanRanges), ct);
    }

    public async Task UpdatePasswordAsync(int id, string encryptedPassword, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WiFiNodes
            .Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Password, encryptedPassword), ct);
    }

    // ── D-18 (Phase 3): SET-03 per-node known-hosts cache clear ─────────────
    // M-HRD-03 / v1.0 (plan 21-02): the obsolete all-rows bulk-reset variant was removed;
    // this per-node path is now the sole re-trust surface. CR-02-safe single-property update.
    public async Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WiFiNodes
            .Where(n => n.Id == nodeId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.HostKeyFingerprint, (string?)null), ct);
    }

    public async Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WiFiNodes.AsNoTracking()
            .AnyAsync(n => !string.IsNullOrEmpty(n.Password), ct);
    }

    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WiFiNodes.AsNoTracking().AnyAsync(ct);
    }

    // ── Cycle-shaped: repo-owned overloads delegate to caller-owned ─────────
    public async Task MarkAllOfflineAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await MarkAllOfflineAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    // ── Cycle-shaped: caller-owned overloads do NOT call SaveChangesAsync ───
    public Task MarkAllOfflineAsync(DatabaseContext db, CancellationToken ct = default)
    {
        return db.WiFiNodes
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, Status.Offline), ct);
    }

    public async Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await UpdateOnlineAsync(db, nodeId, lastPoll, ct);
        await db.SaveChangesAsync(ct);
    }

    public Task UpdateOnlineAsync(DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default)
    {
        return db.WiFiNodes
            .Where(n => n.Id == nodeId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status,       Status.Online)
                .SetProperty(n => n.LastPollDate, lastPoll), ct);
    }

    // ── D-02 TOFU: caller-owned single-property update; CR-02-safe ──────────
    // Uses ExecuteUpdateAsync on the HostKeyFingerprint column ONLY. Never
    // db.WiFiNodes.Update(entity) — that would attach a stub with default
    // Password="" and clobber the encrypted ciphertext (CR-02, Phase 4 binding).
    public Task SetHostKeyFingerprintAsync(DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) =>
        db.WiFiNodes
            .Where(n => n.Id == nodeId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.HostKeyFingerprint, fingerprint), ct);

    // Phase 10 ORCH-07 — caller-owned single-property update for LastEnrichmentRefreshAt.
    // CR-02-safe: ExecuteUpdateAsync touches only the timestamp column; never db.WiFiNodes.Update(entity).
    public Task UpdateLastEnrichmentRefreshAtAsync(DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) =>
        db.WiFiNodes
            .Where(n => n.Id == nodeId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastEnrichmentRefreshAt, (DateTimeOffset?)ts), ct);
}
