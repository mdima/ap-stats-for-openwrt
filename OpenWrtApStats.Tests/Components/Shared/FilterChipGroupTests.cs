using Bunit;
using OpenWrtApStats.Components.Shared;
using Xunit;

namespace OpenWrtApStats.Tests.Components.Shared;

/// <summary>
/// Phase 5 D-06 FilterChipGroup — DASH-06 SSID + AP filter chips.
/// </summary>
public sealed class FilterChipGroupTests
{
    [Fact]
    public void RendersTitle_AndOneChipPerOption()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<FilterChipGroup>(p => p
            .Add(c => c.Title, "SSID")
            .Add(c => c.Options, new List<string> { "guest", "main" })
            .Add(c => c.Selected, new HashSet<string>()));

        Assert.Contains("SSID:", cut.Markup);
        Assert.Equal(2, cut.FindAll("button").Count);
        Assert.Contains("guest", cut.Markup);
        Assert.Contains("main",  cut.Markup);
    }

    [Fact]
    public void SelectedChip_RendersSolidWithAriaPressedTrue()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<FilterChipGroup>(p => p
            .Add(c => c.Title, "AP")
            .Add(c => c.Options, new List<string> { "ap-A" })
            .Add(c => c.Selected, new HashSet<string> { "ap-A" }));

        var btn = cut.Find("button");
        Assert.Contains("btn-secondary", btn.GetAttribute("class"));
        Assert.DoesNotContain("btn-outline-secondary", btn.GetAttribute("class"));
        Assert.Equal("true", btn.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Click_InvokesOnToggledWithOptionValue()
    {
        using var ctx = new BunitContext();
        string? captured = null;
        var cut = ctx.Render<FilterChipGroup>(p => p
            .Add(c => c.Title, "SSID")
            .Add(c => c.Options, new List<string> { "guest" })
            .Add(c => c.Selected, new HashSet<string>())
            .Add(c => c.OnToggled, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string>(this, s => captured = s)));

        cut.Find("button").Click();
        Assert.Equal("guest", captured);
    }

    [Fact]
    public void EmptyOptions_RendersTitleOnlyNoChips()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<FilterChipGroup>(p => p
            .Add(c => c.Title, "SSID")
            .Add(c => c.Options, new List<string>())
            .Add(c => c.Selected, new HashSet<string>()));

        Assert.Contains("SSID:", cut.Markup);
        Assert.Empty(cut.FindAll("button"));
    }
}
