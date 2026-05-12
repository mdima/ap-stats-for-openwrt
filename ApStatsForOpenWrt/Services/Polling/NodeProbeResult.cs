using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Services.Polling;

/// <summary>
/// POCO result of one node's probe. Per-node SSH workers (NodePoller) return this; the
/// PollOrchestrator persists. Discriminated union via abstract base + sealed nested records
/// — Phase 2 establishes this as the convention for cross-boundary results in this codebase.
/// POLL-07: workers MUST NOT touch the DB; they return only this POCO.
/// </summary>
public abstract record NodeProbeResult(int NodeId)
{
    /// <summary>Successful probe. NewlyTrustedFingerprint is non-null only on first-connect TOFU (D-02).</summary>
    public sealed record Success(
        int NodeId,
        IReadOnlyList<WiFiNetwork> Networks,
        IReadOnlyList<WLANStation> Stations,
        string? NewlyTrustedFingerprint
    ) : NodeProbeResult(NodeId);

    /// <summary>Failed probe. Reason is a short stable token (e.g., "host-key-mismatch", "auth-failed", "timeout").</summary>
    public sealed record Failed(int NodeId, string Reason) : NodeProbeResult(NodeId);
}
