using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Polling.Exceptions;

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
    ILogger<PollOrchestrator> log)
{
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
                await networkRepo.UpsertManyAsync(db, r.NodeId, r.Networks, ct);
                await nodeRepo.UpdateOnlineAsync(db, r.NodeId, now, ct);
            }

            // 5. Flush so SQLite assigns WiFiNetwork.Id values for new rows; then we can
            //    resolve station Bssid -> WiFiNetworkId before the dedup + station upsert.
            await db.SaveChangesAsync(ct);

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
                if (s.WiFiNetworkId != 0) continue;     // already set by caller
                if (s.Bssid is null) continue;          // no carrier — parser bug; orchestrator skips
                if (!stationToNodeId.TryGetValue(s, out var owningNodeId)) continue;
                if (bssidLookup.TryGetValue((owningNodeId, s.Bssid.ToUpperInvariant()), out var netId))
                    s.WiFiNetworkId = netId;
            }
            // Drop stations whose WiFiNetworkId could not be resolved (orphaned).
            allStations = allStations.Where(s => s.WiFiNetworkId != 0).ToList();

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
            await tx.CommitAsync(ct);

            // 7.5 B10 LOCK -- post-commit TOFU audit log. Only emitted when the trust persisted.
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
