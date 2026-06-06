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

        // M-HRD-02 / T10: resolve the DB path the SAME way the connection string does
        // (Program.cs: Data Source={DATA_ROOT}/AP-Stats-for-OpenWrt.db). Hardcoding "/data"
        // here would break local Windows dev, which sets DATA_ROOT to redirect storage.
        // WR-04 (21-REVIEW): build the path with Path.Combine (matching MigrationsBackupTests'
        // harness) instead of "{dataRoot}/..." string interpolation. The interpolation only
        // happened to work on the documented Windows dev path (DATA_ROOT=C:\dev\data) because
        // Windows tolerates a forward slash; a trailing separator in DATA_ROOT ("/data/") would
        // also yield a "//AP-Stats..." double-slash .bak path. Trim a trailing separator once so
        // Path.Combine produces a single clean separator on every platform.
        var dataRoot = (Environment.GetEnvironmentVariable("DATA_ROOT") ?? "/data")
            .TrimEnd('/', '\\');
        var dbPath = Path.Combine(dataRoot, "AP-Stats-for-OpenWrt.db");

        // M-HRD-02 fresh-volume guard (RESEARCH Pitfall 2): snapshot existence BEFORE
        // CreateDbContextAsync — GetPendingMigrationsAsync opens a connection that materializes
        // an empty .db, so File.Exists after that point would falsely report a pre-existing DB.
        var dbPreExisted = File.Exists(dbPath);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            log.LogInformation(
                "Applying {Count} pending migrations: {Migrations}",
                pending.Count, string.Join(", ", pending));
        }

        // M-HRD-02 / T10: a single rolling pre-migration .bak set protects operator data before
        // any schema migration runs. Pure File.Copy (NOT a DB write, no checkpoint connection) —
        // copying the -wal sidecar captures committed-but-not-checkpointed data after a dirty
        // shutdown, which is precisely why no checkpoint is needed (D-01). Trigger = migrations
        // pending AND a pre-existing DB (a fresh volume has nothing to protect — D-04). Rolling
        // overwrite each start (T10). A copy failure ABORTS the boot before MigrateAsync — D-04
        // forbids ever migrating without the safety net.
        if (pending.Count > 0 && dbPreExisted)
        {
            try
            {
                // WR-03 (21-REVIEW): copy-to-temp-then-promote so a mid-copy crash (OOM,
                // container stop, full volume) can never leave a TRUNCATED .bak that has already
                // clobbered the previous-cycle good backup. File.Copy(overwrite: true) truncates
                // the destination at the START of the copy, so writing straight to ".bak" is the
                // hazard. Instead: copy each source to a ".bak.tmp" (a torn .tmp is discardable —
                // it never overwrote a good .bak), then File.Move(tmp -> .bak, overwrite: true) to
                // promote it. The Move is the metadata-only rename of an already-complete file, so
                // the previous good .bak is replaced atomically rather than truncated-in-place.
                var sources = new List<string> { dbPath };
                // D-01/D-02 naming: AP-Stats-for-OpenWrt.db-wal.bak / -shm.bak — copied ONLY
                // when the sidecar actually exists (WAL may be absent in DELETE journal mode,
                // -shm absent when no connection is currently mapping shared memory).
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = dbPath + suffix;
                    if (File.Exists(sidecar))
                        sources.Add(sidecar);
                }

                // Phase 1: copy every source to its .bak.tmp (full write before any promotion).
                // Discard any torn .tmp left by a prior interrupted run first.
                foreach (var src in sources)
                {
                    var tmp = src + ".bak.tmp";
                    if (File.Exists(tmp)) File.Delete(tmp);
                    File.Copy(src, tmp, overwrite: true);
                }

                // Phase 2: promote each completed .bak.tmp over its .bak (atomic rename).
                foreach (var src in sources)
                {
                    File.Move(src + ".bak.tmp", src + ".bak", overwrite: true);
                }

                log.LogInformation(
                    "Pre-migration backup written before applying {Count} pending migration(s): {Bak} (+ -wal.bak/-shm.bak when present)",
                    pending.Count, dbPath + ".bak");
            }
            catch (Exception ex)
            {
                // D-03/D-04: name the .bak files and the one-line restore action, then rethrow —
                // NEVER swallow. The process exits 1 and migrations are NOT applied; the operator
                // still has the untouched live DB to fall back on.
                log.LogCritical(
                    ex,
                    "Pre-migration backup FAILED for {Db} — ABORTING before Database.Migrate() so no schema change is applied (M-HRD-02 / T10). " +
                    "Recovery: stop the container, restore by renaming {Bak} (and {WalBak} / {ShmBak} if present) back to the live DB names, then run the PREVIOUS image. Migrations were NOT applied.",
                    dbPath, dbPath + ".bak", dbPath + "-wal.bak", dbPath + "-shm.bak");
                throw;
            }
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
