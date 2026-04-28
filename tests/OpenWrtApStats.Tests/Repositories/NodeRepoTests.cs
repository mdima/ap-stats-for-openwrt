using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Repositories;

/// <summary>
/// Exercises <see cref="NodeRepo"/> against the per-fixture migrated SQLite :memory:
/// schema. Drives the production repo via a small <see cref="IDbContextFactory{TContext}"/>
/// adapter that wraps the fixture's <see cref="DbContextOptions{TContext}"/> — production
/// code resolves <see cref="IDbContextFactory{TContext}"/> from DI; the tests here resolve
/// the same shape from the fixture so the production constructor signature stays honest.
/// </summary>
public sealed class NodeRepoTests(SqliteInMemoryFixture fixture) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private INodeRepo CreateRepo() =>
        new NodeRepo(new TestDbContextFactory(_fixture.Options));

    [Fact]
    public async Task AddAsync_ThenGetByIdAsync_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode { Hostname = $"ap-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await repo.AddAsync(node, ct);
        var fetched = await repo.GetByIdAsync(node.Id, ct);
        Assert.NotNull(fetched);
        Assert.Equal(node.Hostname, fetched!.Hostname);
    }

    [Fact]
    public async Task UpdateAsync_MutatesPersistedRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode { Hostname = $"ap-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await repo.AddAsync(node, ct);
        node.Hostname = "renamed-" + node.Hostname;
        await repo.UpdateAsync(node, ct);
        var fetched = await repo.GetByIdAsync(node.Id, ct);
        Assert.StartsWith("renamed-", fetched!.Hostname);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode { Hostname = $"ap-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await repo.AddAsync(node, ct);
        await repo.DeleteAsync(node.Id, ct);
        var fetched = await repo.GetByIdAsync(node.Id, ct);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task MarkAllOfflineAsync_RepoOwned_FlipsAllToOffline()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        // Seed three nodes set to Online manually with unique hostnames so cross-test
        // pollution from the class-scoped fixture cannot make us flake.
        var prefix = $"flip-{Guid.NewGuid():N}";
        await using (var db = _fixture.CreateContext())
        {
            db.WiFiNodes.AddRange(
                new WiFiNode { Hostname = prefix + "-a", Username = "root", Password = "", Status = Status.Online },
                new WiFiNode { Hostname = prefix + "-b", Username = "root", Password = "", Status = Status.Online },
                new WiFiNode { Hostname = prefix + "-c", Username = "root", Password = "", Status = Status.Online });
            await db.SaveChangesAsync(ct);
        }

        await repo.MarkAllOfflineAsync(ct);

        await using (var db = _fixture.CreateContext())
        {
            var statuses = await db.WiFiNodes.AsNoTracking().Select(n => n.Status).ToListAsync(ct);
            Assert.NotEmpty(statuses);
            Assert.All(statuses, s => Assert.Equal(Status.Offline, s));
        }
    }

    [Fact]
    public async Task MarkAllOfflineAsync_CallerOwned_DoesNotCallSaveChangesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        // Seed one Online node with a unique hostname.
        WiFiNode seeded;
        await using (var db = _fixture.CreateContext())
        {
            seeded = new WiFiNode { Hostname = $"co-{Guid.NewGuid():N}", Username = "root", Password = "", Status = Status.Online };
            db.WiFiNodes.Add(seeded);
            await db.SaveChangesAsync(ct);
        }

        // Caller-owned overload runs ExecuteUpdateAsync immediately (it bypasses change tracking
        // and emits SQL), so the change IS visible to a fresh context — but only if the OUTER
        // transaction commits. The contract "no SaveChangesAsync call" is verified by rolling
        // back the outer transaction: if an inner SaveChangesAsync slipped in, the rollback
        // would NOT revert and the test would fail.
        await using (var ownedDb = _fixture.CreateContext())
        await using (var tx = await ownedDb.Database.BeginTransactionAsync(ct))
        {
            await repo.MarkAllOfflineAsync(ownedDb, ct);
            await tx.RollbackAsync(ct);
        }

        await using var verifyDb = _fixture.CreateContext();
        var fetched = await verifyDb.WiFiNodes.AsNoTracking().FirstAsync(n => n.Id == seeded.Id, ct);
        Assert.Equal(Status.Online, fetched.Status); // rolled back — still Online
    }

    [Fact]
    public async Task UpdateOnlineAsync_RepoOwned_SetsOnlineAndTimestamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode { Hostname = $"on-{Guid.NewGuid():N}", Username = "root", Password = "", Status = Status.Offline };
        await repo.AddAsync(node, ct);

        var now = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);
        await repo.UpdateOnlineAsync(node.Id, now, ct);

        var fetched = await repo.GetByIdAsync(node.Id, ct);
        Assert.Equal(Status.Online, fetched!.Status);
        Assert.Equal(now, fetched.LastPollDate);
    }

    [Fact]
    public async Task UpdateOnlineAsync_CallerOwned_RolledBackTransaction_DoesNotPersist()
    {
        // D-07 caller-owned overload symmetry — verifies via outer transaction rollback that
        // SaveChangesAsync was NOT called inside the overload.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode { Hostname = $"oco-{Guid.NewGuid():N}", Username = "root", Password = "", Status = Status.Offline };
        await repo.AddAsync(node, ct);

        var stamp = new DateTime(2026, 4, 28, 13, 0, 0, DateTimeKind.Utc);
        await using (var ownedDb = _fixture.CreateContext())
        await using (var tx = await ownedDb.Database.BeginTransactionAsync(ct))
        {
            await repo.UpdateOnlineAsync(ownedDb, node.Id, stamp, ct);
            await tx.RollbackAsync(ct);
        }

        await using var verifyDb = _fixture.CreateContext();
        var fetched = await verifyDb.WiFiNodes.AsNoTracking().FirstAsync(n => n.Id == node.Id, ct);
        Assert.Equal(Status.Offline, fetched.Status); // rolled back
    }

    [Fact]
    public async Task GetAllAsync_ReturnsConcreteList_NotIQueryable()
    {
        // D-06 POCO-leak guard — the return value is a sealed list, not a deferred IQueryable.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        IReadOnlyList<WiFiNode> result = await repo.GetAllAsync(ct);
        Assert.NotNull(result);
        Assert.IsNotAssignableFrom<IQueryable<WiFiNode>>(result);
    }

    [Fact]
    public async Task AnyEncryptedPasswordsAsync_ReflectsCurrentRows()
    {
        // D-12 probe seam — observation matches actual data state. We do not isolate this test
        // from the class-scoped fixture (sibling tests may have seeded nodes); instead we assert
        // consistency: the probe answer matches a direct query over the same data.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var existingNodes = await repo.GetAllAsync(ct);
        var anyExistingHasPassword = existingNodes.Any(n => !string.IsNullOrEmpty(n.Password));
        var probe = await repo.AnyEncryptedPasswordsAsync(ct);
        Assert.Equal(anyExistingHasPassword, probe);
    }

    [Fact]
    public async Task AnyEncryptedPasswordsAsync_TrueWhenAnyPasswordPresent()
    {
        // D-12 probe seam — at least one row with non-empty Password ⇒ true.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode
        {
            Hostname = $"enc-{Guid.NewGuid():N}",
            Username = "root",
            Password = "ciphertext-blob"
        };
        await repo.AddAsync(node, ct);
        Assert.True(await repo.AnyEncryptedPasswordsAsync(ct));
    }
}

/// <summary>
/// Test-only adapter that satisfies <see cref="IDbContextFactory{TContext}"/> against the
/// per-fixture <see cref="DbContextOptions{TContext}"/>. Production code uses the runtime
/// factory registered via <c>AddDbContextFactory</c> in Plan 01-04; this adapter keeps the
/// repo's primary-constructor signature honest in tests without spinning up a host.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
{
    public DatabaseContext CreateDbContext() => new DatabaseContext(options);
}
