using Microsoft.Extensions.Logging;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Polling.Exceptions;

namespace OpenWrtApStats.Services.Nodes;

/// <summary>
/// D-12 / WLAN-01..04 implementation. Reuses the Phase 2 probe-factory; persists
/// <see cref="NodeProbeResult.Success.Networks"/> through <see cref="INetworkRepo.UpsertManyAsync(int, IEnumerable{WiFiNetwork}, CancellationToken)"/>
/// (repo-owned overload — discovery owns its own short transaction, NEVER the orchestrator's
/// coordinator). Per D-15 absent rows are marked <c>Enabled=false</c> via the sibling
/// <see cref="INetworkRepo.MarkAbsentDisabledAsync"/> (Plan 03-02).
/// <para>
/// HostKeyMismatchException is caught here (the underlying probe doesn't catch it; only
/// PollOrchestrator does — and discovery isn't reusing the orchestrator's catch chain).
/// </para>
/// </summary>
public sealed class WlanDiscoveryService(
    Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe,
    INodeRepo nodes,
    INetworkRepo networks,
    ILogger<WlanDiscoveryService> log)
    : IWlanDiscoveryService
{
    public async Task<DiscoveryOutcome> DiscoverAsync(int nodeId, CancellationToken ct)
    {
        var node = await nodes.GetByIdAsync(nodeId, ct);
        if (node is null) return new DiscoveryOutcome.NodeNotFound();

        try
        {
            var result = await probe(node, ct);
            return result switch
            {
                NodeProbeResult.Success s
                    => await PersistAsync(nodeId, s.Networks, ct),
                NodeProbeResult.Failed { Reason: var reason }
                    when reason.Contains("within-cycle-reentry", StringComparison.OrdinalIgnoreCase)
                    => new DiscoveryOutcome.Busy(),
                NodeProbeResult.Failed { Reason: var reason }
                    when reason.Contains("host-key", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("host key", StringComparison.OrdinalIgnoreCase)
                    => new DiscoveryOutcome.HostKeyMismatch(),
                NodeProbeResult.Failed { Reason: var reason }
                    => new DiscoveryOutcome.Generic(reason),
                _ => new DiscoveryOutcome.Generic("unknown probe result")
            };
        }
        catch (HostKeyMismatchException)
        {
            log.LogWarning("Discover: host-key-mismatch on node {NodeId}", nodeId);
            return new DiscoveryOutcome.HostKeyMismatch();
        }
    }

    private async Task<DiscoveryOutcome> PersistAsync(
        int nodeId, IReadOnlyList<WiFiNetwork> discovered, CancellationToken ct)
    {
        // Discovery's transaction is DISJOINT from the orchestrator's coordinator
        // (Pitfall 6 / 7). UpsertManyAsync(nodeId, discovered) opens its own DbContext +
        // commits internally; MarkAbsentDisabledAsync is a single-statement
        // ExecuteUpdateAsync (atomic by itself).

        // Count rows that will become Enabled=false BEFORE the absent-disable runs, so the
        // success banner copy can show the disabled count (D-15 / UI-SPEC §Node Detail
        // Discover SUCCESS banner, lines 384-388). Read-only check.
        var presentMacs = discovered
            .Select(n => n.MacAddress)
            .ToHashSet(StringComparer.Ordinal);

        var existing = await networks.GetByNodeAsync(nodeId, ct);
        var willBeDisabledCount = existing.Count(e =>
            !presentMacs.Contains(e.MacAddress) && e.Enabled);

        await networks.UpsertManyAsync(nodeId, discovered, ct);
        await networks.MarkAbsentDisabledAsync(nodeId, presentMacs, ct);

        return new DiscoveryOutcome.Success(discovered.Count, willBeDisabledCount);
    }
}
