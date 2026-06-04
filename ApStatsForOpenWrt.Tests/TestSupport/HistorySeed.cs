using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Tests.TestSupport;

/// <summary>
/// Phase 19 test-support seeder for the history read-side query services. Inserts
/// <see cref="KpiHistorySnapshot"/> and <see cref="RssiHistorySample"/> fixtures shaped to exercise
/// the bucketing rules: rows that span a 5-min boundary, a gap bucket, a roaming MAC across two
/// (NodeId, NetworkId) pairs, and multiple cycles within one bucket (last-value / latest-cycle).
/// <para>
/// All timestamps are written as <c>Kind=Utc</c> here, but SQLite round-trips them as
/// <c>Kind=Unspecified</c> — the services' <c>SpecifyKind(Utc)</c> treatment is what these fixtures
/// validate (RESEARCH Pitfall 3).
/// </para>
/// </summary>
internal static class HistorySeed
{
    /// <summary>5-min bucket width, mirrored from the production constant for fixture arithmetic.</summary>
    internal const int BucketMinutes = 5;

    /// <summary>
    /// Seed one <see cref="WiFiNode"/> + one <see cref="WiFiNetwork"/> so RSSI detail labels resolve.
    /// Returns the (nodeId, networkId) pair. Hostname/SSID are deterministic for label assertions.
    /// </summary>
    internal static async Task<(int NodeId, int NetworkId)> SeedNodeAndNetworkAsync(
        DatabaseContext db, string hostname, string ssid, CancellationToken ct)
    {
        var node = new WiFiNode { Hostname = hostname, Username = "root", Password = "x" };
        db.WiFiNodes.Add(node);
        await db.SaveChangesAsync(ct);

        var net = new WiFiNetwork { WiFiNodeId = node.Id, SSID = ssid, MacAddress = "AA:BB:CC:DD:EE:F0" };
        db.WiFiNetworks.Add(net);
        await db.SaveChangesAsync(ct);

        return (node.Id, net.Id);
    }

    /// <summary>Insert a single KPI snapshot at <paramref name="tsUtc"/> with the given triad.</summary>
    internal static async Task SeedKpiAsync(
        DatabaseContext db, DateTime tsUtc, int online, int stale, int offline, CancellationToken ct)
    {
        db.KpiHistorySnapshots.Add(new KpiHistorySnapshot
        {
            TimestampUtc = DateTime.SpecifyKind(tsUtc, DateTimeKind.Utc),
            OnlineCount = online,
            StaleCount = stale,
            OfflineCount = offline,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Insert a single RSSI sample row.</summary>
    internal static async Task SeedRssiAsync(
        DatabaseContext db, DateTime tsUtc, string mac, int nodeId, int networkId, int rssi, CancellationToken ct)
    {
        db.RssiHistorySamples.Add(new RssiHistorySample
        {
            TimestampUtc = DateTime.SpecifyKind(tsUtc, DateTimeKind.Utc),
            StationMac = mac,
            NodeId = nodeId,
            NetworkId = networkId,
            Rssi = rssi,
        });
        await db.SaveChangesAsync(ct);
    }
}
