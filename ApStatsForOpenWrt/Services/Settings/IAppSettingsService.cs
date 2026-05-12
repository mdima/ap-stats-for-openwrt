namespace ApStatsForOpenWrt.Services.Settings;

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

    /// <summary>
    /// Phase 10 SET-02 — local domain suffixes used by the dashboard (Plan 10-04) to strip
    /// FQDN tails at display time. Stored as a JSON-array string in <c>AppSetting.StringValue</c>
    /// (matching the v0.9 JSON-array storage convention seeded by Phase 9 SCHEMA-06).
    /// Empty list = no stripping (FQDN displayed verbatim). The persisted Hostname column
    /// is always the full FQDN — suffix-stripping is display-only (CHARTER §"Hostname storage and display").
    /// </summary>
    IReadOnlyList<string> LocalDomainSuffixes { get; }

    Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default);
    Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default);
    Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default);

    /// <summary>SET-01 — operator picks from the radio presets (D-03: 15min/30min/1h/6h/24h, default 1h=3600s).</summary>
    Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default);

    /// <summary>SET-02 — operator edits as comma-separated text (D-04); the Settings page splits, trims, and discards empties before invoking this.</summary>
    Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default);

    /// <summary>Called once by AppSettingsBootstrapHostedService after migrations complete.</summary>
    Task WarmAsync(CancellationToken ct = default);

    /// <summary>Raised AFTER any setter has refreshed the snapshot. Phase 2 subscribes.</summary>
    event Action<IAppSettingsService>? Changed;
}
