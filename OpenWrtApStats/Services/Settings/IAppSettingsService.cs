namespace OpenWrtApStats.Services.Settings;

/// <summary>
/// Singleton typed accessor for application settings (D-11). Implementations expose an
/// in-memory snapshot swapped atomically via <c>Interlocked.Exchange</c>. The well-known
/// keys are kept private inside the implementation; callers consume strongly-typed
/// properties only. The <see cref="Changed"/> event fires after the snapshot has been
/// refreshed from the just-written DB state — Phase 2 subscribes to drive
/// <c>PeriodicTimer</c> period reconfiguration.
/// </summary>
public interface IAppSettingsService
{
    int     PollingIntervalSeconds { get; }
    string? UIPasswordHash         { get; }
    bool    WizardDismissed        { get; }

    /// <summary>
    /// Phase 10 ORCH-01. Cached value of the AppSetting key <c>RefreshIntervalSeconds</c>
    /// (seeded by Phase 9 SCHEMA-06 to default 3600). Editable via Plan 10-02's Settings
    /// page (D-03 — radio preset 15min/30min/1h/6h/24h). Read by PollOrchestrator's
    /// per-node refresh-trigger evaluator. The corresponding async setter lands in Plan
    /// 10-02; Plan 10-01 ships the read-side only.
    /// </summary>
    int RefreshIntervalSeconds { get; }

    Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default);
    Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default);
    Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default);

    /// <summary>Called once by AppSettingsBootstrapHostedService after migrations complete.</summary>
    Task WarmAsync(CancellationToken ct = default);

    /// <summary>Raised AFTER any setter has refreshed the snapshot. Phase 2 subscribes.</summary>
    event Action<IAppSettingsService>? Changed;
}
