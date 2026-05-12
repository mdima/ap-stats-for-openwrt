using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Polling;

/// <summary>
/// Phase 10 ORCH-01 — pure-function unit tests for the composite refresh trigger.
/// CHARTER-locked formula:
///   <c>LastEnrichmentRefreshAt IS NULL OR (now - LastEnrichmentRefreshAt) > RefreshIntervalSeconds
///   OR newMacDetectedThisCycle</c>, AND-gated by <c>node.SubnetScanEnabled</c> per D-09.
/// </summary>
public sealed class EnrichmentRefreshTriggerTests
{
    private static WiFiNode MakeNode(DateTimeOffset? lastRefresh = null, bool scanEnabled = true)
        => new()
        {
            Id = 1,
            Hostname = "ap",
            Username = "operator",
            Password = string.Empty,
            SubnetScanEnabled = scanEnabled,
            LastEnrichmentRefreshAt = lastRefresh,
        };

    [Fact]
    public void ShouldRefresh_WhenSubnetScanDisabled_ReturnsFalse_RegardlessOfOtherInputs()
    {
        var node = MakeNode(lastRefresh: null, scanEnabled: false);
        var actual = EnrichmentRefreshTrigger.ShouldRefresh(node, DateTimeOffset.UtcNow, 3600, newMacDetectedThisCycle: true);
        Assert.False(actual);
    }

    [Fact]
    public void ShouldRefresh_WhenLastRefreshNull_AndScanEnabled_ReturnsTrue()
    {
        var node = MakeNode(lastRefresh: null, scanEnabled: true);
        var actual = EnrichmentRefreshTrigger.ShouldRefresh(node, DateTimeOffset.UtcNow, 3600, newMacDetectedThisCycle: false);
        Assert.True(actual);
    }

    [Fact]
    public void ShouldRefresh_WhenNewMacDetected_ReturnsTrue_EvenWhenIntervalNotExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var node = MakeNode(lastRefresh: now.AddSeconds(-30), scanEnabled: true);
        var actual = EnrichmentRefreshTrigger.ShouldRefresh(node, now, 3600, newMacDetectedThisCycle: true);
        Assert.True(actual);
    }

    [Fact]
    public void ShouldRefresh_WhenIntervalExpired_AndNoNewMac_ReturnsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var node = MakeNode(lastRefresh: now.AddSeconds(-3601), scanEnabled: true);
        var actual = EnrichmentRefreshTrigger.ShouldRefresh(node, now, 3600, newMacDetectedThisCycle: false);
        Assert.True(actual);
    }

    [Fact]
    public void ShouldRefresh_WhenIntervalNotExpired_AndNoNewMac_ReturnsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        var node = MakeNode(lastRefresh: now.AddSeconds(-1800), scanEnabled: true);
        var actual = EnrichmentRefreshTrigger.ShouldRefresh(node, now, 3600, newMacDetectedThisCycle: false);
        Assert.False(actual);
    }

    [Fact]
    public void ShouldRefresh_AtExactIntervalBoundary_ReturnsFalse_StrictGreaterThan()
    {
        var now = DateTimeOffset.UtcNow;
        var node = MakeNode(lastRefresh: now.AddSeconds(-3600), scanEnabled: true);
        var actual = EnrichmentRefreshTrigger.ShouldRefresh(node, now, 3600, newMacDetectedThisCycle: false);
        // Strict `>` boundary: exactly equal to the interval is NOT yet expired (matches CHARTER §"Refresh model" formula).
        Assert.False(actual);
    }
}
