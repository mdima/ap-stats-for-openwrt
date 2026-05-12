using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Migrations;

/// <summary>
/// Drives the InitialCreate migration against :memory: SQLite (NOT the EF InMemory provider —
/// STACK.md forbids it; no FK enforcement, no transactions). Asserts PER-01 (schema), PER-02
/// (unique MAC), PER-03 (composite-unique node+mac), and the D-09 / D-10 seed.
/// </summary>
public sealed class InitialCreateTests(SqliteInMemoryFixture fixture) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    [Fact]
    public async Task AppliesCleanly_AgainstFreshSqlite()
    {
        // RESEARCH Pitfall 4 verbatim — schema runs cleanly and seed lands.
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var seed = await db.AppSettings.FindAsync(["PollingIntervalSeconds"], ct);
        Assert.NotNull(seed);
        Assert.Equal(30, seed!.IntValue);
    }

    [Fact]
    public async Task SchemaMatches_DataModel()
    {
        // PER-01 — the four entity tables + __EFMigrationsHistory exist in sqlite_master.
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.Contains("WiFiNodes", names);
        Assert.Contains("WiFiNetworks", names);
        Assert.Contains("WLANStations", names);
        Assert.Contains("AppSettings", names);
        Assert.Contains("__EFMigrationsHistory", names);
    }

    [Fact]
    public async Task WLANStations_MacAddress_IsUnique()
    {
        // PER-02 — same MAC across rows must fail.
        // Microsoft.Data.Sqlite enables PRAGMA foreign_keys=ON by default, so we must seed
        // a parent WiFiNode + WiFiNetwork row before inserting any WLANStation. The original
        // version of this test claimed FK was unenforced on a pre-opened connection — that
        // claim is false (the provider applies foreign_keys=ON regardless of who opened the
        // connection), so the first SaveChanges blew up on FK before the unique-index check
        // ever fired. The test is now structured around real referential integrity and
        // exercises ONLY the PER-02 unique-MAC invariant on the second insert.
        var ct = TestContext.Current.CancellationToken;
        var nodeHost = $"node-{Guid.NewGuid():N}";
        var net1Mac = $"BB:CC:DD:EE:FF:{Random.Shared.Next(0, 256):X2}";
        var net2Mac = $"CC:DD:EE:FF:00:{Random.Shared.Next(0, 256):X2}";
        var stationMac = $"AA:BB:CC:DD:EE:{Random.Shared.Next(0, 256):X2}";

        // Seed: one WiFiNode hosting two distinct WiFiNetworks (different MAC pairs so the
        // PER-03 composite-unique index doesn't fire).
        int net1Id, net2Id;
        await using (var db = _fixture.CreateContext())
        {
            var node = new WiFiNode
            {
                Hostname = nodeHost,
                Username = "operator",
                Password = string.Empty
            };
            db.WiFiNodes.Add(node);
            await db.SaveChangesAsync(ct);

            var net1 = new WiFiNetwork { WiFiNodeId = node.Id, SSID = "ssid-1", MacAddress = net1Mac };
            var net2 = new WiFiNetwork { WiFiNodeId = node.Id, SSID = "ssid-2", MacAddress = net2Mac };
            db.WiFiNetworks.AddRange(net1, net2);
            await db.SaveChangesAsync(ct);

            net1Id = net1.Id;
            net2Id = net2.Id;
        }

        // Insert one station against net1 — should succeed.
        await using (var db = _fixture.CreateContext())
        {
            db.WLANStations.Add(new WLANStation
            {
                MacAddress = stationMac,
                WiFiNetworkId = net1Id
            });
            await db.SaveChangesAsync(ct);
        }

        // Insert a second station with the SAME MAC against net2 — must fail on the
        // unique index on WLANStations.MacAddress (PER-02 binding).
        await using (var db2 = _fixture.CreateContext())
        {
            db2.WLANStations.Add(new WLANStation
            {
                MacAddress = stationMac,
                WiFiNetworkId = net2Id
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync(ct));
        }
    }

    [Fact]
    public async Task WiFiNetworks_NodeAndMac_IsUnique()
    {
        // PER-03 — same (NodeId, MAC) must fail; same MAC across DIFFERENT NodeIds is fine.
        var ct = TestContext.Current.CancellationToken;
        WiFiNode node1;
        WiFiNode node2;
        await using (var db = _fixture.CreateContext())
        {
            node1 = new WiFiNode { Hostname = "ap1", Username = "root", Password = "" };
            node2 = new WiFiNode { Hostname = "ap2", Username = "root", Password = "" };
            db.WiFiNodes.AddRange(node1, node2);
            await db.SaveChangesAsync(ct);
        }

        var mac = $"11:22:33:44:55:{Random.Shared.Next(0, 255):X2}";

        await using (var db = _fixture.CreateContext())
        {
            db.WiFiNetworks.Add(new WiFiNetwork
            {
                WiFiNodeId = node1.Id,
                MacAddress = mac,
                SSID = "n1"
            });
            await db.SaveChangesAsync(ct);
        }

        // Same NodeId + same MAC -> conflict.
        await using (var db2 = _fixture.CreateContext())
        {
            db2.WiFiNetworks.Add(new WiFiNetwork
            {
                WiFiNodeId = node1.Id,
                MacAddress = mac,
                SSID = "dup"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync(ct));
        }

        // Different NodeId + same MAC -> OK.
        await using (var db3 = _fixture.CreateContext())
        {
            db3.WiFiNetworks.Add(new WiFiNetwork
            {
                WiFiNodeId = node2.Id,
                MacAddress = mac,
                SSID = "ok"
            });
            await db3.SaveChangesAsync(ct);
        }
    }

    [Fact]
    public async Task Seeds_DefaultPollingInterval()
    {
        // D-09 / D-10 — exactly one row after migrate; IntValue == 30; other value columns null.
        var ct = TestContext.Current.CancellationToken;
        await using var db = _fixture.CreateContext();
        var rows = await db.AppSettings.AsNoTracking().ToListAsync(ct);
        var seedRow = Assert.Single(rows, r => r.Key == "PollingIntervalSeconds");
        Assert.Equal(30, seedRow.IntValue);
        Assert.Null(seedRow.StringValue);
        Assert.Null(seedRow.BoolValue);
    }
}
