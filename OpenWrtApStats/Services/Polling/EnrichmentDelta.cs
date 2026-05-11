namespace OpenWrtApStats.Services.Polling;

/// <summary>
/// Phase 10 POCO carrier returned by the enrichmentFactory delegate. Combines the Phase 9
/// <see cref="SubnetSweepProbeResult"/> and <see cref="ReverseDnsProbeResult"/> outputs after the
/// orchestrator's gate fires. The orchestrator joins this delta with the Phase 2 NodePoller
/// stations by MAC and feeds the merged set into <c>StationRepo.UpsertCycleAsync</c>.
/// <para>
/// <c>SweepMacToIp</c> keys are uppercase per Phase 2 D-07. <c>ReverseDnsResolved</c> values are
/// nullable: null means NXDOMAIN/SERVFAIL/timeout (Phase 9 D-12 / ORCH-08 — persists as
/// <c>Hostname=null</c> and is retried on the next refresh tick).
/// </para>
/// </summary>
public sealed record EnrichmentDelta(
    int NodeId,
    IReadOnlyDictionary<string, string> SweepMacToIp,
    IReadOnlyDictionary<string, string?> ReverseDnsResolved);
