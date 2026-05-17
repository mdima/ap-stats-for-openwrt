using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Enrichment;
using ApStatsForOpenWrt.Services.Polling.Exceptions;
using ApStatsForOpenWrt.Services.Settings;

namespace ApStatsForOpenWrt.Services.Polling;

/// <summary>
/// Phase 2 PER-04 carve-out #3 (after <see cref="DatabaseContext"/> itself and
/// <c>MigrationsHostedService</c>): the only non-repository class permitted to inject
/// <see cref="IDbContextFactory{TContext}"/> directly. <c>ArchitectureTests.DbContext_IsOnlyUsedByRepositories</c>
/// adds this type's full name to its allowed list (Plan 02-03 Task 1 amendment).
/// <para>
/// Owns the per-cycle coordinator transaction (D-04). Composes caller-owned repo overloads
/// inside one <c>BeginTransactionAsync ... CommitAsync</c>. Workers (<see cref="NodePoller"/>)
/// return POCO <see cref="NodeProbeResult"/> and never touch the DB (POLL-07). Mark-all-Offline
/// (Node + Station only; <see cref="WiFiNetwork"/> has no Status field — RESEARCH §Repository
/// Write Surface row 3 rationale) + per-node upsert pass + dedup (D-08) + SaveChangesAsync +
/// CommitAsync.
/// </para>
/// <para>
/// B3 LOCK: takes <c>Func&lt;WiFiNode, CancellationToken, Task&lt;NodeProbeResult&gt;&gt;</c>
/// (named <c>probeAsync</c>), NOT <c>Func&lt;WiFiNode, NodePoller&gt;</c> — keeps the orchestrator
/// unit-testable without instantiating <see cref="NodePoller"/>.
/// </para>
/// </summary>
public sealed class PollOrchestrator(
    IDbContextFactory<DatabaseContext> factory,
    INodeRepo nodeRepo,
    INetworkRepo networkRepo,
    IStationRepo stationRepo,
    Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeAsync,
    PollNotifier notifier,
    ILogger<PollOrchestrator> log,
    IAppSettingsService settings,
    Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> enrichmentFactory,
    TimeProvider timeProvider,
    IForceEnrichmentService forceEnrichment)
{
    // D-09 — lock-free LastSuccessfulCycleUtc exposure for /health (plan 06-03 reads this).
    // Volatile.Read<T> requires primitive/reference type; DateTime (struct) is NOT eligible —
    // store DateTime.Ticks in a long instead. Mirrors the AppSettingsService.cs lock-free
    // typed-snapshot pattern (Plan 01-03), adapted to a single primitive value. Default = 0
    // (== DateTime.MinValue.Ticks); read via Volatile.Read; written ONLY after successful
    // tx.CommitAsync. Volatile.Read<long> is a torn-free 64-bit load on both 32-bit and 64-bit
    // runtimes, so concurrent reads from the /health pipeline never observe a half-written
    // Ticks value while a poll cycle is committing.
    private long _ticks;

    /// <summary>
    /// UTC timestamp of the most recently committed coordinator transaction. Returns
    /// <see cref="DateTime.MinValue"/> (kind=Utc) before the first successful cycle. Read
    /// non-blocking via <see cref="Volatile.Read(ref long)"/> so the /health endpoint never
    /// serializes against poll cycles. The /health JSON writer in plan 06-03 maps the
    /// MinValue case to <c>lastPollAge: null</c> per D-07.
    /// </summary>
    public DateTime LastSuccessfulCycleUtc
        => new(Volatile.Read(ref _ticks), DateTimeKind.Utc);

    // 14-03 / AUDIT-01 (CONTEXT 14 §D-15 + §D-16): monotonic cycle sequence counter — used as a
    // Serilog scoped property so a first-cycle skip pattern is legible against subsequent-cycle
    // behaviour in the rolling log file. Process-lifetime only; reset on host restart.
    // Interlocked-incremented at the top of RunCycleAsync. First-cycle == 1.
    private static long _cycleSequenceCounter;

    public async Task RunCycleAsync(CancellationToken ct)
    {
        // 14-03 / AUDIT-01 — cycle-scoped instrumentation. The Serilog scope captures CycleSequence
        // (1, 2, 3, ...) on every log line emitted inside this method. First-cycle == 1; legible in
        // /logs/log-*.txt as `... CycleSequence=1 ...`. Per-station Debug skip-reason logs + the
        // cycle-level Information summary line below feed the AUDIT-02 memo (D-15).
        var cycleSeq = Interlocked.Increment(ref _cycleSequenceCounter);
        using var logScope = log.BeginScope(new Dictionary<string, object> { ["CycleSequence"] = cycleSeq });

        // 14-01 / CONTEXT 14 §D-02: fire CycleStarted at the TOP of the cycle — BEFORE BeginTransactionAsync
        // and BEFORE any await that might throw. PollCountdown.razor reads this and holds the badge text at
        // "Polling…" (D-01) until the next successful tx.CommitAsync raises Cycled (line ~365, UNCHANGED).
        // CONTEXT 14 §D-03: try/catch/finally is NOT extended; a thrown exception below leaves the badge
        // held until the next successful Cycled — operator-authorised passive failure signal.
        // Plan 14-02's "Run now" button disable-state (CONTEXT 14 §D-06) also consumes this event for the
        // cycle-in-flight OR condition.
        await notifier.RaiseCycleStartedAsync();

        var nodes = await nodeRepo.GetAllAsync(ct);
        if (nodes.Count == 0)
        {
            log.LogDebug("Poll cycle: 0 nodes configured, skipping");
            return;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        try
        {
            // 1. Mark-all-Offline (D-04 step 1). Node + Station only — WiFiNetwork has no Status field.
            await nodeRepo.MarkAllOfflineAsync(db, ct);
            await stationRepo.MarkAllOfflineAsync(db, ct);

            // 2. Fan out (D-04 step 2). Each probeAsync returns a POCO; HostKeyMismatchException
            //    is caught here (RunNodeProbeAsync) and translated to NodeProbeResult.Failed.
            var pollTasks = nodes.Select(n => RunNodeProbeAsync(n, ct)).ToArray();
            var results = await Task.WhenAll(pollTasks);

            // 3. First-connect TOFU persistence INSIDE the transaction (D-02).
            //    B10 LOCK: collect the audit-log tuples here; emit AFTER tx.CommitAsync succeeds
            //    (step 6.5 below) so the log faithfully reflects what actually persisted.
            var newlyTrusted = new List<(int NodeId, string Hostname, string Fingerprint)>();
            foreach (var r in results.OfType<NodeProbeResult.Success>())
            {
                if (r.NewlyTrustedFingerprint is { } fp)
                {
                    var nodeForLog = nodes.First(n => n.Id == r.NodeId);
                    newlyTrusted.Add((r.NodeId, nodeForLog.Hostname, fp));
                    await nodeRepo.SetHostKeyFingerprintAsync(db, r.NodeId, fp, ct);
                }
            }

            // 4. Per-surviving-node persist pass for networks (so DB-assigned WiFiNetwork.Id values
            //    become available to step 5's BSSID->Id resolution).
            var now = DateTime.UtcNow;
            foreach (var r in results.OfType<NodeProbeResult.Success>())
            {
                // Step 4a: upsert the networks the probe reported as present.
                await networkRepo.UpsertManyAsync(db, r.NodeId, r.Networks, ct);

                // Step 4b: hard-delete WiFiNetworks for this node whose BSSID was NOT in the probe.
                // Runs AFTER UpsertManyAsync so newly-discovered BSSIDs are not caught as "absent" on
                // their first cycle. Runs BEFORE step 5's BSSID-resolution pass so deleted ids cannot
                // appear in the bssidLookup dictionary. Inside the same try {} as the rest of the cycle
                // → D-04 atomicity (a failure rolls back upsert + delete + station updates together).
                // Per-success-only by construction: failed-probe nodes never enter this loop, so a
                // transient SSH failure cannot wipe a node's WLANs.
                var presentMacs = r.Networks.Select(n => n.MacAddress).ToList();
                await networkRepo.DeleteAbsentAsync(db, r.NodeId, presentMacs, ct);

                await nodeRepo.UpdateOnlineAsync(db, r.NodeId, now, ct);
            }

            // 5. Flush so SQLite assigns WiFiNetwork.Id values for new rows; then we can
            //    resolve station Bssid -> WiFiNetworkId before the dedup + station upsert.
            await db.SaveChangesAsync(ct);

            // ─── Phase 10 ORCH-01..08 — composite refresh-trigger evaluation + enrichmentFactory ───
            // Inside the coordinator transaction. ORCH-07 invariant: LastEnrichmentRefreshAt is updated
            // here, in the same try{} so a failure rolls back together with the upsert.
            var enrichmentNow = timeProvider.GetUtcNow();
            var refreshIntervalSeconds = settings.RefreshIntervalSeconds;

            // Always full-fetch MACs + IP/Hostname projection for every success node.
            //
            // Debug session p10-trigger-and-nslookup (Bug 2): an earlier "fast-path" branch
            // skipped this query when no node's time-based interval was eligible, falling back
            // to a cheap MAC-only projection with an EMPTY existingStationsByNode per node. When
            // the new-MAC arm later fired the trigger inside that same cycle (a perfectly valid
            // path — newMacDetected does NOT require the time-based arm to also be eligible),
            // the ipsForDns "existing-stations" branch below (line ~248-254) iterated an empty
            // list. Combined with iwinfo never reporting IPAddress on r.Stations, ipsForDns
            // ended up empty, ReverseDnsProbe early-returned with an empty result map, and
            // every post-cold-start cycle silently lost hostname enrichment. The cost saved
            // (~4 columns × <100 rows per cycle = a few hundred bytes per cycle) was a bad
            // trade against silent enrichment loss.
            var successNodes = results.OfType<NodeProbeResult.Success>().ToList();
            var successNodeIds = successNodes.Select(r => r.NodeId).ToHashSet();
            var allExistingForCycle = successNodeIds.Count == 0
                ? new List<(int WiFiNodeId, string MacAddress, string? IPAddress, string? Hostname)>()
                : (await db.WLANStations.AsNoTracking()
                    .Where(s => s.Network != null && successNodeIds.Contains(s.Network.WiFiNodeId))
                    .Select(s => new
                    {
                        WiFiNodeId = s.Network!.WiFiNodeId,
                        s.MacAddress,
                        s.IPAddress,
                        s.Hostname
                    })
                    .ToListAsync(ct))
                    .Select(x => (x.WiFiNodeId, x.MacAddress, x.IPAddress, x.Hostname))
                    .ToList();

            var existingMacsByNode = allExistingForCycle
                .GroupBy(x => x.WiFiNodeId)
                .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(x => x.MacAddress), StringComparer.Ordinal));
            var existingStationsByNode = allExistingForCycle
                .GroupBy(x => x.WiFiNodeId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var nid in successNodeIds)
            {
                if (!existingMacsByNode.ContainsKey(nid)) existingMacsByNode[nid] = new HashSet<string>(StringComparer.Ordinal);
                if (!existingStationsByNode.ContainsKey(nid)) existingStationsByNode[nid] = new List<(int, string, string?, string?)>();
            }

            // 14-02 / ENRICH-01 (CONTEXT 14 §D-05) — force-enrichment flag capture-and-clear.
            // Done ONCE per cycle at the block entry so the CAS happens exactly once even if a
            // Request() races with a same-cycle consumption. Scope (D-05): bypass the time-based
            // gate for ALL online nodes. SubnetScanEnabled (per-node operator opt-out) is still
            // honored — D-05's "all online nodes" reads as "all online nodes the operator has not
            // opted out of" (CONTEXT.md D-05 §"Force-flag scope", updated 2026-05-16 in response
            // to plan-checker Blocker 4). An operator who set SubnetScanEnabled=false on a node
            // explicitly excluded it from active enrichment regardless of cadence, consistent
            // with EnrichmentRefreshTrigger.cs line 20 early-return.
            var forceConsumed = forceEnrichment.ConsumeIfSet();
            if (forceConsumed)
            {
                log.LogInformation("Force-enrichment flag consumed; bypassing time-based gate for all SubnetScan-enabled online nodes this cycle.");
            }

            // 14-03 / AUDIT-01 (CONTEXT 14 §D-15) — per-cycle enrichment counters. Aggregated at the
            // end of the enrichment loop and emitted as a single Information-level summary line so
            // operator-deployed rolling logs surface the skip pattern without per-station spew at
            // default log level. Per-station Debug entries below distinguish four named skip reasons.
            int enrichmentObserved = 0;
            int enrichmentEnriched = 0;
            int enrichmentSkippedSubnetOptOut = 0;
            int enrichmentSkippedTimeGate = 0;
            int enrichmentNullDelta = 0;
            int enrichmentFactoryThrew = 0;

            foreach (var r in results.OfType<NodeProbeResult.Success>())
            {
                var node = nodes.First(n => n.Id == r.NodeId);
                var observedMacs = r.Stations.Select(s => s.MacAddress).ToHashSet(StringComparer.Ordinal);
                var existingMacs = existingMacsByNode[r.NodeId];
                var newMacDetected = observedMacs.Any(m => !existingMacs.Contains(m));

                // 14-03 / AUDIT-01 — observed station count for this node (basis for the
                // first-cycle coverage ratio in the AUDIT-04 reference fixture).
                enrichmentObserved += r.Stations.Count;

                // 14-02 / D-05 — combined gate: (force AND opted-in) OR time-based trigger.
                // D-07 cadence-from-completion is preserved unchanged: UpdateLastEnrichmentRefreshAtAsync
                // at line ~284 below bumps LastEnrichmentRefreshAt per-node in BOTH the natural and
                // forced paths, so the post-force time-based gate resumes counting from completion.
                var shouldRefresh = (forceConsumed && node.SubnetScanEnabled)
                                 || EnrichmentRefreshTrigger.ShouldRefresh(node, enrichmentNow, refreshIntervalSeconds, newMacDetected);
                if (!shouldRefresh)
                {
                    // 14-03 / AUDIT-01 — distinguish the two skip reasons. SubnetScanEnabled=false is
                    // the per-node operator opt-out (EnrichmentRefreshTrigger.cs:20 early-return); the
                    // alternative "time-gate not eligible" arm is the LastEnrichmentRefreshAt + interval
                    // gate. Different operator-actionable signals — collapsing them under one reason
                    // would hide the opt-out class in the AUDIT-02 memo evidence.
                    if (!node.SubnetScanEnabled)
                    {
                        enrichmentSkippedSubnetOptOut += r.Stations.Count;
                        log.LogDebug(
                            "AUDIT-01: skip enrichment for node {NodeId} ({Hostname}); reason=subnet-scan-disabled; stations={StationCount}",
                            node.Id, node.Hostname, r.Stations.Count);
                    }
                    else
                    {
                        enrichmentSkippedTimeGate += r.Stations.Count;
                        log.LogDebug(
                            "AUDIT-01: skip enrichment for node {NodeId} ({Hostname}); reason=time-gate-not-eligible; LastEnrichmentRefreshAt={LastRefresh}; refreshIntervalSeconds={Interval}; newMacDetected={NewMac}; stations={StationCount}",
                            node.Id, node.Hostname, node.LastEnrichmentRefreshAt, refreshIntervalSeconds, newMacDetected, r.Stations.Count);
                    }
                    continue;
                }

                // Pass node.Hostname through as the peer identifier; the enrichmentFactory is
                // responsible for resolving (or directly using) it inside its SSH session context.
                // Putting DNS resolution in the orchestrator would block the per-cycle wall-clock
                // budget for nodes whose hostnames don't resolve quickly. The factory already opens
                // an SSH connection to node.Hostname (NodePoller pattern), so resolution naturally
                // happens there with the SSH layer's own timeout.
                var peerIp = node.Hostname;

                // Build the IP set that needs reverse DNS:
                //   - Stations from the probe whose IP changed since persistence OR whose persisted
                //     Hostname is null → ORCH-05.
                //   - Stations already persisted on this node where Hostname IS NULL and an IP exists
                //     (ORCH-08 retry-on-next-tick).
                var existingStationsForNode = existingStationsByNode[r.NodeId];
                var existingByMacForNode = existingStationsForNode
                    .ToDictionary(x => x.MacAddress, x => (x.IPAddress, x.Hostname), StringComparer.Ordinal);

                var ipsForDns = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in r.Stations)
                {
                    if (string.IsNullOrEmpty(s.IPAddress)) continue;
                    if (existingByMacForNode.TryGetValue(s.MacAddress, out var persisted))
                    {
                        if (persisted.Hostname is null || persisted.IPAddress != s.IPAddress)
                        {
                            ipsForDns.Add(s.IPAddress);
                        }
                    }
                    else
                    {
                        ipsForDns.Add(s.IPAddress);
                    }
                }
                foreach (var ex in existingStationsForNode)
                {
                    if (ex.Hostname is null && !string.IsNullOrEmpty(ex.IPAddress))
                    {
                        ipsForDns.Add(ex.IPAddress);
                    }
                }

                EnrichmentDelta? delta;
                try
                {
                    delta = await enrichmentFactory(node, ipsForDns.ToList(), peerIp, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 14-03 / AUDIT-01 — factory-threw skip reason. Logs at Warning (the original
                    // shape) so production deployments default to surfacing this; the AUDIT-01
                    // tagged message is the structured form for the audit memo.
                    enrichmentFactoryThrew++;
                    log.LogWarning(ex,
                        "AUDIT-01: enrichment-factory threw for node {NodeId} ({Hostname}); stations on this node skipped this cycle; reason=factory-threw; stations={StationCount}",
                        node.Id, node.Hostname, r.Stations.Count);
                    continue;
                }
                if (delta is null)
                {
                    // 14-03 / AUDIT-01 — null-delta skip reason. The factory returned null
                    // (intentional no-op) rather than throwing; stations on this node still get
                    // skipped this cycle.
                    enrichmentNullDelta++;
                    log.LogDebug(
                        "AUDIT-01: enrichment-factory returned null delta for node {NodeId} ({Hostname}); stations on this node skipped this cycle; reason=null-delta; stations={StationCount}",
                        node.Id, node.Hostname, r.Stations.Count);
                    continue;
                }

                // Merge delta into r.Stations BEFORE the dedup pass below:
                //   - Fill missing IP from delta.SweepMacToIp[mac] (ORCH-04 DB-as-cache cascade).
                //   - For stations with an IPAddress, look up delta.ReverseDnsResolved[ip] and fill
                //     Hostname (null delta value = NXDOMAIN/timeout — UpsertCycleAsync's `?? existingRow.Hostname`
                //     coalesce preserves any existing Hostname).
                foreach (var s in r.Stations)
                {
                    // 14-03 / AUDIT-01 — capture pre-merge state for the per-station enrichment
                    // count. A station "got enriched this cycle" iff it gained an IP or Hostname
                    // it did not previously have. The DB-as-cache reload from existingByMacForNode
                    // counts as enrichment for AUDIT-04 coverage purposes (the operator's UI sees
                    // a populated value either way).
                    var hadIpBefore = !string.IsNullOrEmpty(s.IPAddress);
                    var hadHostnameBefore = !string.IsNullOrEmpty(s.Hostname);

                    if (string.IsNullOrEmpty(s.IPAddress) && delta.SweepMacToIp.TryGetValue(s.MacAddress, out var sweepIp))
                    {
                        s.IPAddress = sweepIp;
                    }
                    // Debug session p10-trigger-and-nslookup (third-order fix, after 87bf42f closed
                    // the fast-path-zeroed-existingStationsByNode trap): if neither ip-neigh
                    // (NodePoller.cs:128) nor the arping sweep gave us an IP for this MAC, fall
                    // back to the persisted IP from existingByMacForNode. The ipsForDns set above
                    // already includes this persisted IP via the line-218-224 existing-stations
                    // branch, and the dns probe may have successfully resolved it — but without
                    // this fallback the next `if` keys off s.IPAddress (still null), the dnsMap
                    // lookup misses, and the resolved hostname is dropped on the floor. Runtime
                    // evidence: operator deployment, 5 consecutive enrichment cycles resolving
                    // 13-14 IPs each, DB hostname count stuck at 0/41 despite the line-86 Reverse-DNS
                    // pass log line firing on every cycle.
                    if (string.IsNullOrEmpty(s.IPAddress) && existingByMacForNode.TryGetValue(s.MacAddress, out var persistedRow))
                    {
                        s.IPAddress = persistedRow.IPAddress;
                    }
                    if (!string.IsNullOrEmpty(s.IPAddress) && delta.ReverseDnsResolved.TryGetValue(s.IPAddress, out var fqdn))
                    {
                        s.Hostname = fqdn; // null = negative DNS; UpsertCycleAsync coalesces with existing.
                    }

                    // 14-03 / AUDIT-01 — count this station as "enriched this cycle" if its IP or
                    // Hostname transitioned null/empty -> populated through this loop body. Fields
                    // that were already populated before the merge are NOT recounted.
                    var hasIpAfter = !string.IsNullOrEmpty(s.IPAddress);
                    var hasHostnameAfter = !string.IsNullOrEmpty(s.Hostname);
                    if ((!hadIpBefore && hasIpAfter) || (!hadHostnameBefore && hasHostnameAfter))
                    {
                        enrichmentEnriched++;
                    }
                }

                // ORCH-07 — persist the refresh timestamp INSIDE this transaction.
                await nodeRepo.UpdateLastEnrichmentRefreshAtAsync(db, r.NodeId, enrichmentNow, ct);
            }
            // 14-03 / AUDIT-01 (CONTEXT 14 §D-15) — cycle-level enrichment summary. Emits at
            // Information level so the operator's production deployment surfaces the per-cycle
            // coverage ratio without enabling Debug. The AUDIT-02 memo reads these summary lines
            // from the operator's /logs/log-*.txt rolling file to extract the first-cycle skip
            // pattern. Coverage is the (enriched / observed) ratio; zero stations observed -> 0.0.
            log.LogInformation(
                "AUDIT-01: cycle {CycleSequence} enrichment summary: observed={Observed} enriched={Enriched} skipped-subnet-opt-out={SkippedOptOut} skipped-time-gate={SkippedTimeGate} null-delta={NullDelta} factory-threw={FactoryThrew} coverage={Coverage:P1}",
                cycleSeq, enrichmentObserved, enrichmentEnriched, enrichmentSkippedSubnetOptOut, enrichmentSkippedTimeGate, enrichmentNullDelta, enrichmentFactoryThrew,
                enrichmentObserved == 0 ? 0.0 : (double)enrichmentEnriched / enrichmentObserved);
            // ─── End Phase 10 enrichment loop ──────────────────────────────────────────────────────

            // Build a station -> owning NodeId map (reference-keyed; avoids O(N²) Contains scans).
            var stationToNodeId = new Dictionary<WLANStation, int>(ReferenceEqualityComparer.Instance);
            foreach (var r in results.OfType<NodeProbeResult.Success>())
                foreach (var s in r.Stations)
                    stationToNodeId[s] = r.NodeId;

            // (NodeId, BSSID-uppercase) -> WiFiNetwork.Id lookup for resolved-on-this-cycle networks.
            var resolvedNodeIds = results.OfType<NodeProbeResult.Success>().Select(r => r.NodeId).ToHashSet();
            var bssidToId = await db.WiFiNetworks
                .Where(n => resolvedNodeIds.Contains(n.WiFiNodeId))
                .Select(n => new { n.WiFiNodeId, n.MacAddress, n.Id })
                .ToListAsync(ct);
            var bssidLookup = bssidToId.ToDictionary(
                x => (x.WiFiNodeId, x.MacAddress),
                x => x.Id);

            var allStations = results.OfType<NodeProbeResult.Success>().SelectMany(r => r.Stations).ToList();
            foreach (var s in allStations)
            {
                // "Resolved" = a non-null, non-zero FK. NODE-04 (Plan 03-04) made WiFiNetworkId
                // nullable to support post-deletion historical observation rows; in this
                // pre-persist orchestrator path null AND 0 both mean "not yet resolved".
                if (s.WiFiNetworkId is not null and not 0) continue;     // already set by caller
                if (s.Bssid is null) continue;          // no carrier — parser bug; orchestrator skips
                if (!stationToNodeId.TryGetValue(s, out var owningNodeId)) continue;
                if (bssidLookup.TryGetValue((owningNodeId, s.Bssid.ToUpperInvariant()), out var netId))
                    s.WiFiNetworkId = netId;
            }
            // Drop stations whose WiFiNetworkId could not be resolved (orphaned).
            allStations = allStations.Where(s => s.WiFiNetworkId is not null and not 0).ToList();

            // 6. Orchestrator-side dedup (D-08 / CR-03 closure). StringComparer.Ordinal: parser uppercased.
            var deduped = allStations
                .GroupBy(s => s.MacAddress, StringComparer.Ordinal)
                .Select(g =>
                {
                    var winner = g
                        .OrderByDescending(s => s.RSS)
                        .ThenByDescending(s => s.LastPollDate)
                        .ThenBy(s => s.WiFiNetworkId)
                        .First();
                    if (g.Count() > 1)
                    {
                        log.LogInformation(
                            "Station {MAC} reported on {N} WLANs in cycle; kept entry from WLAN {WiFiNetworkId} (RSS={RSS} dBm)",
                            winner.MacAddress, g.Count(), winner.WiFiNetworkId, winner.RSS);
                    }
                    return winner;
                })
                .ToList();

            // 7. Station upsert + final SaveChanges.
            await stationRepo.UpsertCycleAsync(db, deduped, ct);
            await db.SaveChangesAsync(ct);

            // 7.5 Clear telemetry for stations missed >= 2 polling cycles (D-08 multiplier; matches IsStale UI cutoff).
            //     Stale window (1 missed cycle, elapsed < 2x): keep last-known RSS/Tx/Rx (Stale-badge state).
            //     Offline window (elapsed >= 2x): blank the cells via Detail.razor / RssiCell.razor "—" render
            //     paths (null RSS / 0.0 bitrate). MUST stay inside the try block so a failure rolls back with
            //     the upsert (D-04 coordinator-tx invariant). `now` is the cycle timestamp captured at step 4.
            var staleCutoff = now - TimeSpan.FromSeconds(2 * settings.PollingIntervalSeconds);
            await db.WLANStations
                .Where(s => s.Status == Status.Offline && s.LastPollDate < staleCutoff)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.RSS, (int?)null)
                    .SetProperty(s => s.TxBitrate, 0.0)
                    .SetProperty(s => s.RxBitrate, 0.0), ct);

            await tx.CommitAsync(ct);

            // 260516-pce — Unified post-commit end-of-cycle wall clock. Consumed by:
            //   (1) _ticks (LastSuccessfulCycleUtc — feeds /health 'lastPollAge' and
            //       Dashboard last-poll timestamps), and
            //   (2) PollSnapshot.CycleEndUtc (consumed by PollCountdown.razor:131 as
            //       the countdown anchor and by Dashboard.razor:318-319 +
            //       StatusBadge staleness derivation).
            // Operator's clarification 2026-05-16: the countdown must restore "after
            // the polling AND the enrichment operation." This capture is strictly
            // downstream of both the SSH fan-out (Task.WhenAll(pollTasks) at line
            // ~107) AND the per-node enrichmentFactory awaits (Phase 10 ORCH-01..08
            // block, line ~308). The previous code reused the 'now' captured at
            // line ~125 (right after the SSH fan-out, BEFORE enrichment + the final
            // SaveChanges + commit) — making PollCountdown restart at
            // PollingIntervalSeconds − cycleDuration (e.g. 25s instead of 30s for a
            // ~5s cycle), the v0.11 QUAL-02 LIVE-UAT surface-(a) blocker. CONTEXT 14
            // §D-04 #3 literal: "Cycled received; _lastCycleEndUtc = now".
            var cycleEndUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _ticks, cycleEndUtc.Ticks);     // D-09 — only after successful commit (write-after-commit invariant; rolled-back cycles do NOT advance _ticks).

            // 7.6 B10 LOCK -- post-commit TOFU audit log. Only emitted when the trust persisted.
            foreach (var t in newlyTrusted)
            {
                log.LogInformation(
                    "Node {Hostname} (Id={NodeId}): trusting host key {Fingerprint} (first connect)",
                    t.Hostname, t.NodeId, t.Fingerprint);
            }

            // 8. Notify subscribers (Phase 5 forward-compat; no subscribers in Phase 2).
            var snapshot = new PollSnapshot(
                TotalNodes: nodes.Count,
                OnlineNodes: results.Count(r => r is NodeProbeResult.Success),
                FailedNodes: results.Count(r => r is NodeProbeResult.Failed),
                StationCount: deduped.Count,
                CycleEndUtc: cycleEndUtc);
            await notifier.RaiseAsync(snapshot);

            log.LogInformation(
                "Poll cycle complete: {Total} nodes, {Online} online, {Failed} failed, {Stations} stations after dedup",
                nodes.Count, snapshot.OnlineNodes, snapshot.FailedNodes, deduped.Count);
        }
        catch (Exception)
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<NodeProbeResult> RunNodeProbeAsync(WiFiNode node, CancellationToken ct)
    {
        try
        {
            return await probeAsync(node, ct);
        }
        catch (HostKeyMismatchException ex)
        {
            log.LogWarning(
                "Node {Hostname} (Id={NodeId}): host key changed — expected {ExpectedFingerprint}, got {ActualFingerprint}. Node skipped this cycle. Clear the cache via Settings to re-trust.",
                node.Hostname, node.Id, ex.ExpectedFingerprint, ex.ActualFingerprint);
            return new NodeProbeResult.Failed(node.Id, "host-key-mismatch");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Node {Hostname} (Id={NodeId}): probe failed (top-level catch).", node.Hostname, node.Id);
            return new NodeProbeResult.Failed(node.Id, "probe-failed");
        }
    }
}
