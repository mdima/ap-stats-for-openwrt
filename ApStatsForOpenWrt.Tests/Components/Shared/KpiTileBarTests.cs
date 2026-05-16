using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services;
using ApStatsForOpenWrt.Services.Polling;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 8 D-18 / REQ-UI-01..04 — KpiTileBar component, 6 bUnit-testable states locked
/// in 08-UI-SPEC.md §Acceptance Test Fixtures > KpiTileBar.razor:
///   1. With 4 rows (1 Online, 2 Stale, 1 Offline) → _total=4, _online=1, _stale=2, _offline=1.
///   2. Click "Online" tile (inactive) → OnActiveChanged invoked with StatusFilter.Online.
///   3. Click active "Online" tile → OnActiveChanged invoked with null (REQ-UI-04 toggle off).
///   4. PollNotifier.Cycled subscription: raising the event re-derives counts and re-renders.
///   5. Dispose unsubscribes from PollNotifier — post-dispose Cycled raises do not re-render.
///   6. Responsive: col-md-3 / col-sm-6 / col-12 wrapper classes locked in markup.
///
/// Test stack: xUnit v3 + bUnit 2.7.2 + Microsoft.Testing.Extensions.CodeCoverage (NEVER coverlet).
/// Coverage gate per D-18: ≥ 80% line + ≥ 80% branch on KpiTileBar.razor.
/// </summary>
public sealed class KpiTileBarTests
{
    private static (BunitContext Ctx, PollNotifier Notifier) CreateContext()
    {
        var ctx = new BunitContext();
        var notifier = new PollNotifier();
        ctx.Services.AddSingleton(notifier);
        return (ctx, notifier);
    }

    private static PollSnapshot DummySnapshot() =>
        new(TotalNodes: 0, OnlineNodes: 0, FailedNodes: 0, StationCount: 0, CycleEndUtc: DateTime.UtcNow);

    // 1. Counts derive correctly from a 4-row sample (1 Online, 2 Stale, 1 Offline). ----------
    [Fact]
    public void With_4_Rows_Derives_Counts_Correctly()
    {
        var (ctx, _) = CreateContext();
        using var _disp = ctx;
        var rows = new List<DashboardKpiRow>
        {
            new(Status.Online,  IsStale: false), // Online
            new(Status.Offline, IsStale: true),  // Stale
            new(Status.Offline, IsStale: true),  // Stale
            new(Status.Offline, IsStale: false), // Offline
        };
        var cut = ctx.Render<KpiTileBar>(p => p.Add(b => b.Rows, rows));

        // Each KpiTile renders the count inside <span class="kpi-count">.
        // Tiles render in order: All / Online / Stale / Offline.
        var counts = cut.FindAll("h3.kpi-count");
        Assert.Equal(4, counts.Count);
        Assert.Equal("4", counts[0].TextContent.Trim()); // total
        Assert.Equal("1", counts[1].TextContent.Trim()); // online
        Assert.Equal("2", counts[2].TextContent.Trim()); // stale
        Assert.Equal("1", counts[3].TextContent.Trim()); // offline
    }

    // 2. Click on inactive Online tile emits StatusFilter.Online. -------------------------------
    [Fact]
    public void Click_Online_Tile_Emits_OnActiveChanged_Online()
    {
        var (ctx, _) = CreateContext();
        using var _disp = ctx;
        StatusFilter? captured = StatusFilter.Stale; // sentinel — must be overwritten
        var captureCount = 0;

        var cut = ctx.Render<KpiTileBar>(p => p
            .Add(b => b.Rows, new List<DashboardKpiRow> { new(Status.Online, false) })
            .Add(b => b.Active, (StatusFilter?)null) // All active
            .Add(b => b.OnActiveChanged, EventCallback.Factory.Create<StatusFilter?>(this, v =>
            {
                captured = v;
                captureCount++;
            })));

        // Tile order: [0]=All, [1]=Online, [2]=Stale, [3]=Offline.
        cut.FindAll("button")[1].Click();

        Assert.Equal(1, captureCount);
        Assert.Equal(StatusFilter.Online, captured);
    }

    // 3. Click on the active Online tile emits null (toggle-off → All becomes active). --------
    [Fact]
    public void Click_Active_Online_Tile_Emits_Null()
    {
        var (ctx, _) = CreateContext();
        using var _disp = ctx;
        StatusFilter? captured = StatusFilter.Online; // sentinel — distinct from emit
        var captureCount = 0;

        var cut = ctx.Render<KpiTileBar>(p => p
            .Add(b => b.Rows, new List<DashboardKpiRow> { new(Status.Online, false) })
            .Add(b => b.Active, StatusFilter.Online) // Online active
            .Add(b => b.OnActiveChanged, EventCallback.Factory.Create<StatusFilter?>(this, v =>
            {
                captured = v;
                captureCount++;
            })));

        // Click the already-active Online tile → REQ-UI-04: emit null, All becomes active.
        cut.FindAll("button")[1].Click();

        Assert.Equal(1, captureCount);
        Assert.Null(captured);
    }

    // 4. Cycled event re-derives counts and re-renders. ----------------------------------------
    [Fact]
    public async Task PollNotifier_Cycled_Triggers_Recompute_And_Render()
    {
        var (ctx, notifier) = CreateContext();
        using var _disp = ctx;
        // Mutable list — simulates the parent Dashboard re-projecting rows on each Cycled.
        var rows = new List<DashboardKpiRow> { new(Status.Online, false) }; // total=1, online=1
        var cut = ctx.Render<KpiTileBar>(p => p.Add(b => b.Rows, rows));

        var initialCounts = cut.FindAll("h3.kpi-count");
        Assert.Equal("1", initialCounts[0].TextContent.Trim()); // total = 1
        Assert.Equal("1", initialCounts[1].TextContent.Trim()); // online = 1

        // Parent appends 2 stale + 1 offline rows; Cycled fires → handler recomputes + renders.
        rows.Add(new(Status.Offline, IsStale: true));
        rows.Add(new(Status.Offline, IsStale: true));
        rows.Add(new(Status.Offline, IsStale: false));
        await notifier.RaiseAsync(DummySnapshot());

        var afterCounts = cut.FindAll("h3.kpi-count");
        Assert.Equal("4", afterCounts[0].TextContent.Trim()); // total = 4
        Assert.Equal("1", afterCounts[1].TextContent.Trim()); // online = 1
        Assert.Equal("2", afterCounts[2].TextContent.Trim()); // stale = 2
        Assert.Equal("1", afterCounts[3].TextContent.Trim()); // offline = 1
    }

    // 5. Dispose unsubscribes from PollNotifier. -----------------------------------------------
    [Fact]
    public async Task Dispose_Unsubscribes_From_PollNotifier()
    {
        var (ctx, notifier) = CreateContext();
        using var _disp = ctx;
        var rows = new List<DashboardKpiRow> { new(Status.Online, false) };
        var cut = ctx.Render<KpiTileBar>(p => p.Add(b => b.Rows, rows));

        // Sanity: the live component re-renders on Cycled (RenderCount increments).
        var rendersBefore = cut.RenderCount;
        rows.Add(new(Status.Online, false));
        await notifier.RaiseAsync(DummySnapshot());
        Assert.True(cut.RenderCount > rendersBefore,
            $"Live KpiTileBar should re-render on Cycled (rendersBefore={rendersBefore}, after={cut.RenderCount})");
        Assert.Equal("2", cut.FindAll("h3.kpi-count")[0].TextContent.Trim());

        // Attach a probe handler so the post-dispose RaiseAsync still has at least one
        // subscriber — proves the notifier itself is alive AND the only delegate it routes to
        // is our probe (not a leaked KpiTileBar.OnCycledAsync).
        var probeInvocations = 0;
        Func<PollSnapshot, Task> probe = _ => { probeInvocations++; return Task.CompletedTask; };
        notifier.Cycled += probe;

        cut.Dispose();

        // Mutate rows again + raise Cycled. If KpiTileBar correctly unsubscribed in Dispose,
        // the probe fires (notifier alive) but no leaked handler tries to re-render the
        // disposed component (which would throw ComponentDisposedException via InvokeAsync).
        rows.Add(new(Status.Offline, IsStale: true));
        rows.Add(new(Status.Offline, IsStale: true));
        await notifier.RaiseAsync(DummySnapshot()); // MUST NOT throw — proves no leaked subscriber

        // Probe fired, confirming the notifier is still functional (sanity check).
        Assert.Equal(1, probeInvocations);

        // Cleanup probe so the test context dispose is clean.
        notifier.Cycled -= probe;
    }

    // 6. Responsive grid wrappers locked. ------------------------------------------------------
    [Fact]
    public void Tiles_Have_Responsive_Wrapper_Classes()
    {
        var (ctx, _) = CreateContext();
        using var _disp = ctx;
        var cut = ctx.Render<KpiTileBar>(p => p.Add(b => b.Rows, new List<DashboardKpiRow>()));

        // 4 wrappers (one per tile), each col-md-3 col-sm-6 col-6 per 08-UI-SPEC §Responsive.
        var wrappers = cut.FindAll("div.col-md-3.col-sm-6.col-6");
        Assert.Equal(4, wrappers.Count);

        // The role/aria-label group anchor for the bar.
        Assert.Contains("role=\"group\"", cut.Markup);
        Assert.Contains("aria-label=\"Status filter — KPI tiles\"", cut.Markup);

        // 4 KpiTile <button>s (All / Online / Stale / Offline).
        Assert.Equal(4, cut.FindAll("button.small-box").Count);
    }
}
