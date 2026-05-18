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
/// 260518-gpd — pause-grace debounce. The 1 -&gt; 0 transition does NOT fire Changed(Paused) or
/// swap the TCS immediately. It schedules a one-shot Task.Delay(pauseGrace) callback that, on
/// completion, re-checks Volatile.Read(ref _count) and only THEN performs the swap + fire. If a
/// concurrent 0 -&gt; 1 transition occurs during the grace window, OnOpened cancels the pending
/// CTS via Interlocked.Exchange and does NOT fire Changed(Live) (we never went Paused, so there
/// is no transition to announce — semantically we stayed Live). Pins the F5 / single-tab-close
/// spurious-cold-start-cycle fix discovered after 260518-pch.
/// </para>
/// <para>
/// Per PER-04 (ArchitectureTests.DbContext_IsOnlyUsedByRepositories): MUST NOT inject
/// IDbContextFactory&lt;DatabaseContext&gt;. The tracker is purely in-memory state.
/// </para>
/// </summary>
public sealed class CircuitTracker : IDisposable
{
    // 260518-gpd — default grace. 30s comfortably absorbs F5 / Alt-Tab / quick-reload patterns
    // operators actually do. The CLAUDE.md / PITFALLS.md prose mentioning
    // "DisconnectedCircuitRetentionPeriod + 30s" was aspirational and never implemented; the
    // 30s figure is the honest, post-260518-gpd value. NOT 3min+30s — that figure assumed
    // DisconnectedCircuitRetentionPeriod covers F5, which it does not (graceful close fires
    // OnCircuitClosedAsync immediately).
    private static readonly TimeSpan DefaultPauseGrace = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _pauseGrace;
    private int _count;
    private TaskCompletionSource<bool> _firstVisitor =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _disposed;     // W4 race guard — pattern from PollCountdown.razor

    // 260518-gpd — pending pause CTS. Non-null between a 1 -> 0 transition and either
    // (a) the grace callback firing Changed(Paused), or (b) a concurrent 0 -> 1 transition
    // cancelling the pause. Written via Interlocked.Exchange; read via Interlocked.Exchange
    // (consume-and-replace pattern) inside both OnOpened (cancel path) and the grace callback
    // (consume-or-no-op path). NEVER read via plain field access.
    private CancellationTokenSource? _pauseCts;

    /// <summary>
    /// Default ctor — production wiring. Pause-grace = 30 s.
    /// </summary>
    public CircuitTracker() : this(pauseGrace: null) { }

    /// <summary>
    /// Test/explicit ctor — accept a custom pause-grace for fast unit tests. Pass <c>null</c>
    /// for the production default of 30 s.
    /// </summary>
    public CircuitTracker(TimeSpan? pauseGrace)
    {
        _pauseGrace = pauseGrace ?? DefaultPauseGrace;
    }

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
    /// (Pitfall 7). Fires ONLY on 0 -&gt; 1 (Live) — synchronously — and 1 -&gt; 0 (Paused) —
    /// after the 260518-gpd grace window — transitions, never on intermediate count changes
    /// and never on 1 -&gt; 0 -&gt; 1 flickers within the grace window.
    /// </summary>
    public event Func<GateState, Task>? Changed;

    /// <summary>
    /// Returns a Task that completes when at least one circuit is open. Returns Task.CompletedTask
    /// immediately if a visitor is already present OR the tracker has been disposed (W4 — defensive
    /// short-circuit so a shutdown race never hangs the polling loop).
    /// <para>
    /// 260518-gpd — during a pending-pause grace window, _firstVisitor is the PREVIOUS,
    /// pre-completed TCS (we have not swapped yet). A caller racing into WaitForVisitorAsync
    /// here falls through to the TCS branch (since _count IS already 0 during grace) and gets
    /// the still-completed previous TCS. Returns immediately. This is intentional — semantically
    /// we are still Live during grace.
    /// </para>
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
        if (Interlocked.Increment(ref _count) != 1) return;     // not a 0 -> 1 transition

        // 260518-gpd — if a pending-pause CTS exists, this 0 -> 1 is the "cancel-the-pending-pause"
        // path: we never fired Paused, so we do NOT fire Live (semantically we stayed Live throughout).
        // Interlocked.Exchange returns the prior CTS; we cancel + dispose it. The grace callback
        // observes either the cancellation OR a re-incremented _count and no-ops.
        var pending = Interlocked.Exchange(ref _pauseCts, null);
        if (pending is not null)
        {
            try { pending.Cancel(); } catch (ObjectDisposedException) { /* benign — callback raced ahead */ }
            pending.Dispose();
            return;
        }

        // Genuine cold-start from Paused — release any waiter on the current TCS and fire Live.
        // W2 — Volatile.Read mirrors AppSettingsService.cs: ARM64 weak-memory safety against
        // a racing OnClosed Interlocked.Exchange swap.
        Volatile.Read(ref _firstVisitor).TrySetResult(true);
        // Notify Changed subscribers (the gate adapter forwards to UI components).
        // Raise idiom mirrors PollNotifier.RaiseAsync — fire-and-forget the Task.
        _ = (Changed?.Invoke(GateState.Live) ?? Task.CompletedTask);
    }

    /// <summary>Called by CircuitTrackerHandler.OnCircuitClosedAsync.</summary>
    public void OnClosed()
    {
        if (_disposed) return;
        if (Interlocked.Decrement(ref _count) != 0) return;     // not a 1 -> 0 transition

        // 260518-gpd — DO NOT swap TCS or fire Changed(Paused) synchronously. Schedule a
        // debounced transition. Replace any existing pending CTS (re-entrancy on 1->0->1->0
        // flicker — the second close restarts the timer; cancel + dispose the first).
        var newCts = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _pauseCts, newCts);
        if (prior is not null)
        {
            try { prior.Cancel(); } catch (ObjectDisposedException) { /* benign */ }
            prior.Dispose();
        }
        _ = SchedulePauseTransitionAsync(newCts);
    }

    /// <summary>
    /// 260518-gpd — grace-window callback. Awaits Task.Delay(_pauseGrace, cts.Token); on
    /// successful (non-cancelled) completion, re-checks _count under Volatile.Read and — only
    /// if STILL 0 AND the CTS reference is STILL the one we own (i.e. we have not been
    /// concurrently replaced by another 1 -> 0) — swaps _firstVisitor to a fresh TCS and
    /// fires Changed(Paused). On cancellation, OperationCanceledException is swallowed and
    /// the method returns without firing.
    /// </summary>
    private async Task SchedulePauseTransitionAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_pauseGrace, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by a concurrent OnOpened (or Dispose). No fire.
            return;
        }

        // Consume our CTS reference. If someone else (Dispose, a re-entrant OnClosed) already
        // exchanged it out, we are no longer authoritative — bail.
        if (_disposed) return;
        var stillOurs = Interlocked.CompareExchange(ref _pauseCts, null, cts);
        if (!ReferenceEquals(stillOurs, cts)) return;
        cts.Dispose();

        // Final re-check under Volatile.Read: a benign race could have incremented _count
        // between Task.Delay returning and the CompareExchange above. If so, bail.
        if (Volatile.Read(ref _count) > 0) return;

        // Authoritative 1 -> 0 transition. Swap TCS + fire Changed(Paused) — mirrors the
        // pre-260518-gpd OnClosed body, now relocated post-grace.
        Interlocked.Exchange(
            ref _firstVisitor,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        _ = (Changed?.Invoke(GateState.Paused) ?? Task.CompletedTask);
    }

    /// <summary>
    /// W4 race guard — cancel + dispose any pending pause CTS BEFORE setting _disposed = true so
    /// the grace callback sees either its own cancellation OR the _disposed flag and no-ops.
    /// Then TrySetCanceled on _firstVisitor releases any pending WaitForVisitorAsync waiter
    /// rather than letting them hang on stoppingToken alone.
    /// </summary>
    public void Dispose()
    {
        var pending = Interlocked.Exchange(ref _pauseCts, null);
        if (pending is not null)
        {
            try { pending.Cancel(); } catch (ObjectDisposedException) { /* benign */ }
            pending.Dispose();
        }
        _disposed = true;
        _firstVisitor.TrySetCanceled();
    }
}
