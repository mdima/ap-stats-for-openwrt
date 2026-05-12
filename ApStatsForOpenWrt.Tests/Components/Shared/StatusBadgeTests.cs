using Bunit;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Data.Models;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 5 D-09 StatusBadge — extended Online/Offline/Stale derivation per UI-SPEC §Status Badges.
/// </summary>
public sealed class StatusBadgeTests
{
    [Theory]
    [InlineData(Status.Online,  false, "text-bg-success", "Online")]
    [InlineData(Status.Online,  true,  "text-bg-success", "Online")]   // Stale never overrides Online
    [InlineData(Status.Polling, false, "text-bg-success", "Online")]   // Polling renders as Online (Phase 3 lock)
    [InlineData(Status.Polling, true,  "text-bg-success", "Online")]
    [InlineData(Status.Offline, false, "text-bg-secondary", "Offline")]
    [InlineData(Status.Offline, true,  "text-bg-warning",   "Stale")]   // NEW Phase 5 — only path to Stale
    public void Renders_ExpectedVariant(Status status, bool isStale, string expectedClass, string expectedLabel)
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<StatusBadge>(p => p.Add(c => c.Status, status).Add(c => c.IsStale, isStale));

        Assert.Contains(expectedClass, cut.Markup);
        Assert.Contains(expectedLabel,  cut.Markup);
    }

    [Fact]
    public void StaleVariant_UsesExclamationCircleIcon()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<StatusBadge>(p => p.Add(c => c.Status, Status.Offline).Add(c => c.IsStale, true));

        Assert.Contains("bi-exclamation-circle-fill", cut.Markup);
    }
}
