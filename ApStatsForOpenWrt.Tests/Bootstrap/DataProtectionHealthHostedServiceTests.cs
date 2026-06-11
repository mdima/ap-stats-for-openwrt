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
/// Verifies the D-12 key-ring health probe arms: no-creds no-op, fail-loud (creds + empty key
/// ring), and no-op (creds + populated key ring). Uses a real <see cref="NodeRepo"/> over
/// <see cref="SqliteInMemoryFixture"/> so the probe seam
/// (<see cref="INodeRepo.AnyEncryptedPasswordsAsync"/>) is exercised end-to-end through EF —
/// PER-04 firewall stays honest in tests.
/// <para>
/// 260609-9fe: the SUT now probes <see cref="BootstrapPaths.KeysDir"/> (the <c>DATA_ROOT</c>-env
/// resolver). The <c>BootstrapRootsModuleInit</c> [ModuleInitializer] points <c>DATA_ROOT</c> at a
/// writable temp dir and pre-creates an EMPTY <c>{root}/keys</c>, so the key-ring state is driven
/// DETERMINISTICALLY in <c>BootstrapPaths.KeysDir</c> (delete stray <c>*.xml</c> for the empty arm;
/// drop a non-PII dummy <c>*.xml</c> for the populated arm, cleaned up in <c>finally</c>) — no
/// <c>Assert.Skip</c> / no host-drive <c>/data/keys</c> dependency.
/// </para>
/// </summary>
/// <remarks>
/// 260609-9fe: serialized with <c>DataProtectionHealthCheckTests</c> via
/// <c>[Collection("DataProtectionKeyRingSerial")]</c> (DisableParallelization). Both classes drive
/// the SAME process-global state — <see cref="BootstrapPaths.KeysDir"/> (env-var driven) and
/// <see cref="Environment.ExitCode"/> — so their populated/empty key-ring arms would otherwise race
/// on the shared keys dir (one arm's dummy <c>*.xml</c> deleted by another arm's cleanup, observed
/// as an <c>IOException</c>). Serializing the two classes removes that cross-class collision.
/// </remarks>
[Collection("DataProtectionKeyRingSerial")]
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
        // D-12 fail-loud arm: insert a WiFiNode with a non-empty Password, ensure the probed
        // key-ring dir (BootstrapPaths.KeysDir) exists but holds ZERO *.xml, run StartAsync, expect:
        //   - lifetime.StopRequested == true
        //   - Environment.ExitCode == 1
        //   - a Critical log event was captured
        //
        // Deterministic (no Assert.Skip): the SUT now probes BootstrapPaths.KeysDir, which the
        // test ModuleInitializer (BootstrapRootsModuleInit) pre-creates as an EMPTY writable temp
        // {DATA_ROOT}/keys. Be defensive against a stray *.xml left by a positive arm by deleting
        // any present at arm start so the "key ring empty" condition is guaranteed.
        var ct = TestContext.Current.CancellationToken;

        // Seed a row carrying ciphertext-shaped data so AnyEncryptedPasswordsAsync returns true.
        // Use a unique hostname so parallel tests in this class do not collide on the unique index.
        var hostname = $"d12-negative-{Guid.NewGuid():N}";
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

        // Guarantee the empty-key-ring condition deterministically: KeysDir exists (ModuleInit
        // pre-creates it) with no *.xml. Clear any stray *.xml a parallel positive arm may have
        // written into the shared dir.
        Directory.CreateDirectory(BootstrapPaths.KeysDir);
        foreach (var stray in Directory.EnumerateFiles(BootstrapPaths.KeysDir, "*.xml"))
            File.Delete(stray);

        var savedExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;

            await svc.StartAsync(ct);

            Assert.True(lifetime.StopRequested, "Probe MUST call StopApplication when encrypted creds exist but the key-ring dir is empty.");
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

    [Fact]
    public async Task Probe_NoOp_WhenPasswordsExistAndKeyRingPopulated()
    {
        // D-12 positive arm: encrypted creds exist AND the key-ring dir holds ≥ 1 *.xml → the
        // probe is a no-op (no StopApplication, ExitCode unchanged, no Critical). Deterministic:
        // drop a dummy non-PII *.xml into BootstrapPaths.KeysDir and clean it up in finally.
        var ct = TestContext.Current.CancellationToken;

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

        // Non-PII placeholder — NEVER real key material. A unique name avoids collision with a
        // parallel arm sharing the env-driven KeysDir; cleaned up in finally.
        Directory.CreateDirectory(BootstrapPaths.KeysDir);
        var dummyKey = Path.Combine(BootstrapPaths.KeysDir, $"key-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(dummyKey, "<key/>", ct);

        var savedExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;

            await svc.StartAsync(ct);

            Assert.False(lifetime.StopRequested, "Probe must NOT call StopApplication when the key ring is populated.");
            Assert.Equal(0, Environment.ExitCode);
            Assert.DoesNotContain(logger.Events, e => e.Level == LogLevel.Critical);
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
            if (File.Exists(dummyKey)) File.Delete(dummyKey);
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

/// <summary>
/// 260609-9fe — serializes the two DataProtection key-ring test classes
/// (<see cref="DataProtectionHealthHostedServiceTests"/> and
/// <c>ApStatsForOpenWrt.Tests.Health.DataProtectionHealthCheckTests</c>). Both drive the same
/// process-global <see cref="BootstrapPaths.KeysDir"/> (env-var driven) and
/// <see cref="Environment.ExitCode"/>; running their populated/empty key-ring arms in parallel
/// races on the shared keys dir. <c>DisableParallelization</c> removes that cross-class collision.
/// </summary>
[CollectionDefinition("DataProtectionKeyRingSerial", DisableParallelization = true)]
public sealed class DataProtectionKeyRingSerialCollection;
