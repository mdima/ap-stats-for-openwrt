using System.Collections.Concurrent;

namespace ApStatsForOpenWrt.Services.Polling;

/// <summary>
/// Process-scoped cache of which adapter binding (iwinfo or iw dev) each node speaks. Lost on
/// restart; re-discovered on first poll per Pitfall 2. Phase 3's SET-03 ("clear known-hosts
/// cache" UI) may also call <see cref="ClearForNode"/> to force re-discovery.
/// Values are the literal strings <c>"iwinfo"</c> or <c>"iw"</c>.
/// </summary>
public sealed class OpenWrtAdapterCache
{
    private readonly ConcurrentDictionary<int, string> _byNodeId = new();

    public string? TryGet(int nodeId) => _byNodeId.TryGetValue(nodeId, out var v) ? v : null;

    public void Set(int nodeId, string adapter) => _byNodeId[nodeId] = adapter;

    public void ClearForNode(int nodeId) => _byNodeId.TryRemove(nodeId, out _);
}
