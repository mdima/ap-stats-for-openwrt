using OpenWrtApStats.Data.Models;

namespace OpenWrtApStats.Data.Repositories;

/// <summary>
/// Repository contract for <see cref="WiFiNode"/>. Method shapes lock decisions
/// D-05 (CRUD + cycle-shaped methods), D-06 (async-only with default
/// <see cref="CancellationToken"/>; POCOs returned), and D-07 (dual scope-ownership
/// overloads — repo-owned commits its own changes, caller-owned does NOT call
/// <c>SaveChangesAsync</c> so the orchestrator can compose a coordinator transaction).
/// </summary>
public interface INodeRepo
{
    // ── CRUD (repo-owned only — used by Phase 3 UI) ─────────────────────────
    Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default);
    Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default);
    Task AddAsync(WiFiNode node, CancellationToken ct = default);
    Task UpdateAsync(WiFiNode node, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>D-12 probe seam: returns true if any WiFiNode has a non-empty Password
    /// (encrypted credential blob). Used by DataProtectionHealthHostedService at bootstrap to
    /// detect a key-ring/DB mismatch — keeps DbContext access exclusive to repositories per PER-04.</summary>
    Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default);

    // ── Cycle-shaped (D-07 dual flavors — used by Phase 2 orchestrator) ─────
    /// <summary>Repo-owned scope. Used by smoke tests and one-off callers.</summary>
    Task MarkAllOfflineAsync(CancellationToken ct = default);

    /// <summary>Caller-owned scope. The PollOrchestrator wraps this in its outer
    /// <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/>.</summary>
    Task MarkAllOfflineAsync(DatabaseContext db, CancellationToken ct = default);

    Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default);
    Task UpdateOnlineAsync(DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default);

    /// <summary>
    /// Caller-owned. Used by <c>PollOrchestrator</c> on first-connect TOFU (D-02) to persist the
    /// captured fingerprint inside the same coordinator transaction as the rest of the cycle's
    /// writes. Repo-owned overload is intentionally NOT exposed — committing a fingerprint
    /// outside the coordinator would violate Pitfall 6's single-writer constraint. CR-02-safe:
    /// uses single-property <c>ExecuteUpdateAsync</c>, never <c>db.WiFiNodes.Update(entity)</c>
    /// (which would clobber the encrypted <c>Password</c>).
    /// </summary>
    Task SetHostKeyFingerprintAsync(DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default);
}
