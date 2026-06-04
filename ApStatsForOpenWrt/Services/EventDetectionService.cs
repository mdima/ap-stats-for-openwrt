using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Gating;
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
/// <b>D-10 / Phase 18 D-03 (OQ7 AMENDED) emission gate.</b> Snapshot-diff state tracking
/// (<see cref="_lastNodeStatus"/>, <see cref="_failureCounters"/>, <see cref="_knownMacs"/>)
/// now runs on EVERY <c>Cycled</c> event so the D-07 consecutive-failure counter counts
/// ACTUAL consecutive polls and no station seen while no one was watching is re-toasted as
/// "new" on reconnect. Only the four <see cref="ToastService.Show"/> emission sites are gated,
/// on <c>(_gate.IsEnabled &amp;&amp; hasVisitor)</c>: a toast fires only when notifications are
/// on (<see cref="INotificationsGate.IsEnabled"/>) AND an operator is connected
/// (<see cref="CircuitTracker.Current"/> == <see cref="GateState.Live"/>, respecting the
/// 260518-gpd 30 s pause-grace). This SUPERSEDES the charter's stale OQ7 "skip all work" gate.
/// Both this service AND <see cref="ToastService"/> gate on <see cref="INotificationsGate"/>,
/// so any caller path is covered.
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
    private readonly CircuitTracker _tracker;
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
        CircuitTracker tracker,
        IServiceScopeFactory scopes,
        ILogger<EventDetectionService> log)
    {
        _notifier = notifier;
        _toasts = toasts;
        _gate = gate;
        // Phase 18 D-03 — concrete CircuitTracker singleton (Program.cs:548) for the strict
        // visitor signal. NOT the ICircuitGate / HistoryAwareCircuitGate decorator (it reports
        // Live under continuous history-on polling, which would defeat this toast gate — D-03
        // forbidden). Singleton→singleton injection is captive-dependency-safe.
        _tracker = tracker;
        _scopes = scopes;
        _log = log;

        // D-08 — subscribe in constructor; Singleton lifetime owns the subscription.
        // Handler signature must match Func<PollSnapshot, Task>? exactly (Pitfall 7 — never async void).
        _notifier.Cycled += OnCycledAsync;
    }

    /// <summary>
    /// PollNotifier.Cycled handler. Wrapped in try/catch so a single bad cycle does not
    /// detach the subscription. Phase 18 D-03 (OQ7 AMENDED) work order:
    ///   1) D-06 lazy bootstrap on first cycle (UNCONDITIONAL — runs even with no visitor /
    ///      notifications off so the known-MAC set stays honest from process start).
    ///   2) Read current nodes + stations via a service scope (READ-ONLY).
    ///   3) Compute <c>hasVisitor</c> from <see cref="CircuitTracker.Current"/>.
    ///   4) Diff (state mutation UNCONDITIONAL) and emit toasts (gated on
    ///      <c>_gate.IsEnabled &amp;&amp; hasVisitor</c>).
    /// There is intentionally NO early-return on the notifications/visitor gate: the charter's
    /// OQ7 "skip all work" behaviour is SUPERSEDED (D-03). State tracking must run every cycle.
    /// </summary>
    private async Task OnCycledAsync(PollSnapshot snapshot)
    {
        await _lock.WaitAsync();
        try
        {
            using var scope = _scopes.CreateScope();
            var stationRepo = scope.ServiceProvider.GetRequiredService<IStationRepo>();
            var nodeRepo    = scope.ServiceProvider.GetRequiredService<INodeRepo>();

            // D-06 — bootstrap MAC set on the FIRST Cycled, not in the constructor.
            // Phase 18 D-03: this runs UNCONDITIONALLY on the first cycle (no longer behind a
            // notifications/visitor gate) so the known-MAC set is seeded from process start —
            // a station first seen while no one was watching is therefore NOT re-toasted as
            // "new" when an operator later connects.
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

            // Phase 18 D-03 — strict visitor signal from the concrete tracker (30 s pause-grace
            // respected transparently). Computed once per cycle and passed into the diff; only
            // the toast emission depends on it, never the state mutation.
            bool hasVisitor = _tracker.Current == GateState.Live;

            await DiffAndEmitAsync(nodes, stations, hasVisitor);
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

    private async Task DiffAndEmitAsync(IReadOnlyList<WiFiNode> nodes, IReadOnlyList<WLANStation> stations, bool hasVisitor)
    {
        // Phase 18 D-03 (OQ7 AMENDED) — the emission gate. A toast fires ONLY when notifications
        // are enabled AND an operator is connected. All state mutations below stay UNCONDITIONAL
        // so the D-07 counter counts actual consecutive polls and the known-MAC set stays honest.
        bool canEmit = _gate.IsEnabled && hasVisitor;

        // D-04 / D-05 — per-node status transitions + D-07 consecutive-failure counter.
        foreach (var node in nodes)
        {
            _lastNodeStatus.TryGetValue(node.Id, out var prev);
            var hadPrev = _lastNodeStatus.ContainsKey(node.Id);

            // D-04 — first cycle Offline (Online/Polling/missing → Offline).
            if (node.Status == Status.Offline && (!hadPrev || prev != Status.Offline))
            {
                if (canEmit)
                {
                    await _toasts.Show(
                        "Access point offline",
                        $"`{node.Hostname}` is no longer reachable. Last seen {RelativeTime(node.LastPollDate)}.",
                        "warning");
                }
            }
            // D-05 — every Offline → Online transition. Symmetric — paired semantics REJECTED.
            else if (hadPrev && prev == Status.Offline && node.Status == Status.Online)
            {
                if (canEmit)
                {
                    await _toasts.Show(
                        "Access point online",
                        $"`{node.Hostname}` is reachable again. Polling resumed.",
                        "success");
                }
            }

            _lastNodeStatus[node.Id] = node.Status;

            // D-07 — persistent-failure counter. Status==Offline = the orchestrator's
            // encoding of "poll failed" (no separate PollSucceeded boolean exists).
            // The counter increment/reset is UNCONDITIONAL (Phase 18 D-03): it counts actual
            // consecutive polls regardless of visitor presence. Only the toast is gated.
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
                // The threshold crossing is computed unconditionally; a crossing that happens
                // while no visitor is present is simply NOT toasted (and never re-toasted later,
                // since prevCount is already >= threshold by the time a visitor connects).
                if (prevCount < FailureToastThreshold && newCount == FailureToastThreshold)
                {
                    if (canEmit)
                    {
                        await _toasts.Show(
                            "Polling failure",
                            $"`{node.Hostname}` has failed {newCount} consecutive polls. Check connectivity or credentials.",
                            "danger");
                    }
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
            // Phase 18 D-03 — _knownMacs.Add runs UNCONDITIONALLY so the MAC is recorded the
            // first time it is seen, EVEN if no operator is watching. This is precisely what
            // prevents a station seen-while-away from being re-toasted as "new" on reconnect:
            // by the time a visitor connects, the MAC is already in the set, so Add returns false.
            if (_knownMacs.Add(station.MacAddress))
            {
                if (canEmit)
                {
                    await _toasts.Show(
                        "New station",
                        $"{DisplayName(station)} (`{station.MacAddress}`) appeared on the network.",
                        "info");
                }
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
