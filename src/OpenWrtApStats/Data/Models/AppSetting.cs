using System.ComponentModel.DataAnnotations;

namespace OpenWrtApStats.Data.Models;

/// <summary>
/// Singleton key-value row for application settings (D-09 vocabulary:
/// <c>PollingIntervalSeconds</c>, <c>UIPasswordHash</c>, <c>SshKnownHostsCacheJson</c>).
/// Only <c>PollingIntervalSeconds</c> is seeded by the InitialCreate migration; the other
/// keys are written on first use by their owning phase.
/// </summary>
public class AppSetting
{
    [Key]
    [MaxLength(128)]
    public string Key { get; set; } = string.Empty;

    public string? StringValue { get; set; }
    public int? IntValue { get; set; }
    public bool? BoolValue { get; set; }
}
