using ApStatsForOpenWrt.Services.Polling;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Polling;

/// <summary>
/// 260518-pch — PollNotifier now caches LastSnapshot + InCycle so per-circuit
/// PollCountdown components mounted mid-cycle (F5 / new tab) can hydrate without
/// waiting for the next Cycled event. Producer-side ordering invariant: state
/// writes happen BEFORE event invocation so a handler reading the cache inside
/// its own callback sees the new values. Pinned by the two Ordering_* facts.
/// </summary>
public sealed class PollNotifierTests
{
    /// <summary>
    /// 260518-pch — RaiseCycleStartedAsync flips InCycle to true. Default is false
    /// on a freshly-constructed PollNotifier (preserves all existing test outcomes
    /// where Build() returns a new PollNotifier and hydration must be a no-op).
    /// </summary>
    [Fact]
    public async Task RaiseCycleStartedAsync_SetsInCycleTrue()
    {
        var notifier = new PollNotifier();
        Assert.False(notifier.InCycle);
        Assert.Null(notifier.LastSnapshot);

        await notifier.RaiseCycleStartedAsync();

        Assert.True(notifier.InCycle);
        Assert.Null(notifier.LastSnapshot);
    }

    /// <summary>
    /// 260518-pch — RaiseAsync sets LastSnapshot to the supplied snapshot AND clears
    /// InCycle. After a CycleStarted -> Cycled sequence the cache reflects the
    /// completed cycle and the in-flight flag is cleared.
    /// </summary>
    [Fact]
    public async Task RaiseAsync_SetsLastSnapshotAndClearsInCycle()
    {
        var notifier = new PollNotifier();
        await notifier.RaiseCycleStartedAsync();
        Assert.True(notifier.InCycle);

        var snap = new PollSnapshot(2, 2, 0, 7, DateTime.UtcNow);
        await notifier.RaiseAsync(snap);

        Assert.Equal(snap, notifier.LastSnapshot);
        Assert.False(notifier.InCycle);
    }

    /// <summary>
    /// 260518-pch — ORDERING INVARIANT: LastSnapshot is written BEFORE Cycled?.Invoke()
    /// so a handler reading Notifier.LastSnapshot inside its own callback observes the
    /// new value (NOT null / NOT the previous value). Pinned to prevent a future
    /// refactor from inverting the order and silently breaking mid-cycle hydration.
    /// </summary>
    [Fact]
    public async Task Ordering_LastSnapshot_VisibleInsideCycledHandler()
    {
        var notifier = new PollNotifier();
        var snap = new PollSnapshot(3, 3, 0, 11, DateTime.UtcNow);
        PollSnapshot? capturedInHandler = null;
        bool capturedInCycleInHandler = true;

        notifier.Cycled += s =>
        {
            capturedInHandler = notifier.LastSnapshot;
            capturedInCycleInHandler = notifier.InCycle;
            return Task.CompletedTask;
        };

        await notifier.RaiseAsync(snap);

        Assert.NotNull(capturedInHandler);
        Assert.Equal(snap, capturedInHandler);
        Assert.False(capturedInCycleInHandler);
    }

    /// <summary>
    /// 260518-pch — ORDERING INVARIANT: InCycle is set to true BEFORE
    /// CycleStarted?.Invoke() so a handler reading Notifier.InCycle inside its own
    /// callback observes true (NOT the default false). Pinned alongside the
    /// LastSnapshot ordering test as the producer-side contract for hydration.
    /// </summary>
    [Fact]
    public async Task Ordering_InCycle_VisibleInsideCycleStartedHandler()
    {
        var notifier = new PollNotifier();
        bool capturedInHandler = false;

        notifier.CycleStarted += () =>
        {
            capturedInHandler = notifier.InCycle;
            return Task.CompletedTask;
        };

        await notifier.RaiseCycleStartedAsync();

        Assert.True(capturedInHandler);
    }
}
