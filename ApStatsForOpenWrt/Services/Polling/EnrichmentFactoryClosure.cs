using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Services.Polling;

/// <summary>
/// QUICK 260511-ma0 (2026-05-11). Extracts the sweep-then-DNS merge logic from the
/// enrichmentFactory closure in <c>Program.cs</c> so it is directly unit-testable
/// without rebuilding a ServiceProvider. The production registration delegates here
/// once <see cref="SubnetSweepProbe.PollAsync"/> has returned its sweep map; this
/// helper then runs the reverse-DNS dispatch (one OR two batches) and returns the
/// merged <c>IP → hostname?</c> map that flows into <see cref="EnrichmentDelta.ReverseDnsResolved"/>.
///
/// <para>
/// Why two passes? The orchestrator builds <c>ipsForReverseDns</c> from
/// (a) probe stations whose IP changed / persisted Hostname is null, and
/// (b) existing-stations rows where Hostname is null. In the delete-then-rebid
/// scenario both branches contribute zero IPs for a freshly-rebidding MAC, yet
/// <see cref="SubnetSweepProbe"/> still discovers the IP. Without a second DNS
/// dispatch the merged delta would carry IPAddress (from sweep) but NULL Hostname,
/// leaving operators with up to <c>RefreshIntervalSeconds</c> of "phantom IP without
/// hostname" UI state. This helper closes that gap inside ONE orchestrator cycle.
/// </para>
///
/// <para>
/// Invariants:
/// <list type="bullet">
///   <item>Steady-state cycles (sweepMap.Values ⊆ ipsForReverseDns) pay AT MOST one
///         <see cref="ReverseDnsProbe.PollAsync"/> SSH round-trip — no second-pass RTT regression.</item>
///   <item>Rebid cycles issue a SECOND <see cref="ReverseDnsProbe.PollAsync"/>
///         against the sweep-only IPs (those in <c>sweepMap.Values</c> but not in
///         <c>ipsForReverseDns</c>). dnsMap2 wins on key collisions (newer answer).</item>
///   <item>Merged-map invariant (carries the 260511-lct missing-IP-defaulting invariant
///         forward): every IP requested in EITHER pass appears as a key in the merged
///         map (null when both passes returned null/missing).</item>
///   <item>Second-pass failure (timeout / network error / linked-CTS exhaust) preserves
///         dnsMap1 verbatim — does NOT throw into the caller. ORCH-08 retry-on-next-tick
///         handles the remaining unresolved IP via the existing-stations branch on the
///         next refresh tick. Matches the "DB-as-cache" philosophy at
///         <c>PollOrchestrator.cs:237</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// Diagnostic signal (Option L1 from the 260511-ma0 plan): the structured log line
/// at <c>ReverseDnsProbe.cs:96-98</c> fires TWICE per node on rebid cycles, ONCE on
/// steady-state cycles. Field names <c>Successful</c>/<c>Total</c>/<c>Negative</c>
/// preserved verbatim — operator's grep tooling pins them.
/// </para>
/// </summary>
public static class EnrichmentFactoryClosure
{
    /// <summary>
    /// Run the sweep-then-DNS sequencing and return the merged reverse-DNS map ready
    /// for the <see cref="EnrichmentDelta"/> ctor. See class summary for invariants.
    /// </summary>
    /// <param name="dnsProbe">The reverse-DNS probe (already bound to the just-connected SSH session).</param>
    /// <param name="node">The node being enriched (passed through to <see cref="ReverseDnsProbe.PollAsync"/>).</param>
    /// <param name="ipsForReverseDns">The caller-supplied IP set the orchestrator pre-computed
    ///     (probe + existing-stations branches). May be empty in the delete-then-rebid scenario.</param>
    /// <param name="sweepMap">The MAC → IP map returned by <see cref="SubnetSweepProbe.PollAsync"/>.
    ///     IPs present here but absent from <paramref name="ipsForReverseDns"/> drive the second
    ///     reverse-DNS dispatch.</param>
    /// <param name="ct">Orchestrator cancellation token. Each <see cref="ReverseDnsProbe.PollAsync"/>
    ///     call constructs its own 30s linked-CTS budget internally; the two budgets are independent
    ///     by design (rare second-pass event).</param>
    /// <returns>A read-only IP → hostname? map. Failed second-pass collapses to dnsMap1; the helper
    ///     never throws into the caller.</returns>
    public static async Task<IReadOnlyDictionary<string, string?>> BuildMergedDnsMapAsync(
        ReverseDnsProbe dnsProbe,
        WiFiNode node,
        IReadOnlyList<string> ipsForReverseDns,
        IReadOnlyDictionary<string, string> sweepMap,
        CancellationToken ct)
    {
        // First pass: caller-supplied IPs (the orchestrator's pre-computed set).
        var dnsRes1 = await dnsProbe.PollAsync(node, ipsForReverseDns, ct);
        var dnsMap1 = dnsRes1 is ReverseDnsProbeResult.Success ds1
            ? ds1.IpToHostname
            : (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.Ordinal);

        // Sweep-only IPs: sweep-discovered addresses the orchestrator did not pre-supply.
        // Bounded by the per-node sweep cardinality (≤ /24). Distinct + ordinal — matches
        // ReverseDnsProbe's existing IP-key discipline.
        var ipsAlreadyQueried = new HashSet<string>(ipsForReverseDns, StringComparer.Ordinal);
        var sweepOnlyIps = sweepMap.Values
            .Where(ip => !ipsAlreadyQueried.Contains(ip))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (sweepOnlyIps.Count == 0)
        {
            // Steady-state path — no second-pass RTT regression. The single first-pass map
            // already satisfies the merged-map invariant for every requested IP.
            return dnsMap1;
        }

        // Second pass: sweep-only IPs. Independent 30s linked-CTS budget (constructed
        // inside PollAsync). Failure paths (timeout / network-error / probe-failed) surface
        // as ReverseDnsProbeResult.Failed — we collapse to an empty dnsMap2 below, which
        // when merged into dnsMap1 leaves dnsMap1 intact (DB-as-cache philosophy). The
        // SECOND PollAsync invocation may also throw OperationCanceledException without
        // ct being cancelled (the linked-CTS-budget-during-SSH-I/O shape that PollAsync
        // normally catches) — we re-catch here to preserve dnsMap1 against the rare case
        // where PollAsync rethrows due to host-shutdown semantics (ct.IsCancellationRequested
        // is the marker — propagate, otherwise swallow).
        IReadOnlyDictionary<string, string?> dnsMap2;
        try
        {
            var dnsRes2 = await dnsProbe.PollAsync(node, sweepOnlyIps, ct);
            dnsMap2 = dnsRes2 is ReverseDnsProbeResult.Success ds2
                ? ds2.IpToHostname
                : (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown — propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Linked-CTS budget exhausted INSIDE PollAsync's SSH I/O — the probe normally
            // catches this and returns Failed(timeout), but FakeSshSession's test seam
            // re-raises raw OCE. Preserve dnsMap1 either way.
            dnsMap2 = new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Defensive: any unexpected second-pass throw collapses to empty dnsMap2 —
            // matches the existing factory-level catch shape at Program.cs:244-248.
            dnsMap2 = new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        // Merge invariant: every key requested in EITHER pass appears in the merged map.
        // dnsMap2 wins on collisions (newer answer for the rare overlap case).
        var merged = new Dictionary<string, string?>(dnsMap1, StringComparer.Ordinal);
        foreach (var kv in dnsMap2)
        {
            merged[kv.Key] = kv.Value;
        }
        return merged;
    }
}
