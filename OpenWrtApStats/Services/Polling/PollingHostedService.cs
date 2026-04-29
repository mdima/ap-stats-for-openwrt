using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
/// </summary>
public sealed class PollingHostedService(
    Func<CancellationToken, Task> runCycleAsync,
    IAppSettingsService settings,
    ILogger<PollingHostedService> log) : BackgroundService
{
    // D-06 — global cycle-overlap gate. Skip the tick when the previous cycle is still in flight.
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("PollingHostedService starting (interval={Seconds}s)", settings.PollingIntervalSeconds);

        var lastInterval = settings.PollingIntervalSeconds;
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(lastInterval));

        try
        {
            // Kick the first cycle without waiting one full period (Phase 6 will gate on visitor;
            // Phase 2 fires immediately so headless polling starts right away). Fire-and-forget so
            // the timer keeps ticking on schedule even when a cycle exceeds the interval; the
            // SemaphoreSlim cycle gate (TryRunCycleAsync) skips overlapping ticks per D-06.
            // Per-cycle exceptions are swallowed inside TryRunCycleAsync (logged at Error) so
            // fire-and-forget never leaks unobserved exceptions to the threadpool.
            _ = TryRunCycleAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
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
            // Expected on host shutdown.
        }
        finally
        {
            timer.Dispose();
            log.LogInformation("PollingHostedService stopped");
        }
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
