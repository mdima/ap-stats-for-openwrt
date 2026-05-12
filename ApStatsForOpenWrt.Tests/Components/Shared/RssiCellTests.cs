using Bunit;
using ApStatsForOpenWrt.Components.Shared;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 5 D-15 RssiCell — color thresholds + null path.
/// Closes architectural gate `RssiCell_ColorThresholds` (CONTEXT lines 220-225).
/// </summary>
public sealed class RssiCellTests
{
    [Theory]
    [InlineData(-50, 4, "text-bg-success")]
    [InlineData(-60, 4, "text-bg-success")]
    [InlineData(-65, 3, "text-bg-success")]
    [InlineData(-70, 3, "text-bg-success")]
    [InlineData(-75, 2, "text-bg-warning")]
    [InlineData(-80, 2, "text-bg-warning")]
    [InlineData(-85, 1, "text-bg-danger")]
    [InlineData(-100, 1, "text-bg-danger")]
    public void RenderingByThreshold_HasCorrectBarsAndColor(int rss, int expectedFilledBars, string expectedColorClass)
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiCell>(p => p.Add(c => c.Rss, (int?)rss));

        Assert.Contains(expectedColorClass, cut.Markup);
        Assert.Contains($"{rss} dBm", cut.Markup);
        var filled = cut.FindAll(".rssi-seg-filled");
        Assert.Equal(expectedFilledBars, filled.Count);

        var empty = cut.FindAll(".rssi-seg-empty");
        Assert.Equal(4 - expectedFilledBars, empty.Count);
    }

    [Fact]
    public void Null_RendersEmDash_WithAriaUnknown()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiCell>(p => p.Add(c => c.Rss, (int?)null));

        Assert.Contains("—", cut.Markup);
        Assert.Contains("Signal unknown", cut.Markup);
        Assert.Empty(cut.FindAll(".rssi-seg"));
    }

    [Fact]
    public void NonNull_AriaLabelDescribesBars()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiCell>(p => p.Add(c => c.Rss, (int?)-65));

        Assert.Contains("Signal -65 dBm, 3 of 4 bars", cut.Markup);
    }
}
