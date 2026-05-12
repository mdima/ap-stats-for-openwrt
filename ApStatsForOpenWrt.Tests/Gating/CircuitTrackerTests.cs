using OpenWrtApStats.Services.Gating;
using Xunit;

namespace OpenWrtApStats.Tests.Gating;

/// <summary>
/// Phase 6 INF-06 / POLL-05 / POLL-06. Covers:
///  - Counter increment/decrement semantics across single-thread sequences.
///  - WaitForVisitorAsync: completed-when-already-Live, pending-when-Paused, fresh-TCS after 1 -&gt; 0.
///  - Changed event fires ONLY on 0 -&gt; 1 (Live) and 1 -&gt; 0 (Paused) transitions, NOT on intermediate counts.
///  - W4 race guard: Dispose sets _disposed BEFORE TrySetCanceled; subsequent OnOpened/OnClosed are no-ops.
///  - Pending waiter on Dispose: WaitForVisitorAsync task observes TaskCanceledException, does NOT hang.
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

    [Fact]
    public void OnClosed_LastCircuit_FlipsToPaused_AndFiresChangedOnce()
    {
        using var sut = new CircuitTracker();
        sut.OnOpened();
        var fired = new List<GateState>();
        sut.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        sut.OnClosed();

        Assert.Equal(GateState.Paused, sut.Current);
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

    [Fact]
    public async Task WaitForVisitorAsync_AfterPausedAgain_GetsFreshTcs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = new CircuitTracker();
        sut.OnOpened();          // Paused -> Live
        sut.OnClosed();          // Live -> Paused (TCS re-armed via Interlocked.Exchange)

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
}
