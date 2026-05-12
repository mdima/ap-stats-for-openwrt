namespace OpenWrtApStats.Services.Gating;

/// <summary>
/// Phase 6 INF-06 / POLL-05 / POLL-06 — replaces <see cref="AlwaysLiveGate"/> (Plan 05-01 D-12 stub).
/// Forwarding adapter over <see cref="CircuitTracker"/>: gate state is derived from the tracker
/// counter; Changed event subscriptions are forwarded so subscribers (LivePausedBadge.razor,
/// PollCountdown.razor — Pattern 2 discipline locked in Plans 05-03/05-05) require ZERO changes.
/// <para>
/// No field-backed event, no separate state — the tracker is the single source of truth. Mirrors
/// the AlwaysLiveGate shape verbatim aside from the forwarding accessors. Stays outside the PER-04
/// repository firewall (no IDbContextFactory injection — ArchitectureTests.DbContext_IsOnlyUsedByRepositories
/// stays green WITHOUT a new carve-out).
/// </para>
/// </summary>
public sealed class CircuitTrackerGate(CircuitTracker tracker) : ICircuitGate
{
    public GateState Current => tracker.Current;

    public event Func<GateState, Task>? Changed
    {
        add    => tracker.Changed += value;
        remove => tracker.Changed -= value;
    }

    /// <summary>
    /// Forwards to <see cref="CircuitTracker.WaitForVisitorAsync(CancellationToken)"/>. Phase 6
    /// D-01: PollingHostedService awaits this before each poll cycle so headless installations
    /// (no UI tab open) draw zero SSH traffic.
    /// </summary>
    public Task WaitForVisitorAsync(CancellationToken ct)
        => tracker.WaitForVisitorAsync(ct);
}
