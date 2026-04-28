using System.ComponentModel.DataAnnotations;
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

    public WiFiNode? Node { get; set; } // navigation; cascade delete via FK convention
}
