using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data;
using Xunit;

namespace OpenWrtApStats.Tests.Migrations;

/// <summary>
/// v0.9.2 — proves the AddFirstSeenToStation migration backfills FirstSeen from
/// LastPollDate for pre-existing rows. Mirrors the AddV091EnrichmentColumnsTests
/// per-test temp-file SQLite pattern (NOT SqliteInMemoryFixture, which migrates ALL
/// migrations before any seeding happens — leaving no window at the v0.9.1 schema
/// point to seed a pre-FirstSeen row).
///
/// Strategy: migrate up to AddV091EnrichmentColumns only, seed a WLANStation row via
/// raw SQL (the v0.9.1 schema lacks the FirstSeen column so the EF change tracker
/// would write a default into a non-existent column), then apply the AddFirstSeenToStation
/// migration and assert the backfill produced FirstSeen == LastPollDate with no
/// 0001-01-01 rows remaining.
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
    /// Backfill correctness: a WLANStation row seeded at the v0.9.1 schema point with a known
    /// LastPollDate gets FirstSeen == that LastPollDate after the new migration runs. Sentinel
    /// 0001-01-01 row count is zero post-migration.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_BackfillsFirstSeenFromLastPollDate_ForPreExistingRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = BuildFactory();

        // Step 1: migrate up to v0.9.1 head (AddV091EnrichmentColumns) — FirstSeen column does
        // NOT exist yet. WLANStations.WiFiNetworkId is nullable post-NODE-04 (Phase 3).
        var knownLastSeen = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        await using (var preDb = await factory.CreateDbContextAsync(ct))
        {
            await preDb.Database.MigrateAsync("AddV091EnrichmentColumns", ct);

            // Step 2: seed v0.9.1-shaped data via raw SQL. The EF runtime model carries the
            // v0.9.2 FirstSeen property but THIS DB schema does NOT, so the change tracker
            // is not usable here (it would emit INSERT … FirstSeen and fail on no-such-column).
            // The v0.9.1 schema already includes IsRandomMac (defaultValue=0) — set it to 0
            // explicitly to be type-safe.
            await preDb.Database.ExecuteSqlRawAsync(
                "INSERT INTO WiFiNodes (Hostname, Username, Password, Status, LastPollDate) " +
                "VALUES ('v092-seed-node', 'op', '', 0, '2026-01-01 00:00:00'); " +
                "INSERT INTO WiFiNetworks (WiFiNodeId, SSID, Channel, Width, MacAddress, TxPower, Enabled) " +
                "VALUES (1, 'v092-ssid', 1, 0, 'BB:CC:DD:EE:FF:01', 0, 1); " +
                "INSERT INTO WLANStations (WiFiNetworkId, MacAddress, IPAddress, Hostname, TxBitrate, RxBitrate, RSS, Status, LastPollDate, IsRandomMac) " +
                "VALUES (1, 'AA:BB:CC:DD:EE:01', NULL, NULL, 0, 0, NULL, 0, '2026-01-15 12:00:00', 0);",
                ct);
        }

        // Step 3: apply the AddFirstSeenToStation migration (head). Runs the raw-SQL backfill.
        await using (var postDb = await factory.CreateDbContextAsync(ct))
        {
            await postDb.Database.MigrateAsync(ct);

            // Step 4a: assert the seeded row's FirstSeen == its LastPollDate.
            var row = await postDb.WLANStations.AsNoTracking()
                .SingleAsync(s => s.MacAddress == "AA:BB:CC:DD:EE:01", ct);
            Assert.Equal(knownLastSeen, DateTime.SpecifyKind(row.FirstSeen, DateTimeKind.Utc));
            Assert.Equal(
                DateTime.SpecifyKind(row.LastPollDate, DateTimeKind.Utc),
                DateTime.SpecifyKind(row.FirstSeen, DateTimeKind.Utc));

            // Step 4b: no rows remain at the 0001-01-01 sentinel.
            var sentinelCount = await postDb.WLANStations.AsNoTracking()
                .CountAsync(s => s.FirstSeen == new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc), ct);
            Assert.Equal(0, sentinelCount);
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
