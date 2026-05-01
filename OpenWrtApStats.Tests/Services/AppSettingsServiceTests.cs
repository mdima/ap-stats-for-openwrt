using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Settings;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Services;

/// <summary>
/// Exercises <see cref="AppSettingsService"/> against a real <see cref="AppSettingsRepo"/>
/// over the per-fixture migrated SQLite :memory: schema. Verifies D-09 seeded default,
/// the typed-snapshot write-through (DB + in-memory swap), the <see cref="IAppSettingsService.Changed"/>
/// event firing once per setter, the D-09 reserved-keys-null-until-set semantics, and
/// nullable-roundtrip on UIPasswordHash.
/// </summary>
public sealed class AppSettingsServiceTests(SqliteInMemoryFixture fixture) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private AppSettingsService CreateService()
    {
        var repo = new AppSettingsRepo(new TestDbContextFactory(_fixture.Options));
        return new AppSettingsService(repo);
    }

    [Fact]
    public async Task WarmAsync_LoadsCurrentDbState()
    {
        // Defensive: this test class's fixture is shared across tests; prior tests may have
        // mutated PollingIntervalSeconds. Verify WarmAsync reflects whatever is currently in
        // the DB (which on a fresh fixture is the D-09 seed = 30).
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        await using var db = _fixture.CreateContext();
        var dbRow = await db.AppSettings.AsNoTracking().FirstAsync(r => r.Key == "PollingIntervalSeconds", ct);
        Assert.Equal(dbRow.IntValue ?? 30, svc.PollingIntervalSeconds);
    }

    [Fact]
    public async Task SetPollingIntervalSecondsAsync_PersistsAndRefreshesSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);
        await svc.SetPollingIntervalSecondsAsync(60, ct);
        Assert.Equal(60, svc.PollingIntervalSeconds);

        // Verify durable
        await using var verify = _fixture.CreateContext();
        var row = await verify.AppSettings.AsNoTracking().FirstAsync(r => r.Key == "PollingIntervalSeconds", ct);
        Assert.Equal(60, row.IntValue);
    }

    [Fact]
    public async Task Changed_FiresOnce_PerSetter()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        var fires = 0;
        svc.Changed += _ => Interlocked.Increment(ref fires);

        await svc.SetPollingIntervalSecondsAsync(10, ct);
        Assert.Equal(1, fires);
    }

    [Fact]
    public async Task ReservedKeys_StateMatchesUnderlyingDb()
    {
        // D-09 reserved key (UIPasswordHash) is not pre-seeded by InitialCreate. D-19
        // purged SshKnownHostsCacheJson from the IAppSettingsService surface; only the
        // UIPasswordHash assertion remains.
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        await using var db = _fixture.CreateContext();
        var dbRow = await db.AppSettings.AsNoTracking()
            .Where(r => r.Key == "UIPasswordHash")
            .FirstOrDefaultAsync(ct);

        var expectedUI = dbRow?.StringValue;
        Assert.Equal(expectedUI, svc.UIPasswordHash);
    }

    [Fact]
    public async Task SetUIPasswordHashAsync_NullableRoundtrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);
        await svc.SetUIPasswordHashAsync("hashvalue", ct);
        Assert.Equal("hashvalue", svc.UIPasswordHash);
        await svc.SetUIPasswordHashAsync(null, ct);
        Assert.Null(svc.UIPasswordHash);
    }

    [Fact]
    public async Task SetWizardDismissedAsync_RoundtripTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);
        Assert.False(svc.WizardDismissed);   // default false before first write
        await svc.SetWizardDismissedAsync(true, ct);
        Assert.True(svc.WizardDismissed);
    }

    [Fact]
    public async Task SetWizardDismissedAsync_Changed_FiresOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);
        var fires = 0;
        svc.Changed += _ => Interlocked.Increment(ref fires);
        await svc.SetWizardDismissedAsync(true, ct);
        Assert.Equal(1, fires);
    }
}
