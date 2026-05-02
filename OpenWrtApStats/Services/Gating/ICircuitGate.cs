namespace OpenWrtApStats.Services.Gating;

/// <summary>
/// D-12 Phase 5 stub seam. Surfaces the polling-gate state to the UI without coupling Phase 5
/// to the Phase 6 <c>CircuitTracker</c> (INF-06 / POLL-05 / POLL-06). Phase 6 will register a
/// <c>CircuitTrackerGate</c> implementation in place of the Phase 5 <see cref="AlwaysLiveGate"/>;
/// no markup change is required for the swap. Subscribers (e.g. <c>LivePausedBadge.razor</c>,
/// <c>PollCountdown.razor</c>) MUST implement <see cref="System.IDisposable"/> and unsubscribe
/// in <c>Dispose</c> (Pattern 2 — captured <c>this</c> leaks otherwise).
///
/// Revision-1 amendment (W3): the <see cref="Changed"/> event is declared as
/// <c>Func&lt;GateState, Task&gt;?</c> (NOT <c>Action&lt;GateState&gt;</c>) so subscribers can
/// use <c>async Task</c> handlers and wrap state mutation in <c>await InvokeAsync(...)</c> per
/// Pitfall 7 — eliminating the async-void exception that an <c>Action</c>-based event would
/// have forced in <c>LivePausedBadge.OnGateChanged</c>. Mirrors the D-03 reconciliation applied
/// to <c>PollNotifier.Cycled</c>. Phase 6's <c>CircuitTrackerGate</c> will raise <c>Changed</c>
/// asynchronously (e.g., enumerate the invocation list and await each handler).
/// </summary>
public interface ICircuitGate
{
    GateState Current { get; }
    event Func<GateState, Task>? Changed;
}

/// <summary>Two-state gate for the Live/Paused indicator (D-12).</summary>
public enum GateState
{
    Live = 0,
    Paused = 1,
}
