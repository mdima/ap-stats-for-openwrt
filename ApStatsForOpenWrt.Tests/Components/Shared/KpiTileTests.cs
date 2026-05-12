using Bunit;
using Microsoft.AspNetCore.Components;
using ApStatsForOpenWrt.Components.Shared;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 8 D-18 / REQ-UI-01..04 — KpiTile component, 6 bUnit-testable visual states locked
/// in 08-UI-SPEC.md §Acceptance Test Fixtures > KpiTile.razor:
///   1. Default render: Label="Online" Count=42 IsActive=false → ".kpi-count" contains "42",
///      icon present, no .kpi-tile-active class, aria-pressed="false".
///   2. Active state: IsActive=true → .kpi-tile-active class present, aria-pressed="true".
///   3. Zero count: Count=0 → renders the literal "0" (regression: not "—", not "Empty").
///   4. High count: Count=9999 → tabular-nums width consistency (assertion: kpi-count class).
///   5. Click event: OnSelected EventCallback fires once per click.
///   6. Theme-stable structure: structural HTML identical regardless of ambient theme
///      (visual contrast is CSS-only, validated separately by the checker).
///
/// Test stack: xUnit v3 + bUnit 2.7.2 + Microsoft.Testing.Extensions.CodeCoverage (NEVER coverlet).
/// Coverage gate per D-18: ≥ 80% line + ≥ 80% branch on KpiTile.razor.
/// </summary>
public sealed class KpiTileTests
{
    [Fact]
    public void Renders_Count_And_Icon_When_Inactive()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<KpiTile>(p => p
            .Add(t => t.Label, "Online")
            .Add(t => t.Count, 42)
            .Add(t => t.IconClass, "bi-wifi")
            .Add(t => t.IsActive, false));

        Assert.Contains("42", cut.Markup);
        Assert.Contains("bi-wifi", cut.Markup);
        Assert.DoesNotContain("kpi-tile-active", cut.Markup);
        Assert.Contains("aria-pressed=\"false\"", cut.Markup);
    }

    [Fact]
    public void Active_State_Adds_Class_And_AriaPressed_True()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<KpiTile>(p => p
            .Add(t => t.Label, "Online")
            .Add(t => t.Count, 42)
            .Add(t => t.IconClass, "bi-wifi")
            .Add(t => t.IsActive, true));

        Assert.Contains("kpi-tile-active", cut.Markup);
        Assert.Contains("aria-pressed=\"true\"", cut.Markup);
    }

    [Fact]
    public void ZeroCount_Renders_Literal_Zero()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<KpiTile>(p => p
            .Add(t => t.Label, "Offline")
            .Add(t => t.Count, 0)
            .Add(t => t.IconClass, "bi-wifi-off"));

        // Regression guard: not "—", not "Empty" — literal "0" lives inside the kpi-count span.
        var span = cut.Find("h3.kpi-count");
        Assert.Equal("0", span.TextContent.Trim());
        Assert.DoesNotContain("Empty", cut.Markup);
        Assert.DoesNotContain("—", cut.Markup);
    }

    [Fact]
    public void HighCount_Renders_With_KpiCount_Class()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<KpiTile>(p => p
            .Add(t => t.Label, "All")
            .Add(t => t.Count, 9999)
            .Add(t => t.IconClass, "bi-collection-fill"));

        Assert.Contains("9999", cut.Markup);
        Assert.Contains("kpi-count", cut.Markup);
        // tabular-nums width consistency is locked in the scoped .razor.css, not assertable
        // via bUnit Markup — checker validates separately. We assert the class hook is present.
        var span = cut.Find("h3.kpi-count");
        Assert.Equal("9999", span.TextContent.Trim());
    }

    [Fact]
    public void Click_Fires_OnSelected_Once()
    {
        using var ctx = new BunitContext();
        var clicks = 0;
        var cut = ctx.Render<KpiTile>(p => p
            .Add(t => t.Label, "Online")
            .Add(t => t.Count, 1)
            .Add(t => t.IconClass, "bi-wifi")
            .Add(t => t.OnSelected, EventCallback.Factory.Create(this, () => { clicks++; })));

        cut.Find("button").Click();

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Renders_Same_Structural_Html_Regardless_Of_Theme()
    {
        // Dark-mode marker (08-UI-SPEC §Acceptance Test Fixtures > KpiTile.razor case 6):
        // structural HTML is identical across themes; visual contrast is CSS-only and
        // validated separately by the checker.
        using var ctx = new BunitContext();
        var cut = ctx.Render<KpiTile>(p => p
            .Add(t => t.Label, "Online")
            .Add(t => t.Count, 5)
            .Add(t => t.IconClass, "bi-wifi"));

        // Structural anchor: the kpi-count button shell + small-box  wrapper exist in any theme.
        Assert.Contains("kpi-count", cut.Markup);
        Assert.Contains("small-box", cut.Markup);
        // Native <button type="button"> for keyboard/ARIA correctness (REQ-UI-01).
        var button = cut.Find("button");
        Assert.Equal("button", button.GetAttribute("type"));
    }
}
