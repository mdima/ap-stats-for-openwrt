using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace OpenWrtApStats.Data.Models;

/// <summary>
/// A WLAN exposed by a <see cref="WiFiNode"/>. Composite uniqueness on
/// <c>(WiFiNodeId, MacAddress)</c> enforces PER-03 — re-discovery of the same WLAN is
/// idempotent, while two different APs broadcasting BSSIDs that happen to collide stay
/// distinct rows.
/// </summary>
[Index(nameof(WiFiNodeId), nameof(MacAddress), IsUnique = true)]
public class WiFiNetwork
{
    public int Id { get; set; }
    public int WiFiNodeId { get; set; }

    [Required, MaxLength(64)]
    public string SSID { get; set; } = string.Empty;

    public int Channel { get; set; }
    public int Width { get; set; }

    [Required, MaxLength(17)] // 17 chars: "AA:BB:CC:DD:EE:FF"
    public string MacAddress { get; set; } = string.Empty;

    public int TxPower { get; set; }
    public bool Enabled { get; set; }

    /// <summary>
    /// Transient (NOT persisted) iface-name carrier used by the parser/orchestrator handoff (Plan 02-02 parser
    /// emits, Plan 02-03 orchestrator consumes). Holds the WLAN iface name (e.g., "wlan0", "wlan1") detected
    /// from `iwinfo` or `iw dev` output. Phase 2 plans use this to enumerate per-iface `iwinfo $iface assoclist`
    /// commands and to map station BSSIDs back to WiFiNetworkId after persist. [NotMapped] so EF ignores it --
    /// the production schema is unaffected; no migration is generated for this property.
    /// </summary>
    [NotMapped]
    public string? IfaceName { get; set; }

    public WiFiNode? Node { get; set; } // navigation; cascade delete via FK convention
}
