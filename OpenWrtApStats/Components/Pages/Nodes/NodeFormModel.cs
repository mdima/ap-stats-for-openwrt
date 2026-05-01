using System.ComponentModel.DataAnnotations;

namespace OpenWrtApStats.Components.Pages.Nodes;

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
}
