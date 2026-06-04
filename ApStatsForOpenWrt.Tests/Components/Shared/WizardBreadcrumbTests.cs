using Bunit;
using ApStatsForOpenWrt.Components.Shared;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 19 Plan 05 / D-01 — WizardBreadcrumb 3→4 step extension. The bespoke breadcrumb now
/// renders `1 Password › 2 Nodes › 3 WLANs › 4 History`. The WR-03 "always-last" invariant
/// shifts from WLANs to History; WLANs gains a completed/clickable-link state when
/// CurrentStep > 3 (it is no longer the terminus).
///
/// Cases:
///   - CurrentStep=4 → `4 History` rendered as the current step (fw-semibold text-body) and
///     `3 WLANs` rendered as a completed link `<a href="/setup/wlans">`.
///   - CurrentStep=3 → `3 WLANs` is current (fw-semibold) and `4 History` is future
///     (text-body-secondary, NOT a link).
/// </summary>
public sealed class WizardBreadcrumbTests
{
    [Fact]
    public void CurrentStep4_RendersHistoryCurrent_AndWlansCompletedLink()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<WizardBreadcrumb>(p => p.Add(c => c.CurrentStep, 4));

        // History (step 4) is the current step — fw-semibold text-body span, NOT a link.
        var historySpan = cut.FindAll("span")
            .FirstOrDefault(s => s.TextContent.Trim() == "4 History");
        Assert.NotNull(historySpan);
        Assert.Contains("fw-semibold", historySpan!.GetAttribute("class") ?? string.Empty);
        Assert.Contains("text-body", historySpan.GetAttribute("class") ?? string.Empty);

        // WLANs (step 3) is now a completed clickable link to /setup/wlans.
        var wlansLink = cut.FindAll("a")
            .FirstOrDefault(a => a.TextContent.Trim() == "3 WLANs");
        Assert.NotNull(wlansLink);
        Assert.Equal("/setup/wlans", wlansLink!.GetAttribute("href"));
        Assert.Contains("text-primary", wlansLink.GetAttribute("class") ?? string.Empty);
    }

    [Fact]
    public void CurrentStep3_RendersWlansCurrent_AndHistoryFuture()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<WizardBreadcrumb>(p => p.Add(c => c.CurrentStep, 3));

        // WLANs (step 3) is the current step — fw-semibold span, not a link.
        var wlansSpan = cut.FindAll("span")
            .FirstOrDefault(s => s.TextContent.Trim() == "3 WLANs");
        Assert.NotNull(wlansSpan);
        Assert.Contains("fw-semibold", wlansSpan!.GetAttribute("class") ?? string.Empty);
        Assert.DoesNotContain(cut.FindAll("a"), a => a.TextContent.Trim() == "3 WLANs");

        // History (step 4) is a future step — muted text-body-secondary span, never a link.
        var historySpan = cut.FindAll("span")
            .FirstOrDefault(s => s.TextContent.Trim() == "4 History");
        Assert.NotNull(historySpan);
        Assert.Contains("text-body-secondary", historySpan!.GetAttribute("class") ?? string.Empty);
        Assert.DoesNotContain(cut.FindAll("a"), a => a.TextContent.Trim() == "4 History");
    }
}
