namespace OpenWrtApStats.Services.Polling;

/// <summary>
/// POCO result of one node's subnet-sweep probe (Phase 9 ENRICH-01..02 + ENRICH-04). Sibling
/// to <see cref="NodeProbeResult"/> — kept as a separate discriminated union so the existing
/// PollOrchestrator return shape stays byte-identical (SC4). Phase 10 wires this into the
/// orchestrator's upsert path; Phase 9 only ships the probe + tests.
/// </summary>
public abstract record SubnetSweepProbeResult(int NodeId)
{
    /// <summary>Successful sweep. <c>MacToIp</c> keys are uppercased (Phase 2 D-07).</summary>
    public sealed record Success(int NodeId, IReadOnlyDictionary<string, string> MacToIp)
        : SubnetSweepProbeResult(NodeId);

    /// <summary>Failed sweep. <c>Reason</c> is a short stable token (e.g., "subnet-derivation-failed",
    /// "timeout", "network-error", "scan-disabled", "invalid-peer-ip", "probe-failed").</summary>
    public sealed record Failed(int NodeId, string Reason) : SubnetSweepProbeResult(NodeId);
}
