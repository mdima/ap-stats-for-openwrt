using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace OpenWrtApStats.Data.Models;

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
}
