using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using OpenWrtApStats.Components.Layout;
using Xunit;

namespace OpenWrtApStats.Tests.Components.Layout;

/// <summary>
/// D-20 smoke spike checkpoint #2 (structural). Verifies MainLayout renders the
/// AdminLTE 4 chrome elements UI-SPEC §MainLayout (lines 64-94) requires.
/// Real sidebar-collapse JS lifecycle is exercised at phase close-out manual smoke.
/// </summary>
public sealed class MainLayoutTests
{
    [Fact]
    public void Renders_AdminLte_Chrome_With_Phase6_Anchor()
    {
        using var ctx = new Bunit.BunitContext();
        // Plan 03-03 added <AuthorizeView> to MainLayout's user-menu — register the bUnit
        // test-authorization stack so AuthorizationPolicyProvider/AuthenticationStateProvider
        // can be resolved. Default state = unauthorized → user-menu hidden (anonymous mode).
        ctx.AddAuthorization();
        // MainLayout inherits LayoutComponentBase, which exposes the slot as `Body` not
        // `ChildContent`. bUnit's AddChildContent looks for [Parameter] ChildContent on the
        // component, so we have to bind Body explicitly via Add(p => p.Body, ...).
        var cut = ctx.Render<MainLayout>(parameters => parameters
            .Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>test body</p>"))));

        // Top-nav with sidebar-toggle
        var topnav = cut.Find("nav.app-header");
        Assert.NotNull(topnav);
        var toggle = topnav.QuerySelector("button[data-lte-toggle='sidebar'] i.bi.bi-list");
        Assert.NotNull(toggle);

        // Phase 6 anchor comment — present in the rendered markup
        Assert.Contains("Phase 6: Live/Paused indicator slot", cut.Markup);

        // Sidebar with brand block
        var aside = cut.Find("aside.app-sidebar");
        Assert.NotNull(aside);
        var brand = aside.QuerySelector(".sidebar-brand .brand-text");
        Assert.NotNull(brand);
        Assert.Equal("OpenWRT-AP-Stats", brand!.TextContent.Trim());

        // NavMenu rendered (3-item: Dashboard / Nodes / Settings; the 4th placeholder is reserved)
        var navItems = aside.QuerySelectorAll(".sidebar-menu .nav-item .nav-link");
        Assert.True(navItems.Count() >= 3, "Sidebar must contain Dashboard / Nodes / Settings");

        // Body slot (test child content)
        Assert.Contains("<p>test body</p>", cut.Markup);

        // Footer with the project name + literal copy
        var footer = cut.Find("footer.app-footer");
        Assert.NotNull(footer);
        Assert.Contains("OpenWRT-AP-Stats", footer.TextContent);
        Assert.Contains("Self-hosted dashboard", footer.TextContent);
    }
}
