using Microsoft.Data.Sqlite;
using Xunit;

namespace OpenWrtApStats.Tests.TestSupport;

/// <summary>
/// Per-test SQLite :memory: fixture. Holds one SqliteConnection open for the fixture
/// lifetime — closing it would drop the in-memory database (RESEARCH Pitfall 2).
/// Wave 1 will extend this fixture with a method that returns an EF context bound to
/// this connection and runs MigrateAsync(); Wave 0 only opens the connection.
/// </summary>
public sealed class SqliteInMemoryFixture : IAsyncLifetime
{
    private SqliteConnection? _connection;

    /// <summary>
    /// Open shared-cache :memory: connection. Use a per-fixture unique data-source
    /// name so parallel xUnit v3 test classes do not share state (RESEARCH Pitfall 2).
    /// </summary>
    public ValueTask InitializeAsync()
    {
        var dataSource = $"Sharable-{Guid.NewGuid():N}";
        _connection = new SqliteConnection($"Data Source={dataSource};Mode=Memory;Cache=Shared");
        _connection.Open();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The connection that any EF context bound to this fixture must use.</summary>
    public SqliteConnection Connection =>
        _connection ?? throw new InvalidOperationException("InitializeAsync has not run.");
}
