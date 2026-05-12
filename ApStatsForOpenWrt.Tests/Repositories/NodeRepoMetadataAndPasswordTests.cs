using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Repositories;

/// <summary>
/// D-09 CR-02 closure tests (Phase 3). Verifies UpdateMetadataAsync + UpdatePasswordAsync
/// preserve the columns they don't touch — RESEARCH §Pitfall 5 binding.
/// </summary>
public sealed class NodeRepoMetadataAndPasswordTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private INodeRepo CreateRepo() =>
        new NodeRepo(new TestDbContextFactory(_fixture.Options));

    [Fact]
    public async Task UpdateMetadataAsync_PreservesPassword()
    {
        // The bug being structurally closed: a stub WiFiNode { Id=X, Hostname=Y, Username=Z }
        // built from an Edit form has Password="" (default). db.WiFiNodes.Update(stub) +
        // SaveChangesAsync would write Password="" — wiping the encrypted ciphertext that
        // SEC-01 promises stays intact across edits.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode
        {
            Hostname = $"ap-{Guid.NewGuid():N}",
            Username = "root",
            Password = "ENCRYPTED-CIPHERTEXT-MARKER"
        };
        await repo.AddAsync(node, ct);

        await repo.UpdateMetadataAsync(node.Id, "new-hostname", "new-username", ct);

        var fetched = await repo.GetByIdAsync(node.Id, ct);
        Assert.NotNull(fetched);
        Assert.Equal("new-hostname", fetched!.Hostname);
        Assert.Equal("new-username", fetched.Username);
        Assert.Equal("ENCRYPTED-CIPHERTEXT-MARKER", fetched.Password);
    }

    [Fact]
    public async Task UpdateMetadataAsync_PreservesHostKeyFingerprint()
    {
        // D-10: TOFU identity is row Id, not hostname — UpdateMetadataAsync MUST preserve HostKeyFingerprint.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode
        {
            Hostname = $"ap-{Guid.NewGuid():N}",
            Username = "root",
            Password = "p",
            HostKeyFingerprint = "SHA256:abc123def456ghijklmnop"
        };
        await repo.AddAsync(node, ct);

        await repo.UpdateMetadataAsync(node.Id, "renamed", "root", ct);

        var fetched = await repo.GetByIdAsync(node.Id, ct);
        Assert.Equal("SHA256:abc123def456ghijklmnop", fetched!.HostKeyFingerprint);
    }

    [Fact]
    public async Task UpdatePasswordAsync_OnlyTouchesPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode
        {
            Hostname = $"ap-{Guid.NewGuid():N}",
            Username = "root",
            Password = "OLD-CIPHERTEXT",
            HostKeyFingerprint = "SHA256:fingerprint"
        };
        await repo.AddAsync(node, ct);

        await repo.UpdatePasswordAsync(node.Id, "NEW-CIPHERTEXT", ct);

        var fetched = await repo.GetByIdAsync(node.Id, ct);
        Assert.Equal("NEW-CIPHERTEXT", fetched!.Password);
        Assert.Equal(node.Hostname, fetched.Hostname);
        Assert.Equal("root", fetched.Username);
        Assert.Equal("SHA256:fingerprint", fetched.HostKeyFingerprint);
    }
}
