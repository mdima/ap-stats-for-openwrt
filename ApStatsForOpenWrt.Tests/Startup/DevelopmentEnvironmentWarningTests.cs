using System.Collections.Concurrent;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Startup;

/// <summary>
/// M-SEC-10 (Plan 15-04) smoke tests for the Development-environment startup warning.
///
/// <list type="number">
///   <item>Warning fires when ASPNETCORE_ENVIRONMENT=Development.</item>
///   <item>Warning does NOT fire in Production.</item>
/// </list>
///
/// <para>
/// Pinned message verbatim from REQUIREMENTS.md line 39 — em-dash is U+2014 NOT a hyphen.
/// </para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "SecurityFix")]
public sealed class DevelopmentEnvironmentWarningTests
{
    private const string PinnedWarningMessage =
        "ASPNETCORE_ENVIRONMENT=Development is set. This should never be true in production deployments — verify your container/orchestrator config.";

    [Fact]
    public async Task Warning_Fires_WhenEnvironmentIsDevelopment()
    {
        var captured = new ConcurrentBag<CapturedLog>();
        await using var factory = new CapturingFactory("Development", captured);
        // Eager bootstrap — startup code runs inside builder.Build()/RunAsync; the warning
        // line lives between Build and RunAsync, so CreateClient is sufficient to observe it.
        _ = factory.CreateClient();

        // String.Equals(Ordinal) so a stray smart-quote / em-dash mismatch surfaces.
        Assert.Contains(captured, log =>
            log.Category == "Startup" &&
            log.Level == LogLevel.Warning &&
            string.Equals(log.Message, PinnedWarningMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Warning_DoesNotFire_WhenEnvironmentIsProduction()
    {
        var captured = new ConcurrentBag<CapturedLog>();
        await using var factory = new CapturingFactory("Production", captured);
        _ = factory.CreateClient();

        Assert.DoesNotContain(captured, log =>
            string.Equals(log.Message, PinnedWarningMessage, StringComparison.Ordinal));
    }

    // ── Fixture + capture sink ─────────────────────────────────────────────────

    /// <summary>
    /// WebApplicationFactory variant that swaps the production logger pipeline for a
    /// pure capturing pipeline (no Serilog, no console, no file sinks). The Serilog
    /// pipeline configured in Program.cs (via builder.Host.UseSerilog) is replaced from
    /// the test side via ILoggingBuilder.ClearProviders + AddProvider(CapturingProvider).
    /// </summary>
    private sealed class CapturingFactory(string environmentName, ConcurrentBag<CapturedLog> captured)
        : WebApplicationFactory<Program>
    {
        private readonly string _environmentName = environmentName;
        private readonly ConcurrentBag<CapturedLog> _captured = captured;
        private readonly string _dataSource = $"DevEnvWarn-{Guid.NewGuid():N}";
        private Microsoft.Data.Sqlite.SqliteConnection? _keepAlive;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Program.cs wires Serilog as the host-wide logger via builder.Host.UseSerilog,
            // which DOMINATES over any IWebHostBuilder.ConfigureLogging tweak. The canonical
            // override seam (mirroring SerilogCapturingFixture) is IHostBuilder.UseSerilog on
            // the CreateHost path — the LAST UseSerilog call wins, so this replaces the
            // production pipeline with a pure capture sink.
            builder.UseSerilog((ctx, services, lc) => lc
                .MinimumLevel.Verbose()
                .WriteTo.Sink(new SerilogCaptureSink(_captured)));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_environmentName);

            builder.ConfigureServices(services =>
            {
                // In-memory SQLite so the host can boot without /data.
                var dbDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IDbContextFactory<DatabaseContext>));
                if (dbDescriptor is not null) services.Remove(dbDescriptor);
                services.AddDbContextFactory<DatabaseContext>(opts =>
                    opts.UseSqlite($"Data Source={_dataSource};Mode=Memory;Cache=Shared"));
            });

            // Keep the in-memory DB alive across the eager CreateClient call.
            _keepAlive ??= new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={_dataSource};Mode=Memory;Cache=Shared");
            if (_keepAlive.State != System.Data.ConnectionState.Open) _keepAlive.Open();
        }

        public new async ValueTask DisposeAsync()
        {
            _keepAlive?.Dispose();
            _keepAlive = null;
            await base.DisposeAsync();
        }
    }

    /// <summary>
    /// Serilog sink that captures every log event so the test can assert on category +
    /// rendered message. Mirrors the InMemorySink pattern in SerilogCapturingFixture.
    /// </summary>
    private sealed class SerilogCaptureSink(ConcurrentBag<CapturedLog> sink) : ILogEventSink
    {
        private readonly ConcurrentBag<CapturedLog> _sink = sink;

        public void Emit(LogEvent logEvent)
        {
            // Serilog's SourceContext property carries the Microsoft.Extensions.Logging
            // category name (set by Microsoft.Extensions.Logging.ILogger when bridged through
            // Serilog). app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")
            // therefore yields SourceContext = "Startup" on every event it emits.
            var category = "";
            if (logEvent.Properties.TryGetValue("SourceContext", out var ctxProp)
                && ctxProp is ScalarValue scalar
                && scalar.Value is string s)
            {
                category = s;
            }

            var level = logEvent.Level switch
            {
                LogEventLevel.Verbose => LogLevel.Trace,
                LogEventLevel.Debug => LogLevel.Debug,
                LogEventLevel.Information => LogLevel.Information,
                LogEventLevel.Warning => LogLevel.Warning,
                LogEventLevel.Error => LogLevel.Error,
                LogEventLevel.Fatal => LogLevel.Critical,
                _ => LogLevel.None,
            };

            // RenderMessage produces the formatted string (e.g. parameters substituted).
            var message = logEvent.RenderMessage();
            _sink.Add(new CapturedLog(category, level, message));
        }
    }

    private sealed record CapturedLog(string Category, LogLevel Level, string Message);
}
