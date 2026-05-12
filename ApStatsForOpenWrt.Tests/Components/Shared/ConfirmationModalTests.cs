using Bunit;
using Microsoft.AspNetCore.Components;
using ApStatsForOpenWrt.Components.Shared;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// D-20 smoke spike checkpoint #3 (Pitfall 6): modal opens from Blazor button click.
/// Verified via bUnit by toggling the bound IsOpen parameter and asserting the modal's
/// CSS-class state (show / display:block / aria-modal=true). The CSS-class-driven design
/// from RESEARCH §Pitfall 6 sidesteps the Bootstrap.Modal JS lifecycle problem entirely
/// — the only test surface needed is bUnit, no real browser.
/// </summary>
public sealed class ConfirmationModalTests
{
    [Fact]
    public void IsOpenFalse_HidesModal()
    {
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<ConfirmationModal>(parameters => parameters
            .Add(p => p.IsOpen, false)
            .Add(p => p.Title, "Delete this node?")
            .Add(p => p.ConfirmLabel, "Delete node")
            .Add(p => p.Body, builder => builder.AddContent(0, "Are you sure?")));

        // Hidden state: aria-hidden="true" and display:none and no .show class
        var modal = cut.Find(".modal");
        Assert.Equal("true", modal.GetAttribute("aria-hidden"));
        Assert.Contains("display: none", modal.GetAttribute("style") ?? "");
        Assert.DoesNotContain("show", modal.GetAttribute("class") ?? "");
    }

    [Fact]
    public void IsOpenTrue_ShowsModalWithBackdrop()
    {
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<ConfirmationModal>(parameters => parameters
            .Add(p => p.IsOpen, true)
            .Add(p => p.Title, "Delete this node?")
            .Add(p => p.ConfirmLabel, "Delete node")
            .Add(p => p.Body, builder => builder.AddContent(0, "Are you sure?")));

        var modal = cut.Find(".modal");
        Assert.Equal("true", modal.GetAttribute("aria-modal"));
        Assert.Contains("display: block", modal.GetAttribute("style") ?? "");
        Assert.Contains("show", modal.GetAttribute("class") ?? "");

        // Modal-backdrop sibling
        var backdrop = cut.Find(".modal-backdrop");
        Assert.NotNull(backdrop);
        Assert.Contains("show", backdrop.GetAttribute("class") ?? "");
    }

    [Fact]
    public void CancelButton_FiresIsOpenChanged()
    {
        using var ctx = new Bunit.BunitContext();
        bool? newValue = null;
        var cut = ctx.Render<ConfirmationModal>(parameters => parameters
            .Add(p => p.IsOpen, true)
            .Add(p => p.Title, "T")
            .Add(p => p.ConfirmLabel, "OK")
            .Add(p => p.IsOpenChanged, EventCallback.Factory.Create<bool>(this, v => newValue = v))
            .Add(p => p.Body, builder => builder.AddContent(0, "?")));

        // Click the modal-footer btn-secondary (Cancel)
        var cancel = cut.Find(".modal-footer .btn-secondary");
        cancel.Click();

        Assert.Equal(false, newValue);
    }

    [Fact]
    public void ConfirmButton_FiresOnConfirmThenIsOpenChanged()
    {
        using var ctx = new Bunit.BunitContext();
        var confirmFired = false;
        bool? newValue = null;
        var cut = ctx.Render<ConfirmationModal>(parameters => parameters
            .Add(p => p.IsOpen, true)
            .Add(p => p.Title, "T")
            .Add(p => p.ConfirmLabel, "Delete")
            .Add(p => p.OnConfirm, EventCallback.Factory.Create(this, () => confirmFired = true))
            .Add(p => p.IsOpenChanged, EventCallback.Factory.Create<bool>(this, v => newValue = v))
            .Add(p => p.Body, builder => builder.AddContent(0, "?")));

        var confirm = cut.Find(".modal-footer .btn-danger");
        confirm.Click();

        Assert.True(confirmFired);
        Assert.Equal(false, newValue);
    }
}
