using ApStatsForOpenWrt.Services.Gating;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Gating;

/// <summary>
/// Phase 6 INF-06 / POLL-05 / POLL-06. Covers:
///  - Counter increment/decrement semantics across single-thread sequences.
///  - WaitForVisitorAsync: completed-when-already-Live, pending-when-Paused, fresh-TCS after 1 -&gt; 0.
///  - Changed event fires ONLY on 0 -&gt; 1 (Live) and 1 -&gt; 0 (Paused) transitions, NOT on intermediate counts.
///  - W4 race guard: Dispose sets _disposed BEFORE TrySetCanceled; subsequent OnOpened/OnClosed are no-ops.
///  - Pending waiter on Dispose: WaitForVisitorAsync task observes TaskCanceledException, does NOT hang.
///  - 260518-gpd: pause-grace debounce (Changed(Paused) defers behind a 30s grace; 1->0->1 within
///    the grace window cancels the pending fire AND does NOT emit Changed(Live); dispose during
///    grace cancels and suppresses; the WaitForVisitorAsync TCS is NOT swapped until grace expires).
/// </summary>
public sealed class CircuitTrackerTests
{
    [Fact]
    public void Initial_Current_IsPaused()
    {
        using var sut = new CircuitTracker();
        Assert.Equal(GateState.Paused, sut.Current);
    }

    [Fact]
    public void OnOpened_FirstCall_FlipsToLive_AndFiresChangedOnce()
    {
        using var sut = new CircuitTracker();
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnOpened();

        Assert.Equal(GateState.Live, sut.Current);
        Assert.Single(fired);
        Assert.Equal(GateState.Live, fired[0]);
    }

    [Fact]
    public void OnOpened_SecondCall_DoesNotFireChanged()
    {
        using var sut = new CircuitTracker();
        sut.OnOpened();
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnOpened();

        Assert.Empty(fired);
        Assert.Equal(GateState.Live, sut.Current);
    }

    /// <summary>
    /// 260518-gpd update — Changed(Paused) fires AFTER the pause-grace window, not synchronously.
    /// Test uses a 50 ms grace ctor + 150 ms await (3x slack for CI jitter). Current is Paused
    /// immediately because it is derived from _count (which IS decremented synchronously); only
    /// the event invocation and TCS swap are debounced.
    /// </summary>
    [Fact]
    public async Task OnClosed_LastCircuit_FlipsToPaused_AndFiresChangedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = new CircuitTracker(TimeSpan.FromMilliseconds(50));
        sut.OnOpened();
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnClosed();

        // Current derives from _count via Volatile.Read — unchanged by the debounce.
        Assert.Equal(GateState.Paused, sut.Current);

        // Wait past the grace window for the debounced fire.
        await Task.Delay(150, ct);

        Assert.Single(fired);
        Assert.Equal(GateState.Paused, fired[0]);
    }

    [Fact]
    public void OnClosed_NotLastCircuit_DoesNotFireChanged()
    {
        using var sut = new CircuitTracker();
        sut.OnOpened();
        sut.OnOpened();
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnClosed();

        Assert.Empty(fired);
        Assert.Equal(GateState.Live, sut.Current);
    }

    [Fact]
    public void WaitForVisitorAsync_WhenLive_CompletesImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = new CircuitTracker();
        sut.OnOpened();

        var task = sut.WaitForVisitorAsync(ct);

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitForVisitorAsync_WhenPaused_AwaitsUntilFirstVisitor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = new CircuitTracker();

        var task = sut.WaitForVisitorAsync(ct);
        Assert.False(task.IsCompleted);

        sut.OnOpened();

        // Bound the await; if the TCS doesn't fire we want a fast failure, not a hang.
        await task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        Assert.True(task.IsCompletedSuccessfully);
    }

    /// <summary>
    /// 260518-gpd update — the TCS is NOT swapped until the pause-grace window expires. Test now
    /// awaits 150 ms after OnClosed before calling WaitForVisitorAsync; only then does it observe
    /// the fresh TCS in the pending state. Pre-debounce this used to be synchronous.
    /// </summary>
    [Fact]
    public async Task WaitForVisitorAsync_AfterPausedAgain_GetsFreshTcs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = new CircuitTracker(TimeSpan.FromMilliseconds(50));
        sut.OnOpened();          // Paused -> Live
        sut.OnClosed();          // Live -> Paused (TCS re-arm DEFERRED behind 50ms grace)

        // Wait past grace so the TCS gets swapped via Interlocked.Exchange inside
        // SchedulePauseTransitionAsync.
        await Task.Delay(150, ct);

        var task = sut.WaitForVisitorAsync(ct);
        Assert.False(task.IsCompleted);  // fresh TCS, NOT the pre-completed one

        sut.OnOpened();          // Paused -> Live again
        await task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Dispose_CompletesPendingWaiter_DoesNotHang()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new CircuitTracker();
        var task = sut.WaitForVisitorAsync(ct);

        sut.Dispose();

        // Pending waiter must complete (cancelled) within the bound; we don't care which exception
        // beyond it being an OperationCanceledException family member (TaskCanceledException is a subclass).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.WaitAsync(TimeSpan.FromSeconds(2), ct));
    }

    [Fact]
    public void AfterDispose_OnOpened_IsNoOp()
    {
        var sut = new CircuitTracker();
        sut.Dispose();
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnOpened();      // must NOT NRE, must NOT fire Changed
        sut.OnClosed();      // must NOT NRE, must NOT fire Changed

        Assert.Empty(fired);
    }

    [Fact]
    public void AfterDispose_WaitForVisitorAsync_ReturnsCompletedTask()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new CircuitTracker();
        sut.Dispose();

        var task = sut.WaitForVisitorAsync(ct);

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Handler_OnCircuitOpenedAsync_CallsTrackerOnOpened()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tracker = new CircuitTracker();
        var handler = new CircuitTrackerHandler(tracker);

        // CircuitHandler.OnCircuitOpenedAsync needs a Circuit instance. Per CLAUDE.md
        // "tests use bUnit, not mocks of CircuitHandler" — but invoking the handler
        // method directly with a null-Circuit is acceptable for THIS forwarding test
        // because the handler does not dereference circuit (verified by reading
        // CircuitTrackerHandler.cs). If the .NET 10 framework prohibits null Circuit,
        // fall back to a bUnit-driven integration test in plan 06-03.
        await handler.OnCircuitOpenedAsync(circuit: null!, ct);

        Assert.Equal(GateState.Live, tracker.Current);
    }

    [Fact]
    public async Task Handler_OnCircuitClosedAsync_CallsTrackerOnClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tracker = new CircuitTracker();
        tracker.OnOpened();
        var handler = new CircuitTrackerHandler(tracker);

        await handler.OnCircuitClosedAsync(circuit: null!, ct);

        Assert.Equal(GateState.Paused, tracker.Current);
    }

    // ---------------------------------------------------------------------------------------
    // 260518-gpd — pause-grace debounce. Seven new facts. All construct CircuitTracker with a
    // short test-grace (50 ms) so the 150 ms slack delay is robust to CI scheduler jitter.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 260518-gpd — within the grace window, Changed(Paused) MUST NOT fire. Pins the debounce
    /// scheduling step: OnClosed sets up the pending CTS + Task.Delay but does not invoke
    /// the event synchronously. Probed at 10 ms (well below the 50 ms grace).
    /// </summary>
    [Fact]
    public async Task OnClosed_TransitionTo0_DoesNotFirePausedImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = new CircuitTracker(TimeSpan.FromMilliseconds(50));
        sut.OnOpened();
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnClosed();

        // Probe well below the grace window — Paused MUST NOT have fired yet.
        await Task.Delay(10, ct);
        Assert.Empty(fired);
    }

    /// <summary>
    /// 260518-gpd — after the grace window expires with count still at 0, Changed(Paused) fires
    /// exactly once. Pins the SchedulePauseTransitionAsync "authoritative 1 -> 0 transition"
    /// path on the happy (truly-walking-away) case.
    /// </summary>
    [Fact]
    public async Task OnClosed_TransitionTo0_FiresPausedAfterGrace()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = new CircuitTracker(TimeSpan.FromMilliseconds(50));
        sut.OnOpened();
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnClosed();

        await Task.Delay(150, ct);   // 3x grace slack
        Assert.Single(fired);
        Assert.Equal(GateState.Paused, fired[0]);
    }

    /// <summary>
    /// 260518-gpd — THE bug-fix pin. F5 / single-tab close-then-reopen within the grace window
    /// MUST suppress BOTH Changed(Paused) (cancelled) AND Changed(Live) (we never went Paused,
    /// so there is no transition to announce). Operator-visible: no spurious cold-start cycle.
    /// </summary>
    [Fact]
    public async Task OnClosed_Then_OnOpenedWithinGrace_DoesNotFirePausedOrLive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = new CircuitTracker(TimeSpan.FromMilliseconds(50));
        sut.OnOpened();
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnClosed();          // schedules the pause
        sut.OnOpened();          // cancels the pending pause; MUST NOT fire Changed(Live)

        await Task.Delay(150, ct);
        Assert.Empty(fired);
    }

    /// <summary>
    /// 260518-gpd — re-entrancy: 1 -> 0 -> 1 -> 0 within a single grace window restarts the
    /// timer from the SECOND close. Exactly ONE Paused fires after the grace expires from the
    /// second close. Pins the Interlocked.Exchange-replaces-prior-CTS path in OnClosed.
    /// </summary>
    [Fact]
    public async Task OnClosed_Then_OnOpenedWithinGrace_ThenOnClosedAgain_RestartsGraceTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = new CircuitTracker(TimeSpan.FromMilliseconds(50));
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnOpened();          // count 0 -> 1, fires Changed(Live)
        sut.OnClosed();          // count 1 -> 0, schedules pause #1
        await Task.Delay(25, ct); // ~half a grace
        sut.OnOpened();          // count 0 -> 1, cancels pause #1; no Changed(Live)
        sut.OnClosed();          // count 1 -> 0, schedules pause #2

        await Task.Delay(150, ct); // 3x grace from the SECOND close

        // Sequence: Live (from first OnOpened), then exactly one Paused (from second close's grace expiry).
        Assert.Equal(2, fired.Count);
        Assert.Equal(GateState.Live, fired[0]);
        Assert.Equal(GateState.Paused, fired[1]);
    }

    /// <summary>
    /// 260518-gpd — WaitForVisitorAsync DURING the grace window. Semantically we are still Live;
    /// the TCS has NOT been swapped yet (it is the previous, pre-completed instance). A caller
    /// racing in here observes Task.CompletedTask via the still-completed TCS branch. After grace,
    /// the TCS is swapped to a fresh, pending one.
    /// </summary>
    [Fact]
    public async Task WaitForVisitorAsync_DuringGraceWindow_ReturnsImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = new CircuitTracker(TimeSpan.FromMilliseconds(50));
        sut.OnOpened();
        sut.OnClosed();          // schedules pause; TCS NOT swapped yet

        // No delay — we are mid-grace. Previous TCS is still TrySetResult(true) from the OnOpened.
        var task = sut.WaitForVisitorAsync(ct);
        Assert.True(task.IsCompletedSuccessfully);

        // Now let grace expire — the swap-and-fire happens; subsequent WaitForVisitorAsync gets
        // a pending fresh TCS.
        await Task.Delay(150, ct);
        var task2 = sut.WaitForVisitorAsync(ct);
        Assert.False(task2.IsCompleted);
    }

    /// <summary>
    /// 260518-gpd — Dispose DURING the grace window cancels the pending pause CTS and suppresses
    /// the Changed(Paused) fire. Pins the Dispose order: cancel CTS BEFORE setting _disposed, so
    /// the in-flight grace callback observes either its own cancellation OR the _disposed flag.
    /// </summary>
    [Fact]
    public async Task Dispose_DuringPendingGrace_CancelsTimerAndDoesNotFirePaused()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new CircuitTracker(TimeSpan.FromMilliseconds(50));
        sut.OnOpened();
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnClosed();          // schedules pause
        sut.Dispose();           // cancels pending CTS before _disposed = true

        await Task.Delay(150, ct);
        Assert.Empty(fired);
    }

    /// <summary>
    /// 260518-gpd — forward guard. A genuine cold-start (no prior close, no pending CTS) still
    /// fires Changed(Live) SYNCHRONOUSLY. No debounce on the open side. Pins that the new
    /// cancel-pending-pause branch in OnOpened did NOT swallow the cold-start Live fire.
    /// </summary>
    [Fact]
    public void OnOpened_FromTrueColdStart_FiresLiveAsBefore()
    {
        using var sut = new CircuitTracker(TimeSpan.FromMilliseconds(50));
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnOpened();          // fresh tracker, no prior close, no pending CTS

        Assert.Single(fired);
        Assert.Equal(GateState.Live, fired[0]);
        Assert.Equal(GateState.Live, sut.Current);
    }
}
