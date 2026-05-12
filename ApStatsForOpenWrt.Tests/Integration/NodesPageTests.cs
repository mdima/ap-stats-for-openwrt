using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// NODE-04 cascade test + end-to-end /Nodes flow integration. Asserts that
/// <see cref="INodeRepo.DeleteAsync"/> against the real EF wiring cascades WiFiNetworks
/// (DeleteBehavior.Cascade) AND dissociates WLANStations (DeleteBehavior.SetNull —
/// historical observation rows survive without dangling references). Schema delta
/// committed in Plan 03-04 Task 0 (20260501104351_NodeDeleteCascadeBehavior).
/// </summary>
[Collection("Integration")]
public sealed class NodesPageTests(SmokeFactoryFixture factory) : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    [Fact]
    public async Task DeleteNode_CascadesWiFiNetworks_AndDissociatesWLANStations()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var nodes = scope.ServiceProvider.GetRequiredService<INodeRepo>();
        var nets = scope.ServiceProvider.GetRequiredService<INetworkRepo>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApStatsForOpenWrt.Data.DatabaseContext>>();

        // Seed: 1 WiFiNode with 2 WiFiNetworks, each with 3 WLANStations (6 total).
        var node = new WiFiNode
        {
            Hostname = $"to-delete-{Guid.NewGuid():N}",
            Username = "root",
            Password = ""
        };
        await nodes.AddAsync(node, ct);

        await nets.UpsertManyAsync(node.Id, new[]
        {
            new WiFiNetwork { MacAddress = "AA:BB:CC:00:99:01", SSID = "x", Enabled = true },
            new WiFiNetwork { MacAddress = "AA:BB:CC:00:99:02", SSID = "y", Enabled = true },
        }, ct);

        var seededNets = await nets.GetByNodeAsync(node.Id, ct);
        var net1Id = seededNets.Single(n => n.MacAddress == "AA:BB:CC:00:99:01").Id;
        var net2Id = seededNets.Single(n => n.MacAddress == "AA:BB:CC:00:99:02").Id;

        // Use a unique MAC namespace so this test does not collide with concurrent test seeds.
        var macSuffix = Guid.NewGuid().ToString("N").Substring(0, 4).ToUpperInvariant();
        var orphanedMacs = new[]
        {
            $"11:11:{macSuffix[..2]}:{macSuffix[2..4]}:11:01",
            $"11:11:{macSuffix[..2]}:{macSuffix[2..4]}:11:02",
            $"11:11:{macSuffix[..2]}:{macSuffix[2..4]}:11:03",
            $"22:22:{macSuffix[..2]}:{macSuffix[2..4]}:22:01",
            $"22:22:{macSuffix[..2]}:{macSuffix[2..4]}:22:02",
            $"22:22:{macSuffix[..2]}:{macSuffix[2..4]}:22:03",
        };

        // Seed stations directly via DbContext (no IStationRepo Add seam available).
        await using (var seedDb = await dbFactory.CreateDbContextAsync(ct))
        {
            seedDb.WLANStations.AddRange(
                new WLANStation { WiFiNetworkId = net1Id, MacAddress = orphanedMacs[0], Status = Status.Online },
                new WLANStation { WiFiNetworkId = net1Id, MacAddress = orphanedMacs[1], Status = Status.Online },
                new WLANStation { WiFiNetworkId = net1Id, MacAddress = orphanedMacs[2], Status = Status.Online },
                new WLANStation { WiFiNetworkId = net2Id, MacAddress = orphanedMacs[3], Status = Status.Online },
                new WLANStation { WiFiNetworkId = net2Id, MacAddress = orphanedMacs[4], Status = Status.Online },
                new WLANStation { WiFiNetworkId = net2Id, MacAddress = orphanedMacs[5], Status = Status.Online });
            await seedDb.SaveChangesAsync(ct);
        }

        // Action: delete the node.
        await nodes.DeleteAsync(node.Id, ct);

        // Assert (a): WiFiNetworks for that node are gone (Cascade).
        var remainingNets = await nets.GetByNodeAsync(node.Id, ct);
        Assert.Empty(remainingNets);

        // Assert (b): all 6 WLANStations survived but their WiFiNetworkId is NULL (SetNull).
        await using var verifyDb = await dbFactory.CreateDbContextAsync(ct);
        var dissociated = await verifyDb.WLANStations
            .AsNoTracking()
            .Where(s => orphanedMacs.Contains(s.MacAddress) && s.WiFiNetworkId == null)
            .CountAsync(ct);
        Assert.Equal(6, dissociated);
    }

    [Fact]
    public async Task NodesIndex_Anonymous_ReturnsHtml()
    {
        // With UIPasswordHash null (default in fresh in-memory DB), /Nodes is anonymous-OK.
        var ct = TestContext.Current.CancellationToken;
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/Nodes", ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("Nodes", body);   // page h3
    }
}
