using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Data.Repositories.History;
using ApStatsForOpenWrt.Services.Polling;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 19 M-HIST-07 (L-03) — THIN bUnit wiring test for RssiHistoryChart.razor. The per-series
/// bucketing logic + multi-series DTO shape are owned + covered by RssiHistoryQueryService (Plan 02);
/// this suite asserts ONLY the component's lifecycle wiring per the L-03 contract:
///   - the detail query service is called on first render (interop happens in OnAfterRenderAsync,
///     never OnInitialized — Pitfall 1),
///   - PollNotifier.Cycled re-invokes the service (the live-refresh path),
///   - DisposeAsync unsubscribes (no captured-`this` leak),
///   - empty DTO → "Collecting history…" placeholder (D-03), non-empty → <canvas>.
/// NO assertion on the DTO payload shape — that is the service's responsibility (L-03).
/// JSInterop runs in Loose mode so the chart-interop.js import + InvokeVoidAsync calls are
/// no-op-recorded (bUnit cannot run real Chart.js).
/// </summary>
public sealed class RssiHistoryChartTests
{
    private const string Mac = "AA:BB:CC:00:00:01";

    // Hand-rolled stub (TESTING.md convention — no mocking framework). Counts service calls and
    // records the MAC last queried so the Mac-change path can be pinned.
    private sealed class StubRssiHistory : IRssiHistoryQueryService
    {
        private readonly RssiDetailDto _detail;
        public int DetailCallCount { get; private set; }
        public string? LastDetailMac { get; private set; }
        public bool DetailCalled => DetailCallCount > 0;

        public StubRssiHistory(RssiDetailDto? detail = null) =>
            _detail = detail ?? new RssiDetailDto(
                Array.Empty<DateTime>(), Array.Empty<RssiDetailSeries>(), IsEmpty: true);

        public Task<IReadOnlyDictionary<string, RssiSparklineSeries>> GetSparklineSeriesAsync(
            IEnumerable<string> macs, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, RssiSparklineSeries>>(
                new Dictionary<string, RssiSparklineSeries>());

        public Task<RssiDetailDto> GetDetailSeriesAsync(string mac, CancellationToken ct)
        {
            DetailCallCount++;
            LastDetailMac = mac;
            return Task.FromResult(_detail);
        }
    }

    private static RssiDetailDto NonEmptyDto()
    {
        var baseUtc = new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc);
        var buckets = new List<DateTime> { baseUtc, baseUtc.AddMinutes(5) };
        var series = new List<RssiDetailSeries>
        {
            new("ap-A · guest", NodeId: 1, NetworkId: 10, Points: new int?[] { -65, -67 }),
            new("ap-B · iot",   NodeId: 2, NetworkId: 20, Points: new int?[] { -72, null }),
        };
        return new RssiDetailDto(buckets, series, IsEmpty: false);
    }

    private static (BunitContext ctx, StubRssiHistory rssi, PollNotifier notifier) Build(RssiDetailDto? dto = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;   // import() + InvokeVoidAsync are no-op-recorded
        var rssi = new StubRssiHistory(dto);
        var notifier = new PollNotifier();
        ctx.Services.AddSingleton<IRssiHistoryQueryService>(rssi);
        ctx.Services.AddSingleton(notifier);
        return (ctx, rssi, notifier);
    }

    [Fact]
    public void FirstRender_CallsDetailQueryService_WithMac_NotDuringOnInitialized()
    {
        var (ctx, rssi, _) = Build(NonEmptyDto());
        using (ctx)
        {
            ctx.Render<RssiHistoryChart>(p => p.Add(c => c.Mac, Mac));
            // OnAfterRenderAsync(firstRender) ran during Render → the detail service was called once.
            Assert.True(rssi.DetailCalled, "Detail query service must be called on first render.");
            Assert.Equal(1, rssi.DetailCallCount);
            Assert.Equal(Mac, rssi.LastDetailMac);
        }
    }

    [Fact]
    public async Task Cycled_ReInvokesDetailQueryService()
    {
        var (ctx, rssi, notifier) = Build(NonEmptyDto());
        using (ctx)
        {
            ctx.Render<RssiHistoryChart>(p => p.Add(c => c.Mac, Mac));
            var afterFirstRender = rssi.DetailCallCount;

            await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 5, DateTime.UtcNow));

            Assert.True(rssi.DetailCallCount > afterFirstRender,
                $"Cycled must re-invoke the detail query service (was {afterFirstRender}, now {rssi.DetailCallCount}).");
        }
    }

    [Fact]
    public async Task Dispose_Unsubscribes_NoFurtherServiceCalls()
    {
        var (ctx, rssi, notifier) = Build(NonEmptyDto());
        using (ctx)
        {
            var cut = ctx.Render<RssiHistoryChart>(p => p.Add(c => c.Mac, Mac));
            await cut.Instance.DisposeAsync();
            var afterDispose = rssi.DetailCallCount;

            // After dispose, a Cycled raise must NOT re-invoke the service (subscription detached).
            await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 5, DateTime.UtcNow));

            Assert.Equal(afterDispose, rssi.DetailCallCount);
        }
    }

    [Fact]
    public void EmptyDto_RendersCollectingHistoryPlaceholder_NoCanvas()
    {
        // D-03: zero-rows → "Collecting history…" placeholder, not an empty axes frame.
        var empty = new RssiDetailDto(Array.Empty<DateTime>(), Array.Empty<RssiDetailSeries>(), IsEmpty: true);
        var (ctx, _, _) = Build(empty);
        using (ctx)
        {
            var cut = ctx.Render<RssiHistoryChart>(p => p.Add(c => c.Mac, Mac));
            Assert.Contains("Collecting history…", cut.Markup);
            Assert.Empty(cut.FindAll("canvas"));
        }
    }

    [Fact]
    public void NonEmptyDto_RendersCanvas()
    {
        var (ctx, _, _) = Build(NonEmptyDto());
        using (ctx)
        {
            var cut = ctx.Render<RssiHistoryChart>(p => p.Add(c => c.Mac, Mac));
            Assert.NotEmpty(cut.FindAll("canvas"));
            Assert.DoesNotContain("Collecting history…", cut.Markup);
        }
    }

    [Fact]
    public void RendersCardTitle()
    {
        var (ctx, _, _) = Build(NonEmptyDto());
        using (ctx)
        {
            var cut = ctx.Render<RssiHistoryChart>(p => p.Add(c => c.Mac, Mac));
            Assert.Contains("RSSI history — last 12 h", cut.Markup);
        }
    }
}
