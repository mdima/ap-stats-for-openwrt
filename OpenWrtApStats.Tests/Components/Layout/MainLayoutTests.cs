using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Components.Layout;
using OpenWrtApStats.Services;
using OpenWrtApStats.Services.Gating;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Settings;
using Xunit;

namespace OpenWrtApStats.Tests.Components.Layout;

/// <summary>
/// D-20 smoke spike checkpoint #2 (structural). Verifies MainLayout renders the
/// AdminLTE 4 chrome elements UI-SPEC §MainLayout (lines 64-94) requires.
/// Real sidebar-collapse JS lifecycle is exercised at phase close-out manual smoke.
///
/// Phase 5 D-12 update: the Phase-3 placeholder anchor at line 16
/// (`data-phase6-anchor="Phase 6: Live/Paused indicator slot"`) is now claimed by
/// the locked Phase-5 right-cluster of <LivePausedBadge /> + <PollCountdown />. The
/// test fixture registers ICircuitGate, PollNotifier, and IAppSettingsService so the
/// new badges render; the assertion now verifies those badges instead of the
/// placeholder anchor text.
///
/// Phase 8 Plan 09 update: MainLayout.razor now mounts <ThemeToggle /> as the last
/// child of the topbar right cluster (REQ-UI-31) and <ToastHost /> at the body root
/// (REQ-UI-40, REQ-UI-44). The fixture additionally registers PreferencesService +
/// INotificationsGate (FakeGate) + ToastService and a Loose-mode bUnit JSInterop
/// module mock for `./js/preferences.js` and `./js/theme.js` so those components can
/// resolve their @inject dependencies. This mirrors the Plan 08-08 pattern adopted
/// by DashboardTests.cs and StaleHysteresisTests.cs (Rule 3 blocking — Phase 8 plan
/// 08-09 deviation #1).
/// </summary>
public sealed class MainLayoutTests
{
    private sealed class StubSettings : IAppSettingsService
    {
        public int     PollingIntervalSeconds { get; } = 30;
        public string? UIPasswordHash         { get; } = null;
        public bool    WizardDismissed        { get; } = true;
        public Task SetPollingIntervalSecondsAsync(int s, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? h, CancellationToken ct = default)     => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool d, CancellationToken ct = default)       => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default)                              => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private sealed class FakeGate : INotificationsGate
    {
        public bool IsEnabled { get; init; } = true;
    }

    [Fact]
    public void Renders_AdminLte_Chrome_With_Phase5_LivePausedBadge_And_PollCountdown()
    {
        using var ctx = new Bunit.BunitContext();
        // Plan 03-03 added <AuthorizeView> to MainLayout's user-menu — register the bUnit
        // test-authorization stack so AuthorizationPolicyProvider/AuthenticationStateProvider
        // can be resolved. Default state = unauthorized → user-menu hidden (anonymous mode).
        ctx.AddAuthorization();
        // Phase 5 D-12 — register the gate seam, notifier, and settings the new badges consume.
        ctx.Services.AddSingleton<ICircuitGate, AlwaysLiveGate>();
        ctx.Services.AddSingleton(new PollNotifier());
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings());
        // Phase 8 Plan 09 — ThemeToggle injects PreferencesService; ToastHost injects ToastService.
        // Loose JS interop mode covers the OnAfterRenderAsync(firstRender:true) hydration paths
        // in ThemeToggle (preferences.js + theme.js) — same pattern as DashboardTests Build helper.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");
        ctx.JSInterop.SetupModule("./js/theme.js");
        ctx.Services.AddSingleton(new PreferencesService(ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton<INotificationsGate>(new FakeGate { IsEnabled = true });
        ctx.Services.AddSingleton<ToastService>(sp =>
            new ToastService(
                sp.GetRequiredService<INotificationsGate>(),
                NullLogger<ToastService>.Instance));

        // MainLayout inherits LayoutComponentBase, which exposes the slot as `Body` not
        // `ChildContent`. bUnit's AddChildContent looks for [Parameter] ChildContent on the
        // component, so we have to bind Body explicitly via Add(p => p.Body, ...).
        var cut = ctx.Render<MainLayout>(parameters => parameters
            .Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>test body</p>"))));

        // Top-nav with sidebar-toggle
        var topnav = cut.Find("nav.app-header");
        Assert.NotNull(topnav);
        // data-app-toggle (NOT data-lte-toggle): the toggle is wired by a document-delegated
        // listener in /js/sidebar-toggle.js so it survives Blazor's interactive remount.
        // See .planning/debug/resolved/sidebar-toggle-no-collapse.md.
        var toggle = topnav.QuerySelector("button[data-app-toggle='sidebar'] i.bi.bi-list");
        Assert.NotNull(toggle);

        // Phase 5 right-cluster: both badges render (LivePausedBadge renders "Live" via AlwaysLiveGate;
        // PollCountdown renders "—" + the initial-pending aria-label before any Cycled has fired).
        Assert.Contains("Live", cut.Markup);                           // LivePausedBadge with AlwaysLiveGate
        Assert.Contains("text-bg-success", cut.Markup);                // LivePausedBadge color
        Assert.Contains("bi-circle-fill", cut.Markup);                 // LivePausedBadge icon
        Assert.Contains("font-variant-numeric: tabular-nums", cut.Markup);  // PollCountdown tabular-nums style
        // Initial PollCountdown aria-label is "Next poll: pending" before any Tick fires;
        // after the first Tick (no Cycled yet) it becomes "Awaiting first poll". Either is acceptable.
        Assert.True(
            cut.Markup.Contains("Next poll: pending") || cut.Markup.Contains("Awaiting first poll"),
            $"Expected pending or awaiting-first-poll aria-label; got: {cut.Markup}");

        // Right-cluster wrapper (UI-SPEC line 609 LOCK).
        Assert.Contains("d-flex align-items-center gap-2 me-3", cut.Markup);

        // The Phase-3 placeholder anchor must be gone (Phase 5 claims the slot).
        Assert.DoesNotContain("Phase 6: Live/Paused indicator slot", cut.Markup);
        Assert.DoesNotContain("data-phase6-anchor", cut.Markup);

        // Sidebar with brand block
        var aside = cut.Find("aside.app-sidebar");
        Assert.NotNull(aside);
        var brand = aside.QuerySelector(".sidebar-brand .brand-text");
        Assert.NotNull(brand);
        Assert.Equal("OpenWRT-AP-Stats", brand!.TextContent.Trim());

        // Phase 8 Plan 09 — REQ-UI-70: branded SVG logo replaces the AdminLTE default PNG
        // in the sidebar brand-link. AdminLTELogo.png MUST NOT appear anywhere; /img/logo.svg
        // MUST be referenced; the brand-image must NOT carry the opacity-75/shadow classes.
        var brandImg = aside.QuerySelector(".sidebar-brand .brand-link img.brand-image");
        Assert.NotNull(brandImg);
        Assert.Equal("/img/logo.svg", brandImg!.GetAttribute("src"));
        Assert.Equal("OpenWRT-AP-Stats logo", brandImg.GetAttribute("alt"));
        Assert.DoesNotContain("AdminLTELogo", cut.Markup);
        Assert.DoesNotContain("opacity-75", brandImg.GetAttribute("class") ?? string.Empty);

        // Phase 8 Plan 09 — REQ-UI-31: ThemeToggle is the LAST child of the topbar right cluster.
        // Locate the cluster div, then its sun/moon button (BootstrapIcons bi-sun-fill/bi-moon-stars-fill).
        var rightCluster = topnav.QuerySelector("div.d-flex.align-items-center.gap-2.me-3");
        Assert.NotNull(rightCluster);
        var themeBtn = rightCluster!.QuerySelector("button i.bi.bi-sun-fill, button i.bi.bi-moon-stars-fill");
        Assert.NotNull(themeBtn);

        // Phase 8 Plan 09 — REQ-UI-40 / REQ-UI-44: ToastHost mounts exactly once at body root.
        // ToastHost renders a position-fixed toast-container with aria-live="polite".
        var toastContainers = cut.FindAll("div.toast-container.position-fixed[aria-live='polite']");
        Assert.Single(toastContainers);

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
