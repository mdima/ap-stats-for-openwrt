namespace OpenWrtApStats.Services.Polling;

/// <summary>
/// POCO result of one node's reverse-DNS probe (Phase 9 ENRICH-03 + ENRICH-05). Sibling to
/// <see cref="SubnetSweepProbeResult"/> and <see cref="NodeProbeResult"/> — kept as a separate
/// discriminated union so the existing PollOrchestrator return shape stays byte-identical (SC4).
/// Phase 10 wires this into the orchestrator's upsert path; Phase 9 only ships the probe + tests.
/// </summary>
public abstract record ReverseDnsProbeResult(int NodeId)
{
    /// <summary>
    /// Successful probe. <c>IpToHostname</c> values are normalized (trailing dot stripped,
    /// case lowered) per ENRICH-05 / D-12; null values represent negative-DNS results
    /// (NXDOMAIN/SERVFAIL/timeout — D-12).
    /// </summary>
    public sealed record Success(int NodeId, IReadOnlyDictionary<string, string?> IpToHostname)
        : ReverseDnsProbeResult(NodeId);

    /// <summary>
    /// Failed probe (whole-batch failure — e.g., SSH disconnect mid-batch). <c>Reason</c> is
    /// a short stable token (e.g., "timeout", "network-error", "probe-failed").
    /// </summary>
    public sealed record Failed(int NodeId, string Reason) : ReverseDnsProbeResult(NodeId);
}
