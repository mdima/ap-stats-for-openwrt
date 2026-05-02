using Bunit;
using OpenWrtApStats.Components.Shared;
using OpenWrtApStats.Data.Models;
using Xunit;

namespace OpenWrtApStats.Tests.Components.Shared;

/// <summary>
/// Phase 5 D-11 PerApStrip — DASH-07 per-AP connectivity strip.
/// </summary>
public sealed class PerApStripTests
{
    private static WiFiNode Make(int id, string host, Status status) => new()
    {
        Id = id, Hostname = host, Username = "root", Password = "x",
        Status = status, LastPollDate = DateTime.UtcNow.AddSeconds(-10),
    };

    [Fact]
    public void RendersOneButtonPerNode_WithHostname()
    {
        using var ctx = new BunitContext();
        var nodes = new List<WiFiNode> { Make(1, "ap-A", Status.Online), Make(2, "ap-B", Status.Offline) };
        var cut = ctx.Render<PerApStrip>(p => p
            .Add(c => c.Nodes, nodes)
            .Add(c => c.SelectedAps, new HashSet<int>()));

        var buttons = cut.FindAll("button");
        Assert.Equal(2, buttons.Count);
        Assert.Contains("ap-A", cut.Markup);
        Assert.Contains("ap-B", cut.Markup);
    }

    [Fact]
    public void OnlineNode_RendersGreenFilledDot()
    {
        using var ctx = new BunitContext();
        var nodes = new List<WiFiNode> { Make(1, "ap-A", Status.Online) };
        var cut = ctx.Render<PerApStrip>(p => p
            .Add(c => c.Nodes, nodes)
            .Add(c => c.SelectedAps, new HashSet<int>()));

        Assert.Contains("bi-circle-fill text-success", cut.Markup);
        Assert.DoesNotContain("text-warning", cut.Markup);   // Phase 5 LOCK: no Stale dot on the strip
    }

    [Fact]
    public void OfflineNode_RendersGrayHollowDot()
    {
        using var ctx = new BunitContext();
        var nodes = new List<WiFiNode> { Make(1, "ap-A", Status.Offline) };
        var cut = ctx.Render<PerApStrip>(p => p
            .Add(c => c.Nodes, nodes)
            .Add(c => c.SelectedAps, new HashSet<int>()));

        Assert.Contains("bi-circle text-secondary", cut.Markup);
        Assert.DoesNotContain("bi-circle-fill text-success", cut.Markup);
    }

    [Fact]
    public void SelectedNode_RendersFilledButton_AriaPressedTrue()
    {
        using var ctx = new BunitContext();
        var nodes = new List<WiFiNode> { Make(1, "ap-A", Status.Online) };
        var cut = ctx.Render<PerApStrip>(p => p
            .Add(c => c.Nodes, nodes)
            .Add(c => c.SelectedAps, new HashSet<int> { 1 }));

        var btn = cut.Find("button");
        Assert.Contains("btn-secondary", btn.GetAttribute("class"));
        Assert.DoesNotContain("btn-outline-secondary", btn.GetAttribute("class"));
        Assert.Equal("true", btn.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Click_InvokesOnApClickedWithNodeId()
    {
        using var ctx = new BunitContext();
        var nodes = new List<WiFiNode> { Make(7, "ap-A", Status.Online) };
        int? captured = null;
        var cut = ctx.Render<PerApStrip>(p => p
            .Add(c => c.Nodes, nodes)
            .Add(c => c.SelectedAps, new HashSet<int>())
            .Add(c => c.OnApClicked, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<int>(this, id => captured = id)));

        cut.Find("button").Click();
        Assert.Equal(7, captured);
    }

    [Fact]
    public void EmptyNodes_RendersWrapperWithNoButtons()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<PerApStrip>(p => p
            .Add(c => c.Nodes, new List<WiFiNode>())
            .Add(c => c.SelectedAps, new HashSet<int>()));

        Assert.Empty(cut.FindAll("button"));
        Assert.Contains("Per-access-point connectivity", cut.Markup);   // outer aria-label still present
    }
}
