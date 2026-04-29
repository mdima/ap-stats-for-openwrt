using System.ComponentModel.DataAnnotations;

namespace OpenWrtApStats.Data.Models;

/// <summary>
/// A single OpenWRT access point declared by the operator. Phase 1 only owns the schema;
/// Phase 2 fills the table by encrypting <see cref="Password"/> through the
/// <c>"node-creds"</c> Data Protection purpose before persisting.
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
}
