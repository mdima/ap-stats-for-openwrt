using ApStatsForOpenWrt.Services.Gating;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Gating;

/// <summary>
/// Phase 6 INF-06. CircuitTrackerGate is a forwarding adapter: it MUST NOT hold its own state
/// or its own backing event field. Tests verify Current and Changed both round-trip through
/// the underlying CircuitTracker instance.
/// </summary>
public sealed class CircuitTrackerGateTests
{
    [Fact]
    public void Current_ForwardsToTracker()
    {
        using var tracker = new CircuitTracker();
        var gate = new CircuitTrackerGate(tracker);

        Assert.Equal(GateState.Paused, gate.Current);
        tracker.OnOpened();
        Assert.Equal(GateState.Live, gate.Current);
        tracker.OnClosed();
        Assert.Equal(GateState.Paused, gate.Current);
    }

    [Fact]
    public void Changed_Subscribe_ForwardsToTracker()
    {
        using var tracker = new CircuitTracker();
        var gate = new CircuitTrackerGate(tracker);
        var fired = new List<GateState>();
        Func<GateState, Task> handler = s => { fired.Add(s); return Task.CompletedTask; };

        gate.Changed += handler;
        tracker.OnOpened();        // tracker fires; the gate's add-accessor wired the handler to tracker.Changed

        Assert.Single(fired);
        Assert.Equal(GateState.Live, fired[0]);
    }

    [Fact]
    public void Changed_Unsubscribe_ForwardsToTracker()
    {
        using var tracker = new CircuitTracker();
        var gate = new CircuitTrackerGate(tracker);
        var fired = new List<GateState>();
        Func<GateState, Task> handler = s => { fired.Add(s); return Task.CompletedTask; };

        gate.Changed += handler;
        gate.Changed -= handler;
        tracker.OnOpened();        // handler unsubscribed — must NOT fire

        Assert.Empty(fired);
    }
}
