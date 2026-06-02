using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ApStatsForOpenWrt.Data.Models;

/// <summary>
/// Phase 18 M-HIST-01. One row per (station MAC, AP, WLAN) per polling cycle for stations that
/// are Online with a non-null RSS (D-04 — sourced from the pre-dedup station set so roams and
/// dual-band associations stay distinct series). Written orchestrator-only inside the coordinator
/// transaction (Plan 03), pruned to a 12 h window.
/// <para>
/// <see cref="StationMac"/> is a plain string with <b>NO foreign key</b> to <c>WLANStation</c>:
/// the MAC is the stable station identity (L-03 / PER-02) and history rows must survive
/// station-row churn. <see cref="NodeId"/> → <see cref="WiFiNode"/> and
/// <see cref="NetworkId"/> → <see cref="WiFiNetwork"/> are cascade FKs (configured Fluent in
/// <see cref="DatabaseContext.OnModelCreating"/>) — deleting a node/network cascade-deletes its
/// history samples.
/// </para>
/// <para>Indexes: descending <see cref="TimestampUtc"/> (cleaner + Phase 19 detail read) and the
/// composite <c>(StationMac, TimestampUtc DESC)</c> for the per-station detail chart (M-HIST-07).
/// The optional <c>(NodeId, TimestampUtc)</c> index is deferred (D-07 YAGNI).</para>
/// </summary>
[Index(nameof(TimestampUtc), AllDescending = true)]
[Index(nameof(StationMac), nameof(TimestampUtc), AllDescending = true)]
public class RssiHistorySample
{
    public int Id { get; set; }

    /// <summary>Cycle timestamp — the orchestrator's single-clock <c>now</c> (D-06), UTC.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>
    /// Station MAC — identity key, NO FK (L-03). Mirrors <see cref="WLANStation.MacAddress"/>
    /// (uppercase, 17-char canonical form).
    /// </summary>
    [Required, MaxLength(17)]
    public string StationMac { get; set; } = string.Empty;

    /// <summary>FK → <see cref="WiFiNode"/>, cascade.</summary>
    public int NodeId { get; set; }

    /// <summary>FK → <see cref="WiFiNetwork"/>, cascade.</summary>
    public int NetworkId { get; set; }

    /// <summary>Received signal strength in dBm. Non-null — null-RSS stations are filtered out write-side (D-04).</summary>
    public int Rssi { get; set; }

    public WiFiNode? Node { get; set; }
    public WiFiNetwork? Network { get; set; }
}
