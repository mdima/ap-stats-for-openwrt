using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Data.Repositories.History;
using ApStatsForOpenWrt.Services.Polling;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 19 M-HIST-05 (L-03) — THIN bUnit wiring test for KpiHistoryChart.razor. The bucketing
/// logic + DTO shape are owned + covered by KpiHistoryQueryService (Plan 02); this suite asserts
/// ONLY the component's lifecycle wiring per the L-03 contract:
///   - the query service is called on first render (interop happens in OnAfterRenderAsync, never
///     OnInitialized — Pitfall 1),
///   - PollNotifier.Cycled re-invokes the service (the live-refresh path),
///   - DisposeAsync unsubscribes (no captured-`this` leak).
/// NO assertion on the DTO payload shape — that is the service's responsibility (L-03).
/// JSInterop runs in Loose mode so the chart-interop.js import + InvokeVoidAsync calls are
/// no-op-recorded (bUnit cannot run real Chart.js).
/// </summary>
public sealed class KpiHistoryChartTests
{
    // Hand-rolled stub (TESTING.md convention — no mocking framework). Counts service calls.
    private sealed class StubKpiHistory : IKpiHistoryQueryService
    {
        private readonly KpiHistoryDto _dto;
        public int CallCount { get; private set; }
        public bool GetCalled => CallCount > 0;

        public StubKpiHistory(KpiHistoryDto? dto = null) =>
            _dto = dto ?? new KpiHistoryDto(Array.Empty<KpiHistoryBucket>(), IsEmpty: true);

        public Task<KpiHistoryDto> GetLast12hAsync(CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_dto);
        }
    }

    private static KpiHistoryDto NonEmptyDto()
    {
        var baseUtc = new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc);
        var buckets = new List<KpiHistoryBucket>
        {
            new(baseUtc,                 OnlineCount: 3, StaleCount: 1, OfflineCount: 2),
            new(baseUtc.AddMinutes(5),   OnlineCount: 4, StaleCount: 0, OfflineCount: 1),
        };
        return new KpiHistoryDto(buckets, IsEmpty: false);
    }

    private static (BunitContext ctx, StubKpiHistory kpi, PollNotifier notifier) Build(KpiHistoryDto? dto = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;   // import() + InvokeVoidAsync are no-op-recorded
        var kpi = new StubKpiHistory(dto);
        var notifier = new PollNotifier();
        ctx.Services.AddSingleton<IKpiHistoryQueryService>(kpi);
        ctx.Services.AddSingleton(notifier);
        return (ctx, kpi, notifier);
    }

    [Fact]
    public void FirstRender_CallsQueryService_NotDuringOnInitialized()
    {
        var (ctx, kpi, _) = Build(NonEmptyDto());
        using (ctx)
        {
            var cut = ctx.Render<KpiHistoryChart>();
            // OnAfterRenderAsync(firstRender) ran during Render → the service was called exactly once.
            Assert.True(kpi.GetCalled, "Query service must be called on first render.");
            Assert.Equal(1, kpi.CallCount);
        }
    }

    [Fact]
    public async Task Cycled_ReInvokesQueryService()
    {
        var (ctx, kpi, notifier) = Build(NonEmptyDto());
        using (ctx)
        {
            ctx.Render<KpiHistoryChart>();
            var afterFirstRender = kpi.CallCount;

            // Raise Cycled — the component must re-query.
            await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 5, DateTime.UtcNow));

            Assert.True(kpi.CallCount > afterFirstRender,
                $"Cycled must re-invoke the query service (was {afterFirstRender}, now {kpi.CallCount}).");
        }
    }

    [Fact]
    public async Task Dispose_Unsubscribes_NoFurtherServiceCalls()
    {
        var (ctx, kpi, notifier) = Build(NonEmptyDto());
        using (ctx)
        {
            var cut = ctx.Render<KpiHistoryChart>();
            await cut.Instance.DisposeAsync();
            var afterDispose = kpi.CallCount;

            // After dispose, a Cycled raise must NOT re-invoke the service (subscription detached).
            await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 5, DateTime.UtcNow));

            Assert.Equal(afterDispose, kpi.CallCount);
        }
    }

    [Fact]
    public void EmptyDto_StillRendersCanvas_ForOnCanvasNoDataOverlay()
    {
        // M-HIST-05 refinement of D-03 (operator request): the empty state now renders the canvas
        // so chart-interop.js can draw an on-canvas "history will appear here" overlay, instead of a
        // Razor placeholder. bUnit (Loose JSInterop) can't see canvas pixels, so assert the canvas is
        // present even when empty, the old Razor placeholder is gone, and the service was queried.
        var empty = new KpiHistoryDto(Array.Empty<KpiHistoryBucket>(), IsEmpty: true);
        var (ctx, kpi, _) = Build(empty);
        using (ctx)
        {
            var cut = ctx.Render<KpiHistoryChart>();
            Assert.NotEmpty(cut.FindAll("canvas"));
            Assert.DoesNotContain("Collecting history…", cut.Markup);
            Assert.True(kpi.GetCalled, "Query service must still be called so renderKpiChart runs the no-data overlay.");
        }
    }

    [Fact]
    public void NonEmptyDto_RendersCanvas()
    {
        var (ctx, _, _) = Build(NonEmptyDto());
        using (ctx)
        {
            var cut = ctx.Render<KpiHistoryChart>();
            Assert.NotEmpty(cut.FindAll("canvas"));
            Assert.DoesNotContain("Collecting history…", cut.Markup);
        }
    }
}
