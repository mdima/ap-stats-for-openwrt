using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data.Models;

namespace OpenWrtApStats.Data.Repositories;

/// <summary>
/// <see cref="INodeRepo"/> implementation. Per PER-05, NEVER constructor-inject a scoped
/// <see cref="DatabaseContext"/>; always go through <see cref="IDbContextFactory{TContext}"/>.
/// The dual-overload pattern (D-07) routes repo-owned overloads through caller-owned
/// implementations: repo-owned opens its own context + commits; caller-owned does NOT call
/// <c>SaveChangesAsync</c> (the orchestrator owns the transaction).
/// </summary>
public sealed class NodeRepo(IDbContextFactory<DatabaseContext> factory) : INodeRepo
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

    public async Task UpdateAsync(WiFiNode node, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.WiFiNodes.Update(node);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.WiFiNodes.FindAsync([id], ct);
        if (entity is null) return;
        db.WiFiNodes.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WiFiNodes.AsNoTracking()
            .AnyAsync(n => !string.IsNullOrEmpty(n.Password), ct);
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
}
