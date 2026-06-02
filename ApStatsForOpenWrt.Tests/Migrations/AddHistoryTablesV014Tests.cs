using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Migrations;

/// <summary>
/// Phase 18 M-HIST-01 — proves the <c>AddHistoryTables_v0_14</c> migration applies cleanly against
/// a fresh SQLite database and pins the history-table schema shape: column sets, the
/// StationMac-has-no-FK / NodeId+NetworkId-cascade-FK contract (L-03), and the
/// (StationMac, TimestampUtc) composite read index. Modeled on
/// <see cref="AddFirstSeenToStationTests"/>'s temp-file factory + <c>pragma_*</c> introspection.
/// <para>
/// No <c>EnsureCreated</c> anywhere — schema arrives strictly via <c>Database.MigrateAsync</c>
/// (CLAUDE.md). Run via MTP: <c>dotnet test ApStatsForOpenWrt.Tests -- --filter-class "*AddHistoryTablesV014*"</c>.
/// </para>
/// </summary>
public sealed class AddHistoryTablesV014Tests : IDisposable
{
    private readonly List<string> _tempDbPaths = [];

    private IDbContextFactory<DatabaseContext> BuildFactory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"v0-14-hist-{Guid.NewGuid():N}.db");
        _tempDbPaths.Add(dbPath);
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new TempFileDbContextFactory(options);
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

    [Fact]
    public async Task MigrateAsync_CreatesKpiHistorySnapshot_WithExpectedColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BuildFactory().CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        var cols = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('KpiHistorySnapshots')")
            .ToListAsync(ct);

        Assert.Equal(
            new[] { "Id", "TimestampUtc", "OnlineCount", "StaleCount", "OfflineCount" }.OrderBy(c => c),
            cols.OrderBy(c => c));
    }

    [Fact]
    public async Task MigrateAsync_CreatesRssiHistorySample_WithExpectedColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BuildFactory().CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        var cols = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('RssiHistorySamples')")
            .ToListAsync(ct);

        Assert.Equal(
            new[] { "Id", "TimestampUtc", "StationMac", "NodeId", "NetworkId", "Rssi" }.OrderBy(c => c),
            cols.OrderBy(c => c));
    }

    [Fact]
    public async Task MigrateAsync_RssiHistorySample_HasTwoCascadeFks_AndNoneToWlanStations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BuildFactory().CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        // pragma_foreign_key_list yields one row per FK; "table" is the principal table.
        var fkTargets = await db.Database
            .SqlQueryRaw<string>("SELECT \"table\" FROM pragma_foreign_key_list('RssiHistorySamples')")
            .ToListAsync(ct);

        Assert.Equal(2, fkTargets.Count);
        Assert.Contains("WiFiNodes", fkTargets);
        Assert.Contains("WiFiNetworks", fkTargets);
        Assert.DoesNotContain("WLANStations", fkTargets);   // L-03: StationMac is identity, NO FK
    }

    [Fact]
    public async Task MigrateAsync_RssiHistorySample_HasCompositeStationMacTimestampIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BuildFactory().CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        var indexNames = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_index_list('RssiHistorySamples')")
            .ToListAsync(ct);

        Assert.Contains("IX_RssiHistorySamples_StationMac_TimestampUtc", indexNames);
    }

    [Fact]
    public async Task MigrateAsync_DoesNotCreateDeferredNodeIdTimestampCompositeIndex()
    {
        // D-07 YAGNI: the optional (NodeId, TimestampUtc) composite index is deferred. EF still
        // emits a single-column FK-backing index (IX_RssiHistorySamples_NodeId) for the cascade FK
        // — that is mandatory and expected; the DEFERRED artifact is the *composite* index, which
        // must be absent.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BuildFactory().CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        var indexNames = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_index_list('RssiHistorySamples')")
            .ToListAsync(ct);

        Assert.DoesNotContain("IX_RssiHistorySamples_NodeId_TimestampUtc", indexNames);
    }

    [Fact]
    public async Task MigrateAsync_BothTables_HaveDescendingTimestampIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BuildFactory().CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        var kpiIndexes = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_index_list('KpiHistorySnapshots')")
            .ToListAsync(ct);
        var rssiIndexes = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_index_list('RssiHistorySamples')")
            .ToListAsync(ct);

        Assert.Contains("IX_KpiHistorySnapshots_TimestampUtc", kpiIndexes);
        Assert.Contains("IX_RssiHistorySamples_TimestampUtc", rssiIndexes);
    }

    /// <summary>
    /// Test-only IDbContextFactory bound to a SQLite file connection string.
    /// </summary>
    private sealed class TempFileDbContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
    {
        public DatabaseContext CreateDbContext() => new(options);
    }
}
