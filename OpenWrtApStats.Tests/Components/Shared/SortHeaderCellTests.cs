using Bunit;
using OpenWrtApStats.Components.Shared;
using Xunit;

namespace OpenWrtApStats.Tests.Components.Shared;

/// <summary>
/// Phase 5 D-05 SortHeaderCell — DASH-05 click-to-sort + reverse + aria-sort.
/// </summary>
public sealed class SortHeaderCellTests
{
    [Fact]
    public void Inactive_RendersAriaSortNone_NoCaret()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<SortHeaderCell>(p => p
            .Add(c => c.Column, "name")
            .Add(c => c.CurrentSort, "lastSeen")
            .Add(c => c.Descending, true)
            .AddChildContent("Name"));

        var th = cut.Find("th");
        Assert.Equal("none", th.GetAttribute("aria-sort"));
        Assert.Empty(cut.FindAll("i.bi-caret-up-fill"));
        Assert.Empty(cut.FindAll("i.bi-caret-down-fill"));
        Assert.Contains("Name", cut.Markup);
    }

    [Fact]
    public void ActiveAscending_RendersCaretUpFill_AriaSortAscending()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<SortHeaderCell>(p => p
            .Add(c => c.Column, "name")
            .Add(c => c.CurrentSort, "name")
            .Add(c => c.Descending, false)
            .AddChildContent("Name"));

        Assert.Equal("ascending", cut.Find("th").GetAttribute("aria-sort"));
        Assert.Contains("bi-caret-up-fill", cut.Markup);
        Assert.DoesNotContain("bi-caret-down-fill", cut.Markup);
    }

    [Fact]
    public void ActiveDescending_RendersCaretDownFill_AriaSortDescending()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<SortHeaderCell>(p => p
            .Add(c => c.Column, "lastSeen")
            .Add(c => c.CurrentSort, "lastSeen")
            .Add(c => c.Descending, true)
            .AddChildContent("Last seen"));

        Assert.Equal("descending", cut.Find("th").GetAttribute("aria-sort"));
        Assert.Contains("bi-caret-down-fill", cut.Markup);
        Assert.DoesNotContain("bi-caret-up-fill", cut.Markup);
    }

    [Fact]
    public void Click_InvokesOnSortWithColumn()
    {
        using var ctx = new BunitContext();
        string? captured = null;
        var cut = ctx.Render<SortHeaderCell>(p => p
            .Add(c => c.Column, "rssi")
            .Add(c => c.CurrentSort, "name")
            .Add(c => c.Descending, false)
            .Add(c => c.OnSort, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string>(this, s => captured = s))
            .AddChildContent("RSSI"));

        cut.Find("button").Click();
        Assert.Equal("rssi", captured);
    }
}
