using Microsoft.AspNetCore.Components.Server.Circuits;

namespace OpenWrtApStats.Services.Gating;

/// <summary>
/// Phase 6 — Blazor CircuitHandler that drives <see cref="CircuitTracker"/> from per-circuit
/// open/close lifecycle events. Registered as scoped (Blazor framework requirement — one handler
/// per circuit). The tracker it forwards to is a singleton, so all per-circuit handlers
/// converge on one Interlocked counter (CLAUDE.md "visitor count is Interlocked + TCS via
/// singleton CircuitTracker").
/// <para>
/// Per CLAUDE.md Convention: "Tests use bUnit, not mocks of CircuitHandler." Unit tests in
/// CircuitTrackerTests instantiate a CircuitTracker directly and assert OnOpened/OnClosed
/// behavior — the handler itself is a thin lifecycle adapter exercised by an integration
/// path (HealthEndpointTests in plan 06-03 indirectly, since browser circuits don't open
/// during /health requests).
/// </para>
/// </summary>
public sealed class CircuitTrackerHandler(CircuitTracker tracker) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tracker.OnOpened();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tracker.OnClosed();
        return Task.CompletedTask;
    }
}
