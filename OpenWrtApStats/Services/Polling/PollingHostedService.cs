using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWrtApStats.Services.Gating;
using OpenWrtApStats.Services.Settings;

namespace OpenWrtApStats.Services.Polling;

/// <summary>
/// 4th IHostedService — registered after AppSettingsBootstrapHostedService. Drives PollOrchestrator
/// on a PeriodicTimer keyed off IAppSettingsService.PollingIntervalSeconds (POLL-02; POLL-10).
/// Reads the interval at the top of each tick — settings change applies on the NEXT tick.
/// Catches all per-cycle exceptions and logs at Error so the loop never terminates from a
/// per-cycle exception (CLAUDE.md Conventions).
/// <para>
/// Takes a <c>Func&lt;CancellationToken, Task&gt; runCycleAsync</c> rather than <c>PollOrchestrator</c>
/// directly — keeps the host fully unit-testable without a real DB or repos. Program.cs wires
/// the runtime delegate as <c>sp =&gt; ct =&gt; sp.GetRequiredService&lt;PollOrchestrator&gt;().RunCycleAsync(ct)</c>.
/// </para>
/// <para>
/// Phase 6 D-01: injects <see cref="ICircuitGate"/> and awaits
/// <see cref="ICircuitGate.WaitForVisitorAsync(CancellationToken)"/> before every poll cycle.
/// The first wait completion (cold-start kick) runs a cycle IMMEDIATELY — no full PeriodicTimer
/// tick — and resets the timer from that moment. The <c>_coldStart</c> flag re-arms whenever
/// the gate transitions to <see cref="GateState.Paused"/> via the <see cref="ICircuitGate.Changed"/>
/// event (Pattern 2 subscribe-in-Init / unsubscribe-in-finally discipline).
/// </para>
/// </summary>
public sealed class PollingHostedService(
    Func<CancellationToken, Task> runCycleAsync,
    IAppSettingsService settings,
    ICircuitGate gate,                                       // Phase 6 D-01 visitor gate
    ILogger<PollingHostedService> log) : BackgroundService
{
    // D-06 — global cycle-overlap gate. Skip the tick when the previous cycle is still in flight.
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    // D-01 cold-start kick. Set true initially so the first WaitForVisitorAsync completion
    // triggers an immediate cycle. Re-armed via OnGateChanged when the gate transitions Paused
    // so the next Live transition triggers another immediate cycle without waiting for the
    // PeriodicTimer (which may be 60s+ from now in the worst case).
    // volatile bool: writer = OnGateChanged callback thread + ExecuteAsync loop body;
    // reader = both. The volatile semantics give us happens-before without a heavier
    // interlocked construct. The benign race (Paused then Live within one tick) is documented
    // in the plan's threat model T-06-15: at worst one extra cycle runs after a quick visitor
    // flicker, which is idempotent (mark-Offline-then-upsert atomicity, Plan 02-03).
    private volatile bool _coldStart = true;

    // Captured at ExecuteAsync entry so OnGateChanged (callback) can pass it to the
    // fire-and-forget cold-start cycle without crossing the bounds of the host lifetime.
    // BackgroundService guarantees ExecuteAsync runs to completion before Dispose, so the
    // captured token is valid for the entire window OnGateChanged can fire (subscription is
    // taken at ExecuteAsync entry and dropped in finally).
    private CancellationToken _stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("PollingHostedService starting (interval={Seconds}s)", settings.PollingIntervalSeconds);

        // Capture the stopping token for OnGateChanged's fire-and-forget kick path. Done before
        // the event subscription so OnGateChanged can never observe a default(CancellationToken).
        _stoppingToken = stoppingToken;

        // Pattern 2 — subscribe in Init (here, ExecuteAsync entry); unsubscribe in finally.
        // The finally block guarantees we drop the handler reference even on cancellation, so
        // the gate (singleton) does not keep this BackgroundService alive past Dispose.
        gate.Changed += OnGateChanged;

        var lastInterval = settings.PollingIntervalSeconds;
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(lastInterval));

        try
        {
            // D-01 — wait for the FIRST visitor before the cold-start kick. AlwaysLiveGate
            // returns Task.CompletedTask immediately; CircuitTrackerGate forwards to the
            // tracker's re-armable TCS, which completes on the 0->1 transition.
            await gate.WaitForVisitorAsync(stoppingToken);

            // Cold-start kick — run a cycle immediately (does not wait one full period).
            // For AlwaysLiveGate (no Changed event), OnGateChanged never fires, so this branch
            // is the ONLY kick path. For CircuitTrackerGate, OnGateChanged(Live) may have already
            // raced ahead and cleared _coldStart by firing its own kick — in that case this
            // branch is a no-op (exactly one kick per Paused->Live transition).
            // Fire-and-forget preserves the SemaphoreSlim overlap-skip semantic locked in Plan 02-03.
            // Per-cycle exceptions are swallowed inside TryRunCycleAsync (logged at Error) so
            // fire-and-forget never leaks unobserved exceptions to the threadpool.
            if (_coldStart)
            {
                _coldStart = false;     // claim the kick before firing (single-kick invariant)
                _ = TryRunCycleAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                // D-01 — every iteration awaits the gate. After a Paused window, this re-blocks
                // until the next visitor arrives (forwarded to CircuitTracker's fresh TCS). For
                // a still-Live gate (the common case) this returns Task.CompletedTask immediately.
                await gate.WaitForVisitorAsync(stoppingToken);

                if (_coldStart)
                {
                    // Came back from Paused -> Live AND the OnGateChanged-driven kick has not yet
                    // cleared the flag. Reset the timer so the next tick is one full interval from
                    // now. The kick itself runs from OnGateChanged on the transition; we ALSO claim
                    // the flag here so the natural-tick branch below does not double-fire.
                    timer.Dispose();
                    timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.PollingIntervalSeconds));
                    _coldStart = false;
                    continue;       // skip the natural-tick body — the kick already covered this iteration
                }

                var current = settings.PollingIntervalSeconds;
                if (current != lastInterval)
                {
                    log.LogInformation("Polling interval changed: {Old}s → {New}s", lastInterval, current);
                    timer.Dispose();
                    timer = new PeriodicTimer(TimeSpan.FromSeconds(current));
                    lastInterval = current;
                }

                _ = TryRunCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on host shutdown (D-05 — orchestrator's existing tx.RollbackAsync(CancellationToken.None)
            // path drains in-flight cycles cleanly).
        }
        finally
        {
            gate.Changed -= OnGateChanged;
            timer.Dispose();
            log.LogInformation("PollingHostedService stopped");
        }
    }

    private Task OnGateChanged(GateState newState)
    {
        if (newState == GateState.Paused)
        {
            // Re-arm the cold-start flag. The NEXT Live transition will trigger an immediate cycle.
            _coldStart = true;
            return Task.CompletedTask;
        }

        // newState == GateState.Live. If _coldStart is set, fire the kick IMMEDIATELY rather
        // than waiting for the next PeriodicTimer tick (which may be one full interval — up to
        // the operator-configured polling-interval-seconds — away). The clear-then-fire ordering
        // guarantees a single kick even if the loop body's `if (_coldStart)` race-fires too:
        // _coldStart is now false, the loop's branch skips, and TryRunCycleAsync's SemaphoreSlim
        // would drop a duplicate concurrent invocation anyway (Plan 02-03 overlap-skip semantic).
        if (_coldStart)
        {
            _coldStart = false;
            _ = TryRunCycleAsync(_stoppingToken);
        }
        return Task.CompletedTask;
    }

    private async Task TryRunCycleAsync(CancellationToken ct)
    {
        // D-06 — skip the tick if the previous cycle is still running.
        if (ct.IsCancellationRequested) return;
        if (!await _cycleGate.WaitAsync(0, ct))
        {
            log.LogInformation("Polling tick skipped: previous cycle still in flight");
            return;
        }
        var startedAt = DateTime.UtcNow;
        try
        {
            await runCycleAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown — swallow (TryRunCycleAsync runs fire-and-forget; rethrow would
            // surface as an unobserved task exception to the threadpool finalizer).
        }
        catch (Exception ex)
        {
            // CLAUDE.md Conventions: per-cycle exceptions MUST NOT terminate the loop.
            log.LogError(ex, "Poll cycle failed (started {StartedAt}, elapsed {Elapsed})", startedAt, DateTime.UtcNow - startedAt);
        }
        finally
        {
            _cycleGate.Release();
        }
    }
}
