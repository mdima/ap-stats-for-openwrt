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
    /// M-SEC-04 — monotonic counter incremented on every UI-password change (set or
    /// clear). The cookie auth pipeline stamps this value as a <c>session_version</c> claim
    /// at sign-in and the <c>OnValidatePrincipal</c> event compares the claim against this
    /// property on every request; mismatch invalidates the cookie. Default 0; persisted as
    /// a new AppSetting row the first time it is bumped.
    /// </summary>
    int SessionVersion { get; }

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

    /// <summary>
    /// M-UPD-02 — master switch for the background update-check feature. Default <c>true</c>
    /// (D-07 ship-armed: default-on so the feature self-activates at the first upstream release).
    /// Written-by: the Settings page toggle (Plan 03, M-UPD-05). Read-by: the checker loop
    /// (Plan 02, M-UPD-01 — an air-gapped operator flips this off for zero outbound traffic) and
    /// the navbar badge render-gate (Plan 03, M-UPD-03). Path B seed: there is NO physical DB row
    /// for this key on a fresh DB — the in-memory warm-time default IS the seed.
    /// </summary>
    bool UpdateCheckEnabled { get; }

    /// <summary>
    /// M-UPD-02 — the most recent GitHub release tag seen by the checker, or <c>null</c> until
    /// the first successful check has discovered one. Written-by: the checker on a real N→N+1
    /// advance (Plan 02). Read-by: the navbar badge (Plan 03 — rendered only when this parses to a
    /// <c>System.Version</c> greater than the running assembly version) and the Settings diagnostics.
    /// </summary>
    string? LatestKnownVersion { get; }

    /// <summary>
    /// M-UPD-02 — the GitHub release page URL for <see cref="LatestKnownVersion"/>, or <c>null</c>
    /// until discovered. Written-by: the checker on a real advance (Plan 02). Read-by: the badge href
    /// + the discovery toast "release notes" link (Plan 03). Non-secret GitHub-public value.
    /// </summary>
    string? LatestReleaseUrl { get; }

    /// <summary>
    /// M-UPD-02 — UTC timestamp of the last completed update check (success OR equal/older result),
    /// or <c>null</c> until the first check completes. Written-by: the checker (Plan 02 — touched on
    /// every non-failed check; left untouched on failure). Read-by: the Settings "last checked"
    /// diagnostic + the badge tooltip "discovered Xh ago". Always carries <see cref="DateTimeKind.Utc"/>
    /// on read — persisted as an ISO-8601 round-trip (<c>o</c>) string because <c>AppSetting</c> has no
    /// DateTime column, and re-stamped UTC on read (SQLite returns Kind=Unspecified — T-17-01-01).
    /// </summary>
    DateTime? LastCheckedAtUtc { get; }

    Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default);
    Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default);
    Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default);

    /// <summary>SET-01 — operator picks from the radio presets (D-03: 15min/30min/1h/6h/24h, default 1h=3600s).</summary>
    Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default);

    /// <summary>SET-02 — operator edits as comma-separated text (D-04); the Settings page splits, trims, and discards empties before invoking this.</summary>
    Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default);

    /// <summary>
    /// M-SEC-04 — explicit setter. Tests use this to set known starting values; production
    /// code calls <see cref="BumpSessionVersionAsync"/> instead.
    /// </summary>
    Task SetSessionVersionAsync(int version, CancellationToken ct = default);

    /// <summary>
    /// M-SEC-04 — reads the current SessionVersion, increments by 1, persists via the same
    /// <c>UpsertAndRefreshAsync</c> path as every other setter. Called atomically inside
    /// <see cref="SetUIPasswordHashAsync"/> (both set and clear paths). The
    /// orchestrator-only-writes invariant is preserved because the existing
    /// <c>IAppSettingsRepo.UpsertAsync</c> is the single writer to <c>AppSetting</c> rows.
    /// </summary>
    Task BumpSessionVersionAsync(CancellationToken ct = default);

    /// <summary>M-UPD-02 / M-UPD-05 — operator toggles the feature on/off from the Settings card (immediate persist).</summary>
    Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>M-UPD-02 — checker persists the discovered release tag (null clears it). Written only by Plan 02.</summary>
    Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default);

    /// <summary>M-UPD-02 — checker persists the discovered release page URL (null clears it). Written only by Plan 02.</summary>
    Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default);

    /// <summary>
    /// M-UPD-02 — checker stamps the last-checked UTC timestamp (null clears it). Persisted as an
    /// ISO-8601 round-trip (<c>o</c>) string; the value MUST be UTC (T-17-01-01).
    /// </summary>
    Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// M-UPD-02 — combined success-write the checker (Plan 02) calls on a real N→N+1 advance:
    /// upserts <see cref="LatestKnownVersion"/>, <see cref="LatestReleaseUrl"/>, and
    /// <see cref="LastCheckedAtUtc"/> atomically, then re-warms the snapshot and raises
    /// <see cref="Changed"/> EXACTLY ONCE (a single badge/toast re-render, not three).
    /// </summary>
    Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default);

    /// <summary>Called once by AppSettingsBootstrapHostedService after migrations complete.</summary>
    Task WarmAsync(CancellationToken ct = default);

    /// <summary>Raised AFTER any setter has refreshed the snapshot. Phase 2 subscribes.</summary>
    event Action<IAppSettingsService>? Changed;
}
