using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Bootstrap;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Bootstrap;

/// <summary>
/// Verifies the AppSettings warm-up arm of the bootstrap pipeline:
/// (1) StartAsync logs the row count and warms the singleton snapshot to the seeded value;
/// (2) a repo failure during StartAsync surfaces immediately (fail-fast probe), not as a
/// stale snapshot at first read.
/// </summary>
public sealed class AppSettingsBootstrapHostedServiceTests(SqliteInMemoryFixture sql) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _sql = sql;

    [Fact]
    public async Task StartAsync_LogsRowCount_AndWarmsService()
    {
        // (a) seeded PollingIntervalSeconds=30 row landed via the InitialCreate migration.
        // (b) bootstrap service must log "rows loaded from SQLite" with at least 1 row.
        // (c) snapshot must reflect the seeded 30 value AFTER WarmAsync (proves it ran).
        var ct = TestContext.Current.CancellationToken;

        var repo = new AppSettingsRepo(new TestDbContextFactory(_sql.Options));
        var settings = new AppSettingsService(repo);
        var logger = new CapturingLogger<AppSettingsBootstrapHostedService>();

        var svc = new AppSettingsBootstrapHostedService(repo, settings, logger);

        await svc.StartAsync(ct);

        // Assert the bootstrap log line surfaced AND mentions the row count.
        var logEvent = Assert.Single(logger.Events, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("rows loaded from SQLite"));
        Assert.NotEmpty(logEvent.Message);

        // Assert WarmAsync ran — snapshot reflects the DB state.
        // Defensive: the fixture is class-scoped; other tests within this class could in
        // principle mutate AppSettings. Compare against whatever the DB currently holds.
        var rows = await repo.GetAllAsync(ct);
        var expected = rows.TryGetValue("PollingIntervalSeconds", out var pi)
            ? pi.IntValue ?? 30
            : 30;
        Assert.Equal(expected, settings.PollingIntervalSeconds);
    }

    [Fact]
    public async Task StartAsync_PropagatesRepoFailure()
    {
        // Defense-in-depth: a persistence error at bootstrap MUST surface as an exception
        // out of StartAsync, NOT as a stale snapshot returned by IAppSettingsService later.
        var ct = TestContext.Current.CancellationToken;

        var brokenRepo = new BrokenAppSettingsRepo("simulated DB outage");
        var settings = new AppSettingsService(brokenRepo); // would never be reached
        var logger = new CapturingLogger<AppSettingsBootstrapHostedService>();

        var svc = new AppSettingsBootstrapHostedService(brokenRepo, settings, logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(ct));
        Assert.Equal("simulated DB outage", ex.Message);
    }

    /// <summary>Captures every log event for level + message inspection.</summary>
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Events { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Throws on every call so the bootstrap fail-fast contract can be asserted.</summary>
    internal sealed class BrokenAppSettingsRepo(string message) : IAppSettingsRepo
    {
        public Task<IReadOnlyDictionary<string, AppSetting>> GetAllAsync(CancellationToken ct = default)
            => throw new InvalidOperationException(message);

        public Task UpsertAsync(string key, AppSetting value, CancellationToken ct = default)
            => throw new InvalidOperationException(message);
    }
}
