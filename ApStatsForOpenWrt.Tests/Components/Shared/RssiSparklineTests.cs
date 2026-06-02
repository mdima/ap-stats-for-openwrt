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

    // ── QUAL-02 sparkline enhancements (operator-requested 2026-06-02) ──────────────────────────

    [Fact]
    public void AutoScale_NormalizesPerSeries_SameShapeDifferentLevel_ProducesIdenticalLine()
    {
        // #4 auto-scale — two series with the SAME shape but different absolute levels must each be
        // normalized to their own min/max, yielding identical polyline geometry (the old fixed
        // −90..−30 domain would have placed them at very different heights).
        var strong = new List<RssiSparklinePoint> { P(0, -46), P(5, -52), P(10, -50) };
        var weak   = new List<RssiSparklinePoint> { P(0, -76), P(5, -82), P(10, -80) };

        using var ctx = new BunitContext();
        var strongPts = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, strong)).Find("polyline").GetAttribute("points");
        var weakPts   = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, weak)).Find("polyline").GetAttribute("points");

        Assert.Equal(strongPts, weakPts);
    }

    [Fact]
    public void GoodSignalBand_RendersRect_ForMixedSeries()
    {
        // #3 (C) — a mixed series straddling the −67 dBm threshold renders the shaded green band.
        var points = new List<RssiSparklinePoint> { P(0, -55), P(5, -70), P(10, -60) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        var band = cut.Find("rect.rssi-sparkline-band");
        Assert.Equal("0", band.GetAttribute("x"));
        Assert.Equal("100", band.GetAttribute("width"));
        // Bootstrap-success tint @ 15% — fixed color (not currentColor) so the zone reads in both themes.
        Assert.Contains("rgba(25,135,84,.15)", band.GetAttribute("fill"));
        var h = double.Parse(band.GetAttribute("height")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(h, 0.1, 21.9); // partial — neither filled nor vanished
    }

    [Fact]
    public void GoodSignalBand_FillsCell_WhenEverySampleStrong()
    {
        // #3 (C) trade-off — all-strong window: the good zone covers the whole auto-scaled domain.
        var points = new List<RssiSparklinePoint> { P(0, -46), P(5, -48), P(10, -47) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        var h = double.Parse(cut.Find("rect.rssi-sparkline-band").GetAttribute("height")!,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(22d, h, precision: 1); // fills the 22-unit viewBox height
    }

    [Fact]
    public void GoodSignalBand_Vanishes_WhenEverySampleWeak()
    {
        // #3 (C) trade-off — all-weak window: the good zone clamps off the top of the domain → no rect.
        var points = new List<RssiSparklinePoint> { P(0, -78), P(5, -80), P(10, -79) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        Assert.Empty(cut.FindAll("rect.rssi-sparkline-band"));
        Assert.Single(cut.FindAll("polyline")); // the line itself still renders
    }

    [Fact]
    public void Title_ShowsLatestReadingAndRange()
    {
        // #1 — native SVG <title> hover tooltip with the latest sample and the window's range.
        var points = new List<RssiSparklinePoint> { P(0, -55), P(5, -70), P(10, -60) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        var title = cut.Find("svg title").TextContent;
        Assert.Contains("Latest -60 dBm", title); // chronologically-last present reading
        Assert.Contains("-70", title);            // range low
        Assert.Contains("-55", title);            // range high
    }

    [Fact]
    public void Title_IgnoresTrailingGap_ForLatestReading()
    {
        // Latest = last PRESENT sample, even when the window ends on an offline (gap) bucket.
        var points = new List<RssiSparklinePoint> { P(0, -55), P(5, -62), P(10, null) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        Assert.Contains("Latest -62 dBm", cut.Find("svg title").TextContent);
    }
}
