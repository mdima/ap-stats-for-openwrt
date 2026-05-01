using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Repositories;

/// <summary>
/// D-15 / WLAN-03 tests: re-discovery is idempotent + absent rows get Enabled=false
/// without being deleted.
/// </summary>
public sealed class NetworkRepoMarkAbsentDisabledTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private INetworkRepo CreateNetworkRepo() =>
        new NetworkRepo(new TestDbContextFactory(_fixture.Options));
    private INodeRepo CreateNodeRepo() =>
        new NodeRepo(new TestDbContextFactory(_fixture.Options));

    [Fact]
    public async Task MarkAbsentDisabledAsync_DisablesOnlyAbsentRows_PerNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodes = CreateNodeRepo();
        var nets = CreateNetworkRepo();

        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n1, ct);
        await nets.UpsertManyAsync(n1.Id, new[]
        {
            new WiFiNetwork { MacAddress = "AA:BB:CC:00:00:01", SSID = "first",  Enabled = true },
            new WiFiNetwork { MacAddress = "AA:BB:CC:00:00:02", SSID = "second", Enabled = true },
            new WiFiNetwork { MacAddress = "AA:BB:CC:00:00:03", SSID = "third",  Enabled = true },
        }, ct);

        var presentMacs = new[] { "AA:BB:CC:00:00:01", "AA:BB:CC:00:00:03" };
        await nets.MarkAbsentDisabledAsync(n1.Id, presentMacs, ct);

        var rows = await nets.GetByNodeAsync(n1.Id, ct);
        Assert.Equal(3, rows.Count);
        Assert.True(rows.Single(r => r.MacAddress == "AA:BB:CC:00:00:01").Enabled);
        Assert.False(rows.Single(r => r.MacAddress == "AA:BB:CC:00:00:02").Enabled);
        Assert.True(rows.Single(r => r.MacAddress == "AA:BB:CC:00:00:03").Enabled);
    }

    [Fact]
    public async Task MarkAbsentDisabledAsync_DoesNotTouchOtherNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodes = CreateNodeRepo();
        var nets = CreateNetworkRepo();

        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "" };
        var n2 = new WiFiNode { Hostname = $"n2-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n1, ct);
        await nodes.AddAsync(n2, ct);

        await nets.UpsertManyAsync(n1.Id, new[]
        {
            new WiFiNetwork { MacAddress = "AA:BB:CC:00:01:01", SSID = "n1-w1", Enabled = true },
        }, ct);
        await nets.UpsertManyAsync(n2.Id, new[]
        {
            new WiFiNetwork { MacAddress = "AA:BB:CC:00:02:01", SSID = "n2-w1", Enabled = true },
        }, ct);

        await nets.MarkAbsentDisabledAsync(n1.Id, Array.Empty<string>(), ct);

        var n1Rows = await nets.GetByNodeAsync(n1.Id, ct);
        var n2Rows = await nets.GetByNodeAsync(n2.Id, ct);
        Assert.False(n1Rows.Single().Enabled);
        Assert.True(n2Rows.Single().Enabled);
    }

    [Fact]
    public async Task MarkAbsentDisabledAsync_AllPresent_NoChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodes = CreateNodeRepo();
        var nets = CreateNetworkRepo();
        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await nodes.AddAsync(n1, ct);
        await nets.UpsertManyAsync(n1.Id, new[]
        {
            new WiFiNetwork { MacAddress = "AA:BB:CC:00:03:01", SSID = "x", Enabled = true },
            new WiFiNetwork { MacAddress = "AA:BB:CC:00:03:02", SSID = "y", Enabled = false },
        }, ct);

        await nets.MarkAbsentDisabledAsync(n1.Id, new[]
        {
            "AA:BB:CC:00:03:01", "AA:BB:CC:00:03:02"
        }, ct);

        var rows = await nets.GetByNodeAsync(n1.Id, ct);
        Assert.True(rows.Single(r => r.MacAddress == "AA:BB:CC:00:03:01").Enabled);
        Assert.False(rows.Single(r => r.MacAddress == "AA:BB:CC:00:03:02").Enabled);
    }
}
