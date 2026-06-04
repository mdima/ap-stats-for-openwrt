using Microsoft.EntityFrameworkCore;

namespace ApStatsForOpenWrt.Data.Models;

/// <summary>
/// Phase 18 M-HIST-01. One row per polling cycle capturing the dashboard's KPI triad
/// (Online / Stale / Offline station counts) over the post-upsert station set. Written
/// orchestrator-only inside the coordinator transaction (Plan 03), pruned to a 12 h window.
/// <para>
/// Count semantics (D-01, reusing the orchestrator's <c>staleCutoff = now - 2×PollingIntervalSeconds</c>):
/// <c>OnlineCount</c> = stations with <c>Status==Online</c>; <c>StaleCount</c> =
/// <c>Status==Offline AND LastPollDate &gt;= staleCutoff</c> (recently offline — the
/// inverted-sense boundary vs the telemetry-blank filter, RESEARCH §Critical Correctness Note);
/// <c>OfflineCount</c> = <c>Status==Offline AND LastPollDate &lt; staleCutoff</c>. Polling-status
/// rows are excluded.
/// </para>
/// <para>The descending <see cref="TimestampUtc"/> index serves the Phase 19 KPI read path and
/// the 12 h range-delete prune.</para>
/// </summary>
[Index(nameof(TimestampUtc), AllDescending = true)]
public class KpiHistorySnapshot
{
    public int Id { get; set; }

    /// <summary>Cycle timestamp — the orchestrator's single-clock <c>now</c> (D-06), UTC.</summary>
    public DateTime TimestampUtc { get; set; }

    public int OnlineCount { get; set; }
    public int StaleCount { get; set; }
    public int OfflineCount { get; set; }
}
