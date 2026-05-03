namespace OpenWrtApStats.Services.Gating;

/// <summary>
/// Phase 5 stub. Always reports <see cref="GateState.Live"/>. The <c>add</c>/<c>remove</c>
/// accessors are no-ops because the state never changes — Phase 6's <c>CircuitTrackerGate</c>
/// will replace this with a real subscription source backed by <c>CircuitTracker</c>
/// (INF-06 / POLL-05 / POLL-06). The empty <c>add { } remove { }</c> compile away — no
/// field-backed event. This is the documented Phase-5-only shape (CONTEXT D-12).
///
/// Revision-1 amendment (W3): <see cref="ICircuitGate.Changed"/> is now
/// <c>Func&lt;GateState, Task&gt;?</c>; the no-op accessor signature changes accordingly.
/// </summary>
public sealed class AlwaysLiveGate : ICircuitGate
{
    public GateState Current => GateState.Live;
    public event Func<GateState, Task>? Changed { add { } remove { } }

    /// <summary>
    /// Always-live stub returns <see cref="Task.CompletedTask"/> immediately — useful for tests
    /// that opt out of gating (e.g. PollOrchestratorTests, NodePollerTests, fixtures that drive
    /// the cycle without simulating circuit lifecycle). Mirrors the no-op accessor philosophy
    /// of the Changed event: nothing to await because a visitor is permanently "present".
    /// </summary>
    public Task WaitForVisitorAsync(CancellationToken ct) => Task.CompletedTask;
}
