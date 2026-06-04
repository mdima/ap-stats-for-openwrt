using ApStatsForOpenWrt.Services.Settings;

namespace ApStatsForOpenWrt.Services.Gating;

/// <summary>
/// Phase 18 D-02 (M-HIST-03) — polling-gate split decorator. Wraps the production
/// <see cref="CircuitTrackerGate"/> + <see cref="IAppSettingsService"/> to make the polling gate
/// <c>historyEnabled || hasVisitor</c>:
/// <list type="bullet">
///   <item><see cref="WaitForVisitorAsync"/> short-circuits to <see cref="Task.CompletedTask"/> when
///         <see cref="IAppSettingsService.HistoryEnabled"/> is true (continuous polling regardless of
///         visitor presence); otherwise it forwards to the inner gate (today's pause-when-no-visitor).</item>
///   <item><see cref="Current"/> reports <see cref="GateState.Live"/> when history is enabled; otherwise
///         it forwards the inner gate's state. (The UI Live/Paused badge is intentionally NOT coupled to
///         history — CONTEXT.md D-02; the badge subscribes to the inner tracker, this decorator is the
///         polling-loop gate only.)</item>
///   <item>On a <b>false→true</b> <see cref="IAppSettingsService.HistoryEnabled"/> transition the decorator
///         fires a synthetic <see cref="Changed"/>(<see cref="GateState.Live"/>) so a PAUSED
///         <c>PollingHostedService</c> blocked at <see cref="WaitForVisitorAsync"/> wakes via its EXISTING
///         cold-start kick — no edit to <c>PollingHostedService</c> or <c>CircuitTracker</c>. A
///         <b>true→false</b> transition fires NO synthetic event (the next <see cref="WaitForVisitorAsync"/>
///         re-evaluates to the inner gate).</item>
///   <item>Inner gate <see cref="Changed"/> transitions are forwarded through this decorator's own event
///         unchanged, so existing subscribers (LivePausedBadge / PollCountdown) keep working when wired to
///         the decorator.</item>
/// </list>
/// ⚠ The settings event is <see cref="Action{T}"/> over <see cref="IAppSettingsService"/> — NOT the gate's
/// <c>Func&lt;GateState,Task&gt;</c>; <see cref="OnSettingsChanged"/> matches the Action signature.
/// Implements <see cref="IDisposable"/> and unsubscribes from BOTH source events in <see cref="Dispose"/>
/// (the captured <c>this</c> would leak the singleton otherwise — Pattern 2 discipline).
/// </summary>
public sealed class HistoryAwareCircuitGate : ICircuitGate, IDisposable
{
    private readonly ICircuitGate _inner;             // the production CircuitTrackerGate
    private readonly IAppSettingsService _settings;
    private bool _lastHistoryEnabled;
    private bool _disposed;

    public event Func<GateState, Task>? Changed;

    public HistoryAwareCircuitGate(ICircuitGate inner, IAppSettingsService settings)
    {
        _inner = inner;
        _settings = settings;
        // Seed the edge tracker from the (post-warm by registration order) current value so the
        // FIRST false→true toggle is detected correctly. See 18-RESEARCH §"Ordering caveat".
        _lastHistoryEnabled = _settings.HistoryEnabled;
        _inner.Changed += OnInnerChanged;            // forward inner transitions through
        _settings.Changed += OnSettingsChanged;      // wake-on-toggle
    }

    public GateState Current => _settings.HistoryEnabled ? GateState.Live : _inner.Current;

    public Task WaitForVisitorAsync(CancellationToken ct)
        => _settings.HistoryEnabled ? Task.CompletedTask : _inner.WaitForVisitorAsync(ct);

    private Task OnInnerChanged(GateState s) => Changed?.Invoke(s) ?? Task.CompletedTask;

    private void OnSettingsChanged(IAppSettingsService s)
    {
        var now = s.HistoryEnabled;
        if (now && !_lastHistoryEnabled)              // false→true: synthetic wake
            _ = Changed?.Invoke(GateState.Live) ?? Task.CompletedTask;
        // true→false: no synthetic event — next WaitForVisitorAsync re-evaluates to the inner gate.
        _lastHistoryEnabled = now;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.Changed -= OnInnerChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
