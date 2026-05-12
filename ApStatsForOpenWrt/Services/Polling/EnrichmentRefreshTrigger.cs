using OpenWrtApStats.Data.Models;

namespace OpenWrtApStats.Services.Polling;

/// <summary>
/// Phase 10 ORCH-01 composite refresh trigger. Pure function over (node, now, refreshInterval,
/// newMacDetectedThisCycle) — no I/O, no logger, no DI. CHARTER-locked formula:
/// <c>LastEnrichmentRefreshAt IS NULL OR (now - LastEnrichmentRefreshAt) &gt; RefreshIntervalSeconds
/// OR newMacDetectedThisCycle</c>, AND-gated by <c>node.SubnetScanEnabled</c> per D-09.
/// Easy unit-test target: see <c>EnrichmentRefreshTriggerTests</c>.
/// </summary>
public static class EnrichmentRefreshTrigger
{
    public static bool ShouldRefresh(
        WiFiNode node,
        DateTimeOffset now,
        int refreshIntervalSeconds,
        bool newMacDetectedThisCycle)
    {
        if (!node.SubnetScanEnabled) return false;
        if (node.LastEnrichmentRefreshAt is null) return true;
        if (newMacDetectedThisCycle) return true;
        return (now - node.LastEnrichmentRefreshAt.Value).TotalSeconds > refreshIntervalSeconds;
    }
}
