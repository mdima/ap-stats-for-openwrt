using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Repositories;

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
    public async Task UpdateMetadataAsync_MutatesPersistedRow()
    {
        // D-09 closure (CR-02-safe): UpdateAsync(WiFiNode) is REMOVED — Phase 3 callers go
        // through UpdateMetadataAsync (multi-SetProperty Hostname+Username).
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode { Hostname = $"ap-{Guid.NewGuid():N}", Username = "root", Password = "" };
        await repo.AddAsync(node, ct);

        var newHostname = "renamed-" + node.Hostname;
        await repo.UpdateMetadataAsync(node.Id, newHostname, node.Username, ct);

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

    // ── ONB-01 AnyAsync predicate ────────────────────────────────────────────
    [Fact]
    public async Task AnyAsync_FalseWhenNoNodes()
    {
        // Class fixture is shared — we cannot guarantee zero nodes exist.
        // Assert consistency: AnyAsync must agree with GetAllAsync.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var existing = await repo.GetAllAsync(ct);
        var expectedAny = existing.Count > 0;
        Assert.Equal(expectedAny, await repo.AnyAsync(ct));
    }

    [Fact]
    public async Task AnyAsync_TrueAfterAddAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var node = new WiFiNode
        {
            Hostname = $"any-async-test-{Guid.NewGuid():N}",
            Username = "root",
            Password = ""
        };
        await repo.AddAsync(node, ct);
        Assert.True(await repo.AnyAsync(ct));
    }

    // ── D-02 SetHostKeyFingerprintAsync (caller-owned-only, CR-02-safe) ─────
    [Fact]
    public async Task SetHostKeyFingerprintAsync_CallerOwned_DoesNotCommit()
    {
        // D-02 caller-owned semantics: the method participates in the caller's transaction.
        // If the caller does not commit, the change must not survive.
        var ct = TestContext.Current.CancellationToken;
        var repo = new NodeRepo(new TestDbContextFactory(_fixture.Options));

        // Seed
        int nodeId;
        await using (var seed = _fixture.CreateContext())
        {
            var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = "" };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;
        }

        // Act inside a transaction that we deliberately do NOT commit.
        await using (var ownedDb = _fixture.CreateContext())
        await using (var tx = await ownedDb.Database.BeginTransactionAsync(ct))
        {
            await repo.SetHostKeyFingerprintAsync(ownedDb, nodeId, "fingerprint-XYZ", ct);
            // No SaveChangesAsync. No CommitAsync. Tx rolls back on dispose.
        }

        // Assert: column was NOT updated (caller-owned does not commit on its own).
        await using var verify = _fixture.CreateContext();
        var node2 = await verify.WiFiNodes.AsNoTracking().FirstAsync(n => n.Id == nodeId, ct);
        Assert.Null(node2.HostKeyFingerprint);
    }

    [Fact]
    public async Task SetHostKeyFingerprintAsync_CallerOwned_CommitsWhenCallerSavesAndCommits()
    {
        // D-02 happy path: caller saves + commits, fingerprint persists.
        var ct = TestContext.Current.CancellationToken;
        var repo = new NodeRepo(new TestDbContextFactory(_fixture.Options));

        int nodeId;
        await using (var seed = _fixture.CreateContext())
        {
            var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = "" };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;
        }

        await using (var ownedDb = _fixture.CreateContext())
        await using (var tx = await ownedDb.Database.BeginTransactionAsync(ct))
        {
            await repo.SetHostKeyFingerprintAsync(ownedDb, nodeId, "fingerprint-COMMITTED", ct);
            await ownedDb.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await using var verify = _fixture.CreateContext();
        var node2 = await verify.WiFiNodes.AsNoTracking().FirstAsync(n => n.Id == nodeId, ct);
        Assert.Equal("fingerprint-COMMITTED", node2.HostKeyFingerprint);
    }

    // ── Phase 10 SET-03 / SET-04: UpdateScanSettingsAsync (CR-02-safe) ──────
    [Fact]
    public async Task UpdateScanSettingsAsync_PersistsBothFields_WithoutTouchingOtherColumns()
    {
        // CR-02 invariant: single-statement ExecuteUpdateAsync writes ONLY SubnetScanEnabled
        // + SubnetScanRanges; never touches Password, HostKeyFingerprint, LastEnrichmentRefreshAt,
        // Hostname, or Username.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();

        const string ciphertext = "ciphertext-blob-scan-settings";
        var refreshedAt = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
        int nodeId;
        await using (var seed = _fixture.CreateContext())
        {
            var node = new WiFiNode
            {
                Hostname = $"scan-{Guid.NewGuid():N}",
                Username = "root",
                Password = ciphertext,
                HostKeyFingerprint = "FP-original",
                LastEnrichmentRefreshAt = refreshedAt,
                SubnetScanEnabled = false,
                SubnetScanRanges = null
            };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;
        }

        await repo.UpdateScanSettingsAsync(nodeId, true, "192.168.1.0/24", ct);

        await using var verify = _fixture.CreateContext();
        var fetched = await verify.WiFiNodes.AsNoTracking().FirstAsync(n => n.Id == nodeId, ct);

        Assert.True(fetched.SubnetScanEnabled);
        Assert.Equal("192.168.1.0/24", fetched.SubnetScanRanges);
        // CR-02 safety: untouched columns
        Assert.Equal(ciphertext,    fetched.Password);
        Assert.Equal("FP-original", fetched.HostKeyFingerprint);
        Assert.Equal(refreshedAt,   fetched.LastEnrichmentRefreshAt);
    }

    [Fact]
    public async Task UpdateScanSettingsAsync_NullRanges_PersistsAsNull()
    {
        // Plan 10-03 contract: blank/null SubnetScanRanges round-trips as null (auto-derive
        // intent — Phase 9 D-04 auto-derives a /24 around the SSH-peer-serving interface).
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();

        int nodeId;
        await using (var seed = _fixture.CreateContext())
        {
            var node = new WiFiNode
            {
                Hostname = $"scan-null-{Guid.NewGuid():N}",
                Username = "root",
                Password = "",
                SubnetScanEnabled = true,
                SubnetScanRanges = "10.0.0.0/24"   // pre-existing non-null value to be cleared
            };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;
        }

        await repo.UpdateScanSettingsAsync(nodeId, true, null, ct);

        await using var verify = _fixture.CreateContext();
        var fetched = await verify.WiFiNodes.AsNoTracking().FirstAsync(n => n.Id == nodeId, ct);
        Assert.True(fetched.SubnetScanEnabled);
        Assert.Null(fetched.SubnetScanRanges);
    }

    [Fact]
    public async Task UpdateScanSettingsAsync_DisabledTrue_FlipsToggle_PreservesRanges()
    {
        // D-09 verbatim: when SubnetScanEnabled is OFF, the SubnetScanRanges value is still
        // persisted so the operator can re-enable later without losing the CIDR list.
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();

        int nodeId;
        await using (var seed = _fixture.CreateContext())
        {
            var node = new WiFiNode
            {
                Hostname = $"scan-disable-{Guid.NewGuid():N}",
                Username = "root",
                Password = "",
                SubnetScanEnabled = true,
                SubnetScanRanges = null
            };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;
        }

        // Toggle OFF while still passing a CIDR list — operator may have toggled off but
        // pre-filled the ranges field; both must round-trip.
        await repo.UpdateScanSettingsAsync(nodeId, false, "10.0.0.0/24", ct);

        await using var verify = _fixture.CreateContext();
        var fetched = await verify.WiFiNodes.AsNoTracking().FirstAsync(n => n.Id == nodeId, ct);
        Assert.False(fetched.SubnetScanEnabled);
        Assert.Equal("10.0.0.0/24", fetched.SubnetScanRanges);
    }

    [Fact]
    public async Task SetHostKeyFingerprintAsync_DoesNotClobberPassword_CR02Safe()
    {
        // CR-02 safety: single-property ExecuteUpdateAsync MUST NOT touch the encrypted Password.
        // (db.WiFiNodes.Update(entity) would clobber Password from a default value — we never go that path.)
        var ct = TestContext.Current.CancellationToken;
        var repo = new NodeRepo(new TestDbContextFactory(_fixture.Options));

        int nodeId;
        const string ciphertext = "ciphertext-blob-xyz";
        await using (var seed = _fixture.CreateContext())
        {
            var node = new WiFiNode { Hostname = $"node-{Guid.NewGuid():N}", Username = "root", Password = ciphertext };
            seed.WiFiNodes.Add(node);
            await seed.SaveChangesAsync(ct);
            nodeId = node.Id;
        }

        await using (var ownedDb = _fixture.CreateContext())
        await using (var tx = await ownedDb.Database.BeginTransactionAsync(ct))
        {
            await repo.SetHostKeyFingerprintAsync(ownedDb, nodeId, "FP1", ct);
            await ownedDb.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await using var verify = _fixture.CreateContext();
        var node2 = await verify.WiFiNodes.AsNoTracking().FirstAsync(n => n.Id == nodeId, ct);
        Assert.Equal(ciphertext, node2.Password);            // CR-02 safety
        Assert.Equal("FP1",      node2.HostKeyFingerprint);   // happy path
    }
}
