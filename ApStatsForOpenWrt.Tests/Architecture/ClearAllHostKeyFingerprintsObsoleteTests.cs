using ApStatsForOpenWrt.Data.Repositories;
using System.Reflection;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Architecture;

/// <summary>
/// M-SEC-06 (plan 16-04) — reflection gate on the <c>[Obsolete]</c> attribute applied to
/// <see cref="INodeRepo.ClearAllHostKeyFingerprintsAsync"/> and its <see cref="NodeRepo"/>
/// implementation. Pins the verbatim v0.13 removal-target message and the warning-only form
/// (<see cref="System.ObsoleteAttribute.IsError"/> == false), so the deprecation-gate bUnit
/// tests stay compilable. If a v0.13 sweep changes the message or flips to error before
/// deleting the method, these tests fail loud.
/// </summary>
[Trait("Category", "SecurityFix")]
public class ClearAllHostKeyFingerprintsObsoleteTests
{
    private const string PinnedMessage =
        "Removed in v0.13. Use ClearHostKeyFingerprintAsync(int nodeId) instead. The bulk Clear-all-host-keys path is no longer reachable from the UI.";

    [Fact]
    public void INodeRepo_ClearAllHostKeyFingerprintsAsync_HasObsoleteWarningWithPinnedMessage_MSec06()
    {
        var method = typeof(INodeRepo).GetMethod("ClearAllHostKeyFingerprintsAsync");
        Assert.NotNull(method);
        var attr = method!.GetCustomAttribute<System.ObsoleteAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(PinnedMessage, attr!.Message);
        Assert.False(attr.IsError);
    }

    [Fact]
    public void NodeRepo_ClearAllHostKeyFingerprintsAsync_HasObsoleteWarningWithPinnedMessage_MSec06()
    {
        var method = typeof(NodeRepo).GetMethod("ClearAllHostKeyFingerprintsAsync");
        Assert.NotNull(method);
        var attr = method!.GetCustomAttribute<System.ObsoleteAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(PinnedMessage, attr!.Message);
        Assert.False(attr.IsError);
    }
}
