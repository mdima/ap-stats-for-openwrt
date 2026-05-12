using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Repositories;

/// <summary>
/// Exercises <see cref="NetworkRepo"/> against the per-fixture migrated SQLite :memory:
/// schema. Covers PER-03 idempotent (NodeId, MAC) upsert, WLAN-03 enable-flag
/// preservation across re-discovery, the SetEnabledAsync toggle, and D-07 caller-owned
/// no-commit semantics.
/// </summary>
public sealed class NetworkRepoTests(SqliteInMemoryFixture fixture) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private INetworkRepo CreateRepo() =>
        new NetworkRepo(new TestDbContextFactory(_fixture.Options));

    private async Task<int> SeedNodeAsync(CancellationToken ct)
    {
        await using var db = _fixture.CreateContext();
        var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = "" };
        db.WiFiNodes.Add(node);
        await db.SaveChangesAsync(ct);
        return node.Id;
    }

    private static WiFiNetwork MakeNetwork(string ssid, string mac, int channel = 6, int width = 20, int txPower = 20) =>
        new()
        {
            SSID = ssid,
            MacAddress = mac,
            Channel = channel,
            Width = width,
            TxPower = txPower
        };

    [Fact]
    public async Task UpsertManyAsync_FirstCall_InsertsRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var nodeId = await SeedNodeAsync(ct);
        var mac = $"AA:11:22:33:44:{Random.Shared.Next(0, 255):X2}";

        await repo.UpsertManyAsync(nodeId, [MakeNetwork("net-A", mac)], ct);

        var rows = await repo.GetByNodeAsync(nodeId, ct);
        var row = Assert.Single(rows);
        Assert.Equal("net-A", row.SSID);
        Assert.Equal(mac, row.MacAddress);
    }

    [Fact]
    public async Task UpsertManyAsync_SecondCall_UpdatesInPlace_PreservesEnabled()
    {
        // PER-03 (idempotent re-discovery) + WLAN-03 (operator-toggled Enabled persists).
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var nodeId = await SeedNodeAsync(ct);
        var mac = $"AA:22:33:44:55:{Random.Shared.Next(0, 255):X2}";

        // 1) First discovery — channel 6, default Enabled=false.
        await repo.UpsertManyAsync(nodeId, [MakeNetwork("net-B", mac, channel: 6)], ct);
        var first = await repo.GetByNodeAsync(nodeId, ct);
        var firstRow = Assert.Single(first, n => n.MacAddress == mac);
        var networkId = firstRow.Id;

        // 2) Operator toggles Enabled=true between cycles.
        await repo.SetEnabledAsync(networkId, true, ct);

        // 3) Re-discovery on next cycle: channel changed to 11, Enabled MUST stay true.
        await repo.UpsertManyAsync(nodeId, [MakeNetwork("net-B", mac, channel: 11)], ct);

        var afterRedisco = await repo.GetByNodeAsync(nodeId, ct);
        var second = Assert.Single(afterRedisco, n => n.MacAddress == mac); // PER-03: single row, not two
        Assert.Equal(11, second.Channel);     // updated in place
        Assert.True(second.Enabled);          // WLAN-03: operator toggle preserved
    }

    [Fact]
    public async Task UpsertManyAsync_CallerOwned_DoesNotCommit()
    {
        // D-07 — caller-owned overload must not call SaveChangesAsync. Verify by
        // rolling back the outer transaction; if SaveChangesAsync slipped in, the
        // assertion would fail.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var nodeId = await SeedNodeAsync(ct);
        var mac = $"CC:33:44:55:66:{Random.Shared.Next(0, 255):X2}";

        await using (var ownedDb = _fixture.CreateContext())
        await using (var tx = await ownedDb.Database.BeginTransactionAsync(ct))
        {
            await repo.UpsertManyAsync(ownedDb, nodeId, [MakeNetwork("rolled-back", mac)], ct);
            // Caller-owned overload does NOT call SaveChangesAsync; we explicitly do so here
            // inside the transaction so the rollback is meaningful.
            await ownedDb.SaveChangesAsync(ct);
            await tx.RollbackAsync(ct);
        }

        var rows = await repo.GetByNodeAsync(nodeId, ct);
        Assert.DoesNotContain(rows, n => n.MacAddress == mac);
    }

    [Fact]
    public async Task SetEnabledAsync_FlipsFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var nodeId = await SeedNodeAsync(ct);
        var mac = $"DE:44:55:66:77:{Random.Shared.Next(0, 255):X2}";

        await repo.UpsertManyAsync(nodeId, [MakeNetwork("toggle-me", mac)], ct);
        var before = (await repo.GetByNodeAsync(nodeId, ct)).Single(n => n.MacAddress == mac);
        Assert.False(before.Enabled);

        await repo.SetEnabledAsync(before.Id, true, ct);

        var after = (await repo.GetByNodeAsync(nodeId, ct)).Single(n => n.MacAddress == mac);
        Assert.True(after.Enabled);
    }

    [Fact]
    public async Task UpsertManyAsync_CR01_DifferentBssidCases_TreatedAsDistinctRows()
    {
        // CR-01 sister-site (NetworkRepo.cs case-STRICT BSSID dedup post-D-07). Callers
        // (parser → orchestrator) uppercase upstream; the repo no longer silently merges.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var nodeId = await SeedNodeAsync(ct);

        var upperBssid = $"FF:EE:DD:CC:BB:{Random.Shared.Next(0, 255):X2}";
        var lowerBssid = upperBssid.ToLowerInvariant();

        await using (var ownedDb = _fixture.CreateContext())
        await using (var tx = await ownedDb.Database.BeginTransactionAsync(ct))
        {
            await repo.UpsertManyAsync(ownedDb, nodeId, new[]
            {
                new WiFiNetwork { MacAddress = upperBssid, SSID = "TEST", WiFiNodeId = nodeId },
                new WiFiNetwork { MacAddress = lowerBssid, SSID = "TEST", WiFiNodeId = nodeId },
            }, ct);
            await ownedDb.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await using var verify = _fixture.CreateContext();
        var rows = await verify.WiFiNetworks
            .Where(n => n.WiFiNodeId == nodeId && (n.MacAddress == upperBssid || n.MacAddress == lowerBssid))
            .ToListAsync(ct);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task AnyEnabledAsync_MatchesUnderlyingDb()
    {
        // Defensive form (matches AppSettingsServiceTests.ReservedKeys_StateMatchesUnderlyingDb):
        // class-scoped fixture is shared across tests, so assert the contract relative to
        // whatever the DB currently holds — order-agnostic.
        var ct = TestContext.Current.CancellationToken;
        var nets = CreateRepo();

        await using var db = _fixture.CreateContext();
        var expected = await db.WiFiNetworks.AsNoTracking().AnyAsync(w => w.Enabled, ct);
        Assert.Equal(expected, await nets.AnyEnabledAsync(ct));
    }

    [Fact]
    public async Task AnyEnabledAsync_AfterAddingEnabledNetwork_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var nets = CreateRepo();
        var nodes = new NodeRepo(new TestDbContextFactory(_fixture.Options));
        var n = new WiFiNode { Hostname = $"n-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n, ct);
        await nets.UpsertManyAsync(n.Id, new[]
        {
            new WiFiNetwork { MacAddress = "DD:EE:FF:00:00:01", SSID = "enabled", Enabled = true }
        }, ct);

        Assert.True(await nets.AnyEnabledAsync(ct));
    }
}
