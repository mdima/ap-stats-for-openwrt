using Bunit;
using ApStatsForOpenWrt.Components.Shared;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 5 D-06 FilterChipGroup — DASH-06 SSID + AP filter chips.
/// Phase 8 D-18 / REQ-UI-11 EXTENSION — optional StatusBadgeFor parameter; 5 visual
/// states locked in 08-UI-SPEC.md §FilterChipGroup.razor.
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

    // ---- Phase 8 REQ-UI-11 EXTENSION — 5 bUnit-testable visual states ----
    // 08-UI-SPEC.md §FilterChipGroup.razor (extended branches only):
    //   1. StatusBadgeFor == null               → no nested StatusBadge (regression guard)
    //   2. StatusBadgeFor returns Online        → nested text-bg-success
    //   3. StatusBadgeFor returns Offline       → nested text-bg-secondary
    //   4. StatusBadgeFor returns Stale         → nested text-bg-warning
    //   5. StatusBadgeFor returns Hidden        → no nested badge for that chip

    private static readonly IReadOnlyList<string> SampleAps =
        new[] { "kitchen-ap", "office-ap", "garage-ap" };

    [Fact]
    public void StatusBadgeFor_Null_RendersWithoutNestedBadge_Phase5RegressionGuard()
    {
        // State 1: default branch (SSID + Status existing call sites must remain unchanged).
        using var ctx = new BunitContext();
        var cut = ctx.Render<FilterChipGroup>(p => p
            .Add(c => c.Title, "AP")
            .Add(c => c.Options, SampleAps)
            .Add(c => c.Selected, new HashSet<string>()));
        // No StatusBadgeFor supplied → null branch.

        // No StatusBadge ever emitted: the only `.badge` markup that StatusBadge produces
        // is the `text-bg-*` color class. Assert none appears.
        Assert.DoesNotContain("text-bg-success",   cut.Markup);
        Assert.DoesNotContain("text-bg-secondary", cut.Markup);
        Assert.DoesNotContain("text-bg-warning",   cut.Markup);
        // Existing chip <button>s still render one per option.
        Assert.Equal(SampleAps.Count, cut.FindAll("button").Count);
    }

    [Fact]
    public void StatusBadgeFor_Online_RendersNestedTextBgSuccess()
    {
        // State 2: Online branch.
        using var ctx = new BunitContext();
        var cut = ctx.Render<FilterChipGroup>(p => p
            .Add(c => c.Title, "AP")
            .Add(c => c.Options, SampleAps)
            .Add(c => c.Selected, new HashSet<string>())
            .Add(c => c.StatusBadgeFor, (Func<string, FilterChipGroup.BadgeState>)(_ => FilterChipGroup.BadgeState.Online)));

        Assert.Contains("text-bg-success", cut.Markup);
        Assert.Contains("Online",          cut.Markup);
        // Each chip <button> contains a nested <span class="ms-1"> wrapping the badge.
        Assert.Equal(SampleAps.Count, cut.FindAll("button > span.ms-1").Count);
    }

    [Fact]
    public void StatusBadgeFor_Offline_RendersNestedTextBgSecondary()
    {
        // State 3: Offline branch.
        using var ctx = new BunitContext();
        var cut = ctx.Render<FilterChipGroup>(p => p
            .Add(c => c.Title, "AP")
            .Add(c => c.Options, SampleAps)
            .Add(c => c.Selected, new HashSet<string>())
            .Add(c => c.StatusBadgeFor, (Func<string, FilterChipGroup.BadgeState>)(_ => FilterChipGroup.BadgeState.Offline)));

        Assert.Contains("text-bg-secondary", cut.Markup);
        Assert.Contains("Offline",           cut.Markup);
        Assert.DoesNotContain("text-bg-success", cut.Markup);
        Assert.DoesNotContain("text-bg-warning", cut.Markup);
    }

    [Fact]
    public void StatusBadgeFor_Stale_RendersNestedTextBgWarning()
    {
        // State 4: Stale branch (Status.Offline + IsStale=true → Phase 5 D-09 mapping).
        using var ctx = new BunitContext();
        var cut = ctx.Render<FilterChipGroup>(p => p
            .Add(c => c.Title, "AP")
            .Add(c => c.Options, SampleAps)
            .Add(c => c.Selected, new HashSet<string>())
            .Add(c => c.StatusBadgeFor, (Func<string, FilterChipGroup.BadgeState>)(_ => FilterChipGroup.BadgeState.Stale)));

        Assert.Contains("text-bg-warning", cut.Markup);
        Assert.Contains("Stale",           cut.Markup);
        Assert.DoesNotContain("text-bg-success",   cut.Markup);
        Assert.DoesNotContain("text-bg-secondary", cut.Markup);
    }

    [Fact]
    public void StatusBadgeFor_Hidden_SuppressesBadgeForThatChip()
    {
        // State 5: Hidden branch (allows partial coverage when an AP's status is unknown).
        using var ctx = new BunitContext();
        var cut = ctx.Render<FilterChipGroup>(p => p
            .Add(c => c.Title, "AP")
            .Add(c => c.Options, SampleAps)
            .Add(c => c.Selected, new HashSet<string>())
            .Add(c => c.StatusBadgeFor, (Func<string, FilterChipGroup.BadgeState>)(_ => FilterChipGroup.BadgeState.Hidden)));

        // Chip <button>s still render, one per option.
        Assert.Equal(SampleAps.Count, cut.FindAll("button").Count);
        // No StatusBadge color class appears anywhere — Hidden suppresses the badge
        // entirely (no <span class="ms-1"> wrapper either).
        Assert.DoesNotContain("text-bg-success",   cut.Markup);
        Assert.DoesNotContain("text-bg-secondary", cut.Markup);
        Assert.DoesNotContain("text-bg-warning",   cut.Markup);
        Assert.Empty(cut.FindAll("button > span.ms-1"));
    }

    [Fact]
    public void StatusBadgeFor_PerOption_MixedStates_RenderEachChipIndependently()
    {
        // Coverage tightening: exercise all four non-null branches in a single render
        // so the inner @switch's case arms are reached together (D-18 ≥ 80% branch on
        // the new conditional).
        using var ctx = new BunitContext();
        var cut = ctx.Render<FilterChipGroup>(p => p
            .Add(c => c.Title, "AP")
            .Add(c => c.Options, new List<string> { "online-ap", "offline-ap", "stale-ap", "hidden-ap" })
            .Add(c => c.Selected, new HashSet<string>())
            .Add(c => c.StatusBadgeFor, (Func<string, FilterChipGroup.BadgeState>)(opt => opt switch
            {
                "online-ap"  => FilterChipGroup.BadgeState.Online,
                "offline-ap" => FilterChipGroup.BadgeState.Offline,
                "stale-ap"   => FilterChipGroup.BadgeState.Stale,
                "hidden-ap"  => FilterChipGroup.BadgeState.Hidden,
                _            => FilterChipGroup.BadgeState.Hidden,
            })));

        Assert.Contains("text-bg-success",   cut.Markup);
        Assert.Contains("text-bg-secondary", cut.Markup);
        Assert.Contains("text-bg-warning",   cut.Markup);
        // Three visible badges (online/offline/stale); hidden-ap suppresses one.
        Assert.Equal(3, cut.FindAll("button > span.ms-1").Count);
    }
}
