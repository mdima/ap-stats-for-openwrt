namespace ApStatsForOpenWrt.Data.Repositories.History;

/// <summary>
/// Phase 19 M-HIST-06 / M-HIST-07 (L-03 / L-05 / L-06 / D-04) — read-side query service that folds
/// the persisted <c>RssiHistorySamples</c> into the two differently-shaped RSSI surfaces:
/// the inline sparkline (per-MAC map, adaptive ~1 h window, latest-cycle→MAX per bucket) and the
/// station-detail multi-series chart (one series per (NodeId, NetworkId), last-value per bucket over
/// the full 12 h axis, labelled AP hostname + SSID).
/// <para>
/// Lives under <c>ApStatsForOpenWrt.Data.Repositories.History</c> so it may legally inject
/// <c>IDbContextFactory&lt;DatabaseContext&gt;</c> (ArchitectureTests carve-out).
/// </para>
/// </summary>
public interface IRssiHistoryQueryService
{
    /// <summary>
    /// M-HIST-06 / L-05 / D-04. Return a per-MAC sparkline series map scoped to the passed
    /// <paramref name="macs"/> set (single parameterized read, NOT per-row fetches). Per MAC, per
    /// 5-min bucket over the adaptive ~1 h window: take the latest cycle then <c>MAX(Rssi)</c> across
    /// that cycle's (AP, WLAN) rows (flattens roams to one continuous line); a bucket with no rows is
    /// a gap (null point). MACs with no samples in the window are ABSENT from the map (drives the
    /// sparkline em-dash, D-03).
    /// </summary>
    Task<IReadOnlyDictionary<string, RssiSparklineSeries>> GetSparklineSeriesAsync(
        IEnumerable<string> macs, CancellationToken ct);

    /// <summary>
    /// M-HIST-07 / L-06. Return one series per distinct (NodeId, NetworkId) the MAC was seen on in the
    /// full 12 h window; each series is last-value-per-bucket over the full grid (NO cross-series
    /// aggregation — preserves per-AP detail). Series label resolves NodeId→Hostname + NetworkId→SSID.
    /// Zero rows → empty-state flag set.
    /// </summary>
    Task<RssiDetailDto> GetDetailSeriesAsync(string mac, CancellationToken ct);
}

/// <summary>A per-MAC sparkline series: the ordered bucket points over the adaptive ~1 h window.</summary>
/// <param name="Points">Ascending full-axis points; null <c>Rssi</c> == gap/offline bucket (L-05).</param>
public sealed record RssiSparklineSeries(IReadOnlyList<RssiSparklinePoint> Points);

/// <summary>One sparkline bucket point.</summary>
/// <param name="BucketUtc">Bucket slot-start instant (UTC).</param>
/// <param name="Rssi">MAX(Rssi) of the latest cycle in this bucket, or null for a gap.</param>
public sealed record RssiSparklinePoint(DateTime BucketUtc, int? Rssi);

/// <summary>
/// The detail multi-series read result: the shared ascending bucket-timestamp axis, one series per
/// (NodeId, NetworkId), and a zero-rows flag (D-03 placeholder).
/// </summary>
/// <param name="BucketsUtc">The shared full-axis bucket timestamps (oldest first).</param>
/// <param name="Series">One series per distinct (NodeId, NetworkId); empty when no rows.</param>
/// <param name="IsEmpty">True when the MAC had zero samples in the 12 h window.</param>
public sealed record RssiDetailDto(
    IReadOnlyList<DateTime> BucketsUtc, IReadOnlyList<RssiDetailSeries> Series, bool IsEmpty);

/// <summary>
/// One detail series for a distinct (NodeId, NetworkId). <see cref="Points"/> aligns 1:1 with the
/// owning <see cref="RssiDetailDto.BucketsUtc"/>; a null entry is a gap (no sample in that bucket).
/// </summary>
/// <param name="Label">Legend label = AP hostname + SSID (L-06).</param>
/// <param name="NodeId">The AP node id.</param>
/// <param name="NetworkId">The WLAN network id.</param>
/// <param name="Points">Last-value-per-bucket RSSI, null == gap; aligned to <see cref="RssiDetailDto.BucketsUtc"/>.</param>
public sealed record RssiDetailSeries(string Label, int NodeId, int NetworkId, IReadOnlyList<int?> Points);
