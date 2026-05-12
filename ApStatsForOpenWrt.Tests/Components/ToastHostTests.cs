using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Services;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components;

/// <summary>
/// Phase 8 Plan 08-06 Task 2 — exercises <see cref="ToastHost"/> against a real
/// <see cref="ToastService"/> Singleton wired through bUnit's <c>Services</c> collection.
/// Six bUnit-testable visual states from 08-UI-SPEC.md §Acceptance Test Fixtures > ToastHost.razor:
///   1. Empty queue → container rendered, no <c>.toast.show</c> children.
///   2. One info toast → <c>.toast.show.text-bg-info</c> + <c>bi-info-circle-fill</c> + message text.
///   3. Five toasts already present + a 6th arrival → oldest dropped (FIFO; REQ-UI-43 cap-5).
///   4. Manual dismiss via <c>.btn-close</c> click → toast removed; auto-dismiss CTS cancelled.
///   5. Auto-dismiss after the 5000ms Task.Delay window elapses → toast removed.
///   6. XSS regression (T-08-16) — message containing literal <c>&lt;script&gt;</c> tag is
///      HTML-encoded by Blazor's default <c>@variable</c> interpolation, NEVER rendered raw.
///
/// Gate-only test pattern: hand-rolled FakeGate : INotificationsGate keeps PreferencesService
/// (and its JS interop) out of these component tests entirely. Gate is always open here
/// (<c>IsEnabled = true</c>) — Test 2 of ToastServiceTests already locks the gate-closed branch.
///
/// Test stack: xUnit v3 + bUnit 2.7.2 + Microsoft.Testing.Extensions.CodeCoverage (NEVER coverlet).
/// Coverage gate per D-18: ≥ 80% line + ≥ 80% branch on ToastHost.razor.
/// </summary>
public sealed class ToastHostTests
{
    private sealed class FakeGate : INotificationsGate
    {
        public bool IsEnabled { get; init; } = true;
    }

    private static (BunitContext Ctx, ToastService Svc) CreateContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<INotificationsGate>(new FakeGate { IsEnabled = true });
        ctx.Services.AddSingleton<ToastService>(sp =>
            new ToastService(
                sp.GetRequiredService<INotificationsGate>(),
                NullLogger<ToastService>.Instance));
        var svc = ctx.Services.GetRequiredService<ToastService>();
        return (ctx, svc);
    }

    // 1. Empty queue: container rendered, no .toast children. ----------------------------------
    [Fact]
    public void EmptyQueue_RendersContainer_AndNoToastChildren()
    {
        var (ctx, _) = CreateContext();
        using var _disp = ctx;

        var cut = ctx.Render<ToastHost>();

        Assert.Contains("toast-container", cut.Markup);
        Assert.Empty(cut.FindAll(".toast.show"));
    }

    // 2. One info toast: classes + icon + message text. ----------------------------------------
    [Fact]
    public async Task OneInfoToast_RendersExpectedClassesIconAndText()
    {
        var (ctx, svc) = CreateContext();
        using var _disp = ctx;
        var cut = ctx.Render<ToastHost>();

        await svc.Show("New station", "kitchen-ap", "info");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("text-bg-info", cut.Markup);
            Assert.Contains("bi-info-circle-fill", cut.Markup);
            Assert.Contains("New station", cut.Markup);
            Assert.Contains("kitchen-ap", cut.Markup);
            Assert.Single(cut.FindAll(".toast.show"));
        });
    }

    // 3. FIFO cap-5 — sixth arrival drops the oldest (REQ-UI-43). ------------------------------
    [Fact]
    public async Task FiveToasts_SixthArrival_DropsOldest_FIFO()
    {
        var (ctx, svc) = CreateContext();
        using var _disp = ctx;
        var cut = ctx.Render<ToastHost>();

        for (var i = 0; i < 5; i++)
        {
            await svc.Show($"Title-{i}", $"Body-{i}", "info");
        }

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".toast.show").Count));

        await svc.Show("Title-5", "Body-5", "info");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(5, cut.FindAll(".toast.show").Count);
            Assert.DoesNotContain("Title-0", cut.Markup);   // oldest dropped
            Assert.Contains("Title-5", cut.Markup);          // newest present
            Assert.Contains("Title-1", cut.Markup);          // remaining 4 still present
        });
    }

    // 4. Manual dismiss — clicking .btn-close removes the toast. -------------------------------
    [Fact]
    public async Task ManualDismiss_RemovesToast()
    {
        var (ctx, svc) = CreateContext();
        using var _disp = ctx;
        var cut = ctx.Render<ToastHost>();
        await svc.Show("T", "M", "info");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".toast.show")));

        cut.Find("button.btn-close").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".toast.show")));
    }

    // 5. Auto-dismiss after the 5000ms window. -------------------------------------------------
    [Fact]
    public async Task AutoDismiss_RemovesToastAfterTimeout()
    {
        var (ctx, svc) = CreateContext();
        using var _disp = ctx;
        var cut = ctx.Render<ToastHost>();
        await svc.Show("T", "M", "info");

        // 7s buffer over the 5000ms LOCK to absorb CI scheduling jitter without flake risk.
        cut.WaitForAssertion(
            () => Assert.Empty(cut.FindAll(".toast.show")),
            TimeSpan.FromSeconds(7));
    }

    // 6. XSS regression (T-08-16): script tag in message is HTML-encoded. ---------------------
    [Fact]
    public async Task Message_WithScriptTag_IsHtmlEncoded()
    {
        var (ctx, svc) = CreateContext();
        using var _disp = ctx;
        var cut = ctx.Render<ToastHost>();

        await svc.Show("T", "<script>alert(1)</script>", "info");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("&lt;script&gt;", cut.Markup);
            Assert.DoesNotContain("<script>alert(1)</script>", cut.Markup);
        });
    }

    // 7. Coverage tightener — exercise the 3 remaining IconFor switch arms (success / warning /
    //    danger) AND the Dispose loop body with non-empty _timers (queue is still populated when
    //    the context is disposed). The `_` default arm of IconFor is unreachable in production
    //    because ToastService validates severity against the same 4-element set BEFORE emission
    //    (Test 3 of ToastServiceTests locks that throw). 08-UI-SPEC §Anatomy locks the 4 severity
    //    → bi-* mappings byte-for-byte; this test asserts each verbatim.
    //    Also covers the Dispose try-block path (cts.Cancel + cts.Dispose) for in-flight timers
    //    that have NOT yet auto-dismissed — the Dispose foreach loop walks _timers and cancels.
    [Fact]
    public async Task AllSeverities_RenderExpectedIcons_AndDisposeCancelsPendingTimers()
    {
        var (ctx, svc) = CreateContext();
        var cut = ctx.Render<ToastHost>();

        // Emit one toast of each remaining severity (info already covered by Test 2).
        // FIFO cap is 5; we are emitting 4, so all four queue entries survive.
        await svc.Show("S", "success body", "success");
        await svc.Show("W", "warning body", "warning");
        await svc.Show("D", "danger body",  "danger");
        await svc.Show("I", "info body",    "info");

        cut.WaitForAssertion(() =>
        {
            // All four icon classes present in markup → IconFor switch arms covered.
            Assert.Contains("bi-check-circle-fill",        cut.Markup);   // success
            Assert.Contains("bi-exclamation-triangle-fill", cut.Markup);  // warning
            Assert.Contains("bi-x-octagon-fill",            cut.Markup);  // danger
            Assert.Contains("bi-info-circle-fill",          cut.Markup);  // info

            // role="alert" for danger (08-UI-SPEC §ToastHost.razor markup contract).
            Assert.Contains("role=\"alert\"",  cut.Markup);
            Assert.Contains("role=\"status\"", cut.Markup);
        });

        // Dispose the context with 4 in-flight auto-dismiss timers — this walks the Dispose
        // foreach loop body (cts.Cancel + cts.Dispose), exercising the lines that no other
        // test reaches because Tests 1+4+5+6 leave _timers empty by the time the context is
        // torn down (auto-dismiss fired or manual dismiss happened).
        ctx.Dispose();

        // Sanity: subsequent Show calls on the disposed-circuit ToastService MUST NOT throw —
        // the host unsubscribed in Dispose, so there are simply no remaining subscribers.
        await svc.Show("Title-after-dispose", "should be ignored by host", "info");
    }
}
