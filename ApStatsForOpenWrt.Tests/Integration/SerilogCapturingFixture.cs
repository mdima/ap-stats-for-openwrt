using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApStatsForOpenWrt.Data;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// W7 / B2 — fixture that mirrors <see cref="SmokeFactoryFixture"/>'s in-memory SQLite +
/// keep-alive connection pattern AND additionally swaps the Serilog logger pipeline for one
/// whose only sink is <see cref="TestCorrelatorSink"/>. The <c>Filter.ByExcluding</c> clause
/// MUST match the production filter in <c>Program.cs</c> (D-10) byte-for-byte so the test
/// exercises the SAME expression. Do NOT simplify the doubled-quote literal
/// <c>p.ToString() == "\"/health\""</c> — its fragility IS the reason for runtime coverage.
/// <para>
/// <see cref="SmokeFactoryFixture"/> is sealed and therefore cannot be subclassed for the W7
/// test scope. This is a sibling fixture (NOT a derived class) with the minimum surface needed
/// for the filter-regression assertion: in-memory SQLite swap + TestCorrelator sink + nothing
/// else (no shared state with SmokeFactoryFixture, no production console / file sinks — the
/// test scope cares only about the filter behaviour).
/// </para>
/// </summary>
public sealed class SerilogCapturingFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dataSource = $"SerilogCap-{Guid.NewGuid():N}";
    private SqliteConnection? _keepAlive;

    /// <summary>
    /// In-memory capture sink. Each test reads <see cref="CapturedEvents"/> and clears it via
    /// <see cref="ClearCaptured"/>. Mirrors the contract of TestCorrelator (GetEvents in-context)
    /// without relying on AsyncLocal context propagation through the WebApplicationFactory's
    /// TestServer transport — empirically, the AsyncLocal context set by `CreateContext()` does
    /// NOT survive the HttpClient -> TestServer hop, so a per-fixture in-memory sink is the
    /// canonical replacement.
    /// </summary>
    public InMemorySink Captured { get; } = new();

    public IReadOnlyList<LogEvent> CapturedEvents => Captured.Snapshot();
    public void ClearCaptured() => Captured.Clear();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Override the host's Serilog pipeline BEFORE the production UseSerilog wires up.
        // SerilogHostBuilderExtensions.UseSerilog(IHostBuilder, ...) is the canonical hook;
        // the LAST UseSerilog call wins, so this overrides Program.cs's own UseSerilog.
        // The Filter.ByExcluding clause is copied verbatim from Program.cs (D-10) so the
        // doubled-quote literal regression vector is exercised end-to-end.
        builder.UseSerilog((ctx, services, lc) => lc
            .Enrich.FromLogContext()
            .Filter.ByExcluding(e =>
                e.Properties.TryGetValue("RequestPath", out var p) &&
                p.ToString() == "\"/health\"")
            .MinimumLevel.Information()
            .WriteTo.Sink(Captured));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // In-memory SQLite — same pattern as SmokeFactoryFixture so the host can boot.
            var dbDescriptor = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IDbContextFactory<DatabaseContext>));
            if (dbDescriptor is not null) services.Remove(dbDescriptor);

            services.AddDbContextFactory<DatabaseContext>(opts =>
                opts.UseSqlite($"Data Source={_dataSource};Mode=Memory;Cache=Shared"));
        });
    }

    public ValueTask InitializeAsync()
    {
        // Hold a connection open for the fixture lifetime so the in-memory DB survives
        // across the requests issued by individual tests (closing the last connection drops
        // the in-memory database — RESEARCH Pitfall 2 binding).
        _keepAlive = new SqliteConnection($"Data Source={_dataSource};Mode=Memory;Cache=Shared");
        _keepAlive.Open();

        // Force the factory to start the host eagerly so the Serilog pipeline is fully set up
        // before any test runs.
        _ = CreateClient();
        return ValueTask.CompletedTask;
    }

    public new ValueTask DisposeAsync()
    {
        _keepAlive?.Dispose();
        base.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Thread-safe in-memory Serilog sink for the W7 / D-10 runtime assertion. Replacement for
/// Serilog.Sinks.TestCorrelator — the latter's AsyncLocal-context model does not survive the
/// WebApplicationFactory -> TestServer transport hop, so events emitted from request handlers
/// are not visible to <c>GetLogEventsFromCurrentContext()</c>. This sink stores every event in
/// a synchronized list, regardless of AsyncLocal context.
/// </summary>
public sealed class InMemorySink : ILogEventSink
{
    private readonly List<LogEvent> _events = new();
    private readonly object _gate = new();

    public void Emit(LogEvent logEvent)
    {
        lock (_gate) _events.Add(logEvent);
    }

    public IReadOnlyList<LogEvent> Snapshot()
    {
        lock (_gate) return _events.ToList();
    }

    public void Clear()
    {
        lock (_gate) _events.Clear();
    }
}
