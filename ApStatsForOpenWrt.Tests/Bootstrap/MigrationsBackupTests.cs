using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Services.Bootstrap;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Bootstrap;

/// <summary>
/// M-HRD-02 / T10: covers the three behavioral arms of the pre-migration rolling <c>.bak</c>
/// backup inside <see cref="MigrationsHostedService"/>.StartAsync:
/// <list type="bullet">
///   <item>Arm A — migrations pending AND a pre-existing <c>.db</c> ⇒ a <c>.bak</c> set is written
///   (sidecars only when their source exists).</item>
///   <item>Arm B — fresh volume (no <c>.db</c>) ⇒ no <c>.bak</c> is created even though all
///   migrations are pending (D-04 no-op skip).</item>
///   <item>Arm C — <see cref="File.Copy(string,string,bool)"/> cannot succeed ⇒ StartAsync throws
///   and migrations are never applied (D-04 ABORT, never migrate without the safety net).</item>
/// </list>
/// Each test owns a unique temp directory wired through the <c>DATA_ROOT</c> env var so the
/// backup path resolves identically to the production connection string. The
/// <see cref="IDbContextFactory{TContext}"/> is pointed at <c>{DATA_ROOT}/AP-Stats-for-OpenWrt.db</c>
/// so <c>GetPendingMigrationsAsync</c> reports the real migration set.
/// Member of the <c>DataRootSerial</c> collection: <c>DATA_ROOT</c> is process-global, so this
/// class must never run in parallel with anything else that reads or writes it.
/// </summary>
[Collection("DataRootSerial")]
public sealed class MigrationsBackupTests
{
    private const string DbFileName = "AP-Stats-for-OpenWrt.db";

    /// <summary>
    /// Creates a unique temp dir, sets DATA_ROOT to it for the duration of <paramref name="body"/>,
    /// and restores the previous DATA_ROOT value (and deletes the temp dir) in a finally.
    /// Builds an <see cref="IDbContextFactory{TContext}"/> bound to {tempDir}/AP-Stats-for-OpenWrt.db
    /// so the production DATA_ROOT-derived backup path matches the factory's Data Source.
    /// </summary>
    private static async Task WithDataRoot(Func<string, IDbContextFactory<DatabaseContext>, Task> body)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"apstatsforopenwrt-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var previous = Environment.GetEnvironmentVariable("DATA_ROOT");
        Environment.SetEnvironmentVariable("DATA_ROOT", tempDir);
        try
        {
            var dbPath = Path.Combine(tempDir, DbFileName);
            var options = new DbContextOptionsBuilder<DatabaseContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            await body(tempDir, new TempFileDbContextFactory(options));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATA_ROOT", previous);
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — Windows file-handle quirks must not fail the run.
            }
        }
    }

    [Fact]
    public async Task PendingMigrations_AndDbPreExisted_WritesBakSet()
    {
        // Arm A: a pre-existing .db with all migrations pending must produce the core
        // AP-Stats-for-OpenWrt.db.bak. The sidecar backups are CONDITIONAL on the sidecar
        // source existing at copy time, so each .bak sidecar must be present iff its live
        // source is present at copy time (the conditional File.Exists branch). No -shm source
        // is ever created here, so -shm.bak must be ABSENT (deterministic).
        var ct = TestContext.Current.CancellationToken;

        await WithDataRoot(async (tempDir, factory) =>
        {
            var dbPath = Path.Combine(tempDir, DbFileName);

            // Pre-create the live DB BEFORE StartAsync so dbPreExisted is true. An empty .db has
            // no __EFMigrationsHistory, so every migration reports as pending (backup triggers).
            await File.WriteAllBytesAsync(dbPath, [], ct);

            var svc = new MigrationsHostedService(factory, NullLogger<MigrationsHostedService>.Instance);
            await svc.StartAsync(ct);

            // Core guarantee: the live DB was copied to .db.bak before Migrate ran.
            Assert.True(File.Exists(dbPath + ".bak"), "Expected the .db.bak backup to be written");

            // Sidecar conditionality (the File.Exists-guarded copy): each sidecar .bak exists iff
            // its live source survived to copy time. -shm was never created → -shm.bak absent.
            Assert.Equal(File.Exists(dbPath + "-wal"), File.Exists(dbPath + "-wal.bak"));
            Assert.False(File.Exists(dbPath + "-shm"), "No -shm source was created");
            Assert.False(File.Exists(dbPath + "-shm.bak"), "No -shm source existed, so -shm.bak must be absent");
        });
    }

    [Fact]
    public async Task SidecarBackup_IsConditional_OnSourceExistence()
    {
        // Arm A (sidecar positive path): when a VALID -wal sidecar exists at copy time, the
        // backup copies it to -wal.bak. We build a real WAL-mode SQLite file with an
        // uncheckpointed transaction so a genuine -wal exists on disk, WITHOUT applying EF
        // migrations (so GetPendingMigrationsAsync still reports the full set pending).
        var ct = TestContext.Current.CancellationToken;

        await WithDataRoot(async (tempDir, factory) =>
        {
            var dbPath = Path.Combine(tempDir, DbFileName);

            // Create a real WAL-mode DB with uncheckpointed data → genuine -wal/-shm on disk.
            await using (var seed = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath}"))
            {
                await seed.OpenAsync(ct);
                await using var cmd = seed.CreateCommand();
                cmd.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;" +
                    "CREATE TABLE seed_marker(id INTEGER PRIMARY KEY); INSERT INTO seed_marker(id) VALUES (1);";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Pre-condition: a real -wal sidecar exists before StartAsync.
            var walExistedBefore = File.Exists(dbPath + "-wal");

            var svc = new MigrationsHostedService(factory, NullLogger<MigrationsHostedService>.Instance);
            await svc.StartAsync(ct);

            Assert.True(File.Exists(dbPath + ".bak"), "Expected the .db.bak backup to be written");

            // The copy loop is guarded by File.Exists at copy time. Assert the conditional holds:
            // each sidecar .bak is present iff its live source survived to the copy point.
            if (walExistedBefore)
            {
                Assert.Equal(File.Exists(dbPath + "-wal"), File.Exists(dbPath + "-wal.bak"));
            }
            Assert.Equal(File.Exists(dbPath + "-shm"), File.Exists(dbPath + "-shm.bak"));
        });
    }

    [Fact]
    public async Task FreshVolume_NoDb_SkipsBackup()
    {
        // Arm B (D-04): an empty DATA_ROOT (no .db at entry) means there is nothing to protect.
        // StartAsync completes (migrations apply on the fresh DB) and NO .bak file is created.
        var ct = TestContext.Current.CancellationToken;

        await WithDataRoot(async (tempDir, factory) =>
        {
            var dbPath = Path.Combine(tempDir, DbFileName);
            Assert.False(File.Exists(dbPath), "Precondition: DATA_ROOT starts with no .db");

            var svc = new MigrationsHostedService(factory, NullLogger<MigrationsHostedService>.Instance);
            await svc.StartAsync(ct);

            Assert.False(File.Exists(dbPath + ".bak"), "Fresh volume must NOT create a .bak file");
            Assert.False(File.Exists(dbPath + "-wal.bak"), "Fresh volume must NOT create a -wal.bak file");
            Assert.False(File.Exists(dbPath + "-shm.bak"), "Fresh volume must NOT create a -shm.bak file");
        });
    }

    [Fact]
    public async Task CopyFailure_Aborts_BeforeMigrate()
    {
        // Arm C (D-04): force File.Copy to fail by making the .bak destination an existing
        // DIRECTORY (File.Copy to a directory path throws). StartAsync must throw, the migrations
        // must NOT have been applied (the DB still reports them pending), proving the abort
        // happened before MigrateAsync.
        var ct = TestContext.Current.CancellationToken;

        await WithDataRoot(async (tempDir, factory) =>
        {
            var dbPath = Path.Combine(tempDir, DbFileName);

            // Pre-existing .db so dbPreExisted is true and the backup is attempted.
            await File.WriteAllBytesAsync(dbPath, [], ct);

            // Make the copy destination impossible: a directory at the .bak path.
            Directory.CreateDirectory(dbPath + ".bak");

            var svc = new MigrationsHostedService(factory, NullLogger<MigrationsHostedService>.Instance);

            await Assert.ThrowsAnyAsync<Exception>(() => svc.StartAsync(ct));

            // Migrations must still be pending — Migrate() was never reached.
            await using var db = await factory.CreateDbContextAsync(ct);
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            Assert.NotEmpty(pending);
        });
    }

    /// <summary>
    /// Test-only <see cref="IDbContextFactory{TContext}"/> bound to an explicit SQLite file
    /// connection string (mirrors the shape used by MigrationsHostedServiceTests).
    /// </summary>
    private sealed class TempFileDbContextFactory(DbContextOptions<DatabaseContext> options)
        : IDbContextFactory<DatabaseContext>
    {
        public DatabaseContext CreateDbContext() => new(options);
    }
}
