using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data;
using Xunit;

namespace ApStatsForOpenWrt.Tests.TestSupport;

/// <summary>
/// Per-test SQLite :memory: fixture. Holds one <see cref="SqliteConnection"/> open for the
/// fixture lifetime — closing it would drop the in-memory database (RESEARCH Pitfall 2).
/// Each call to <see cref="CreateContext"/> returns a fresh EF context bound to the same
/// in-memory database. The InitialCreate migration is applied once in
/// <see cref="InitializeAsync"/>; subsequent contexts see the migrated schema.
/// </summary>
public sealed class SqliteInMemoryFixture : IAsyncLifetime
{
    private SqliteConnection? _connection;
    private DbContextOptions<DatabaseContext>? _options;

    /// <summary>
    /// Open a shared-cache :memory: connection with a per-fixture unique data-source
    /// name so parallel xUnit v3 test classes do not share state, then run MigrateAsync
    /// against it once so every context the fixture hands out sees the schema + seed.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        var dataSource = $"Sharable-{Guid.NewGuid():N}";
        _connection = new SqliteConnection($"Data Source={dataSource};Mode=Memory;Cache=Shared");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite(_connection)
            .Options;

        // Apply migrations once at fixture init — schema is shared across all contexts the
        // fixture hands out, because the held-open connection backs the same in-memory DB.
        await using var seed = new DatabaseContext(_options);
        await seed.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns a new EF context bound to the held-open SQLite connection. Caller should
    /// <c>await using</c> the returned context.
    /// </summary>
    public DatabaseContext CreateContext() =>
        new(_options ?? throw new InvalidOperationException("InitializeAsync has not run."));

    /// <summary>The connection that any EF context bound to this fixture must use.</summary>
    public SqliteConnection Connection =>
        _connection ?? throw new InvalidOperationException("InitializeAsync has not run.");

    /// <summary>The shared options instance — exposed so callers that need to compose
    /// additional EF features (e.g., interceptors) can derive from it.</summary>
    public DbContextOptions<DatabaseContext> Options =>
        _options ?? throw new InvalidOperationException("InitializeAsync has not run.");
}
