using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Repositories;

/// <summary>
/// Quick-260506-005 / D-XX tests: per-cycle WiFiNetworks absence is hard-DELETE (not Enabled-toggle).
/// Closes the BSSID-rotation accumulation defect (operator-confirmed: AP2 had 5x DesertStorm rows on
/// channel 11, one per historically-observed BSSID). FK contract: WLANStation.WiFiNetworkId is set
/// to null on parent delete (DeleteBehavior.SetNull), stations are NOT deleted.
/// </summary>
public sealed class NetworkRepoDeleteAbsentTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private INetworkRepo CreateNetworkRepo() =>
        new NetworkRepo(new TestDbContextFactory(_fixture.Options));
    private INodeRepo CreateNodeRepo() =>
        new NodeRepo(new TestDbContextFactory(_fixture.Options));

    [Fact]
    public async Task DeleteAbsentAsync_RepoOwned_DeletesAbsentNetworks_ForGivenNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodes = CreateNodeRepo();
        var nets = CreateNetworkRepo();

        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n1, ct);

        // Use a unique high-byte prefix per fact so the IClassFixture-shared DB does not collide
        // across tests' assertions. AA:01:00:* is this fact's namespace.
        var macA = "AA:01:00:00:00:01";
        var macB = "AA:01:00:00:00:02";
        var macC = "AA:01:00:00:00:03";
        await nets.UpsertManyAsync(n1.Id, new[]
        {
            new WiFiNetwork { MacAddress = macA, SSID = "first",  Enabled = true },
            new WiFiNetwork { MacAddress = macB, SSID = "second", Enabled = true },
            new WiFiNetwork { MacAddress = macC, SSID = "third",  Enabled = true },
        }, ct);

        await nets.DeleteAbsentAsync(n1.Id, new[] { macA }, ct);

        var rows = await nets.GetByNodeAsync(n1.Id, ct);
        Assert.Single(rows);
        Assert.Equal(macA, rows[0].MacAddress);
    }

    [Fact]
    public async Task DeleteAbsentAsync_RepoOwned_PreservesPresentNetworks()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodes = CreateNodeRepo();
        var nets = CreateNetworkRepo();

        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n1, ct);

        var macA = "AA:02:00:00:00:01";
        var macB = "AA:02:00:00:00:02";
        await nets.UpsertManyAsync(n1.Id, new[]
        {
            new WiFiNetwork { MacAddress = macA, SSID = "preserve-me", Channel = 11, Width = 80, TxPower = 22, Enabled = true },
            new WiFiNetwork { MacAddress = macB, SSID = "delete-me",   Channel = 6,  Width = 20, TxPower = 17, Enabled = true },
        }, ct);

        await nets.DeleteAbsentAsync(n1.Id, new[] { macA }, ct);

        var rows = await nets.GetByNodeAsync(n1.Id, ct);
        var survivor = Assert.Single(rows);
        Assert.Equal(macA, survivor.MacAddress);
        Assert.Equal("preserve-me", survivor.SSID);
        Assert.Equal(11, survivor.Channel);
        Assert.Equal(80, survivor.Width);
        Assert.Equal(22, survivor.TxPower);
        Assert.True(survivor.Enabled);
    }

    [Fact]
    public async Task DeleteAbsentAsync_RepoOwned_DoesNotTouchOtherNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodes = CreateNodeRepo();
        var nets = CreateNetworkRepo();

        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "" };
        var n2 = new WiFiNode { Hostname = $"n2-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n1, ct);
        await nodes.AddAsync(n2, ct);

        var n1MacA = "AA:03:00:00:01:01";
        var n2MacX = "AA:03:00:00:02:01";
        var n2MacY = "AA:03:00:00:02:02";
        await nets.UpsertManyAsync(n1.Id, new[]
        {
            new WiFiNetwork { MacAddress = n1MacA, SSID = "n1-w1", Enabled = true },
        }, ct);
        await nets.UpsertManyAsync(n2.Id, new[]
        {
            new WiFiNetwork { MacAddress = n2MacX, SSID = "n2-x", Enabled = true },
            new WiFiNetwork { MacAddress = n2MacY, SSID = "n2-y", Enabled = true },
        }, ct);

        // Delete EVERYTHING for n1; n2 must be untouched.
        await nets.DeleteAbsentAsync(n1.Id, Array.Empty<string>(), ct);

        var n1Rows = await nets.GetByNodeAsync(n1.Id, ct);
        var n2Rows = await nets.GetByNodeAsync(n2.Id, ct);
        Assert.Empty(n1Rows);
        Assert.Equal(2, n2Rows.Count);
        Assert.Contains(n2Rows, r => r.MacAddress == n2MacX);
        Assert.Contains(n2Rows, r => r.MacAddress == n2MacY);
    }

    [Fact]
    public async Task DeleteAbsentAsync_CallerOwned_RunsWithoutSaveChanges()
    {
        // ExecuteDeleteAsync is immediate; caller-owned overload's contract is "no SaveChangesAsync
        // call from inside the method", not "deferred". Asserting we can call the caller-owned
        // overload against a directly-opened DatabaseContext and observe the delete via the same
        // context immediately afterwards (no SaveChangesAsync call required).
        var ct = TestContext.Current.CancellationToken;
        var nodes = CreateNodeRepo();
        var nets = CreateNetworkRepo();

        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n1, ct);
        var macA = "AA:04:00:00:00:01";
        var macB = "AA:04:00:00:00:02";
        await nets.UpsertManyAsync(n1.Id, new[]
        {
            new WiFiNetwork { MacAddress = macA, SSID = "x", Enabled = true },
            new WiFiNetwork { MacAddress = macB, SSID = "y", Enabled = true },
        }, ct);

        await using var db = _fixture.CreateContext();
        await nets.DeleteAbsentAsync(db, n1.Id, Array.Empty<string>(), ct);
        // Deliberately NO SaveChangesAsync call — ExecuteDeleteAsync persists immediately.

        var rowsViaSameContext = await db.WiFiNetworks
            .AsNoTracking()
            .Where(n => n.WiFiNodeId == n1.Id)
            .ToListAsync(ct);
        Assert.Empty(rowsViaSameContext);

        // Cross-check via a second context to prove the deletion is durable, not just tracker-state.
        await using var verify = _fixture.CreateContext();
        var rowsViaSecondContext = await verify.WiFiNetworks
            .AsNoTracking()
            .Where(n => n.WiFiNodeId == n1.Id)
            .ToListAsync(ct);
        Assert.Empty(rowsViaSecondContext);
    }

    [Fact]
    public async Task DeleteAbsentAsync_NullsWLANStationWiFiNetworkIds_OnCascade()
    {
        // FK contract: WLANStation.WiFiNetworkId has DeleteBehavior.SetNull. Deleting the parent
        // network must NOT delete the station row; it must dissociate (FK -> null).
        var ct = TestContext.Current.CancellationToken;
        var nodes = CreateNodeRepo();
        var nets = CreateNetworkRepo();

        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n1, ct);
        var macA = "AA:05:00:00:00:01";
        await nets.UpsertManyAsync(n1.Id, new[]
        {
            new WiFiNetwork { MacAddress = macA, SSID = "doomed", Enabled = true },
        }, ct);

        // Look up the seeded network id, seed a station bound to it.
        int seededNetId;
        var stationMac = $"BB:05:00:00:00:{Random.Shared.Next(0, 255):X2}";
        await using (var seed = _fixture.CreateContext())
        {
            seededNetId = await seed.WiFiNetworks
                .Where(n => n.WiFiNodeId == n1.Id && n.MacAddress == macA)
                .Select(n => n.Id)
                .FirstAsync(ct);

            seed.WLANStations.Add(new WLANStation
            {
                MacAddress = stationMac,
                WiFiNetworkId = seededNetId,
                Status = Status.Online,
                LastPollDate = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync(ct);
        }

        await nets.DeleteAbsentAsync(n1.Id, Array.Empty<string>(), ct);

        await using var verify = _fixture.CreateContext();
        // (a) the network is gone
        var netGone = await verify.WiFiNetworks.AsNoTracking().AnyAsync(n => n.Id == seededNetId, ct);
        Assert.False(netGone);
        // (b) the station row STILL EXISTS
        var station = await verify.WLANStations.AsNoTracking().FirstOrDefaultAsync(s => s.MacAddress == stationMac, ct);
        Assert.NotNull(station);
        // (c) the station's WiFiNetworkId is null (cascade SetNull)
        Assert.Null(station!.WiFiNetworkId);
    }

    [Fact]
    public async Task DeleteAbsentAsync_WithEmptyPresentMacs_DeletesAllNetworksForNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodes = CreateNodeRepo();
        var nets = CreateNetworkRepo();

        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n1, ct);
        await nets.UpsertManyAsync(n1.Id, new[]
        {
            new WiFiNetwork { MacAddress = "AA:06:00:00:00:01", SSID = "a", Enabled = true },
            new WiFiNetwork { MacAddress = "AA:06:00:00:00:02", SSID = "b", Enabled = true },
            new WiFiNetwork { MacAddress = "AA:06:00:00:00:03", SSID = "c", Enabled = true },
        }, ct);

        await nets.DeleteAbsentAsync(n1.Id, Array.Empty<string>(), ct);

        var rows = await nets.GetByNodeAsync(n1.Id, ct);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task DeleteAbsentAsync_WithNonexistentNodeId_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodes = CreateNodeRepo();
        var nets = CreateNetworkRepo();

        // Seed a real node + network; we'll target a different (non-existent) node id.
        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n1, ct);
        var macA = "AA:07:00:00:00:01";
        await nets.UpsertManyAsync(n1.Id, new[]
        {
            new WiFiNetwork { MacAddress = macA, SSID = "intact", Enabled = true },
        }, ct);

        // Use an id that cannot exist (negative). No throw, no rows affected.
        await nets.DeleteAbsentAsync(-9999, new[] { "ZZ:99:99:99:99:99" }, ct);

        var rows = await nets.GetByNodeAsync(n1.Id, ct);
        var survivor = Assert.Single(rows);
        Assert.Equal(macA, survivor.MacAddress);
    }
}
