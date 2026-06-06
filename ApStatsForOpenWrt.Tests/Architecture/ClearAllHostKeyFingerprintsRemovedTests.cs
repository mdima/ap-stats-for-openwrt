using ApStatsForOpenWrt.Data.Repositories;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Architecture;

/// <summary>
/// M-HRD-03 (plan 21-02) — method-is-gone reflection gate. The obsolete all-rows TOFU-reset
/// <c>ClearAllHostKeyFingerprintsAsync</c> was removed from <see cref="INodeRepo"/> and its
/// <see cref="NodeRepo"/> implementation for the v1.0 release (charter SET-03 / M-SEC-06
/// follow-through). These assertions pin the removal: if the bulk re-trust method is ever
/// re-introduced, they fail loud. The per-node <see cref="INodeRepo.ClearHostKeyFingerprintAsync"/>
/// re-trust path is the sole surviving surface and is exercised by NodeRepoClearFingerprintsTests.
/// </summary>
[Trait("Category", "SecurityFix")]
public class ClearAllHostKeyFingerprintsRemovedTests
{
    [Fact]
    public void INodeRepo_ClearAllHostKeyFingerprintsAsync_IsRemoved_MHrd03()
    {
        Assert.Null(typeof(INodeRepo).GetMethod("ClearAllHostKeyFingerprintsAsync"));
    }

    [Fact]
    public void NodeRepo_ClearAllHostKeyFingerprintsAsync_IsRemoved_MHrd03()
    {
        Assert.Null(typeof(NodeRepo).GetMethod("ClearAllHostKeyFingerprintsAsync"));
    }
}
