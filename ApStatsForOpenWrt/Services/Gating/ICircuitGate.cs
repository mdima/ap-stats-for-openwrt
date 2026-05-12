namespace OpenWrtApStats.Services.Gating;

/// <summary>
/// D-12 Phase 5 stub seam, Phase 6 wired to <see cref="CircuitTracker"/>. Surfaces the
/// polling-gate state to the UI AND the visitor-gate primitive that PollingHostedService
/// awaits (CONTEXT.md D-01). Subscribers (e.g. <c>LivePausedBadge.razor</c>,
/// <c>PollCountdown.razor</c>) MUST implement <see cref="System.IDisposable"/> and unsubscribe
/// in <c>Dispose</c> (Pattern 2 — captured <c>this</c> leaks otherwise).
///
/// Revision-1 amendment (W3): the <see cref="Changed"/> event is declared as
/// <c>Func&lt;GateState, Task&gt;?</c> (NOT <c>Action&lt;GateState&gt;</c>) so subscribers can
/// use <c>async Task</c> handlers and wrap state mutation in <c>await InvokeAsync(...)</c> per
/// Pitfall 7 — eliminating the async-void exception that an <c>Action</c>-based event would
/// have forced in <c>LivePausedBadge.OnGateChanged</c>. Mirrors the D-03 reconciliation applied
/// to <c>PollNotifier.Cycled</c>. Phase 6's <c>CircuitTrackerGate</c> raises <c>Changed</c>
/// asynchronously (forwards to <see cref="CircuitTracker"/>'s raise idiom).
///
/// Phase 6 amendment: <see cref="WaitForVisitorAsync"/> added so PollingHostedService can
/// await visitor presence before each poll cycle (D-01 cold-start kick).
/// <see cref="AlwaysLiveGate"/> returns <see cref="System.Threading.Tasks.Task.CompletedTask"/>
/// (drop-in for tests that opt out of gating); <see cref="CircuitTrackerGate"/> forwards to
/// <see cref="CircuitTracker.WaitForVisitorAsync(System.Threading.CancellationToken)"/>.
/// </summary>
public interface ICircuitGate
{
    GateState Current { get; }
    event Func<GateState, Task>? Changed;

    /// <summary>
    /// Returns a Task that completes when at least one circuit is currently active.
    /// Returns <see cref="System.Threading.Tasks.Task.CompletedTask"/> immediately when a
    /// visitor is already present. Phase 6 D-01: PollingHostedService awaits this before the
    /// first poll cycle and before every loop iteration thereafter; the cold-start kick runs
    /// a poll immediately when the wait completes after a Paused transition.
    /// </summary>
    Task WaitForVisitorAsync(CancellationToken ct);
}

/// <summary>Two-state gate for the Live/Paused indicator (D-12).</summary>
public enum GateState
{
    Live = 0,
    Paused = 1,
}
