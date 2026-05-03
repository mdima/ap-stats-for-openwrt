using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWrtApStats.Services.Bootstrap;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Bootstrap;

/// <summary>
/// Verifies the INF-07 / D-01 / D-02 writability probe both arms (success and failure).
/// <para>
/// Production paths <c>/data</c>, <c>/data/keys</c>, <c>/logs</c> are hard-coded per Phase 1
/// D-13 and cannot be probed safely from a Windows or CI Linux test host. The service exposes
/// an <c>internal</c> constructor that accepts a caller-supplied path list — tests use it
/// to point the probe at <see cref="TempDirectoryFixture"/>-owned directories for the success
/// arm, and at a pre-existing FILE path for the failure arm (<see cref="Directory.CreateDirectory"/>
/// throws <see cref="IOException"/> when a non-directory exists at the target name on both
/// Windows and Linux — cross-platform IOException trigger).
/// </para>
/// <para>
/// Every fact wraps the body in the standard Environment.ExitCode save/restore guard from the
/// <see cref="DataProtectionHealthHostedServiceTests"/> analog so a failing assertion does not
/// leak a non-zero exit code into sibling tests.
/// </para>
/// </summary>
public sealed class WritabilityCheckHostedServiceTests(TempDirectoryFixture temp) : IClassFixture<TempDirectoryFixture>
{
    private readonly TempDirectoryFixture _temp = temp;

    [Fact]
    public async Task SuccessArm_DoesNotSetExitCode_DoesNotStopApplication()
    {
        // D-01 negative arm: all probe paths are writable. StartAsync returns without setting
        // ExitCode and without calling StopApplication; no Error events are captured.
        var ct = TestContext.Current.CancellationToken;

        var lifetime = new FakeLifetime();
        var logger = new CapturingLogger<WritabilityCheckHostedService>();

        var paths = new[]
        {
            Path.Combine(_temp.Root, "success-data"),
            Path.Combine(_temp.Root, "success-data", "keys"),
            Path.Combine(_temp.Root, "success-logs"),
        };

        var svc = new WritabilityCheckHostedService(lifetime, logger, paths);

        var savedExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;

            await svc.StartAsync(ct);

            Assert.False(lifetime.StopRequested, "Probe must NOT call StopApplication when all paths are writable.");
            Assert.Equal(0, Environment.ExitCode);
            Assert.DoesNotContain(logger.Events, e => e.Level == LogLevel.Error);
            Assert.DoesNotContain(logger.Events, e => e.Level == LogLevel.Critical);
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public async Task SuccessArm_LeavesNoSentinelFiles()
    {
        // D-01 sentinel-cleanup invariant: after a successful probe, no `.write-probe-*` files
        // remain in any of the probed directories. The probe writes + deletes the sentinel
        // synchronously inside ProbeWritable; if delete fails the test fails.
        var ct = TestContext.Current.CancellationToken;

        var lifetime = new FakeLifetime();
        var logger = new CapturingLogger<WritabilityCheckHostedService>();

        var dataDir = Path.Combine(_temp.Root, "cleanup-data");
        var keysDir = Path.Combine(dataDir, "keys");
        var logsDir = Path.Combine(_temp.Root, "cleanup-logs");
        var paths = new[] { dataDir, keysDir, logsDir };

        var svc = new WritabilityCheckHostedService(lifetime, logger, paths);

        var savedExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            await svc.StartAsync(ct);

            foreach (var dir in paths)
            {
                var residue = Directory.EnumerateFiles(dir, ".write-probe-*").ToList();
                Assert.Empty(residue);
            }
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public async Task FailureArm_IOException_SetsExitCode78_StopsApplication_LogsError()
    {
        // D-02 positive arm: target path is a pre-existing FILE, so Directory.CreateDirectory
        // throws IOException ("Cannot create ... because a file or directory with the same name
        // already exists" — cross-platform behavior on Windows and Linux). Probe MUST:
        //   - log Error (NOT Critical — distinct from DataProtectionHealthHostedService)
        //   - set Environment.ExitCode = 78 (EX_CONFIG)
        //   - call StopApplication
        //   - short-circuit on the FIRST failure (subsequent paths not probed)
        var ct = TestContext.Current.CancellationToken;

        // Pre-create a regular file at the target path. Directory.CreateDirectory(filePath)
        // raises IOException on both Windows (".. already exists as a file") and Linux
        // ("Cannot create '...': File exists").
        var blockerFile = Path.Combine(_temp.Root, "blocker-file");
        await File.WriteAllTextAsync(blockerFile, "blocker", ct);

        var lifetime = new FakeLifetime();
        var logger = new CapturingLogger<WritabilityCheckHostedService>();

        var paths = new[] { blockerFile };

        var svc = new WritabilityCheckHostedService(lifetime, logger, paths);

        var savedExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;

            await svc.StartAsync(ct);

            Assert.True(lifetime.StopRequested, "Probe MUST call StopApplication when a path is unwritable.");
            Assert.Equal(78, Environment.ExitCode);
            Assert.Contains(logger.Events, e => e.Level == LogLevel.Error);
            // Belt+braces: ensure NOT Critical (LogLevel discrimination per RESEARCH Finding #3 Shape A).
            Assert.DoesNotContain(logger.Events, e => e.Level == LogLevel.Critical);
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public async Task FailureArm_ShortCircuits_OnFirstUnwritablePath()
    {
        // D-02 ordering guarantee: the per-path try/catch returns from StartAsync immediately
        // on the first failure. We verify by placing the blocker FIRST in the path list and
        // asserting the second (writable) path was NOT touched (no .write-probe-* file
        // appeared and no second log event captured beyond the single Error).
        var ct = TestContext.Current.CancellationToken;

        var blockerFile = Path.Combine(_temp.Root, "shortcircuit-blocker");
        await File.WriteAllTextAsync(blockerFile, "blocker", ct);

        var laterDir = Path.Combine(_temp.Root, "should-not-be-probed");
        Directory.CreateDirectory(laterDir);

        var lifetime = new FakeLifetime();
        var logger = new CapturingLogger<WritabilityCheckHostedService>();

        var svc = new WritabilityCheckHostedService(lifetime, logger, new[] { blockerFile, laterDir });

        var savedExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;

            await svc.StartAsync(ct);

            Assert.True(lifetime.StopRequested);
            Assert.Equal(78, Environment.ExitCode);

            // Exactly ONE Error log event — the second path was never probed.
            Assert.Single(logger.Events, e => e.Level == LogLevel.Error);

            // No sentinel residue in the second directory — proves the loop short-circuited.
            var residue = Directory.EnumerateFiles(laterDir, ".write-probe-*").ToList();
            Assert.Empty(residue);
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
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
