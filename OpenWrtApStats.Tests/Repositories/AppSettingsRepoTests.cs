using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Repositories;

/// <summary>
/// Exercises <see cref="AppSettingsRepo"/> against the per-fixture migrated SQLite
/// :memory: schema. Asserts that the seeded D-09 PollingIntervalSeconds=30 row is
/// readable, that <c>UpsertAsync</c> is genuinely idempotent (insert-or-update), and
/// that nullable columns roundtrip through the repo without coercion.
/// </summary>
public sealed class AppSettingsRepoTests(SqliteInMemoryFixture fixture) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private IAppSettingsRepo CreateRepo() =>
        new AppSettingsRepo(new TestDbContextFactory(_fixture.Options));

    [Fact]
    public async Task GetAllAsync_AfterMigrate_IncludesSeed()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var all = await repo.GetAllAsync(ct);
        Assert.True(all.TryGetValue("PollingIntervalSeconds", out var seed));
        Assert.Equal(30, seed!.IntValue);
    }

    [Fact]
    public async Task UpsertAsync_NewKey_InsertsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var key = $"TestKey-{Guid.NewGuid():N}";
        await repo.UpsertAsync(key, new AppSetting { Key = key, StringValue = "hello" }, ct);
        var all = await repo.GetAllAsync(ct);
        Assert.True(all.TryGetValue(key, out var row));
        Assert.Equal("hello", row!.StringValue);
    }

    [Fact]
    public async Task UpsertAsync_ExistingKey_UpdatesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        // PollingIntervalSeconds is seeded to 30. Update to 60.
        await repo.UpsertAsync("PollingIntervalSeconds",
            new AppSetting { Key = "PollingIntervalSeconds", IntValue = 60 }, ct);

        await using var verify = _fixture.CreateContext();
        var rows = await verify.AppSettings.AsNoTracking()
            .Where(r => r.Key == "PollingIntervalSeconds").ToListAsync(ct);
        Assert.Single(rows);
        Assert.Equal(60, rows[0].IntValue);
    }

    [Fact]
    public async Task UpsertAsync_NullablesRoundtrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = CreateRepo();
        var key = $"Nullables-{Guid.NewGuid():N}";
        await repo.UpsertAsync(key, new AppSetting { Key = key, IntValue = 42, StringValue = null }, ct);
        var all = await repo.GetAllAsync(ct);
        Assert.True(all.TryGetValue(key, out var row));
        Assert.Null(row!.StringValue);
        Assert.Equal(42, row.IntValue);
    }
}
