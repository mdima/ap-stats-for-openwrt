using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ApStatsForOpenWrt.Data.Models;

/// <summary>
/// A wireless client currently associated with one of the monitored WLANs. MAC is the stable
/// identity (PER-02). The cross-WLAN unique index reconciles re-discovery to one logical
/// station; FK is nullable post-NODE-04 (dissociation contract — historical observation rows
/// survive node deletion).
/// </summary>
[Index(nameof(MacAddress), IsUnique = true)]
public class WLANStation
{
    public int Id { get; set; }

    /// <summary>
    /// Current WLAN association. Nullable since NODE-04 (Phase 3): deleting a WiFiNode
    /// cascades its WiFiNetworks (DeleteBehavior.Cascade) AND dissociates its WLANStations
    /// (DeleteBehavior.SetNull) — historical observation rows survive with FK = NULL rather
    /// than being deleted along with the network. The coordinator-transaction pattern
    /// (mark-Offline-then-upsert atomically inside one outer transaction) still owns the
    /// polling-cycle invariant — null FK only appears when the parent WiFiNetwork was
    /// destroyed by NODE-04 cascade, NOT during a poll cycle.
    /// </summary>
    public int? WiFiNetworkId { get; set; }

    [Required, MaxLength(17)]
    public string MacAddress { get; set; } = string.Empty;

    [MaxLength(45)] // IPv6 max textual length
    public string? IPAddress { get; set; }

    [MaxLength(255)]
    public string? Hostname { get; set; }

    /// <summary>Operator-assigned symbolic name — DASH-14 / Phase 5.</summary>
    [MaxLength(255)]
    public string? Name { get; set; }

    public double TxBitrate { get; set; }
    public double RxBitrate { get; set; }
    /// <summary>
    /// Received Signal Strength in dBm. Null means the parser did not find an RSS value
    /// for this station (absent from output). Zero is a valid signal level and must NOT
    /// be used as a sentinel — CR-03.
    /// </summary>
    public int? RSS { get; set; }

    public Status Status { get; set; } = Status.Offline;

    public DateTime LastPollDate { get; set; }

    public WiFiNetwork? Network { get; set; }

    /// <summary>
    /// Transient (NOT persisted) BSSID carrier used by the parser/NodePoller/orchestrator handoff
    /// (B4 LOCK; sister of <see cref="WiFiNetwork.IfaceName"/>). NodePoller stamps each emitted
    /// station with the iface's BSSID; the orchestrator resolves <c>Bssid</c> →
    /// <c>WiFiNetworkId</c> AFTER <c>networkRepo.UpsertManyAsync</c> assigns DB IDs. <c>[NotMapped]</c>
    /// so EF ignores it — the production schema is unaffected; no migration is generated.
    /// </summary>
    [NotMapped]
    public string? Bssid { get; set; }

    /// <summary>
    /// Phase 9 SCHEMA-04. Random/locally-administered MAC flag, computed from U/L bit on MAC
    /// byte 0 — set on existing rows by the Phase 9 migration backfill (D-08 SQL: <c>((instr(...)
    /// - 1) &amp; 2) != 0</c>); Phase 10 ORCH-03 computes on INSERT for new rows. Default false at
    /// the SQL level. User-toggleable from station detail page (Phase 10 DASH-22).
    /// </summary>
    public bool IsRandomMac { get; set; }

    /// <summary>
    /// v0.9.2 — UTC timestamp of the first cycle in which this MAC was observed by the
    /// dashboard. INSERT-only: <see cref="ApStatsForOpenWrt.Data.Repositories.StationRepo.UpsertCycleAsync(ApStatsForOpenWrt.Data.DatabaseContext, System.Collections.Generic.IEnumerable{WLANStation}, System.Threading.CancellationToken)"/>
    /// sets <c>probe.FirstSeen = probe.LastPollDate</c> on the INSERT branch and leaves
    /// <c>existingRow.FirstSeen</c> untouched on the UPDATE branch. Joins the existing
    /// preserved-on-update list alongside <see cref="Name"/> and <see cref="IsRandomMac"/>
    /// (ORCH-02 convention). Migration backfills pre-existing rows from
    /// <see cref="LastPollDate"/> (best lower bound) so no <c>0001-01-01</c> rows remain.
    /// </summary>
    public DateTime FirstSeen { get; set; }
}
