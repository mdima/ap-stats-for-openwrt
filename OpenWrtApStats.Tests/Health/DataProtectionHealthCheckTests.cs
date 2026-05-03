using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Health;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Health;

/// <summary>
/// Phase 6 INF-06 — request-time Data Protection key-ring readability probe coverage.
/// Mirrors the three arms of <c>DataProtectionHealthHostedServiceTests</c>:
/// <list type="bullet">
///   <item>Guard 1 — no encrypted credentials → Healthy (key ring is irrelevant).</item>
///   <item>Guard 2 — creds present + /data/keys populated → Healthy.</item>
///   <item>Negative — creds present + /data/keys missing → Unhealthy.</item>
/// </list>
/// Environment-dependent arms use the same skip-with-Inconclusive guard as
/// <c>DataProtectionHealthHostedServiceTests.cs:91-96</c> so the assertions don't false-fail on
/// an operator's dev box that happens to have <c>/data/keys</c> populated (or NOT populated
/// for the negative arm).
/// </summary>
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
        // Guard 1 false + Guard 2 true. Skip with Inconclusive on dev boxes that don't have
        // /data/keys populated — same idiom as DataProtectionHealthHostedServiceTests.cs:91-96.
        if (!Directory.Exists("/data/keys") ||
            !Directory.EnumerateFiles("/data/keys", "*.xml").Any())
        {
            Assert.Skip("Skipping positive-arm: /data/keys not populated on this host (CI runner / dev box without volume mount). The probe behavior is exercised in the integration-test arm in plan 06-03.");
        }

        var ct = TestContext.Current.CancellationToken;
        var hostname = $"dphc-positive-{Guid.NewGuid():N}";
        await SeedEncryptedNodeAsync(hostname, ct);

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
            await RemoveSeededNodeAsync(hostname, ct);
        }
    }

    [Fact]
    public async Task ReturnsUnhealthy_WhenCredsExistButKeyRingMissing()
    {
        // Guard 1 false + Guard 2 false. Hard to test on a host where /data/keys IS populated;
        // skip-with-Inconclusive in that direction. The integration test in plan 06-03 covers
        // the negative arm via a SmokeFactoryFixture override.
        if (Directory.Exists("/data/keys") &&
            Directory.EnumerateFiles("/data/keys", "*.xml").Any())
        {
            Assert.Skip("Skipping negative-arm: /data/keys IS populated on this host; the unhealthy condition is unreachable here. Plan 06-03's HealthEndpointTests exercises this arm via fixture override.");
        }

        var ct = TestContext.Current.CancellationToken;
        var hostname = $"dphc-negative-{Guid.NewGuid():N}";
        await SeedEncryptedNodeAsync(hostname, ct);

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
