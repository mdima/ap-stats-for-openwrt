using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace OpenWrtApStats.Data.Models;

/// <summary>
/// A wireless client currently associated with one of the monitored WLANs. PER-02 binds
/// <see cref="MacAddress"/> as the stable identity (a station can hop WLANs over time);
/// the class-level unique index reconciles cross-WLAN re-discovery to one logical station.
/// </summary>
[Index(nameof(MacAddress), IsUnique = true)]
public class WLANStation
{
    public int Id { get; set; }

    /// <summary>
    /// Current WLAN association. Non-nullable per <c>seed/datamodel.md</c> (PER-01 binding).
    /// The coordinator-transaction pattern (mark-Offline-then-upsert atomically inside one
    /// outer transaction) eliminates any transient window where this FK would temporarily
    /// point at "no network" — the concern is solved architecturally, not by relaxing the
    /// schema.
    /// </summary>
    public int WiFiNetworkId { get; set; }

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
    public int RSS { get; set; }

    public Status Status { get; set; } = Status.Offline;

    public DateTime LastPollDate { get; set; }

    public WiFiNetwork? Network { get; set; }
}
