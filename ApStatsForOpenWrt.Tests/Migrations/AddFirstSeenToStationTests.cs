using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Migrations;

/// <summary>
/// v0.9.2 — proves the regenerated InitialCreate baseline applies the FirstSeen column on
/// WLANStations against a fresh SQLite database.
///
/// Historical context (pre-Phase 12 Wave B): this fixture originally exercised the
/// <c>AddFirstSeenToStation</c> migration's raw-SQL backfill of FirstSeen from LastPollDate
/// for pre-existing v0.9.1 rows. Phase 12 Wave B (RUNTIME-03) collapsed the 6-migration
/// chain into a single <c>InitialCreate</c> baseline per CONTEXT.md D-01 ("pure delete + regen
/// — no schema-parity exercise, no data port"); milestone v0.10 is a clean-break, so there are
/// no pre-existing rows to back-fill at boot time. The backfill <c>[Fact]</c> was removed
/// because the named intermediate migration target (<c>AddV091EnrichmentColumns</c>) no longer
/// exists. The schema-presence assertion below survives — it remains a useful drift detector
/// for the regenerated baseline's column shape.
/// </summary>
public sealed class AddFirstSeenToStationTests : IDisposable
{
    private readonly List<string> _tempDbPaths = [];

    private (IDbContextFactory<DatabaseContext> factory, string dbPath) BuildFactory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"v0-9-2-{Guid.NewGuid():N}.db");
        _tempDbPaths.Add(dbPath);
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return (new TempFileDbContextFactory(options), dbPath);
    }

    public void Dispose()
    {
        foreach (var path in _tempDbPaths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup — Windows file-handle quirks should not fail the run.
            }
        }
    }

    /// <summary>
    /// Schema invariant: after MigrateAsync against a fresh DB the FirstSeen column exists on
    /// WLANStations.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_AppliesCleanly_AddsFirstSeenColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = BuildFactory();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        var stationCols = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('WLANStations')")
            .ToListAsync(ct);
        Assert.Contains("FirstSeen", stationCols);
    }

    /// <summary>
    /// Test-only IDbContextFactory bound to a SQLite file connection string.
    /// </summary>
    private sealed class TempFileDbContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
    {
        public DatabaseContext CreateDbContext() => new(options);
    }
}
