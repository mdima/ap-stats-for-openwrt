using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Migrations;

/// <summary>
/// Phase 9 D-14 migration-apply test for the v0.9.1 enrichment columns. Each fact builds a
/// fresh per-test SQLite file (NOT the InMemory provider — STACK.md ban) so the migration
/// chain runs end-to-end.
///
/// Covers SCHEMA-01..04, SCHEMA-06, ENRICH-07: column presence, default values, AppSetting seeds.
///
/// Historical context (pre-Phase 12 Wave B): this fixture originally also exercised the
/// raw-SQL U/L-bit backfill (D-08) by seeding the database at the v0.9 head
/// (<c>Phase04_RSS_Nullable</c>) and migrating up. Phase 12 Wave B (RUNTIME-03) collapsed the
/// 6-migration chain into a single <c>InitialCreate</c> baseline per CONTEXT.md D-01 ("pure
/// delete + regen — no schema-parity exercise, no data port"); milestone v0.10 is a clean-break,
/// so there are no pre-existing rows to back-fill at boot time. The backfill <c>[Fact]</c> was
/// removed because its named intermediate migration target (<c>Phase04_RSS_Nullable</c>) no
/// longer exists. The schema-presence + seed-content assertions below survive — they remain
/// useful drift detectors for the regenerated baseline's column shape and seed correctness.
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

    // Phase 12 Wave B (RUNTIME-03): the original D-08 U/L-bit backfill [Fact]
    // (MigrateAsync_BackfillsIsRandomMac_FromULBit) was removed because its named
    // intermediate target migration `Phase04_RSS_Nullable` no longer exists after the
    // 6-chain was collapsed into a single InitialCreate baseline. Per CONTEXT.md D-01
    // milestone v0.10 is a clean-break (no data port), so backfill semantics are
    // unreachable by design — the InitialCreate Up() runs against an empty DB. The
    // schema-presence + seed-content facts in this file remain as drift detectors.

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
