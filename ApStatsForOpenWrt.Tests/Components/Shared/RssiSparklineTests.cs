using Bunit;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Data.Repositories.History;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 19 M-HIST-06 (L-03 / L-05 / D-02 / D-03) — direct SVG-markup assertions on the
/// server-rendered RssiSparkline.razor. ZERO JS interop; the rendered &lt;polyline&gt;/&lt;span&gt;
/// markup is the contract (RESEARCH §Pattern 3 / §Code Examples).
/// </summary>
public sealed class RssiSparklineTests
{
    private static RssiSparklinePoint P(int minute, int? rssi) =>
        new(new DateTime(2026, 6, 2, 9, minute, 0, DateTimeKind.Utc), rssi);

    [Fact]
    public void RendersPolyline_WithGapAtOfflineBucket_SplitsIntoTwoSegments()
    {
        // present, present, GAP, present → two contiguous runs → two <polyline> segments (L-05).
        var points = new List<RssiSparklinePoint>
        {
            P(0, -55),
            P(5, -60),
            P(10, null),   // gap (offline bucket)
            P(15, -52),
        };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        var polylines = cut.FindAll("polyline");
        Assert.Equal(2, polylines.Count);
    }

    [Fact]
    public void Polyline_UsesCurrentColor_AndNonScalingStroke()
    {
        var points = new List<RssiSparklinePoint> { P(0, -55), P(5, -60) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        Assert.Contains("currentColor", cut.Markup);       // D-02 theme inheritance, no JS
        Assert.Contains("non-scaling-stroke", cut.Markup); // crisp stroke despite preserveAspectRatio=none
        Assert.Contains("preserveAspectRatio=\"none\"", cut.Markup);
        // Line-only — no fill.
        var polyline = cut.Find("polyline");
        Assert.Equal("none", polyline.GetAttribute("fill"));
    }

    [Fact]
    public void SinglePresentRun_RendersOnePolyline()
    {
        var points = new List<RssiSparklinePoint> { P(0, -55), P(5, -60), P(10, -58) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        Assert.Single(cut.FindAll("polyline"));
    }

    [Fact]
    public void ZeroData_RendersEmDashSpan_NoSvg()
    {
        // D-03 — empty Points (MAC absent from the map) → em-dash, no <svg>.
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, Array.Empty<RssiSparklinePoint>()));

        Assert.Empty(cut.FindAll("svg"));
        Assert.Contains("—", cut.Markup);
        Assert.Contains("aria-label=\"No history yet\"", cut.Markup);
    }

    [Fact]
    public void AllGapPoints_RendersEmDash_NoPolyline()
    {
        // All-null window (present in the map but every bucket a gap) → no segments → em-dash.
        var points = new List<RssiSparklinePoint> { P(0, null), P(5, null) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        Assert.Empty(cut.FindAll("polyline"));
        Assert.Contains("—", cut.Markup);
    }

    [Fact]
    public void Points_ScaleHigherRssi_NearerTopOfViewBox()
    {
        // y inverts dBm: a stronger (higher) RSSI must map to a SMALLER y than a weaker one.
        var points = new List<RssiSparklinePoint> { P(0, -40), P(5, -80) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        var pointsAttr = cut.Find("polyline").GetAttribute("points")!;
        var coords = pointsAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, coords.Length);
        var y0 = double.Parse(coords[0].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);
        var y1 = double.Parse(coords[1].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(y0 < y1, $"Stronger RSSI (-40) y={y0} should be nearer the top (smaller) than weaker (-80) y={y1}.");
    }
}
