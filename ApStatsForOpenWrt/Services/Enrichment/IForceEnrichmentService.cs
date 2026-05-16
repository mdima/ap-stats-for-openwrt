namespace ApStatsForOpenWrt.Services.Enrichment;

/// <summary>
/// One-shot force-enrichment flag (v0.11 M-OPS-02 / CONTEXT 14 §D-05). The operator clicks "Run now"
/// on Settings/Index.razor → <see cref="Request"/> sets the flag → on the next scheduled poll tick,
/// <c>PollOrchestrator.RunCycleAsync</c> calls <see cref="ConsumeIfSet"/> at the top of the
/// Phase 10 enrichment block and bypasses the time-based <c>EnrichmentRefreshTrigger.ShouldRefresh</c>
/// gate for every online node (D-05 scope: all online nodes). The flag CAS-clears on consumption;
/// per-node operator opt-out (<c>WiFiNode.SubnetScanEnabled = false</c>) is still honored.
/// <para>
/// Singleton lifetime. Lock-free: backed by a single <c>int</c> field + <see cref="System.Threading.Interlocked.CompareExchange(ref int, int, int)"/>.
/// Double-Request is idempotent. Double-ConsumeIfSet on the same set-cycle returns true exactly once.
/// </para>
/// <para>
/// UI consumers (Settings/Index.razor) also read <see cref="IsQueued"/> as one half of the D-06
/// OR-condition that disables the "Run now" button while a request is pending consumption.
/// </para>
/// </summary>
public interface IForceEnrichmentService
{
    /// <summary>Set the flag. Idempotent — multiple Request() calls collapse into a single pending request.</summary>
    void Request();

    /// <summary>Atomic capture-and-clear: returns true if the flag was set (then clears it); returns false otherwise.</summary>
    bool ConsumeIfSet();

    /// <summary>Read-only peek for UI button disable-state (D-06). Does NOT clear the flag.</summary>
    bool IsQueued { get; }
}
