using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Repositories;

/// <summary>
/// D-18 SET-03 — per-node "clear known-hosts cache" tests (Phase 3).
/// M-HRD-03 / v1.0 (plan 21-02): the obsolete all-rows bulk-clear variant was removed; only the
/// per-node <see cref="INodeRepo.ClearHostKeyFingerprintAsync"/> re-trust path remains.
/// CR-02-safe: the method MUST preserve Password — RESEARCH §Pitfall 5.
/// </summary>
public sealed class NodeRepoClearFingerprintsTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private INodeRepo CreateRepo() =>
        new NodeRepo(new TestDbContextFactory(_fixture.Options));

    [Fact]
    public async Task ClearHostKeyFingerprintAsync_OnlyClearsTargetRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var n1 = new WiFiNode { Hostname = $"n1-{Guid.NewGuid():N}", Username = "root", Password = "P1", HostKeyFingerprint = "FP1" };
        var n2 = new WiFiNode { Hostname = $"n2-{Guid.NewGuid():N}", Username = "root", Password = "P2", HostKeyFingerprint = "FP2" };
        await repo.AddAsync(n1, ct);
        await repo.AddAsync(n2, ct);

        await repo.ClearHostKeyFingerprintAsync(n1.Id, ct);

        var f1 = await repo.GetByIdAsync(n1.Id, ct);
        var f2 = await repo.GetByIdAsync(n2.Id, ct);
        Assert.Null(f1!.HostKeyFingerprint);
        Assert.Equal("FP2", f2!.HostKeyFingerprint);
        Assert.Equal("P1", f1.Password);
        Assert.Equal("P2", f2.Password);
    }

    [Fact]
    public async Task ClearHostKeyFingerprintAsync_MissingNodeId_DoesNothing()
    {
        // Single-statement ExecuteUpdateAsync with no matching row is a no-op (returns 0).
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        await repo.ClearHostKeyFingerprintAsync(int.MaxValue, ct);   // no exception
    }
}
