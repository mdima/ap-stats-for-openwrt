using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Services.Gating;
using ApStatsForOpenWrt.Services.Settings;

namespace ApStatsForOpenWrt.Services.UpdateCheck;

/// <summary>
/// M-UPD-01 — 7th IHostedService, registered AFTER AppSettingsBootstrap (toggle warm) and Polling
/// (bystander to the polling mission). The SIMPLER charter loop (NOT the polling cold-start machinery):
/// <code>
/// while (!stopping) {
///   await gate.WaitForVisitorAsync();           // L-07 visitor gate at TOP
///   if (settings.UpdateCheckEnabled) await checker.CheckOnceAsync();  // toggle guard; first iteration IS the startup check
///   if (!await timer.WaitForNextTickAsync()) break;  // 24h cadence
/// }
/// </code>
/// <para>
/// The 24h <see cref="PeriodicTimer"/> is constructed via the <see cref="TimeProvider"/>-aware ctor
/// so tests advance a <c>FakeTimeProvider</c> instead of waiting a real 24h — the only deviation from
/// the <c>PollingHostedService</c> analog's parameterless ctor, and it exists solely for testability.
/// </para>
/// <para>
/// DELIBERATELY ABSENT (polling-specific; PATTERNS.md "DO NOT copy"): no <c>_coldStart</c> kick, no
/// <c>gate.Changed</c> subscription, no <c>SemaphoreSlim</c> overlap gate, no timer reconfiguration.
/// The 24h checker has none of those concerns. <c>CheckOnceAsync</c> swallows its own failures
/// (single Warning, no timestamp bump), but the loop body ALSO guards it with a catch-all so a
/// per-iteration throw can never terminate the BackgroundService (CLAUDE.md Conventions).
/// </para>
/// </summary>
public sealed class UpdateCheckHostedService(
    IUpdateChecker checker,
    IAppSettingsService settings,
    ICircuitGate gate,                                  // L-07 visitor gate (DI abstraction)
    TimeProvider timeProvider,                          // injected clock — fake in tests, System in prod
    ILogger<UpdateCheckHostedService> log) : BackgroundService
{
    // M-UPD-01 — hard-coded 24h cadence (REQUIREMENTS). Read by the PeriodicTimer ctor below.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("UpdateCheckHostedService starting (interval={Hours}h)", CheckInterval.TotalHours);

        // TimeProvider-aware ctor so the 24h tick is unit-testable without a real wait.
        var timer = new PeriodicTimer(CheckInterval, timeProvider);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // L-07 — visitor gate at the TOP. Losing visitors / no visitor blocks here so a
                // headless container with nobody watching issues zero outbound traffic.
                await gate.WaitForVisitorAsync(stoppingToken);

                // M-UPD-01 toggle guard. The first iteration after the first visitor IS the startup
                // check. CheckOnceAsync swallows its own failures; the catch-all below is belt+braces
                // so even an unexpected throw cannot terminate the loop (CLAUDE.md Conventions).
                if (settings.UpdateCheckEnabled)
                {
                    try
                    {
                        await checker.CheckOnceAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw; // propagate to the shutdown handler below
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Update check threw unexpectedly; loop continues to the next tick.");
                    }
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on host shutdown.
        }
        finally
        {
            timer.Dispose();
            log.LogInformation("UpdateCheckHostedService stopped");
        }
    }
}
