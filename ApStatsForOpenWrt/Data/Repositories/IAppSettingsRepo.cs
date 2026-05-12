using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Data.Repositories;

/// <summary>
/// Persistence seam for the <see cref="AppSetting"/> key-value table. Keeps
/// <c>IDbContextFactory&lt;DatabaseContext&gt;</c> exclusive to the
/// <c>ApStatsForOpenWrt.Data.Repositories</c> namespace per PER-04 (ROADMAP §Phase 1
/// Success Criterion 5: "Repositories are the only code that touches DbContext").
/// AppSettingsService is the singleton typed accessor (D-11) — it consumes this
/// repo, NEVER an EF factory directly.
/// </summary>
public interface IAppSettingsRepo
{
    /// <summary>Returns every AppSetting row keyed by <see cref="AppSetting.Key"/>.
    /// Read paths use <c>AsNoTracking</c>; the table is bounded (≤ 3 rows in Phase 1).</summary>
    Task<IReadOnlyDictionary<string, AppSetting>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts a row if absent or updates fields in place. Single-statement upsert
    /// per call (no batching needed at Phase 1's row counts). The repo opens its own context
    /// (D-07 repo-owned shape; AppSettings has no cycle-shaped caller-owned overload because
    /// it is not part of the polling coordinator transaction).</summary>
    Task UpsertAsync(string key, AppSetting value, CancellationToken ct = default);
}
