using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Repositories;

/// <summary>
/// Exercises <see cref="StationRepo"/> against the per-fixture migrated SQLite :memory:
/// schema. Covers PER-02 MAC-keyed reconciliation across WLAN moves, DASH-14
/// operator-name preservation, MarkAllOfflineAsync repo-owned, SetNameAsync, and
/// D-07 caller-owned no-commit semantics.
/// </summary>
public sealed class StationRepoTests(SqliteInMemoryFixture fixture) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private IStationRepo CreateRepo() =>
        new StationRepo(new TestDbContextFactory(_fixture.Options));

    private async Task<(int nodeId, int net1Id, int net2Id)> SeedNodeWithTwoNetworksAsync(CancellationToken ct)
    {
        await using var db = _fixture.CreateContext();
        var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = "" };
        db.WiFiNodes.Add(node);
        await db.SaveChangesAsync(ct);

        var net1 = new WiFiNetwork
        {
            WiFiNodeId = node.Id,
            SSID = "net-1",
            MacAddress = $"BB:11:22:33:44:{Random.Shared.Next(0, 255):X2}"
        };
        var net2 = new WiFiNetwork
        {
            WiFiNodeId = node.Id,
            SSID = "net-2",
            MacAddress = $"BB:22:33:44:55:{Random.Shared.Next(0, 255):X2}"
        };
        db.WiFiNetworks.AddRange(net1, net2);
        await db.SaveChangesAsync(ct);

        return (node.Id, net1.Id, net2.Id);
    }

    private static WLANStation MakeStation(string mac, int networkId, int rss = -50) =>
        new()
        {
            MacAddress = mac,
            WiFiNetworkId = networkId,
            IPAddress = "192.168.1.10",
            Hostname = "client",
            TxBitrate = 100,
            RxBitrate = 100,
            RSS = rss,
            LastPollDate = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc)
        };

    [Fact]
    public async Task UpsertCycleAsync_NewMac_InsertsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var (_, net1Id, _) = await SeedNodeWithTwoNetworksAsync(ct);
        var mac = $"DD:01:02:03:04:{Random.Shared.Next(0, 255):X2}";

        await repo.UpsertCycleAsync([MakeStation(mac, net1Id)], ct);

        var fetched = await repo.GetByMacAsync(mac, ct);
        Assert.NotNull(fetched);
        Assert.Equal(net1Id, fetched!.WiFiNetworkId);
        Assert.Equal(Status.Online, fetched.Status); // upsert flips Status to Online
    }

    [Fact]
    public async Task UpsertCycleAsync_SameMacDifferentNetwork_UpdatesInPlace()
    {
        // PER-02 — station hopped from net-1 to net-2; row count must stay 1.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var (_, net1Id, net2Id) = await SeedNodeWithTwoNetworksAsync(ct);
        var mac = $"DD:02:03:04:05:{Random.Shared.Next(0, 255):X2}";

        await repo.UpsertCycleAsync([MakeStation(mac, net1Id)], ct);

        // Cycle 2 — same MAC, different WiFiNetworkId.
        await repo.UpsertCycleAsync([MakeStation(mac, net2Id, rss: -40)], ct);

        // Should still be a single row (PER-02 unique index would have thrown otherwise).
        await using var db = _fixture.CreateContext();
        var rows = await db.WLANStations.AsNoTracking().Where(s => s.MacAddress == mac).ToListAsync(ct);
        var row = Assert.Single(rows);
        Assert.Equal(net2Id, row.WiFiNetworkId); // network updated
        Assert.Equal(-40, row.RSS);              // telemetry updated
    }

    [Fact]
    public async Task UpsertCycleAsync_PreservesOperatorName()
    {
        // DASH-14 forward-compat — operator-assigned Name persists across cycles.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var (_, net1Id, _) = await SeedNodeWithTwoNetworksAsync(ct);
        var mac = $"DD:03:04:05:06:{Random.Shared.Next(0, 255):X2}";

        await repo.UpsertCycleAsync([MakeStation(mac, net1Id)], ct);
        await repo.SetNameAsync(mac, "Alice's laptop", ct);

        // Next cycle — probe POCO has no Name; existing Name MUST persist.
        var probe = MakeStation(mac, net1Id, rss: -55);
        probe.Name = null;
        await repo.UpsertCycleAsync([probe], ct);

        var fetched = await repo.GetByMacAsync(mac, ct);
        Assert.Equal("Alice's laptop", fetched!.Name);
    }

    [Fact]
    public async Task UpsertCycleAsync_CallerOwned_DoesNotCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var (_, net1Id, _) = await SeedNodeWithTwoNetworksAsync(ct);
        var mac = $"DD:04:05:06:07:{Random.Shared.Next(0, 255):X2}";

        await using (var ownedDb = _fixture.CreateContext())
        await using (var tx = await ownedDb.Database.BeginTransactionAsync(ct))
        {
            await repo.UpsertCycleAsync(ownedDb, [MakeStation(mac, net1Id)], ct);
            // The caller-owned overload mutates change-tracker state but does NOT call SaveChangesAsync.
            // Saving inside the transaction lets the rollback meaningfully revert.
            await ownedDb.SaveChangesAsync(ct);
            await tx.RollbackAsync(ct);
        }

        var fetched = await repo.GetByMacAsync(mac, ct);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task MarkAllOfflineAsync_RepoOwned_FlipsAllToOffline()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var (_, net1Id, _) = await SeedNodeWithTwoNetworksAsync(ct);
        var prefix = Random.Shared.Next(0, 255);

        var macs = Enumerable.Range(0, 3).Select(i => $"DD:{prefix:X2}:11:22:33:{i:X2}").ToList();
        await repo.UpsertCycleAsync(macs.Select(m => MakeStation(m, net1Id)), ct);

        await repo.MarkAllOfflineAsync(ct);

        await using var db = _fixture.CreateContext();
        var statuses = await db.WLANStations.AsNoTracking()
            .Where(s => macs.Contains(s.MacAddress))
            .Select(s => s.Status).ToListAsync(ct);
        Assert.Equal(3, statuses.Count);
        Assert.All(statuses, s => Assert.Equal(Status.Offline, s));
    }

    [Fact]
    public async Task SetNameAsync_PersistsOperatorName()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var (_, net1Id, _) = await SeedNodeWithTwoNetworksAsync(ct);
        var mac = $"DD:99:88:77:66:{Random.Shared.Next(0, 255):X2}";

        await repo.UpsertCycleAsync([MakeStation(mac, net1Id)], ct);
        await repo.SetNameAsync(mac, "Bob's phone", ct);

        var fetched = await repo.GetByMacAsync(mac, ct);
        Assert.Equal("Bob's phone", fetched!.Name);

        // Null-out clears the name.
        await repo.SetNameAsync(mac, null, ct);
        var clearedFetched = await repo.GetByMacAsync(mac, ct);
        Assert.Null(clearedFetched!.Name);
    }

    [Fact]
    public async Task DeleteByMacAsync_RemovesRow()
    {
        // D-14 (Phase 5) — operator-initiated delete via single-statement ExecuteDeleteAsync.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var (_, net1Id, _) = await SeedNodeWithTwoNetworksAsync(ct);
        var mac = $"DD:DE:DE:DE:DE:{Random.Shared.Next(0, 255):X2}";

        await repo.UpsertCycleAsync([MakeStation(mac, net1Id)], ct);
        Assert.NotNull(await repo.GetByMacAsync(mac, ct));   // baseline

        await repo.DeleteByMacAsync(mac, ct);

        Assert.Null(await repo.GetByMacAsync(mac, ct));
    }

    [Fact]
    public async Task DeleteByMacAsync_NoMatch_DoesNotThrow()
    {
        // Idempotent — deleting a MAC that does not exist is a no-op (no exception).
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();

        // No seed; MAC does not exist.
        await repo.DeleteByMacAsync("AA:BB:CC:DD:EE:FF", ct);   // must not throw
        Assert.Null(await repo.GetByMacAsync("AA:BB:CC:DD:EE:FF", ct));
    }

    [Fact]
    public async Task DeleteByMacAsync_OnlyDeletesTargetMac()
    {
        // Only the targeted MAC is removed; siblings survive.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var (_, net1Id, _) = await SeedNodeWithTwoNetworksAsync(ct);
        var macKept    = $"AA:AA:AA:AA:AA:{Random.Shared.Next(0, 255):X2}";
        var macDeleted = $"BB:BB:BB:BB:BB:{Random.Shared.Next(0, 255):X2}";

        await repo.UpsertCycleAsync(new[] { MakeStation(macKept, net1Id), MakeStation(macDeleted, net1Id) }, ct);
        await repo.DeleteByMacAsync(macDeleted, ct);

        Assert.NotNull(await repo.GetByMacAsync(macKept, ct));
        Assert.Null(await repo.GetByMacAsync(macDeleted, ct));
    }

    [Fact]
    public async Task UpsertCycleAsync_CR01_DifferentMacCases_TreatedAsDistinctRows()
    {
        // CR-01 closure proof: post-D-07, the repo dedup is case-STRICT (StringComparer.Ordinal).
        // Callers MUST uppercase upstream — the parser (Plan 02-02) does. If a future caller
        // forgets, this test fails honestly: two rows where the operator expected one, instead
        // of silent collapse-and-data-loss.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var (_, net1Id, _) = await SeedNodeWithTwoNetworksAsync(ct);

        var upperMac = $"DD:01:02:03:04:{Random.Shared.Next(0, 255):X2}";
        var lowerMac = upperMac.ToLowerInvariant();

        await using (var ownedDb = _fixture.CreateContext())
        await using (var tx = await ownedDb.Database.BeginTransactionAsync(ct))
        {
            await repo.UpsertCycleAsync(ownedDb, new[]
            {
                new WLANStation { MacAddress = upperMac, WiFiNetworkId = net1Id, LastPollDate = DateTime.UtcNow },
                new WLANStation { MacAddress = lowerMac, WiFiNetworkId = net1Id, LastPollDate = DateTime.UtcNow },
            }, ct);
            await ownedDb.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await using var verify = _fixture.CreateContext();
        var rows = await verify.WLANStations
            .Where(s => s.MacAddress == upperMac || s.MacAddress == lowerMac)
            .ToListAsync(ct);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.MacAddress == upperMac);
        Assert.Single(rows, r => r.MacAddress == lowerMac);
    }
}
