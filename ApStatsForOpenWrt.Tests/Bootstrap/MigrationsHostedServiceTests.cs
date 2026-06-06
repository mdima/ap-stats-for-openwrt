using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Services.Bootstrap;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Bootstrap;

/// <summary>
/// Verifies <see cref="MigrationsHostedService"/> exercises the EF migration pipeline against
/// a per-test SQLite file. INF-02 is verified here as a PROXY (the production hardcoded path
/// "/data/AP-Stats-for-OpenWrt.db" is exercised by a manual one-time WSL2 run, recorded in
/// <c>01-VALIDATION.md §"Manual-Only Verifications"</c>); the factory-redirect proxy validates
/// the migration pipeline itself. INF-03 (idempotent re-run) is asserted directly.
/// Member of the <c>DataRootSerial</c> collection: <c>MigrationsHostedService.StartAsync</c>
/// reads the process-global <c>DATA_ROOT</c> env var (M-HRD-02 backup path), which
/// <c>MigrationsBackupTests</c> mutates — the two classes must never run in parallel.
/// </summary>
[Collection("DataRootSerial")]
public sealed class MigrationsHostedServiceTests(TempDirectoryFixture temp) : IClassFixture<TempDirectoryFixture>
{
    private readonly TempDirectoryFixture _temp = temp;

    private (IDbContextFactory<DatabaseContext> factory, string dbPath) BuildFactory()
    {
        var dbPath = Path.Combine(_temp.DataDir, $"db-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return (new TempFileDbContextFactory(options), dbPath);
    }

    [Fact]
    public async Task CreatesDatabaseFileAt_DataPath()
    {
        // INF-02 (proxy): given a fresh per-test SQLite file path, StartAsync must create
        // the file on disk AND the seed row must be visible through the migrated schema.
        var ct = TestContext.Current.CancellationToken;
        var (factory, dbPath) = BuildFactory();

        var svc = new MigrationsHostedService(factory, NullLogger<MigrationsHostedService>.Instance);
        await svc.StartAsync(ct);

        Assert.True(File.Exists(dbPath), $"Expected DB file at {dbPath} after migration");

        await using var db = await factory.CreateDbContextAsync(ct);
        var seedExists = await db.AppSettings.AnyAsync(a => a.Key == "PollingIntervalSeconds", ct);
        Assert.True(seedExists, "Seed AppSetting (PollingIntervalSeconds) must be present after migration");
    }

    [Fact]
    public async Task AppliesPendingMigrations_ThenIsIdempotent()
    {
        // INF-03: the second StartAsync against the same factory must be idempotent —
        // GetPendingMigrationsAsync returns empty after the first apply.
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = BuildFactory();

        var svc = new MigrationsHostedService(factory, NullLogger<MigrationsHostedService>.Instance);

        // First run applies the migration.
        await svc.StartAsync(ct);

        await using (var db1 = await factory.CreateDbContextAsync(ct))
        {
            var pendingAfterFirst = (await db1.Database.GetPendingMigrationsAsync(ct)).ToList();
            Assert.Empty(pendingAfterFirst);
        }

        // Second run is a no-op — does not throw, does not duplicate the seed.
        await svc.StartAsync(ct);

        await using var db2 = await factory.CreateDbContextAsync(ct);
        var pendingAfterSecond = (await db2.Database.GetPendingMigrationsAsync(ct)).ToList();
        Assert.Empty(pendingAfterSecond);

        var seedCount = await db2.AppSettings.CountAsync(a => a.Key == "PollingIntervalSeconds", ct);
        Assert.Equal(1, seedCount);
    }

    /// <summary>
    /// Test-only IDbContextFactory bound to a SQLite file connection string. Mirrors the
    /// production <see cref="TestDbContextFactory"/> shape but with explicit options so each
    /// test gets its own per-fact SQLite file under TempDirectoryFixture.DataDir.
    /// </summary>
    private sealed class TempFileDbContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
    {
        public DatabaseContext CreateDbContext() => new(options);
    }
}
