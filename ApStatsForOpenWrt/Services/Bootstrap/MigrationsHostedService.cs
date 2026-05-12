using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data;

namespace ApStatsForOpenWrt.Services.Bootstrap;

/// <summary>
/// Boot-time runner for EF Core migrations (INF-03). Owns the migration pipeline directly —
/// this is the single carve-out from PER-04 (one full type name, registered in
/// <c>ArchitectureTests.DbContext_IsOnlyUsedByRepositories</c>) because there is no repo seam
/// below the migration system itself.
/// <para>
/// Order: registered FIRST in <c>Program.cs</c> so the schema exists before
/// <c>DataProtectionHealthHostedService</c> queries <c>WiFiNodes</c> (Pitfall 10).
/// </para>
/// <para>
/// Failure mode: <see cref="DatabaseFacade.MigrateAsync(CancellationToken)"/> is intentionally
/// NOT wrapped in try/catch (Pitfall: migration silent failure). A failed migration MUST crash
/// the process so the container orchestrator (Phase 7) sees exit 1 and marks the container failed.
/// </para>
/// </summary>
public sealed class MigrationsHostedService(
    IDbContextFactory<DatabaseContext> factory,
    ILogger<MigrationsHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Idempotent — WSL2/container first-run convenience. /data and /logs are hardcoded per D-13.
        Directory.CreateDirectory("/data");
        Directory.CreateDirectory("/logs");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            log.LogInformation(
                "Applying {Count} pending migrations: {Migrations}",
                pending.Count, string.Join(", ", pending));
        }

        // Fail fast on error (PITFALL: migration silent failure).
        // Do NOT wrap this in try/catch — a failed migration MUST crash the process.
        await db.Database.MigrateAsync(cancellationToken);

        log.LogInformation("Database migration complete");

        // PRAGMA journal_mode=WAL is persisted on the DB file (one-shot per file). Once set,
        // every subsequent connection sees WAL — readers do not block writers and writers do
        // not block readers at the file level. PRAGMA synchronous=NORMAL is the recommended
        // pairing with WAL (durability is preserved across normal shutdowns; only correlated
        // power loss + OS crash can lose committed-but-not-checkpointed pages — acceptable for
        // this workload). synchronous is per-connection, so this command only affects the
        // current connection — but NORMAL is also the SQLite default for WAL mode in newer
        // versions, so omitting it on later connections is safe.
        // Closes Phase 1 PITFALLS.md §SQLite WAL gap surfaced by Phase 6 live testing.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(cancellationToken);

        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var verify = conn.CreateCommand())
        {
            verify.CommandText = "PRAGMA journal_mode;";
            var journalMode = (await verify.ExecuteScalarAsync(cancellationToken)) as string;
            log.LogInformation("SQLite journal_mode: {JournalMode}", journalMode);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
