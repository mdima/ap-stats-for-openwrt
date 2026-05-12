using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Data.Repositories;

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
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// CR-02 closure (D-09 / RESEARCH §Pitfall 5). Updates Hostname + Username only via
    /// single-statement <c>ExecuteUpdateAsync</c> — never touches Password or HostKeyFingerprint.
    /// Repo-owned only: the Edit-Node UI is a one-shot operation outside any coordinator
    /// transaction. Replaces the deleted <c>UpdateAsync(WiFiNode)</c> shape that re-tracked
    /// the whole row and clobbered the encrypted Password from a stub's default value.
    /// D-10: this surface preserves HostKeyFingerprint on hostname/username edit — TOFU identity is row Id, not hostname.
    /// </summary>
    Task UpdateMetadataAsync(int id, string hostname, string username, CancellationToken ct = default);

    /// <summary>
    /// Phase 10 SET-03 / SET-04 (CR-02 closure carry-over from D-09). Updates ONLY
    /// <see cref="WiFiNode.SubnetScanEnabled"/> and <see cref="WiFiNode.SubnetScanRanges"/>
    /// via single-statement <c>ExecuteUpdateAsync</c> — never touches Password,
    /// HostKeyFingerprint, LastEnrichmentRefreshAt, Hostname, or Username. Repo-owned
    /// only: the Edit-Node UI is a one-shot operation outside any coordinator transaction.
    /// </summary>
    Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default);

    /// <summary>
    /// CR-02 closure (D-09). Updates Password only via single-statement <c>ExecuteUpdateAsync</c>.
    /// Caller passes the already-encrypted ciphertext (Data Protection "node-creds" purpose).
    /// Empty-password "no change" semantics live at the UI layer (D-08): callers MUST NOT
    /// invoke this method with empty <paramref name="encryptedPassword"/>.
    /// </summary>
    Task UpdatePasswordAsync(int id, string encryptedPassword, CancellationToken ct = default);

    /// <summary>
    /// D-18 SET-03 bulk path. Sets <c>HostKeyFingerprint = null</c> on every <see cref="WiFiNode"/>.
    /// CR-02-safe: single-property <c>ExecuteUpdateAsync</c> — never touches Password.
    /// </summary>
    Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default);

    /// <summary>
    /// D-18 SET-03 per-node path. Single-row variant of <see cref="ClearAllHostKeyFingerprintsAsync"/>.
    /// Used by the per-node Re-trust button on /Nodes/{id}.
    /// </summary>
    Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default);

    /// <summary>D-12 probe seam: returns true if any WiFiNode has a non-empty Password
    /// (encrypted credential blob). Used by DataProtectionHealthHostedService at bootstrap to
    /// detect a key-ring/DB mismatch — keeps DbContext access exclusive to repositories per PER-04.</summary>
    Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default);

    /// <summary>
    /// ONB-01 auto-redirect predicate. Returns true if any WiFiNode row exists.
    /// Single AsNoTracking().AnyAsync() — avoids loading full node list for a boolean check.
    /// </summary>
    Task<bool> AnyAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Phase 10 ORCH-07 caller-owned. Updates ONLY <see cref="WiFiNode.LastEnrichmentRefreshAt"/>;
    /// composed by <c>PollOrchestrator.RunCycleAsync</c> at the end of a successful enrichment tick,
    /// inside the same outer coordinator transaction as the station upsert. CR-02-safe single-property
    /// <c>ExecuteUpdateAsync</c> — never <c>db.WiFiNodes.Update(entity)</c>.
    /// </summary>
    Task UpdateLastEnrichmentRefreshAtAsync(DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default);
}
