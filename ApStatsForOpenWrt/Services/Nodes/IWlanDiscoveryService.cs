namespace OpenWrtApStats.Services.Nodes;

/// <summary>
/// WLAN-01 / D-12 seam: probe one node, persist discovered networks via UpsertManyAsync,
/// then mark absent rows Enabled=false via MarkAbsentDisabledAsync (D-15). Discovery's
/// transaction is DISJOINT from any in-flight poll cycle (Pitfall 7 binding).
/// </summary>
public interface IWlanDiscoveryService
{
    Task<DiscoveryOutcome> DiscoverAsync(int nodeId, CancellationToken ct);
}
