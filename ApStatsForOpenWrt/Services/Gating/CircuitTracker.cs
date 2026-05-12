using System.Threading;
using System.Threading.Tasks;

namespace ApStatsForOpenWrt.Services.Gating;

/// <summary>
/// Phase 6 INF-06 / POLL-05 / POLL-06. Singleton visitor counter feeding both the polling-policy
/// gate (PollingHostedService awaits WaitForVisitorAsync per D-01) and the UI Live/Paused indicator
/// (CircuitTrackerGate forwards Current + Changed to LivePausedBadge / PollCountdown — Pattern 2
/// subscriber discipline already locked in Plans 05-05 + 05-03).
/// <para>
/// Per CLAUDE.md Convention: "Visitor count is Interlocked + TCS." Counter is Interlocked.Increment/
/// Decrement over an int field; the re-armable TaskCompletionSource&lt;bool&gt; is swapped via
/// Interlocked.Exchange on the 1 -&gt; 0 transition so the NEXT first-visitor wait gets a fresh,
/// non-pre-completed task. The W4 race guard (volatile bool _disposed) mirrors the
/// PollCountdown pattern locked in Plan 05-05.
/// </para>
/// <para>
/// Per PER-04 (ArchitectureTests.DbContext_IsOnlyUsedByRepositories): MUST NOT inject
/// IDbContextFactory&lt;DatabaseContext&gt;. The tracker is purely in-memory state.
/// </para>
/// </summary>
public sealed class CircuitTracker : IDisposable
{
    private int _count;
    private TaskCompletionSource<bool> _firstVisitor =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _disposed;     // W4 race guard — pattern from PollCountdown.razor

    /// <summary>
    /// Current gate state derived from the visitor counter.
    /// Volatile.Read on the int counter: a single atomic load (no snapshot record needed —
    /// only one piece of state is exposed, unlike AppSettingsService which holds 3 fields).
    /// </summary>
    public GateState Current
        => Volatile.Read(ref _count) > 0 ? GateState.Live : GateState.Paused;

    /// <summary>
    /// Async event for gate transitions. Func&lt;GateState, Task&gt;? mirrors PollNotifier.Cycled
    /// (D-03 / W3 amendment) so subscribers can use async Task handlers without async-void
    /// (Pitfall 7). Fires ONLY on 0 -&gt; 1 (Live) and 1 -&gt; 0 (Paused) transitions, never on
    /// intermediate count changes.
    /// </summary>
    public event Func<GateState, Task>? Changed;

    /// <summary>
    /// Returns a Task that completes when at least one circuit is open. Returns Task.CompletedTask
    /// immediately if a visitor is already present OR the tracker has been disposed (W4 — defensive
    /// short-circuit so a shutdown race never hangs the polling loop).
    /// </summary>
    public Task WaitForVisitorAsync(CancellationToken ct)
    {
        if (_disposed) return Task.CompletedTask;
        if (Volatile.Read(ref _count) > 0) return Task.CompletedTask;
        // W2 — mirror AppSettingsService.cs Volatile.Read pattern: read the TCS reference under
        // a fence so a concurrent OnClosed Interlocked.Exchange swap is observed before WaitAsync.
        return Volatile.Read(ref _firstVisitor).Task.WaitAsync(ct);
    }

    /// <summary>Called by CircuitTrackerHandler.OnCircuitOpenedAsync.</summary>
    public void OnOpened()
    {
        if (_disposed) return;
        if (Interlocked.Increment(ref _count) == 1)
        {
            // Transition 0 -> 1: release any waiter on the current TCS.
            // W2 — Volatile.Read mirrors AppSettingsService.cs: ARM64 weak-memory safety against
            // a racing OnClosed Interlocked.Exchange swap.
            Volatile.Read(ref _firstVisitor).TrySetResult(true);
            // Notify Changed subscribers (the gate adapter forwards to UI components).
            // Raise idiom mirrors PollNotifier.RaiseAsync — fire-and-forget the Task.
            _ = (Changed?.Invoke(GateState.Live) ?? Task.CompletedTask);
        }
    }

    /// <summary>Called by CircuitTrackerHandler.OnCircuitClosedAsync.</summary>
    public void OnClosed()
    {
        if (_disposed) return;
        if (Interlocked.Decrement(ref _count) == 0)
        {
            // Transition 1 -> 0: re-arm a fresh TCS so the NEXT WaitForVisitorAsync caller
            // gets a fresh, non-pre-completed task. Mirrors AppSettingsService.cs Interlocked.Exchange
            // snapshot-swap pattern.
            Interlocked.Exchange(
                ref _firstVisitor,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            _ = (Changed?.Invoke(GateState.Paused) ?? Task.CompletedTask);
        }
    }

    /// <summary>
    /// W4 race guard — set _disposed FIRST so any in-flight OnOpened/OnClosed/WaitForVisitorAsync
    /// caller sees the flag and short-circuits. TrySetCanceled on _firstVisitor releases any
    /// pending waiter rather than letting them hang on stoppingToken alone.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _firstVisitor.TrySetCanceled();
    }
}
