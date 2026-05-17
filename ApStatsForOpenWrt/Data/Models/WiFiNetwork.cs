using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ApStatsForOpenWrt.Data.Models;

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
    /// WLAN iface name (e.g., "wlan0", "phy0-ap0") detected from `iwinfo` or `iw dev` output by the
    /// Phase 2 parser; persisted since v0.11 (CONTEXT 14 §D-10 — conditional yield to support the
    /// DISC-01 disconnect path, which issues `ubus call hostapd.&lt;iface&gt; del_client` against the AP).
    /// Pre-v0.11 this property was [NotMapped]; v0.11 promotes it to a persisted nullable string so
    /// the Stations/Detail.razor Disconnect button can resolve the iface without an extra SSH probe.
    /// Polling read path unchanged beyond writing this one extra field per WLAN row.
    /// </summary>
    [MaxLength(64)]
    public string? IfaceName { get; set; }

    public WiFiNode? Node { get; set; } // navigation; cascade delete via FK convention
}
