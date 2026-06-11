using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Bootstrap;
using ApStatsForOpenWrt.Services.Health;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Health;

/// <summary>
/// Phase 6 INF-06 — request-time Data Protection key-ring readability probe coverage.
/// Mirrors the three arms of <c>DataProtectionHealthHostedServiceTests</c>:
/// <list type="bullet">
///   <item>Guard 1 — no encrypted credentials → Healthy (key ring is irrelevant).</item>
///   <item>Guard 2 — creds present + key-ring dir populated → Healthy.</item>
///   <item>Negative — creds present + key-ring dir empty → Unhealthy.</item>
/// </list>
/// 260609-9fe: the SUT now probes <see cref="BootstrapPaths.KeysDir"/> (the <c>DATA_ROOT</c>-env
/// resolver). The <c>BootstrapRootsModuleInit</c> [ModuleInitializer] points <c>DATA_ROOT</c> at a
/// writable temp dir with a pre-created EMPTY <c>{root}/keys</c>, so the env-dependent arms are
/// driven DETERMINISTICALLY in <c>BootstrapPaths.KeysDir</c> (drop a non-PII dummy <c>*.xml</c> for
/// the populated arm; delete stray <c>*.xml</c> for the empty arm) — no <c>Assert.Skip</c> / no
/// host-drive <c>/data/keys</c> dependency.
/// <para>
/// Serialized with <c>DataProtectionHealthHostedServiceTests</c> via
/// <c>[Collection("DataProtectionKeyRingSerial")]</c> — both classes share the process-global
/// <see cref="BootstrapPaths.KeysDir"/> and <see cref="Environment.ExitCode"/>, so their key-ring
/// arms must not run in parallel.
/// </para>
/// </summary>
[Collection("DataProtectionKeyRingSerial")]
public sealed class DataProtectionHealthCheckTests(SqliteInMemoryFixture sql) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _sql = sql;

    [Fact]
    public async Task ReturnsHealthy_WhenNoEncryptedCredsExist()
    {
        // Guard 1 — empty WiFiNodes table (or rows with empty Password); key ring never inspected.
        // The IClassFixture is shared with the other two tests in this class, both of which
        // clean up their seeded rows in finally. Defensive: also clear any leftover encrypted-cred
        // rows from earlier test runs in this fixture so the assertion is not order-dependent.
        var ct = TestContext.Current.CancellationToken;
        await ClearEncryptedCredRowsAsync(ct);

        var repo = new NodeRepo(new TestDbContextFactory(_sql.Options));
        var sut  = new DataProtectionHealthCheck(repo);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("ok", result.Description);
    }

    [Fact]
    public async Task ReturnsHealthy_WhenCredsExistAndKeyRingPopulated()
    {
        // Guard 1 false + Guard 2 true. Deterministic: drop a non-PII dummy *.xml into the probed
        // BootstrapPaths.KeysDir (NEVER real key material), clean up in finally. No Assert.Skip.
        var ct = TestContext.Current.CancellationToken;
        var hostname = $"dphc-positive-{Guid.NewGuid():N}";
        await SeedEncryptedNodeAsync(hostname, ct);

        Directory.CreateDirectory(BootstrapPaths.KeysDir);
        var dummyKey = Path.Combine(BootstrapPaths.KeysDir, $"key-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(dummyKey, "<key/>", ct);

        try
        {
            var repo = new NodeRepo(new TestDbContextFactory(_sql.Options));
            var sut  = new DataProtectionHealthCheck(repo);

            var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.Equal("ok", result.Description);
        }
        finally
        {
            if (File.Exists(dummyKey)) File.Delete(dummyKey);
            await RemoveSeededNodeAsync(hostname, ct);
        }
    }

    [Fact]
    public async Task ReturnsUnhealthy_WhenCredsExistButKeyRingMissing()
    {
        // Guard 1 false + Guard 2 false. Deterministic: ensure BootstrapPaths.KeysDir exists with
        // ZERO *.xml (ModuleInit pre-creates it empty; defensively delete stray *.xml a parallel
        // positive arm may have written). No Assert.Skip.
        var ct = TestContext.Current.CancellationToken;
        var hostname = $"dphc-negative-{Guid.NewGuid():N}";
        await SeedEncryptedNodeAsync(hostname, ct);

        Directory.CreateDirectory(BootstrapPaths.KeysDir);
        foreach (var stray in Directory.EnumerateFiles(BootstrapPaths.KeysDir, "*.xml"))
            File.Delete(stray);

        try
        {
            var repo = new NodeRepo(new TestDbContextFactory(_sql.Options));
            var sut  = new DataProtectionHealthCheck(repo);

            var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal("fail", result.Description);
        }
        finally
        {
            await RemoveSeededNodeAsync(hostname, ct);
        }
    }

    /// <summary>
    /// W3 — concrete seeder (no hand-wave). AnyEncryptedPasswordsAsync condition is
    /// <c>!string.IsNullOrEmpty(n.Password)</c> per NodeRepo.cs, so any non-empty
    /// ciphertext-shaped string flips Guard 1 true. Uses a unique hostname per call so parallel
    /// invocations within the same class do not collide on the unique-index constraint.
    /// </summary>
    [Fact]
    public async Task ReturnsUnhealthy_WhenRepoThrows()
    {
        // Closes the catch (Exception) arm — the production code's defensive non-throw policy.
        // Symmetrical with DbHealthCheckTests.ReturnsUnhealthy_WhenRepoThrows; uses an inline
        // INodeRepo double whose AnyEncryptedPasswordsAsync throws.
        var ct = TestContext.Current.CancellationToken;
        var sut = new DataProtectionHealthCheck(new ThrowingNodeRepo());

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("fail", result.Description);
        Assert.NotNull(result.Exception);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    /// <summary>
    /// Inline test double — DataProtectionHealthCheck only invokes
    /// <see cref="INodeRepo.AnyEncryptedPasswordsAsync"/>. All other surface members throw
    /// <see cref="NotImplementedException"/> so any future drift in the production probe call
    /// is loud at test time. Mirrors the project-wide inline-double convention from Plan 02-03.
    /// </summary>
    private sealed class ThrowingNodeRepo : INodeRepo
    {
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("simulated repo failure (Plan 06-02 DataProtectionHealthCheck catch-arm coverage)");

        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateMetadataAsync(int id, string hostname, string username, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdatePasswordAsync(int id, string encryptedPassword, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> AnyAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkAllOfflineAsync(DatabaseContext db, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateOnlineAsync(DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetHostKeyFingerprintAsync(DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateLastEnrichmentRefreshAtAsync(DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private async Task SeedEncryptedNodeAsync(string hostname, CancellationToken ct)
    {
        await using var ctx = new DatabaseContext(_sql.Options);
        ctx.WiFiNodes.Add(new WiFiNode
        {
            Hostname     = hostname,
            Username     = "root",
            Password     = "U2FsdGVkX1placeholderCiphertext==",   // any non-empty string flips AnyEncryptedPasswordsAsync true
            Status       = Status.Offline,
            LastPollDate = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync(ct);
    }

    private async Task RemoveSeededNodeAsync(string hostname, CancellationToken ct)
    {
        await using var ctx = new DatabaseContext(_sql.Options);
        var row = await ctx.WiFiNodes.SingleOrDefaultAsync(n => n.Hostname == hostname, ct);
        if (row is null) return;
        ctx.WiFiNodes.Remove(row);
        await ctx.SaveChangesAsync(ct);
    }

    private async Task ClearEncryptedCredRowsAsync(CancellationToken ct)
    {
        // Defensive: remove ANY WiFiNode row in the shared fixture that has a non-empty Password
        // before asserting the empty-creds arm. Other tests in this class clean up their seeds in
        // finally; this is belt + braces against an early-exit failure that left a row behind.
        await using var ctx = new DatabaseContext(_sql.Options);
        var encrypted = await ctx.WiFiNodes
            .Where(n => n.Password != "")
            .ToListAsync(ct);
        if (encrypted.Count == 0) return;
        ctx.WiFiNodes.RemoveRange(encrypted);
        await ctx.SaveChangesAsync(ct);
    }
}
