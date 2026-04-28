using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Settings;

namespace OpenWrtApStats.Services.Bootstrap;

/// <summary>
/// Final bootstrap step: confirms the AppSettings table is reachable and warms the
/// <see cref="IAppSettingsService"/> typed singleton snapshot. Runs LAST so that schema
/// migrations and the D-12 probe both complete before any settings consumer reads a
/// snapshot value.
/// <para>
/// Two-stage: (a) explicit fail-fast probe via <see cref="IAppSettingsRepo.GetAllAsync"/> so
/// a persistence error surfaces at bootstrap with a clear log line attributed to AppSettings,
/// rather than as a stale snapshot at first read; (b) <see cref="IAppSettingsService.WarmAsync"/>
/// promotes the row set into the typed snapshot (D-11). The repo step is defense-in-depth —
/// <see cref="IAppSettingsService.WarmAsync"/> internally calls the same repo, but the explicit
/// probe gives us a clear bootstrap log line and an early failure point.
/// </para>
/// </summary>
public sealed class AppSettingsBootstrapHostedService(
    IAppSettingsRepo settingsRepo,
    IAppSettingsService settings,
    ILogger<AppSettingsBootstrapHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // (a) Fail-fast: verify the AppSettings table is reachable before warming the snapshot.
        //     A failure here surfaces an EF / SQLite error at bootstrap rather than a stale
        //     snapshot at first read.
        var rows = await settingsRepo.GetAllAsync(cancellationToken);
        log.LogInformation("AppSettings warm-up: {Count} rows loaded from SQLite", rows.Count);

        // (b) Promote into the singleton typed accessor (D-11). WarmAsync internally calls the
        //     same repo, but the explicit step above gives us a clear bootstrap log line and an
        //     early failure point.
        await settings.WarmAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
