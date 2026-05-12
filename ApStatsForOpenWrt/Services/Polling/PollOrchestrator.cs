using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Polling.Exceptions;
using OpenWrtApStats.Services.Settings;

namespace OpenWrtApStats.Services.Polling;

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
    TimeProvider timeProvider)
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

    public async Task RunCycleAsync(CancellationToken ct)
    {
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

            foreach (var r in results.OfType<NodeProbeResult.Success>())
            {
                var node = nodes.First(n => n.Id == r.NodeId);
                var observedMacs = r.Stations.Select(s => s.MacAddress).ToHashSet(StringComparer.Ordinal);
                var existingMacs = existingMacsByNode[r.NodeId];
                var newMacDetected = observedMacs.Any(m => !existingMacs.Contains(m));

                if (!EnrichmentRefreshTrigger.ShouldRefresh(node, enrichmentNow, refreshIntervalSeconds, newMacDetected))
                {
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
                    log.LogWarning(ex, "Node {Hostname} (Id={NodeId}): enrichmentFactory threw; persisted IPs/Hostnames preserved (DB-as-cache).", node.Hostname, node.Id);
                    continue;
                }
                if (delta is null) continue;

                // Merge delta into r.Stations BEFORE the dedup pass below:
                //   - Fill missing IP from delta.SweepMacToIp[mac] (ORCH-04 DB-as-cache cascade).
                //   - For stations with an IPAddress, look up delta.ReverseDnsResolved[ip] and fill
                //     Hostname (null delta value = NXDOMAIN/timeout — UpsertCycleAsync's `?? existingRow.Hostname`
                //     coalesce preserves any existing Hostname).
                foreach (var s in r.Stations)
                {
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
                }

                // ORCH-07 — persist the refresh timestamp INSIDE this transaction.
                await nodeRepo.UpdateLastEnrichmentRefreshAtAsync(db, r.NodeId, enrichmentNow, ct);
            }
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
            Interlocked.Exchange(ref _ticks, now.Ticks);     // D-09 — only after successful commit (write-after-commit invariant; rolled-back cycles do NOT advance _ticks).

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
                CycleEndUtc: now);
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
