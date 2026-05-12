using Bunit;
using OpenWrtApStats.Components.Shared;
using Xunit;

namespace OpenWrtApStats.Tests.Components.Shared;

/// <summary>
/// Phase 5 B1 ownership — EmptyState.razor's CTA is optional. Both forms must work:
/// CTA-bearing (Phase 3 callers: DASH-09, DASH-10, Nodes Index, Node Detail) and CTA-less
/// (Phase 5 DASH-11). Regression guards for both forms live here.
/// </summary>
public sealed class EmptyStateTests
{
    [Fact]
    public void WithoutCtaLabel_OmitsCtaButton()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<EmptyState>(p => p
            .Add(c => c.IconClass, "bi-wifi")
            .Add(c => c.Heading, "WLANs are healthy, no clients right now")
            .Add(c => c.Body, "Last poll: just now."));
            // Note: no CtaLabel / CtaHref / CtaIconClass passed.

        Assert.Contains("WLANs are healthy", cut.Markup);
        Assert.Contains("Last poll: just now", cut.Markup);
        Assert.Contains("bi-wifi", cut.Markup);
        // The CTA <a class="btn btn-primary"> must not render when CtaLabel is empty.
        Assert.DoesNotContain("class=\"btn btn-primary\"", cut.Markup);
        Assert.DoesNotContain("btn-primary", cut.Markup);
        Assert.Empty(cut.FindAll("a.btn"));
    }

    [Fact]
    public void WithCtaLabel_RendersCtaButton()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<EmptyState>(p => p
            .Add(c => c.IconClass, "bi-router-fill")
            .Add(c => c.Heading, "No access points yet")
            .Add(c => c.Body, "Add your first OpenWRT access point to start monitoring.")
            .Add(c => c.CtaLabel, "Add your first OpenWRT node")
            .Add(c => c.CtaHref, "/Nodes/Add")
            .Add(c => c.CtaIconClass, "bi-plus-lg"));

        // Existing DASH-09 shape — must still render the CTA exactly as Phase 3.
        Assert.Contains("No access points yet", cut.Markup);
        Assert.Contains("class=\"btn btn-primary\"", cut.Markup);
        Assert.Contains("href=\"/Nodes/Add\"", cut.Markup);
        Assert.Contains("Add your first OpenWRT node", cut.Markup);
        Assert.Contains("bi-plus-lg", cut.Markup);
        Assert.Single(cut.FindAll("a.btn-primary"));
    }

    [Fact]
    public void WithEmptyStringCtaLabel_OmitsCtaButton()
    {
        // Belt-and-braces: explicitly empty string also omits the CTA (DASH-11 may pass "" rather than omitting).
        using var ctx = new BunitContext();
        var cut = ctx.Render<EmptyState>(p => p
            .Add(c => c.IconClass, "bi-wifi")
            .Add(c => c.Heading, "WLANs are healthy, no clients right now")
            .Add(c => c.Body, "Last poll: 5s ago.")
            .Add(c => c.CtaLabel, "")
            .Add(c => c.CtaHref, "")
            .Add(c => c.CtaIconClass, ""));

        Assert.DoesNotContain("btn-primary", cut.Markup);
    }

    // Phase 7 row 4.1 follow-up: optional secondary ghost-link below the primary CTA.

    [Fact]
    public void WithoutSecondaryLabel_OmitsSecondaryLink()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<EmptyState>(p => p
            .Add(c => c.IconClass, "bi-router-fill")
            .Add(c => c.Heading, "No access points yet")
            .Add(c => c.Body, "Add your first OpenWRT access point.")
            .Add(c => c.CtaLabel, "Add your first OpenWRT node")
            .Add(c => c.CtaHref, "/Nodes/Add")
            .Add(c => c.CtaIconClass, "bi-plus-lg"));
            // No SecondaryLabel passed.

        Assert.Single(cut.FindAll("a.btn-primary"));
        // No secondary anchor — only the primary CTA.
        Assert.Empty(cut.FindAll("a.text-body-secondary"));
    }

    [Fact]
    public void WithSecondaryLabel_RendersGhostLinkBelowPrimary()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<EmptyState>(p => p
            .Add(c => c.IconClass, "bi-router-fill")
            .Add(c => c.Heading, "No access points yet")
            .Add(c => c.Body, "Add your first OpenWRT access point.")
            .Add(c => c.CtaLabel, "Add your first OpenWRT node")
            .Add(c => c.CtaHref, "/Nodes/Add")
            .Add(c => c.CtaIconClass, "bi-plus-lg")
            .Add(c => c.SecondaryLabel, "Continue setup")
            .Add(c => c.SecondaryHref, "/setup/password")
            .Add(c => c.SecondaryIconClass, "bi-arrow-right-circle"));

        // Primary CTA still present.
        Assert.Single(cut.FindAll("a.btn-primary"));
        // Secondary ghost-link is present with the right copy + href + icon.
        var secondary = cut.Find("a.text-body-secondary");
        Assert.Contains("Continue setup", secondary.TextContent);
        Assert.Equal("/setup/password", secondary.GetAttribute("href"));
        Assert.NotNull(secondary.QuerySelector("i.bi-arrow-right-circle"));

        // DOM order: secondary must follow primary.
        var primaryIdx = cut.Markup.IndexOf("Add your first OpenWRT node", System.StringComparison.Ordinal);
        var secondaryIdx = cut.Markup.IndexOf("Continue setup", System.StringComparison.Ordinal);
        Assert.True(primaryIdx >= 0 && secondaryIdx > primaryIdx);
    }
}
