using System.ComponentModel.DataAnnotations;

namespace ApStatsForOpenWrt.Components.Pages.Nodes;

/// <summary>
/// Form model for the shared <c>_NodeForm</c> component (Add + Edit). Hostname / Username
/// are required on both paths; Password is optional at the model level (Add path enforces
/// non-empty in <c>Add.HandleSaveAsync</c>; Edit path leaves blank to keep existing per D-08).
///
/// <para>
/// Sibling .cs file rather than a nested type inside <c>_NodeForm.razor</c>'s @code block
/// because Razor compilers expose nested types under the component class — sibling pages
/// would have to reference <c>_NodeForm.NodeFormModel</c>. A standalone class in the same
/// namespace is cleaner.
/// </para>
/// </summary>
public sealed class NodeFormModel
{
    [Required(ErrorMessage = "Hostname is required.")]
    public string? Hostname { get; set; }

    [Required(ErrorMessage = "Username is required.")]
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// Phase 10 SET-04 / D-09. Default true. When false, this node skips the active subnet
    /// sweep entirely (the SubnetSweepProbe short-circuits on this flag — Phase 9 SHIP at
    /// <c>SubnetSweepProbe.cs:45-49</c>).
    /// </summary>
    public bool SubnetScanEnabled { get; set; } = true;

    /// <summary>
    /// Phase 10 SET-03 / D-09. Comma-separated CIDR list (e.g., "192.168.1.0/24, 10.0.0.0/24").
    /// Null = auto-derive /24 around the SSH-peer-serving interface (Phase 9 D-04). The
    /// authoritative parser is <c>SubnetDeriver.ParseCidrList</c>; this property is just the
    /// operator's raw input string — trimmed before persistence.
    /// </summary>
    public string? SubnetScanRanges { get; set; }
}
