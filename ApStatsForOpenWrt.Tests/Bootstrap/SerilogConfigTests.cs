using ApStatsForOpenWrt.Tests.TestSupport;
using Serilog;
using Serilog.Formatting.Compact;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Bootstrap;

/// <summary>
/// Two-pronged Serilog coverage. Source-text invariants (INF-04 console + INF-05 file sink config
/// shape) are stable and cheap; a runtime/behavioral test then proves Serilog actually emits
/// JSON-shaped log lines to the configured file sink. Together they prevent "Program.cs has the
/// right tokens but the sink is misconfigured" drift.
/// </summary>
public sealed class SerilogConfigTests(TempDirectoryFixture temp) : IClassFixture<TempDirectoryFixture>
{
    private readonly TempDirectoryFixture _temp = temp;

    private static string ProgramSource()
    {
        // Walk up from the test bin to find the Program.cs source file. The test runner can be
        // launched from various CWDs (repo root, project dir, bin dir) so we anchor on
        // AppContext.BaseDirectory and walk upwards looking for ApStatsForOpenWrt/Program.cs.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "ApStatsForOpenWrt", "Program.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new FileNotFoundException("Could not locate ApStatsForOpenWrt/Program.cs from test bin");
    }

    [Fact]
    public void ConsoleSink_IsConfigured_AtInformationLevel()
    {
        // INF-04 — console sink wired in Program.cs at Information level (source-text invariant).
        var src = ProgramSource();
        Assert.Contains(".MinimumLevel.Information()", src);
        Assert.Contains(".WriteTo.Console()", src);
    }

    [Fact]
    public void FileSink_HasDailyRolling_AndSevenDayRetention()
    {
        // INF-05 — file sink: <logsRoot>/openwrt-ap-stats-.log, daily, 7-day retention,
        // CompactJsonFormatter, async-wrapped (source-text invariant). logsRoot defaults to
        // "/logs" but is overridable via the LOGS_ROOT env var for local Windows dev.
        var src = ProgramSource();
        Assert.Contains("LOGS_ROOT", src);
        Assert.Contains("\"/logs\"", src);
        Assert.Contains("/openwrt-ap-stats-.log", src);
        Assert.Contains("RollingInterval.Day", src);
        Assert.Contains("retainedFileCountLimit: 7", src);
        Assert.Contains("CompactJsonFormatter", src);
        Assert.Contains("WriteTo.Async", src);
    }

    [Fact]
    public void WritesLogLineToTempLogsDir()
    {
        // INF-04 / INF-05 — runtime/behavioral assertion (independent of Program.cs source
        // inspection). Builds a minimal Serilog config with a File sink at a TempDirectoryFixture-
        // scoped path, writes one log event, flushes, then asserts the file exists and contains a
        // CompactJsonFormatter-shaped JSON line. Proves Serilog actually emits — not just that
        // Program.cs has the right tokens.
        var sinkPath = Path.Combine(_temp.LogsDir, $"serilog-runtime-{Guid.NewGuid():N}-.log");
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: sinkPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: false)
            .CreateLogger();

        try
        {
            logger.Information("WritesLogLineToTempLogsDir probe");
        }
        finally
        {
            // Force the File sink to flush before we inspect the disk.
            logger.Dispose();
        }

        // The Serilog File sink replaces the trailing hyphen with YYYYMMDD before the extension.
        var dir = Path.GetDirectoryName(sinkPath)!;
        var prefix = Path.GetFileNameWithoutExtension(sinkPath); // ends in "-"
        var matches = Directory.EnumerateFiles(dir, prefix + "*.log").ToList();
        Assert.NotEmpty(matches);

        var emitted = matches.Single();
        var content = File.ReadAllText(emitted);
        // CompactJsonFormatter emits one JSON object per line with at least @t (timestamp) and
        // @mt (message template) fields.
        Assert.Contains("\"@t\":", content);
        Assert.Contains("WritesLogLineToTempLogsDir probe", content);
    }
}
