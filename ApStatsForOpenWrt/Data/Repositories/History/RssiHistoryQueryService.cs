using ApStatsForOpenWrt.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace ApStatsForOpenWrt.Data.Repositories.History;

/// <summary>
/// Phase 19 M-HIST-06 / M-HIST-07 — windowed-read + in-memory fold implementation of
/// <see cref="IRssiHistoryQueryService"/> (RESEARCH Pattern 5). Both shapes read via
/// <c>IDbContextFactory&lt;DatabaseContext&gt;</c> (legal — repo sub-namespace) and fold through the
/// shared <see cref="HistoryBuckets"/> grid. SQL filters are parameterized (T-19-03: <c>macs.Contains</c>
/// → parameterized IN; no raw SQL).
/// </summary>
public sealed class RssiHistoryQueryService(IDbContextFactory<DatabaseContext> factory) : IRssiHistoryQueryService
{
    public async Task<IReadOnlyDictionary<string, RssiSparklineSeries>> GetSparklineSeriesAsync(
        IEnumerable<string> macs, CancellationToken ct)
    {
        var macSet = macs as IReadOnlyCollection<string> ?? macs.ToList();
        if (macSet.Count == 0)
            return new Dictionary<string, RssiSparklineSeries>();

        await using var db = factory.CreateDbContext();

        // Anchor to the LATEST sample's TimestampUtc (the polling clock, D-06) — NOT wall-clock
        // UtcNow. Visitor-gated polling can pause writes, so the newest sample may be older than a
        // wall-clock SparklineWindow; reading [anchor-window, anchor] keeps the sparkline populated
        // whenever any history exists, consistent with the KPI/detail surfaces (which already anchor
        // the grid to the latest row). A UtcNow-based read cutoff blanked every sparkline after any
        // polling gap longer than the window, while the 12 h KPI chart still rendered (QUAL-02 bug).
        var latest = await db.RssiHistorySamples
            .Where(s => macSet.Contains(s.StationMac))
            .MaxAsync(s => (DateTime?)s.TimestampUtc, ct);
        if (latest is null)
            return new Dictionary<string, RssiSparklineSeries>();

        var anchor = HistoryBuckets.AsUtc(latest.Value);
        var readCutoff = anchor - HistoryBuckets.SparklineWindow;
        // T-19-03: macs.Contains(...) → parameterized IN. Scoped to the visible MAC set (D-04) and
        // the [anchor-window, anchor] range so SQLite returns only the needed rows.
        var rows = await db.RssiHistorySamples
            .AsNoTracking()
            .Where(s => s.TimestampUtc >= readCutoff && macSet.Contains(s.StationMac))
            .OrderBy(s => s.TimestampUtc)
            .ToListAsync(ct);

        if (rows.Count == 0)
            return new Dictionary<string, RssiSparklineSeries>();

        var grid = HistoryBuckets.BuildGrid(anchor, HistoryBuckets.SparklineWindow);

        var result = new Dictionary<string, RssiSparklineSeries>();
        foreach (var macGroup in rows.GroupBy(r => r.StationMac))
        {
            // Per bucket: gather all rows, find the latest cycle (max TimestampUtc), then MAX(Rssi)
            // across that cycle's (AP, WLAN) rows (L-05 — flattens roams to one continuous line).
            var bucketRows = new List<RssiHistorySample>?[grid.Count];
            foreach (var row in macGroup)
            {
                var idx = HistoryBuckets.BucketIndex(
                    HistoryBuckets.AsUtc(row.TimestampUtc), anchor, HistoryBuckets.SparklineWindow);
                if (idx < 0) continue;
                (bucketRows[idx] ??= []).Add(row);
            }

            var points = new RssiSparklinePoint[grid.Count];
            for (var i = 0; i < grid.Count; i++)
            {
                var slot = bucketRows[i];
                if (slot is null || slot.Count == 0)
                {
                    points[i] = new RssiSparklinePoint(grid[i], null);   // gap
                    continue;
                }

                var latestCycle = slot.Max(r => HistoryBuckets.AsUtc(r.TimestampUtc));
                var max = slot
                    .Where(r => HistoryBuckets.AsUtc(r.TimestampUtc) == latestCycle)
                    .Max(r => r.Rssi);
                points[i] = new RssiSparklinePoint(grid[i], max);
            }

            result[macGroup.Key] = new RssiSparklineSeries(points);
        }

        return result;
    }

    public async Task<RssiDetailDto> GetDetailSeriesAsync(string mac, CancellationToken ct)
    {
        await using var db = factory.CreateDbContext();

        // Anchor the 12 h grid to the POLLING CLOCK = MAX(KpiHistorySnapshots.TimestampUtc) (D-06).
        // One KPI snapshot is written per cycle in the same transaction (same `now`) as the RSSI
        // samples, so the latest snapshot is always >= any station sample — it is the authoritative
        // "current poll time". Anchoring here (instead of the station's OWN latest sample) fixes two
        // QUAL-02 bugs: (1) a station with no samples in the window but an active poll clock showed
        // "Collecting history…" forever; (2) a disconnected station's chart froze at its last sample
        // instead of sliding forward with trailing null gaps. Fallback: when NO KPI snapshot exists
        // yet (fresh DB / pre-history), anchor to the station's latest row so the existing RSSI-only
        // tests stay green. IsEmpty is true ONLY when NEITHER a KPI snapshot NOR any station row
        // exists — i.e. there is genuinely no history at all (the placeholder is then truthful).
        var kpiClock = await db.KpiHistorySnapshots
            .MaxAsync(s => (DateTime?)s.TimestampUtc, ct);
        var stationLatest = await db.RssiHistorySamples
            .Where(s => s.StationMac == mac)
            .MaxAsync(s => (DateTime?)s.TimestampUtc, ct);

        var anchorRaw = kpiClock ?? stationLatest;
        if (anchorRaw is null)
            return new RssiDetailDto([], [], IsEmpty: true);   // no poll clock AND no station rows

        var anchor = HistoryBuckets.AsUtc(anchorRaw.Value);   // Pitfall 3 round-trip kind fix
        var grid = HistoryBuckets.BuildGrid(anchor, HistoryBuckets.FullWindow);

        // Read the station's rows over the SAME window the grid spans (derive the cutoff from the
        // anchor, not DateTime.UtcNow, so the windowed read is consistent with the displayed grid).
        var cutoff = anchor - HistoryBuckets.FullWindow;
        var rows = await db.RssiHistorySamples
            .AsNoTracking()
            .Where(s => s.TimestampUtc >= cutoff && s.StationMac == mac)
            .OrderBy(s => s.TimestampUtc)
            .ToListAsync(ct);

        // A poll clock exists but this station has no samples in the window: render empty axes up to
        // the current poll time (no series) — NOT the permanent placeholder.
        if (rows.Count == 0)
            return new RssiDetailDto(grid, [], IsEmpty: false);

        // Resolve labels: NodeId→Hostname, NetworkId→SSID (mirror Detail.razor:337-338).
        var nodeIds = rows.Select(r => r.NodeId).Distinct().ToList();
        var networkIds = rows.Select(r => r.NetworkId).Distinct().ToList();
        var hostnames = await db.WiFiNodes.AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.Hostname, ct);
        var ssids = await db.WiFiNetworks.AsNoTracking()
            .Where(w => networkIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.SSID, ct);

        var series = new List<RssiDetailSeries>();
        // One series per distinct (NodeId, NetworkId) — NO cross-series aggregation (L-06).
        foreach (var group in rows.GroupBy(r => (r.NodeId, r.NetworkId)))
        {
            var folded = HistoryBuckets.FoldLastValue(
                group.ToList(),
                r => r.TimestampUtc,
                anchor,
                HistoryBuckets.FullWindow);

            var points = new int?[grid.Count];
            for (var i = 0; i < grid.Count; i++)
                points[i] = folded[i]?.Rssi;

            var host = hostnames.GetValueOrDefault(group.Key.NodeId, $"node {group.Key.NodeId}");
            var ssid = ssids.GetValueOrDefault(group.Key.NetworkId, $"network {group.Key.NetworkId}");
            series.Add(new RssiDetailSeries($"{host} · {ssid}", group.Key.NodeId, group.Key.NetworkId, points));
        }

        return new RssiDetailDto(grid, series, IsEmpty: false);
    }
}
