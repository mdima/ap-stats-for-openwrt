using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Polling;

namespace ApStatsForOpenWrt.Services;

/// <summary>
/// Phase 8 D-08 / D-11 — Singleton snapshot-diff detector that subscribes to
/// <see cref="PollNotifier.Cycled"/> and emits four toast categories per the LOCKED
/// contract from 08-CONTEXT.md and 08-UI-SPEC.md §Toast copy templates per category:
/// <list type="bullet">
///   <item>AP→Offline (warning, FIRST cycle the AP is observed Offline — D-04, no debounce).</item>
///   <item>AP→Online  (success, EVERY Offline→Online transition — D-05, symmetric).</item>
///   <item>New MAC    (info, MAC not in the bootstrap set — D-06).</item>
///   <item>Persistent failure (danger, EXACTLY 3 consecutive fails — D-07; counter resets on any successful cycle).</item>
/// </list>
///
/// <para>
/// <b>D-10 defense-in-depth gate.</b> This service early-exits BEFORE the snapshot diff
/// when <see cref="INotificationsGate.IsEnabled"/> is <c>false</c> ("if nobody watches,
/// do no work"). Both this service AND <see cref="ToastService"/> gate, so any caller
/// path is covered.
/// </para>
///
/// <para>
/// <b>HARD CONSTRAINT (PER-04 / CLAUDE.md orchestrator-only-writes).</b> This service
/// NEVER calls any DB write method (no SaveChanges, no ExecuteUpdate, no ExecuteDelete)
/// and NEVER injects a DbContext factory. The bootstrap MAC scan via
/// <see cref="IStationRepo.GetAllMacsAsync"/> is READ-ONLY. The architectural firewall
/// (ArchitectureTests.DbContext_IsOnlyUsedByRepositories) is upheld because this service
/// reaches the repos through <see cref="IServiceScopeFactory"/> rather than the EF Core
/// factory abstraction directly.
/// </para>
///
/// <para>
/// <b>D-06 in-memory MAC bootstrap.</b> <see cref="WLANStation"/> has no <c>FirstSeenAt</c>
/// column, and Phase 8 is hard-blocked from schema changes. The closest faithful
/// approximation to "MAC never recorded in DB" is therefore: on the FIRST <c>Cycled</c>
/// event, query the current MAC set via <see cref="IStationRepo.GetAllMacsAsync"/> and
/// seed an in-memory <see cref="HashSet{T}"/>. A MAC observed in any subsequent cycle
/// that is not in the set is "new". Effective semantics = "never in DB at process startup,
/// and not seen since". A UI restart "forgets" recently-toasted-as-new MACs — this is
/// documented and accepted (D-06 caveat); the alternative would be a schema change which
/// Phase 8 explicitly forbids.
/// </para>
/// </summary>
public sealed class EventDetectionService : IDisposable
{
    /// <summary>D-07 — toast fires on EXACTLY this consecutive-failure count for a given AP.</summary>
    private const int FailureToastThreshold = 3;

    private readonly PollNotifier _notifier;
    private readonly ToastService _toasts;
    private readonly INotificationsGate _gate;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EventDetectionService> _log;

    // Snapshot state — protected by SemaphoreSlim because Cycled handlers are async and
    // the next-cycle event could race the previous one's diff under tight test timing.
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>D-04 / D-05 — last observed status per node, keyed by <see cref="WiFiNode.Id"/>.</summary>
    private readonly Dictionary<int, Status> _lastNodeStatus = new();

    /// <summary>D-07 — consecutive-failure counter per node. Reset to 0 on any successful cycle.</summary>
    private readonly Dictionary<int, int> _failureCounters = new();

    /// <summary>D-06 — known MAC set, seeded lazily on the first cycle.</summary>
    private readonly HashSet<string> _knownMacs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>D-06 — lazy seeding gate. Bootstrap runs exactly once.</summary>
    private bool _bootstrapped;

    public EventDetectionService(
        PollNotifier notifier,
        ToastService toasts,
        INotificationsGate gate,
        IServiceScopeFactory scopes,
        ILogger<EventDetectionService> log)
    {
        _notifier = notifier;
        _toasts = toasts;
        _gate = gate;
        _scopes = scopes;
        _log = log;

        // D-08 — subscribe in constructor; Singleton lifetime owns the subscription.
        // Handler signature must match Func<PollSnapshot, Task>? exactly (Pitfall 7 — never async void).
        _notifier.Cycled += OnCycledAsync;
    }

    /// <summary>
    /// PollNotifier.Cycled handler. Wrapped in try/catch so a single bad cycle does not
    /// detach the subscription. D-11 work order:
    ///   1) D-10 gate check (defense-in-depth — skips ALL work, not just emission).
    ///   2) D-06 lazy bootstrap on first cycle.
    ///   3) Read current nodes + stations via a service scope (READ-ONLY).
    ///   4) Diff and emit toasts.
    /// </summary>
    private async Task OnCycledAsync(PollSnapshot snapshot)
    {
        // D-10 — early-exit BEFORE acquiring the lock or doing any work. This is the
        // BRIEF's "if nobody watches, do no work" optimization (per 08-CONTEXT.md §specifics).
        if (!_gate.IsEnabled) return;

        await _lock.WaitAsync();
        try
        {
            using var scope = _scopes.CreateScope();
            var stationRepo = scope.ServiceProvider.GetRequiredService<IStationRepo>();
            var nodeRepo    = scope.ServiceProvider.GetRequiredService<INodeRepo>();

            // D-06 — bootstrap MAC set on the FIRST Cycled, not in the constructor.
            // Run inside the gate so a session that starts with notifications OFF doesn't
            // pre-fetch the MACs uselessly (D-10 holds for the bootstrap too).
            if (!_bootstrapped)
            {
                await BootstrapKnownMacsAsync(stationRepo);
                _bootstrapped = true;
                // After bootstrap, the FIRST cycle's stations are NOT toasted as new
                // (they are already in DB, hence in the seeded set).
            }

            // PollSnapshot only carries counts — query the repos for per-row detail.
            var nodes    = await nodeRepo.GetAllAsync(CancellationToken.None);
            var stations = await stationRepo.GetAllAsync(CancellationToken.None);

            await DiffAndEmitAsync(nodes, stations);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EventDetectionService cycle handling failed");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task BootstrapKnownMacsAsync(IStationRepo repo)
    {
        try
        {
            var macs = await repo.GetAllMacsAsync(CancellationToken.None);
            foreach (var m in macs)
            {
                _knownMacs.Add(m);
            }
            _log.LogInformation("EventDetectionService bootstrapped {Count} known MACs", _knownMacs.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EventDetectionService MAC bootstrap failed; new-station toasts may be noisy until next restart");
        }
    }

    private async Task DiffAndEmitAsync(IReadOnlyList<WiFiNode> nodes, IReadOnlyList<WLANStation> stations)
    {
        // D-04 / D-05 — per-node status transitions + D-07 consecutive-failure counter.
        foreach (var node in nodes)
        {
            _lastNodeStatus.TryGetValue(node.Id, out var prev);
            var hadPrev = _lastNodeStatus.ContainsKey(node.Id);

            // D-04 — first cycle Offline (Online/Polling/missing → Offline).
            if (node.Status == Status.Offline && (!hadPrev || prev != Status.Offline))
            {
                await _toasts.Show(
                    "Access point offline",
                    $"`{node.Hostname}` is no longer reachable. Last seen {RelativeTime(node.LastPollDate)}.",
                    "warning");
            }
            // D-05 — every Offline → Online transition. Symmetric — paired semantics REJECTED.
            else if (hadPrev && prev == Status.Offline && node.Status == Status.Online)
            {
                await _toasts.Show(
                    "Access point online",
                    $"`{node.Hostname}` is reachable again. Polling resumed.",
                    "success");
            }

            _lastNodeStatus[node.Id] = node.Status;

            // D-07 — persistent-failure counter. Status==Offline = the orchestrator's
            // encoding of "poll failed" (no separate PollSucceeded boolean exists).
            var pollFailed = node.Status == Status.Offline;
            if (!pollFailed)
            {
                _failureCounters[node.Id] = 0;
            }
            else
            {
                var prevCount = _failureCounters.GetValueOrDefault(node.Id);
                var newCount = prevCount + 1;
                _failureCounters[node.Id] = newCount;
                // EXACTLY 3 — first crossing only; no re-toast on 4, 5, ...
                if (prevCount < FailureToastThreshold && newCount == FailureToastThreshold)
                {
                    await _toasts.Show(
                        "Polling failure",
                        $"`{node.Hostname}` has failed {newCount} consecutive polls. Check connectivity or credentials.",
                        "danger");
                }
            }
        }

        // D-06 — new-MAC detection. The bootstrap set was seeded on the first cycle (above);
        // any MAC not in it now is genuinely new. Set is updated post-toast (HashSet.Add returns
        // true if the element was added). The 08-UI-SPEC template references SSID + AP hostname;
        // resolving those would require a second repo round-trip and the WiFiNetwork join. To keep
        // the hot path lean we degrade to the simpler "appeared on the network" copy. (A future
        // phase may extend the toast to include the resolved SSID/AP — out of scope for v1.)
        foreach (var station in stations)
        {
            if (_knownMacs.Add(station.MacAddress))
            {
                await _toasts.Show(
                    "New station",
                    $"{DisplayName(station)} (`{station.MacAddress}`) appeared on the network.",
                    "info");
            }
        }
    }

    private static string DisplayName(WLANStation s) =>
        !string.IsNullOrWhiteSpace(s.Name)     ? s.Name! :
        !string.IsNullOrWhiteSpace(s.Hostname) ? s.Hostname! :
        !string.IsNullOrWhiteSpace(s.IPAddress) ? s.IPAddress! :
        "Unknown";

    private static string RelativeTime(DateTime when)
    {
        if (when == default) return "unknown time";
        var when_utc = when.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(when, DateTimeKind.Utc)
            : when.ToUniversalTime();
        var elapsed = DateTime.UtcNow - when_utc;
        if (elapsed.TotalSeconds < 0) elapsed = TimeSpan.Zero;
        return elapsed.TotalMinutes < 1
            ? $"{(int)elapsed.TotalSeconds}s ago"
            : $"{(int)elapsed.TotalMinutes}m ago";
    }

    /// <summary>
    /// Quick task 260511-cmi — drop a MAC from the in-memory "ever seen" set so a future
    /// re-association of the same device fires the new-station info toast (D-06 first-time
    /// semantics). Called by <c>Stations/Detail.razor</c>'s <c>HandleDeleteAsync</c> before
    /// <see cref="IStationRepo.DeleteByMacAsync"/>; without this, an operator-deleted MAC
    /// stays shadow-known in <c>_knownMacs</c> until app restart and the rebid is silently
    /// reclassified as already-seen. No-op when the MAC was never bootstrapped or already
    /// forgotten (<see cref="HashSet{T}.Remove"/> returns false but does not throw).
    /// Lock-acquired to serialize against an in-flight <see cref="OnCycledAsync"/> diff.
    /// </summary>
    public void ForgetMac(string mac)
    {
        if (string.IsNullOrEmpty(mac)) return;
        _lock.Wait();
        try
        {
            _knownMacs.Remove(mac);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        try { _notifier.Cycled -= OnCycledAsync; } catch { /* best-effort detach */ }
        _lock.Dispose();
    }
}

/// <summary>
/// Phase 8 D-11 — eager-start hosted service that resolves <see cref="EventDetectionService"/>
/// from DI in <c>StartAsync</c>. The Singleton's constructor subscribes to
/// <see cref="PollNotifier.Cycled"/>, but Singletons are constructed lazily on first
/// resolve; without this shim, a session with no UI activity would never instantiate
/// the detector and miss every poll cycle.
/// </summary>
public sealed class EventDetectionEagerStartHostedService : IHostedService
{
    private readonly EventDetectionService _svc;
    public EventDetectionEagerStartHostedService(EventDetectionService svc) => _svc = svc;
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;   // resolution alone is sufficient
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
