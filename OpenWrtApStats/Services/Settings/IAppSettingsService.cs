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

    Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default);
    Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default);

    /// <summary>Called once by AppSettingsBootstrapHostedService after migrations complete.</summary>
    Task WarmAsync(CancellationToken ct = default);

    /// <summary>Raised AFTER any setter has refreshed the snapshot. Phase 2 subscribes.</summary>
    event Action<IAppSettingsService>? Changed;
}
