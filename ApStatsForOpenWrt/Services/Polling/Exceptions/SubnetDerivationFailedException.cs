namespace ApStatsForOpenWrt.Services.Polling.Exceptions;

/// <summary>
/// Thrown by <c>SubnetDeriver</c> / <c>SubnetSweepProbe</c> when auto-derivation cannot proceed
/// (no iface match, no IPv4 match, ambiguous interface). Phase 9 D-05: probe surface returns
/// <c>SubnetSweepProbeResult.Failed("subnet-derivation-failed")</c> and emits one Information-
/// level Serilog event (not Warning — operator-actionable config, not a malfunction).
/// </summary>
public sealed class SubnetDerivationFailedException : Exception
{
    public int NodeId { get; }
    public string Reason { get; }

    public SubnetDerivationFailedException(int nodeId, string reason)
        : base($"Subnet auto-derivation failed for node {nodeId}: {reason}.")
    {
        NodeId = nodeId;
        Reason = reason;
    }
}
