using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Data;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// Reusable WebApplicationFactory&lt;Program&gt; for Phase 3 integration tests.
///
/// <para>
/// Reused by:
/// <list type="bullet">
/// <item>Plan 03-01 — AdminLteAssetSmokeTests (D-20 4 asset-200 checkpoints).</item>
/// <item>Plan 03-03 — AuthGateTests + RateLimitTests (SEC-05 / SEC-06 / Pitfall 1 / Pitfall 4).</item>
/// <item>Plan 03-04 — Nodes page integration tests (NODE-04 cascade, etc.).</item>
/// <item>Plan 03-05 — SettingsRoundTripTests (SET-02 enable/disable, SET-03 clear).</item>
/// </list>
/// </para>
///
/// <para>
/// The factory swaps the production SQLite <c>/data/AP-Stats-for-OpenWrt.db</c> connection string
/// for in-memory SQLite (per-test) so the host can boot without <c>/data</c> being writable in
/// CI. Hosted services (Migrations, DataProtectionHealth, AppSettingsBootstrap, Polling) still
/// run; the in-memory shared-cache connection is held open across the fixture lifetime so
/// the migration history and seeded AppSettings rows survive across requests.
/// </para>
/// </summary>
public sealed class SmokeFactoryFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dataSource = $"Sharable-{Guid.NewGuid():N}";
    private Microsoft.Data.Sqlite.SqliteConnection? _keepAlive;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Replace the production DbContextFactory with an in-memory SQLite one so
            // tests don't write to /data. EF Core 10 in-memory + shared-cache mirrors
            // the SqliteInMemoryFixture pattern from Phase 1+2.
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
        // across the requests issued by individual tests (closing the last connection
        // drops the in-memory database — RESEARCH Pitfall 2 binding).
        _keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dataSource};Mode=Memory;Cache=Shared");
        _keepAlive.Open();

        // Force factory to start the host eagerly so the smoke tests don't race startup.
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
