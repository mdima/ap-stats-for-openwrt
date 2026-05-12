using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data.Models;
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
        // Defensive: shared fixture means a prior test may have left WizardDismissed=true
        // (see WarmAsync_LoadsCurrentDbState above). Round-trip both directions to verify
        // the setter independently of starting state.
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);
        await svc.SetWizardDismissedAsync(false, ct);
        Assert.False(svc.WizardDismissed);
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

    // ── Phase 10 Plan 10-02 SET-01 / SET-02 / D-04 ──────────────────────────

    /// <summary>
    /// SET-02 — Phase 9 SCHEMA-06 seeds <c>LocalDomainSuffixes = "[]"</c>. WarmAsync must
    /// deserialize the JSON-array StringValue into an empty list (not null).
    /// </summary>
    [Fact]
    public async Task WarmAsync_LocalDomainSuffixes_EmptyJsonArray_ProducesEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;

        // Force the seeded value back to "[]" (a sibling test may have mutated it).
        await using (var seed = _fixture.CreateContext())
        {
            var row = await seed.AppSettings.FirstOrDefaultAsync(r => r.Key == "LocalDomainSuffixes", ct);
            if (row is null)
            {
                seed.AppSettings.Add(new AppSetting { Key = "LocalDomainSuffixes", StringValue = "[]" });
            }
            else
            {
                row.StringValue = "[]";
            }
            await seed.SaveChangesAsync(ct);
        }

        var svc = CreateService();
        await svc.WarmAsync(ct);

        Assert.NotNull(svc.LocalDomainSuffixes);
        Assert.Empty(svc.LocalDomainSuffixes);
    }

    /// <summary>
    /// SET-02 — non-empty JSON-array StringValue is deserialized verbatim.
    /// </summary>
    [Fact]
    public async Task WarmAsync_LocalDomainSuffixes_NonEmptyJsonArray_ProducesList()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var seed = _fixture.CreateContext())
        {
            var row = await seed.AppSettings.FirstOrDefaultAsync(r => r.Key == "LocalDomainSuffixes", ct);
            if (row is null)
            {
                seed.AppSettings.Add(new AppSetting { Key = "LocalDomainSuffixes", StringValue = "[\"michelecasa\",\"lab.local\"]" });
            }
            else
            {
                row.StringValue = "[\"michelecasa\",\"lab.local\"]";
            }
            await seed.SaveChangesAsync(ct);
        }

        var svc = CreateService();
        await svc.WarmAsync(ct);

        Assert.Equal(2, svc.LocalDomainSuffixes.Count);
        Assert.Contains("michelecasa", svc.LocalDomainSuffixes);
        Assert.Contains("lab.local", svc.LocalDomainSuffixes);
    }

    /// <summary>
    /// SET-02 defensive path — if the persisted StringValue is not valid JSON, WarmAsync
    /// MUST return an empty list rather than throw. This protects the orchestrator's read
    /// path from a corrupted setting blocking the entire poll cycle.
    /// </summary>
    [Fact]
    public async Task WarmAsync_LocalDomainSuffixes_MalformedJson_ProducesEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var seed = _fixture.CreateContext())
        {
            var row = await seed.AppSettings.FirstOrDefaultAsync(r => r.Key == "LocalDomainSuffixes", ct);
            if (row is null)
            {
                seed.AppSettings.Add(new AppSetting { Key = "LocalDomainSuffixes", StringValue = "NOT_VALID_JSON" });
            }
            else
            {
                row.StringValue = "NOT_VALID_JSON";
            }
            await seed.SaveChangesAsync(ct);
        }

        var svc = CreateService();
        await svc.WarmAsync(ct);

        Assert.NotNull(svc.LocalDomainSuffixes);
        Assert.Empty(svc.LocalDomainSuffixes);
    }

    /// <summary>
    /// SET-02 / D-04 — empty/whitespace tokens passed to SetLocalDomainSuffixesAsync
    /// are discarded BEFORE serialization, and remaining tokens are trimmed.
    /// </summary>
    [Fact]
    public async Task SetLocalDomainSuffixesAsync_TrimsAndDiscardsEmptyTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        await svc.SetLocalDomainSuffixesAsync(new[] { "michelecasa", "  ", "  trimmed  ", "" }, ct);

        Assert.Equal(2, svc.LocalDomainSuffixes.Count);
        Assert.Equal("michelecasa", svc.LocalDomainSuffixes[0]);
        Assert.Equal("trimmed", svc.LocalDomainSuffixes[1]);

        // Verify durable: persisted JSON-array string contains only the cleaned tokens.
        await using var verify = _fixture.CreateContext();
        var row = await verify.AppSettings.AsNoTracking().FirstAsync(r => r.Key == "LocalDomainSuffixes", ct);
        Assert.Equal("[\"michelecasa\",\"trimmed\"]", row.StringValue);
    }

    /// <summary>
    /// SET-01 — SetRefreshIntervalSecondsAsync persists and the snapshot reflects the new value.
    /// </summary>
    [Fact]
    public async Task SetRefreshIntervalSecondsAsync_PersistsAndRefreshesSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        await svc.SetRefreshIntervalSecondsAsync(7200, ct);
        Assert.Equal(7200, svc.RefreshIntervalSeconds);

        // Verify durable
        await using var verify = _fixture.CreateContext();
        var row = await verify.AppSettings.AsNoTracking().FirstAsync(r => r.Key == "RefreshIntervalSeconds", ct);
        Assert.Equal(7200, row.IntValue);
    }

    /// <summary>
    /// SET-01 — Changed event fires once per call to SetRefreshIntervalSecondsAsync.
    /// </summary>
    [Fact]
    public async Task SetRefreshIntervalSecondsAsync_Changed_FiresOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        var fires = 0;
        svc.Changed += _ => Interlocked.Increment(ref fires);
        await svc.SetRefreshIntervalSecondsAsync(1800, ct);

        Assert.Equal(1, fires);
    }

    /// <summary>
    /// SET-02 — Changed event fires once per call to SetLocalDomainSuffixesAsync.
    /// </summary>
    [Fact]
    public async Task SetLocalDomainSuffixesAsync_Changed_FiresOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        var fires = 0;
        svc.Changed += _ => Interlocked.Increment(ref fires);
        await svc.SetLocalDomainSuffixesAsync(new[] { "example.com" }, ct);

        Assert.Equal(1, fires);
    }
}
