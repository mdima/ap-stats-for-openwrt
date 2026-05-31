namespace ApStatsForOpenWrt.Services.UpdateCheck;

/// <summary>
/// M-UPD-01 — the singleton seam for the background update check. ONE code path: both the
/// <c>UpdateCheckHostedService</c> 24h loop AND the Settings page "Check now" button (Plan 03,
/// M-UPD-05) call this single method. It fetches the upstream GitHub <c>releases/latest</c>,
/// compares the parsed tag against the running assembly version via <see cref="System.Version"/>,
/// persists through <c>IAppSettingsService</c> (never a <c>DbContext</c>), and raises
/// <c>UpdateNotifier.UpdateAvailable</c> exactly once on a real N→N+1 advance (transition-fires-once,
/// D-05 — the dedup discipline lives in the implementation, not in the notifier).
/// <para>
/// The implementation swallows all failures internally (network / non-200 / parse) with a single
/// Serilog Warning and never throws — the timestamp of the last *successful* check is preserved on
/// failure.
/// </para>
/// </summary>
public interface IUpdateChecker
{
    /// <summary>
    /// Runs one update check. Never throws: failures are logged once at Warning and swallowed.
    /// </summary>
    Task CheckOnceAsync(CancellationToken ct = default);
}
