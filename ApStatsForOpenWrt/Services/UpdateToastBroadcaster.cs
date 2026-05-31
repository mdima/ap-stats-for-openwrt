using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Services.UpdateCheck;

namespace ApStatsForOpenWrt.Services;

/// <summary>
/// Phase 17 Plan 03 / M-UPD-04 (toast surface) — singleton broadcaster that subscribes ONCE
/// per process to <see cref="UpdateNotifier.UpdateAvailable"/> and emits a single discovery
/// toast through the singleton <see cref="ToastService"/>. The singleton ToastService then
/// broadcasts the entry to EVERY circuit's <c>ToastHost</c> for free.
///
/// <para>
/// <b>Why a singleton and not a per-circuit Blazor subscriber (D-04 refinement).</b> A
/// per-circuit component subscribing to <see cref="UpdateNotifier"/> and calling
/// <see cref="ToastService.Show"/> would, with N open circuits, invoke <c>Show</c> N times per
/// raise — and each <c>Show</c> already fans out to all N ToastHosts (N×N toasts). Routing
/// through ONE singleton subscriber preserves the "one raise → exactly one toast per circuit"
/// broadcast contract. This mirrors <see cref="EventDetectionService"/> exactly: a singleton
/// that subscribes in its constructor and is eager-started by a hosted-service shim.
/// </para>
///
/// <para>
/// <b>D-04 reuse-as-is.</b> The toast carries NO link (the navbar badge owns the link) and uses
/// severity <c>info</c>. <see cref="ToastService.Show"/> already early-returns when
/// <see cref="INotificationsGate.IsEnabled"/> is <c>false</c>, so the gate is respected without
/// any logic here. <c>ToastEntry</c>/<c>ToastHost</c> are NOT extended.
/// </para>
///
/// <para>
/// <b>Transition-fires-once.</b> The Plan-02 checker raises <see cref="UpdateNotifier.UpdateAvailable"/>
/// only on a real N→N+1 advance, so this handler emits exactly one toast per raise; it never
/// dedups (dedup lives in the checker, D-05).
/// </para>
/// </summary>
public sealed class UpdateToastBroadcaster : IDisposable
{
    private readonly UpdateNotifier _notifier;
    private readonly ToastService _toasts;
    private readonly ILogger<UpdateToastBroadcaster> _log;

    public UpdateToastBroadcaster(
        UpdateNotifier notifier,
        ToastService toasts,
        ILogger<UpdateToastBroadcaster> log)
    {
        _notifier = notifier;
        _toasts = toasts;
        _log = log;

        // Singleton lifetime owns the subscription. Handler matches Func<UpdateInfo, Task>?
        // exactly (Pitfall 7 — never async void).
        _notifier.UpdateAvailable += OnUpdateAvailableAsync;
    }

    private async Task OnUpdateAvailableAsync(UpdateInfo info)
    {
        try
        {
            // D-04 — NO link; severity "info"; the badge carries the release URL.
            await _toasts.Show(
                $"Version {info.NewVersion} available",
                "A newer release is available.",
                "info");
        }
        catch (Exception ex)
        {
            // A bad toast emission must never detach the subscription or bubble into the
            // notifier's raise loop.
            _log.LogWarning(ex, "UpdateToastBroadcaster failed to emit the discovery toast");
        }
    }

    public void Dispose()
    {
        try { _notifier.UpdateAvailable -= OnUpdateAvailableAsync; } catch { /* best-effort detach */ }
    }
}

/// <summary>
/// Phase 17 Plan 03 — eager-start shim that resolves <see cref="UpdateToastBroadcaster"/> from DI
/// in <c>StartAsync</c>. The singleton's constructor subscribes to
/// <see cref="UpdateNotifier.UpdateAvailable"/>, but singletons are constructed lazily on first
/// resolve; without this shim a session with no UI activity would never instantiate the
/// broadcaster and miss the discovery toast. Mirrors
/// <see cref="EventDetectionEagerStartHostedService"/> exactly.
/// </summary>
public sealed class UpdateToastBroadcasterEagerStartHostedService : IHostedService
{
    private readonly UpdateToastBroadcaster _svc;
    public UpdateToastBroadcasterEagerStartHostedService(UpdateToastBroadcaster svc) => _svc = svc;
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;   // resolution alone is sufficient
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
