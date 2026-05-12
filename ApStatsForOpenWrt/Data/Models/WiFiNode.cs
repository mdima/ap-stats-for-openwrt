using System.ComponentModel.DataAnnotations;

namespace ApStatsForOpenWrt.Data.Models;

/// <summary>
/// A single OpenWRT access point declared by the operator. Phase 1 only owns the schema;
/// Phase 2 fills the table by encrypting <see cref="Password"/> through the
/// <c>"node-creds"</c> Data Protection purpose before persisting.
/// Phase 2 adds <see cref="HostKeyFingerprint"/> for SSH host-key TOFU per D-01.
/// </summary>
public class WiFiNode
{
    public int Id { get; set; }

    [Required, MaxLength(255)]
    public string Hostname { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string Username { get; set; } = string.Empty;

    /// <summary>Data-Protection ciphertext (Base64). Plaintext only inside the application during a poll.</summary>
    [MaxLength(2048)]
    public string Password { get; set; } = string.Empty;

    public Status Status { get; set; } = Status.Offline;

    public DateTime LastPollDate { get; set; }

    /// <summary>
    /// SHA256 fingerprint of the SSH host key (non-padded base64; matches `ssh` CLI format).
    /// Null/empty means "first-connect TOFU has not yet captured a key" (D-01). Mismatch on
    /// subsequent connect throws <c>HostKeyMismatchException</c>; clear via Phase 3 SET-03 to re-trust.
    /// </summary>
    [MaxLength(256)]
    public string? HostKeyFingerprint { get; set; }

    /// <summary>
    /// Optional CIDR list governing the active subnet sweep (Phase 9 ENRICH-06 / SCHEMA-01).
    /// Null means "auto-derive a /24 around the LAN interface that owns the SSH-peer route" per
    /// Phase 9 D-04. Format: comma-separated CIDRs (e.g., "192.168.1.0/24,10.0.0.0/24") per the
    /// PATTERNS.md Claude's-discretion lock. Phase 10 SET-03 surfaces editing.
    /// </summary>
    public string? SubnetScanRanges { get; set; }

    /// <summary>
    /// Phase 9 SCHEMA-02. Default true — when false the node skips the active sweep entirely
    /// (Phase 10 SET-04 surfaces the toggle).
    /// </summary>
    public bool SubnetScanEnabled { get; set; } = true;

    /// <summary>
    /// Phase 9 SCHEMA-03. Auto-updated by the orchestrator (Phase 10 ORCH-07); null until the
    /// first refresh tick fires successfully on this node.
    /// </summary>
    public DateTimeOffset? LastEnrichmentRefreshAt { get; set; }
}
