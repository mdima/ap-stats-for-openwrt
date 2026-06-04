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
    public void ZeroData_RendersNothing_NoSvg()
    {
        // D-03 revised (260604 cosmetic tune) — empty Points (MAC absent from the map) → the
        // component renders NOTHING (the em-dash "No history yet" span was removed pre-v0.14-close).
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, Array.Empty<RssiSparklinePoint>()));

        Assert.Empty(cut.FindAll("svg"));
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void AllGapPoints_RendersNothing_NoPolyline()
    {
        // All-null window (present in the map but every bucket a gap) → no segments → nothing
        // rendered (D-03 revised, 260604 — em-dash empty-state removed).
        var points = new List<RssiSparklinePoint> { P(0, null), P(5, null) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        Assert.Empty(cut.FindAll("polyline"));
        Assert.Equal(string.Empty, cut.Markup.Trim());
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

    private const string FillGreen = "rgba(25,135,84,.12)";
    private const string FillYellow = "rgba(255,193,7,.16)";
    private const string FillRed = "rgba(220,53,69,.12)";

    private static double D(string? s) =>
        double.Parse(s!, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Tiers_GreenOnly_WhenEverySampleStrong()
    {
        // #3 — every sample ≥ −67 → the good band fills the whole auto-scaled cell, no yellow/red.
        var points = new List<RssiSparklinePoint> { P(0, -48), P(5, -52), P(10, -50) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        var tiers = cut.FindAll("rect.rssi-sparkline-tier");
        Assert.Single(tiers);
        Assert.Contains(FillGreen, tiers[0].GetAttribute("fill"));
        Assert.InRange(D(tiers[0].GetAttribute("height")), 21.5, 22.0); // fills the cell
    }

    [Fact]
    public void Tiers_RedOnly_WhenEverySampleWeak()
    {
        // #3 — every sample < −80 → only the weak band.
        var points = new List<RssiSparklinePoint> { P(0, -82), P(5, -86), P(10, -84) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        var tiers = cut.FindAll("rect.rssi-sparkline-tier");
        Assert.Single(tiers);
        Assert.Contains(FillRed, tiers[0].GetAttribute("fill"));
        Assert.InRange(D(tiers[0].GetAttribute("height")), 21.5, 22.0);
    }

    [Fact]
    public void Tiers_GreenAndYellow_BothVisible_WhenSignalCrossesGoodFairBoundary()
    {
        // THE operator's ask — a station crossing −67 in-window shows BOTH green (top) and yellow
        // (bottom), split at the boundary, with auto-scale still on.
        var points = new List<RssiSparklinePoint> { P(0, -58), P(5, -74), P(10, -62) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        var tiers = cut.FindAll("rect.rssi-sparkline-tier");
        var fills = tiers.Select(t => t.GetAttribute("fill")!).ToList();
        Assert.Equal(2, tiers.Count);
        Assert.Contains(fills, f => f.Contains(FillGreen));
        Assert.Contains(fills, f => f.Contains(FillYellow));
        Assert.DoesNotContain(fills, f => f.Contains(FillRed));

        // Green is the top band (y=0); the two bands are vertically adjacent (no gap, no overlap).
        var green = tiers.First(t => t.GetAttribute("fill")!.Contains(FillGreen));
        var yellow = tiers.First(t => t.GetAttribute("fill")!.Contains(FillYellow));
        Assert.Equal("0", green.GetAttribute("y"));
        Assert.Equal(D(green.GetAttribute("y")) + D(green.GetAttribute("height")),
                     D(yellow.GetAttribute("y")), precision: 1);
    }

    [Fact]
    public void Tiers_YellowAndRed_BothVisible_WhenSignalCrossesFairWeakBoundary()
    {
        // A station crossing −80 in-window shows BOTH yellow and red, no green.
        var points = new List<RssiSparklinePoint> { P(0, -76), P(5, -84), P(10, -78) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        var fills = cut.FindAll("rect.rssi-sparkline-tier").Select(t => t.GetAttribute("fill")!).ToList();
        Assert.Equal(2, fills.Count);
        Assert.Contains(fills, f => f.Contains(FillYellow));
        Assert.Contains(fills, f => f.Contains(FillRed));
        Assert.DoesNotContain(fills, f => f.Contains(FillGreen));
    }

    [Fact]
    public void Title_ShowsLatestReadingTierAndRange()
    {
        // #1 — the hover tooltip is now a BOOTSTRAP tooltip: data-bs-toggle="tooltip" + the text on
        // a data-bs-title attribute (no native SVG <title> child). Same latest + tier word + range.
        var points = new List<RssiSparklinePoint> { P(0, -55), P(5, -70), P(10, -60) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        // Native SVG <title> is gone; the opt-in attribute is present.
        Assert.Empty(cut.FindAll("svg title"));
        var tip = cut.Find("[data-bs-toggle=\"tooltip\"]");
        var title = tip.GetAttribute("data-bs-title")!;
        Assert.Contains("Latest -60 dBm", title); // chronologically-last present reading
        Assert.Contains("(good)", title);         // tier word — colour-blind-safe
        Assert.Contains("-70", title);            // range low
        Assert.Contains("-55", title);            // range high
    }

    [Fact]
    public void Title_IgnoresTrailingGap_ForLatestReading()
    {
        // Latest = last PRESENT sample, even when the window ends on an offline (gap) bucket. Read off
        // the Bootstrap-tooltip data-bs-title attribute (the native SVG <title> child is gone).
        var points = new List<RssiSparklinePoint> { P(0, -55), P(5, -62), P(10, null) };
        using var ctx = new BunitContext();
        var cut = ctx.Render<RssiSparkline>(p => p.Add(c => c.Points, points));

        var title = cut.Find("[data-bs-toggle=\"tooltip\"]").GetAttribute("data-bs-title")!;
        Assert.Contains("Latest -62 dBm", title);
    }
}
