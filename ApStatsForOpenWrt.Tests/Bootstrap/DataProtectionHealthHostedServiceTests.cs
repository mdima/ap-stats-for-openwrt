using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Bootstrap;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Bootstrap;

/// <summary>
/// Verifies the D-12 key-ring health probe both arms (fires + does not fire). Uses a real
/// <see cref="NodeRepo"/> over <see cref="SqliteInMemoryFixture"/> so the probe seam
/// (<see cref="INodeRepo.AnyEncryptedPasswordsAsync"/>) is exercised end-to-end through EF —
/// PER-04 firewall stays honest in tests.
/// </summary>
public sealed class DataProtectionHealthHostedServiceTests(SqliteInMemoryFixture sql) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _sql = sql;

    [Fact]
    public async Task Probe_NoOp_WhenWiFiNodesEmpty()
    {
        // D-12 negative arm: empty DB + empty key ring -> probe does NOTHING (no exit-code
        // change, no StopApplication invocation). We do NOT need to seed anything; the
        // SqliteInMemoryFixture starts with zero WiFiNodes (only the seed AppSetting exists).
        var ct = TestContext.Current.CancellationToken;

        var nodeRepo = new NodeRepo(new TestDbContextFactory(_sql.Options));
        var lifetime = new FakeLifetime();
        var logger = new CapturingLogger<DataProtectionHealthHostedService>();

        var svc = new DataProtectionHealthHostedService(nodeRepo, lifetime, logger);

        // Pin Environment.ExitCode to a known sentinel so we can detect a change.
        var savedExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;

            await svc.StartAsync(ct);

            Assert.False(lifetime.StopRequested, "Probe must NOT call StopApplication when DB has no encrypted creds.");
            Assert.Equal(0, Environment.ExitCode);
            Assert.DoesNotContain(logger.Events, e => e.Level == LogLevel.Critical);
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public async Task Probe_FailsLoud_WhenPasswordsExistAndKeyRingMissing()
    {
        // D-12 positive arm: insert a WiFiNode with a non-empty Password, point the probe at
        // an EMPTY temp keys dir, run StartAsync, expect:
        //   - lifetime.StopRequested == true
        //   - Environment.ExitCode == 1
        //   - a Critical log event was captured
        var ct = TestContext.Current.CancellationToken;

        // Seed a row carrying ciphertext-shaped data so AnyEncryptedPasswordsAsync returns true.
        // Use a unique hostname so parallel tests in this class do not collide on the unique index.
        var hostname = $"d12-positive-{Guid.NewGuid():N}";
        await using (var db = _sql.CreateContext())
        {
            db.WiFiNodes.Add(new WiFiNode
            {
                Hostname     = hostname,
                Username     = "root",
                Password     = "ciphertext-blob",
                Status       = Status.Offline,
                LastPollDate = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        var nodeRepo = new NodeRepo(new TestDbContextFactory(_sql.Options));
        var lifetime = new FakeLifetime();
        var logger = new CapturingLogger<DataProtectionHealthHostedService>();

        var svc = new DataProtectionHealthHostedService(nodeRepo, lifetime, logger);

        // The service hardcodes "/data/keys" per D-13. On Windows that path is a drive-root
        // location ("C:\data\keys" on the current drive). We rely on Directory.Exists OR
        // EnumerateFiles("*.xml") returning Empty — which is the case on a clean test
        // environment. To be defensive against an operator who actually has /data/keys on
        // their dev box, we check first and skip-with-Inconclusive if the dir is populated.
        if (Directory.Exists("/data/keys") && Directory.EnumerateFiles("/data/keys", "*.xml").Any())
        {
            // Pre-existing key ring on the dev machine — assertion is meaningless, so we
            // mark the test as skipped rather than producing a false failure.
            Assert.Skip("/data/keys is populated on this machine; D-12 positive arm cannot be verified here. Run on a clean container or temp-redirected variant.");
            return;
        }

        var savedExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;

            await svc.StartAsync(ct);

            Assert.True(lifetime.StopRequested, "Probe MUST call StopApplication when encrypted creds exist but /data/keys is empty.");
            Assert.Equal(1, Environment.ExitCode);
            Assert.Contains(logger.Events, e => e.Level == LogLevel.Critical);
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
            // Clean up the seeded row so other tests in this class are not affected.
            await using var db = _sql.CreateContext();
            var row = await db.WiFiNodes.SingleAsync(n => n.Hostname == hostname, ct);
            db.WiFiNodes.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Captures <see cref="StopApplication"/> calls without needing a real host.</summary>
    internal sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public bool StopRequested { get; private set; }
        public void StopApplication() => StopRequested = true;
    }

    /// <summary>Records every log event so tests can assert on level + message.</summary>
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Events { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, formatter(state, exception)));
    }
}
