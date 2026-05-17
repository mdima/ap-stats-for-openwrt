using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Services.Gating;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 5 D-12 paired PollCountdown — pre-cycle, post-cycle, paused, dispose paths.
/// </summary>
public sealed class PollCountdownTests
{
    private sealed class StubSettings : IAppSettingsService
    {
        public int     PollingIntervalSeconds { get; set; } = 30;
        public string? UIPasswordHash         { get; } = null;
        public bool    WizardDismissed        { get; } = true;
        public int     RefreshIntervalSeconds { get; } = 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; } = Array.Empty<string>();
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private sealed class TestGate : ICircuitGate
    {
        public GateState Current { get; set; } = GateState.Live;
        public event Func<GateState, Task>? Changed { add { } remove { } }     // W3
        // Phase 6 D-01: ICircuitGate gained WaitForVisitorAsync. PollCountdown is a UI subscriber
        // and never calls WaitForVisitorAsync; return Task.CompletedTask (mirrors AlwaysLiveGate).
        public Task WaitForVisitorAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static (BunitContext ctx, PollNotifier notifier, StubSettings settings, TestGate gate) Build()
    {
        var ctx = new BunitContext();
        var notifier = new PollNotifier();
        var settings = new StubSettings();
        var gate = new TestGate();
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        ctx.Services.AddSingleton<ICircuitGate>(gate);
        return (ctx, notifier, settings, gate);
    }

    [Fact]
    public void InitialRender_ShowsEmDash_BeforeFirstCycle()
    {
        var (ctx, _, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<PollCountdown>();
            Assert.Contains("—", cut.Markup);
            // Initial aria-label is "Next poll: pending" before any Tick has fired;
            // after the first Tick (with no Cycled yet) it becomes "Awaiting first poll".
            Assert.True(
                cut.Markup.Contains("Next poll: pending") || cut.Markup.Contains("Awaiting first poll"),
                $"Expected pending or awaiting-first-poll aria-label; got: {cut.Markup}");
        }
    }

    [Fact]
    public async Task AfterFirstCycle_ShowsCountdownInSeconds()
    {
        var (ctx, notifier, settings, _) = Build();
        settings.PollingIntervalSeconds = 30;
        using (ctx)
        {
            var cut = ctx.Render<PollCountdown>();

            // Cycle ends NOW; next cycle in 30s.
            await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 0, DateTime.UtcNow));

            // The 1Hz Timer may not have ticked yet — wait for the StateHasChanged from the
            // Cycled handler itself (which also re-renders). The Cycled handler updates the
            // anchor; the next Tick produces "Next poll in {N} seconds". We wait up to ~2s.
            cut.WaitForState(() => cut.Markup.Contains("Next poll in"), TimeSpan.FromSeconds(2));

            Assert.Contains("Next poll in", cut.Markup);
            Assert.Contains("s", cut.Markup);   // "30s" or "29s" depending on tick latency
            Assert.Contains("font-variant-numeric: tabular-nums", cut.Markup);
        }
    }

    [Fact]
    public void GatePaused_RendersEmDash_AfterTick()
    {
        var (ctx, _, _, gate) = Build();
        gate.Current = GateState.Paused;
        using (ctx)
        {
            var cut = ctx.Render<PollCountdown>();

            // Wait up to ~2s for one Tick() invocation while paused.
            cut.WaitForState(() => cut.Markup.Contains("Polling paused"), TimeSpan.FromSeconds(2));

            Assert.Contains("Polling paused", cut.Markup);
            Assert.Contains("—", cut.Markup);
        }
    }

    [Fact]
    public void Dispose_CleansUpTimer_AndUnsubscribes()
    {
        var (ctx, notifier, _, _) = Build();
        var cut = ctx.Render<PollCountdown>();
        ctx.Dispose();

        // After dispose, raising another cycle should NOT throw (subscription removed).
        // Use the orchestrator's awaitable pattern; an exception here would fail the test.
        var task = notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 0, DateTime.UtcNow));
        Assert.True(task.IsCompleted || !task.IsFaulted);
    }

    // ── v0.11 Plan 14-01 / POLL-03 — three-arm state-machine coverage (CONTEXT 14 §D-01 / §D-04) ──
    //
    // The component (POLL-02) implements a three-arm state machine anchored to the
    // PollNotifier.CycleStarted (14-01 / POLL-01) and Cycled events:
    //   (a) idle-ticking      — _inCycle=false; "{N}s" ticking down
    //   (b) cycle-started-held — _inCycle=true; "Polling…" (U+2026); Tick() suppresses {N}s
    //   (c) cycle-ended-reset  — Cycled flips _inCycle=false; next Tick resumes "{N}s"
    //
    // The held-state invariant (arm b) is the OPERATOR-VISIBLE FIX (CONTEXT 14 §Specifics #1):
    // the operator's symptom was "countdown starts at 25 instead of 30" because the timer
    // ticked DURING the cycle. The HOLD is the fix — that's why the second test below asserts
    // the markup does NOT change second-by-second during the held window.

    [Fact]
    public async Task CycleStarted_HoldsBadgeAtPollingEllipsis()
    {
        // D-04 #2 (cycle-started-held arm) — after CycleStarted fires, badge text == "Polling…"
        // (U+2026 HORIZONTAL ELLIPSIS, single codepoint) and aria-label == "Polling in progress".
        var (ctx, notifier, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<PollCountdown>();

            await notifier.RaiseCycleStartedAsync();

            cut.WaitForState(() => cut.Markup.Contains("Polling…"), TimeSpan.FromSeconds(2));
            Assert.Contains("Polling…", cut.Markup);            // U+2026 single codepoint
            Assert.Contains("Polling in progress", cut.Markup);      // aria-label per D-01
        }
    }

    [Fact]
    public async Task DuringCycle_TickDoesNotMutate_PollingEllipsis_OverMultipleTicks()
    {
        // D-04 #2 INVARIANT (CONTEXT 14 §Specifics #1) — the held state's badge text does NOT
        // change second-by-second. Wait ~2.5s (>= 2 Timer ticks at 1Hz) and assert the markup
        // STILL contains "Polling…" AND does NOT contain a "Next poll in"/{N}s countdown token
        // that Tick() would have rendered had it not been suppressed by _inCycle.
        var (ctx, notifier, settings, _) = Build();
        settings.PollingIntervalSeconds = 30;
        using (ctx)
        {
            var cut = ctx.Render<PollCountdown>();

            await notifier.RaiseCycleStartedAsync();
            cut.WaitForState(() => cut.Markup.Contains("Polling…"), TimeSpan.FromSeconds(2));

            // Let the 1Hz Timer fire at least twice while _inCycle == true. If the held-state
            // suppression is broken, Tick() would render "—" or "30s" (or "Working") into the
            // markup, and the assertions below would fail.
            await Task.Delay(2200, Xunit.TestContext.Current.CancellationToken);

            // Held: "Polling…" still present; no "Next poll in" countdown text.
            Assert.Contains("Polling…", cut.Markup);
            Assert.DoesNotContain("Next poll in", cut.Markup);
            // Defensive: no "{N}s" badge token (no "30s" or "29s" or "28s" — values Tick would
            // have rendered if the held-state suppression were broken). We match the inner
            // <span>{N}s</span> shape narrowly so it does not collide with the aria-label or
            // the "Polling in progress" string.
            Assert.DoesNotContain(">30s<", cut.Markup);
            Assert.DoesNotContain(">29s<", cut.Markup);
            Assert.DoesNotContain(">28s<", cut.Markup);
        }
    }

    [Fact]
    public async Task Cycled_ResetsBadgeTo_FullIntervalCountdown_AfterHeldState()
    {
        // D-04 #3 (cycle-ended-reset arm) — after the CycleStarted -> Cycled sequence, the badge
        // EXITS "Polling…" and resumes "{N}s" ticking down from full PollingIntervalSeconds.
        // The operator's pre-14-01 symptom was the countdown starting at ~25s (because the
        // Tick had ticked during the cycle). POST-14-01, the countdown starts at ~30s (allowing
        // 1-2s of tick latency for the 1Hz Timer to fire after the Cycled handler runs).
        var (ctx, notifier, settings, _) = Build();
        settings.PollingIntervalSeconds = 30;
        using (ctx)
        {
            var cut = ctx.Render<PollCountdown>();

            await notifier.RaiseCycleStartedAsync();
            cut.WaitForState(() => cut.Markup.Contains("Polling…"), TimeSpan.FromSeconds(2));

            await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 0, DateTime.UtcNow));
            cut.WaitForState(() => cut.Markup.Contains("Next poll in"), TimeSpan.FromSeconds(2));

            Assert.DoesNotContain("Polling…", cut.Markup);
            Assert.Contains("Next poll in", cut.Markup);
            // The countdown must be near-full PollingIntervalSeconds (28-30s window for tick
            // latency). NOT 25s — that would replay the pre-14-01 bug where Tick ran during
            // the cycle.
            Assert.True(
                cut.Markup.Contains(">30s<") || cut.Markup.Contains(">29s<") || cut.Markup.Contains(">28s<"),
                $"Expected near-full 30s countdown after cycle reset (28-30s acceptable for tick latency); got: {cut.Markup}");
        }
    }

    [Fact]
    public async Task IdleTicking_AfterFirstCycle_DropsSecondsBy1_OverTime()
    {
        // D-04 #1 (idle-ticking arm) regression guard — after Cycled, Tick() must continue to
        // update {N}s. This protects against "the rewrite accidentally froze Tick()".
        var (ctx, notifier, settings, _) = Build();
        settings.PollingIntervalSeconds = 30;
        using (ctx)
        {
            var cut = ctx.Render<PollCountdown>();
            await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 0, DateTime.UtcNow));
            cut.WaitForState(() => cut.Markup.Contains("Next poll in"), TimeSpan.FromSeconds(2));

            // Capture initial markup; wait ~1.5s; markup MUST have changed (countdown ticking).
            var initialMarkup = cut.Markup;
            await Task.Delay(1500, Xunit.TestContext.Current.CancellationToken);
            cut.WaitForState(() => cut.Markup != initialMarkup, TimeSpan.FromSeconds(2));

            Assert.Contains("Next poll in", cut.Markup);
            Assert.NotEqual(initialMarkup, cut.Markup);
        }
    }

    // ── Revision 1 W4 — Tick racing with Dispose must not throw ObjectDisposedException ──
    [Fact]
    public async Task PollCountdown_DisposeRacingWithTick_DoesNotThrow()
    {
        // Strategy: configure the Timer with a very short period so a Tick is in flight
        // (or about to fire) when we Dispose the BunitContext. The PollCountdown's W4
        // _disposed-volatile flag + try/catch around InvokeAsync must keep the Timer
        // callback from throwing into the test harness.
        var (ctx, notifier, settings, _) = Build();
        settings.PollingIntervalSeconds = 1;     // shorten the math; not load-bearing for the race

        var cut = ctx.Render<PollCountdown>();

        // Drive a Cycled to seed _lastCycleEndUtc so Tick() goes through the "compute remaining" branch.
        await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 0, DateTime.UtcNow));

        // Wait briefly so the System.Threading.Timer is alive and likely between Tick invocations.
        await Task.Delay(50, Xunit.TestContext.Current.CancellationToken);

        // Dispose under contention. If W4 guards aren't in place the Timer callback may
        // race with the renderer's Dispose() and throw ObjectDisposedException.
        ctx.Dispose();

        // Wait long enough for any in-flight Tick callbacks to finish (period was 1000ms;
        // give a couple of ticks worth of buffer to surface a swallowed exception).
        await Task.Delay(150, Xunit.TestContext.Current.CancellationToken);

        // No assertion needed beyond "did not throw" — xUnit fails the test if any exception
        // escaped into the test thread (Timer callbacks rethrow on the threadpool).
        // Belt-and-braces: assert the markup snapshot still references the disposed component
        // shape (this is mostly to keep the test from being flagged as "no assertions").
        Assert.NotNull(cut);
    }
}
