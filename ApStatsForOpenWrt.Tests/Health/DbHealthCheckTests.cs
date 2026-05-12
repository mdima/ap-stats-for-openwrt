using Microsoft.Extensions.Diagnostics.HealthChecks;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Health;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Health;

/// <summary>
/// Phase 6 INF-06 — request-time DB reachability probe coverage. Exercises both arms:
/// Healthy when the repo round-trip succeeds (real <see cref="NodeRepo"/> over
/// <see cref="SqliteInMemoryFixture"/>), Unhealthy when the repo throws (inline
/// <see cref="ThrowingNodeRepo"/> double — no mocking framework, project-wide convention).
/// Mirror analog: <c>DataProtectionHealthHostedServiceTests.cs</c>.
/// </summary>
public sealed class DbHealthCheckTests(SqliteInMemoryFixture sql) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _sql = sql;

    [Fact]
    public async Task ReturnsHealthy_WhenDbReachable()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = new NodeRepo(new TestDbContextFactory(_sql.Options));
        var sut  = new DbHealthCheck(repo);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("ok", result.Description);
    }

    [Fact]
    public async Task ReturnsUnhealthy_WhenRepoThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new DbHealthCheck(new ThrowingNodeRepo());

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("fail", result.Description);
        Assert.NotNull(result.Exception);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    /// <summary>
    /// Inline test double — DbHealthCheck only invokes <see cref="INodeRepo.AnyAsync"/>; every
    /// other method throws <see cref="NotImplementedException"/> so any drift in the production
    /// probe call is loud at test time. No mocking framework — see Plan 02-03 NodePollerTests
    /// for the project-wide inline-double convention.
    /// </summary>
    private sealed class ThrowingNodeRepo : INodeRepo
    {
        public Task<bool> AnyAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("simulated DB outage (Plan 06-02 DbHealthCheck negative arm)");

        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateMetadataAsync(int id, string hostname, string username, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdatePasswordAsync(int id, string encryptedPassword, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkAllOfflineAsync(DatabaseContext db, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateOnlineAsync(DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetHostKeyFingerprintAsync(DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateLastEnrichmentRefreshAtAsync(DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
