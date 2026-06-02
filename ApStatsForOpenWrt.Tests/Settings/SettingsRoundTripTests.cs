using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Settings;

/// <summary>
/// Phase 17 Plan 17-01 — pins the four update-check AppSetting keys (M-UPD-02) against a real
/// <see cref="AppSettingsRepo"/> over a migrated SQLite :memory: schema. Verifies the RESOLVED
/// FINDING #1 Path B seed posture (warm-time default, NO DB row, NO EF migration): a freshly
/// warmed service over a repo where these four keys are absent reads <c>UpdateCheckEnabled=true</c>
/// (D-07 ship-armed) and three nulls. Also pins the loss-free round-trip, the
/// <see cref="DateTimeKind.Utc"/>-preserving <c>LastCheckedAtUtc</c> round-trip, and the
/// combined-write helper firing <see cref="IAppSettingsService.Changed"/> EXACTLY ONCE.
/// </summary>
public sealed class SettingsRoundTripTests(SqliteInMemoryFixture fixture) : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private AppSettingsService CreateService()
    {
        var repo = new AppSettingsRepo(new TestDbContextFactory(_fixture.Options));
        return new AppSettingsService(repo);
    }

    /// <summary>
    /// Path B seed: with no DB row present for any of the four keys, a warmed service reads
    /// <c>UpdateCheckEnabled=true</c> — the in-memory default IS the seed (M-UPD-02 observable
    /// acceptance: "fresh DB after migration carries UpdateCheckEnabled=true").
    /// </summary>
    [Fact]
    public async Task FreshWarm_UpdateCheckEnabled_DefaultsTrue_NoDbRow()
    {
        var ct = TestContext.Current.CancellationToken;

        // Ensure the key is genuinely absent (a sibling test may have written it on the shared fixture).
        await using (var seed = _fixture.CreateContext())
        {
            var row = await seed.AppSettings.FirstOrDefaultAsync(r => r.Key == "UpdateCheckEnabled", ct);
            if (row is not null) { seed.AppSettings.Remove(row); await seed.SaveChangesAsync(ct); }
        }

        var svc = CreateService();
        await svc.WarmAsync(ct);

        Assert.True(svc.UpdateCheckEnabled);

        // And confirm there is genuinely no physical row backing the default (Path B).
        await using var verify = _fixture.CreateContext();
        var present = await verify.AppSettings.AsNoTracking()
            .AnyAsync(r => r.Key == "UpdateCheckEnabled", ct);
        Assert.False(present);
    }

    /// <summary>
    /// Path B seed: the three optional keys default to null with no DB row required.
    /// </summary>
    [Fact]
    public async Task FreshWarm_OptionalKeys_DefaultNull_NoDbRow()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var seed = _fixture.CreateContext())
        {
            foreach (var key in new[] { "LatestKnownVersion", "LatestReleaseUrl", "LastCheckedAtUtc" })
            {
                var row = await seed.AppSettings.FirstOrDefaultAsync(r => r.Key == key, ct);
                if (row is not null) seed.AppSettings.Remove(row);
            }
            await seed.SaveChangesAsync(ct);
        }

        var svc = CreateService();
        await svc.WarmAsync(ct);

        Assert.Null(svc.LatestKnownVersion);
        Assert.Null(svc.LatestReleaseUrl);
        Assert.Null(svc.LastCheckedAtUtc);
    }

    [Fact]
    public async Task SetLatestKnownVersionAsync_RoundTrips_NoLoss()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        await svc.SetLatestKnownVersionAsync("1.2.4", ct);
        Assert.Equal("1.2.4", svc.LatestKnownVersion);

        // Durable
        await using var verify = _fixture.CreateContext();
        var row = await verify.AppSettings.AsNoTracking().FirstAsync(r => r.Key == "LatestKnownVersion", ct);
        Assert.Equal("1.2.4", row.StringValue);
    }

    [Fact]
    public async Task SetLatestReleaseUrlAsync_RoundTrips_NoLoss()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        await svc.SetLatestReleaseUrlAsync("https://github.com/owner/repo/releases/tag/v1.2.4", ct);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v1.2.4", svc.LatestReleaseUrl);
    }

    /// <summary>
    /// T-17-01-01 — LastCheckedAtUtc must round-trip with <see cref="DateTimeKind.Utc"/> preserved.
    /// SQLite returns Kind=Unspecified; the service re-attaches UTC on read (SpecifyKind discipline,
    /// mirroring Dashboard.razor RelativeTime). A malformed/Unspecified value would silently shift
    /// the "discovered Xh ago" diagnostic by the local UTC offset.
    /// </summary>
    [Fact]
    public async Task SetLastCheckedAtUtcAsync_RoundTrips_KindUtcPreserved()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        var when = DateTime.UtcNow;
        await svc.SetLastCheckedAtUtcAsync(when, ct);

        var read = svc.LastCheckedAtUtc;
        Assert.NotNull(read);
        Assert.Equal(DateTimeKind.Utc, read!.Value.Kind);
        // Round-trippable `o` format preserves sub-second precision exactly.
        Assert.Equal(when, read.Value);
    }

    [Fact]
    public async Task SetLastCheckedAtUtcAsync_Null_RoundTripsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        await svc.SetLastCheckedAtUtcAsync(DateTime.UtcNow, ct);
        Assert.NotNull(svc.LastCheckedAtUtc);

        await svc.SetLastCheckedAtUtcAsync(null, ct);
        Assert.Null(svc.LastCheckedAtUtc);
    }

    [Fact]
    public async Task SetUpdateCheckEnabledAsync_RoundTripsFalse_AndFiresChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        var fires = 0;
        svc.Changed += _ => Interlocked.Increment(ref fires);

        await svc.SetUpdateCheckEnabledAsync(false, ct);
        Assert.False(svc.UpdateCheckEnabled);
        Assert.Equal(1, fires);

        await svc.SetUpdateCheckEnabledAsync(true, ct);
        Assert.True(svc.UpdateCheckEnabled);
        Assert.Equal(2, fires);
    }

    /// <summary>
    /// The combined success-write helper used by the checker (Plan 02) on a real N→N+1 advance:
    /// it upserts all three values then re-warms + fires <see cref="IAppSettingsService.Changed"/>
    /// EXACTLY ONCE — guards against a naive three-chained-UpsertAndRefreshAsync implementation that
    /// would fire Changed three times (three badge re-renders / spurious toast triggers downstream).
    /// </summary>
    [Fact]
    public async Task SetLatestReleaseAsync_FiresChangedExactlyOnce_AndWritesAllThree()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        var fires = 0;
        svc.Changed += _ => Interlocked.Increment(ref fires);

        var when = DateTime.UtcNow;
        await svc.SetLatestReleaseAsync(
            "2.0.0",
            "https://github.com/owner/repo/releases/tag/v2.0.0",
            when,
            ct);

        Assert.Equal(1, fires);
        Assert.Equal("2.0.0", svc.LatestKnownVersion);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v2.0.0", svc.LatestReleaseUrl);
        Assert.NotNull(svc.LastCheckedAtUtc);
        Assert.Equal(DateTimeKind.Utc, svc.LastCheckedAtUtc!.Value.Kind);
        Assert.Equal(when, svc.LastCheckedAtUtc.Value);
    }

    // ── Phase 18 Plan 18-02 M-HIST-04 — HistoryEnabled Path-B slice ─────────────

    /// <summary>
    /// Path B seed: with no DB row present for <c>HistoryEnabled</c>, a warmed service reads
    /// <c>HistoryEnabled=true</c> — the in-memory warm-time `?? true` IS the seed (M-HIST-04
    /// default-on). Also confirms there is genuinely no physical row backing the default.
    /// </summary>
    [Fact]
    public async Task FreshWarm_HistoryEnabled_DefaultsTrue_NoDbRow()
    {
        var ct = TestContext.Current.CancellationToken;

        // Ensure the key is genuinely absent (a sibling test may have written it on the shared fixture).
        await using (var seed = _fixture.CreateContext())
        {
            var row = await seed.AppSettings.FirstOrDefaultAsync(r => r.Key == "HistoryEnabled", ct);
            if (row is not null) { seed.AppSettings.Remove(row); await seed.SaveChangesAsync(ct); }
        }

        var svc = CreateService();
        await svc.WarmAsync(ct);

        Assert.True(svc.HistoryEnabled);

        await using var verify = _fixture.CreateContext();
        var present = await verify.AppSettings.AsNoTracking()
            .AnyAsync(r => r.Key == "HistoryEnabled", ct);
        Assert.False(present);
    }

    /// <summary>
    /// M-HIST-04 round-trip: setting false then re-warming reads false back; setting true reads true.
    /// The setter fires <see cref="IAppSettingsService.Changed"/> exactly once per call (the
    /// wake-on-toggle decorator in Plan 04 subscribes to this signal).
    /// </summary>
    [Fact]
    public async Task SetHistoryEnabledAsync_RoundTripsFalse_AndFiresChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = CreateService();
        await svc.WarmAsync(ct);

        var fires = 0;
        svc.Changed += _ => Interlocked.Increment(ref fires);

        await svc.SetHistoryEnabledAsync(false, ct);
        Assert.False(svc.HistoryEnabled);
        Assert.Equal(1, fires);

        // Durable: a fresh service over the same DB reads the persisted false.
        var svc2 = CreateService();
        await svc2.WarmAsync(ct);
        Assert.False(svc2.HistoryEnabled);

        await svc.SetHistoryEnabledAsync(true, ct);
        Assert.True(svc.HistoryEnabled);
        Assert.Equal(2, fires);
    }
}
