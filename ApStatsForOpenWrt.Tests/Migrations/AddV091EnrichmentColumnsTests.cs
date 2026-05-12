using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Migrations;

/// <summary>
/// Phase 9 D-14 migration-apply test for <c>AddV091EnrichmentColumns</c>. Each fact builds a
/// fresh per-test SQLite file (NOT the InMemory provider — STACK.md ban) so the migration
/// chain runs end-to-end. The U/L-bit backfill (D-08) is exercised by seeding the database
/// at the v0.9 head (<c>Phase04_RSS_Nullable</c>) via raw SQL, then applying the Phase 9
/// migration and asserting <c>IsRandomMac</c> was set correctly per row.
///
/// Covers SCHEMA-01..06, ENRICH-07: column presence, default values, raw-SQL backfill, AppSetting seeds.
/// </summary>
public sealed class AddV091EnrichmentColumnsTests : IDisposable
{
    private readonly List<string> _tempDbPaths = [];

    private (IDbContextFactory<DatabaseContext> factory, string dbPath) BuildFactory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"phase9-{Guid.NewGuid():N}.db");
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
    /// Schema invariant (SCHEMA-01..04, SCHEMA-06): after MigrateAsync against a fresh DB the
    /// four new columns exist on the expected tables and the two new AppSetting seeds are present.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_AppliesCleanly_OnFreshDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = BuildFactory();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        // WiFiNodes — three new columns visible via pragma_table_info.
        var wifiNodeCols = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('WiFiNodes')")
            .ToListAsync(ct);
        Assert.Contains("SubnetScanRanges", wifiNodeCols);
        Assert.Contains("SubnetScanEnabled", wifiNodeCols);
        Assert.Contains("LastEnrichmentRefreshAt", wifiNodeCols);

        // WLANStations — IsRandomMac visible.
        var stationCols = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('WLANStations')")
            .ToListAsync(ct);
        Assert.Contains("IsRandomMac", stationCols);

        // Two new AppSetting seed rows present (full assertion is the dedicated fact below).
        var keyCount = await db.AppSettings
            .CountAsync(r => r.Key == "RefreshIntervalSeconds" || r.Key == "LocalDomainSuffixes", ct);
        Assert.Equal(2, keyCount);
    }

    /// <summary>
    /// SCHEMA-05 / ENRICH-07 / D-08: existing WLANStation rows are back-filled correctly from
    /// the U/L bit on MAC byte 0.
    ///
    /// Strategy: migrate up to the v0.9 head (Phase04_RSS_Nullable), seed two stations via
    /// raw SQL (the v0.9 schema lacks the IsRandomMac column so EF tracking cannot be used),
    /// then apply the Phase 9 migration and read <c>IsRandomMac</c> back via raw SQL.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_BackfillsIsRandomMac_FromULBit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = BuildFactory();

        // Step 1: migrate up to v0.9 head (Phase04_RSS_Nullable — pre-Phase 9 schema).
        await using (var preDb = await factory.CreateDbContextAsync(ct))
        {
            await preDb.Database.MigrateAsync("Phase04_RSS_Nullable", ct);

            // Step 2: seed v0.9-shaped data via raw SQL. The EF model carries Phase 9
            // properties (IsRandomMac, SubnetScanRanges, ...) but this DB schema does NOT,
            // so we cannot use the change tracker; raw SQL is the correct seam at this point.
            // Note: at v0.9 head, WLANStations.WiFiNetworkId is non-nullable
            // (SetNull cascade lands in NodeDeleteCascadeBehavior — already applied here).
            await preDb.Database.ExecuteSqlRawAsync(
                "INSERT INTO WiFiNodes (Hostname, Username, Password, Status, LastPollDate) " +
                "VALUES ('seed-node', 'op', '', 0, '2026-01-01T00:00:00Z'); " +
                "INSERT INTO WiFiNetworks (WiFiNodeId, SSID, Channel, Width, MacAddress, TxPower, Enabled) " +
                "VALUES (1, 'seed-ssid', 1, 0, 'AA:BB:CC:DD:EE:FF', 0, 1); " +
                "INSERT INTO WLANStations (WiFiNetworkId, MacAddress, TxBitrate, RxBitrate, RSS, Status, LastPollDate) " +
                "VALUES (1, '02:11:22:33:44:55', 0, 0, NULL, 0, '2026-01-01T00:00:00Z'), " +
                "       (1, '00:11:22:33:44:55', 0, 0, NULL, 0, '2026-01-01T00:00:00Z');",
                ct);
        }

        // Step 3: apply the Phase 9 migration (head). This runs the D-08 backfill.
        await using (var postDb = await factory.CreateDbContextAsync(ct))
        {
            await postDb.Database.MigrateAsync(ct);

            // Step 4: assert backfill correctness via raw SQL (avoids any EF default-value
            // surprises on tracked entities).
            var randomMacRow = await postDb.Database.SqlQueryRaw<int>(
                "SELECT IsRandomMac AS Value FROM WLANStations WHERE MacAddress = '02:11:22:33:44:55'")
                .SingleAsync(ct);
            Assert.Equal(1, randomMacRow); // U/L bit set => IsRandomMac == true => stored as 1.

            var stableMacRow = await postDb.Database.SqlQueryRaw<int>(
                "SELECT IsRandomMac AS Value FROM WLANStations WHERE MacAddress = '00:11:22:33:44:55'")
                .SingleAsync(ct);
            Assert.Equal(0, stableMacRow); // U/L bit clear => IsRandomMac == false => stored as 0.
        }
    }

    /// <summary>
    /// SCHEMA-06 / D-09: after MigrateAsync the two new AppSetting seed rows have the exact
    /// values specified — RefreshIntervalSeconds=3600, LocalDomainSuffixes="[]", with the
    /// non-target value columns left NULL.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_SeedsTwoAppSettingRows_WithCorrectValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = BuildFactory();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        var refresh = await db.AppSettings.AsNoTracking()
            .SingleAsync(r => r.Key == "RefreshIntervalSeconds", ct);
        Assert.Equal(3600, refresh.IntValue);
        Assert.Null(refresh.StringValue);
        Assert.Null(refresh.BoolValue);

        var domains = await db.AppSettings.AsNoTracking()
            .SingleAsync(r => r.Key == "LocalDomainSuffixes", ct);
        Assert.Equal("[]", domains.StringValue);
        Assert.Null(domains.IntValue);
        Assert.Null(domains.BoolValue);
    }

    /// <summary>
    /// Test-only IDbContextFactory bound to a SQLite file connection string. Mirrors the
    /// shape used in <c>MigrationsHostedServiceTests</c>.
    /// </summary>
    private sealed class TempFileDbContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
    {
        public DatabaseContext CreateDbContext() => new(options);
    }
}
