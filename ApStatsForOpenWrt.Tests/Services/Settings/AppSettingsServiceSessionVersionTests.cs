using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Services.Settings;

/// <summary>
/// M-SEC-04 — pins the SessionVersion AppSetting + the bump-on-password-change invariant.
///
/// <para>
/// Plan 15-01 ships the AppSettings half of M-SEC-04 in isolation: introduce <c>SessionVersion</c>
/// (int, default 0) and wire <see cref="IAppSettingsService.BumpSessionVersionAsync"/> to run
/// atomically inside both the set and clear paths of
/// <see cref="IAppSettingsService.SetUIPasswordHashAsync"/>. Plan 15-04 will stamp this on the
/// auth cookie and check it in <c>OnValidatePrincipal</c>; these tests pin the bump invariant
/// so 15-04 can rely on it.
/// </para>
///
/// <para>
/// Tests are <c>[Trait("Category","SecurityFix")]</c> so the M-SEC-04 unit-test gate can be
/// selected via <c>dotnet test --filter "Category=SecurityFix"</c> for the milestone verifier.
/// </para>
///
/// <para>
/// Fixture-state hygiene: <see cref="SqliteInMemoryFixture"/> is an <see cref="IClassFixture{T}"/>
/// shared across tests in this class, so every test starts by purging any pre-existing
/// <c>SessionVersion</c> row from the <c>AppSettings</c> table. This gives a deterministic
/// "fresh DB" baseline regardless of test execution order (xUnit v3 does not guarantee order).
/// </para>
/// </summary>
[Trait("Category", "SecurityFix")]
public sealed class AppSettingsServiceSessionVersionTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    private AppSettingsService CreateService()
    {
        var repo = new AppSettingsRepo(new TestDbContextFactory(_fixture.Options));
        return new AppSettingsService(repo);
    }

    /// <summary>
    /// Reset the shared in-memory DB to a known "no SessionVersion row" state so every fact
    /// in this class has a deterministic starting point regardless of execution order.
    /// </summary>
    private async Task ResetSessionVersionRowAsync(CancellationToken ct)
    {
        await using var db = _fixture.CreateContext();
        var row = await db.AppSettings.FirstOrDefaultAsync(r => r.Key == "SessionVersion", ct);
        if (row is not null)
        {
            db.AppSettings.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    // ── Test 1: Default 0 on a fresh DB (no AppSetting row) ─────────────────

    /// <summary>
    /// M-SEC-04 — On a fresh service (no <c>SessionVersion</c> row in <c>AppSettings</c>),
    /// <see cref="IAppSettingsService.SessionVersion"/> reads 0 after <c>WarmAsync</c>. This
    /// is the in-memory fallback that holds until the first bump persists row #1.
    /// </summary>
    [Fact]
    public async Task SessionVersion_FreshDb_Defaults_To_Zero()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetSessionVersionRowAsync(ct);

        var svc = CreateService();
        await svc.WarmAsync(ct);

        Assert.Equal(0, svc.SessionVersion);
    }

    // ── Test 2: First SetUIPasswordHashAsync("hash") bumps 0 → 1 ────────────

    /// <summary>
    /// M-SEC-04 — Setting the UI password from "no password" to a real hash increments
    /// <c>SessionVersion</c> from 0 to exactly 1. This is the load-bearing rotation-on-set path.
    /// </summary>
    [Fact]
    public async Task SetUIPasswordHashAsync_SetPath_Bumps_To_One()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetSessionVersionRowAsync(ct);

        var svc = CreateService();
        await svc.WarmAsync(ct);
        Assert.Equal(0, svc.SessionVersion);

        await svc.SetUIPasswordHashAsync("anyhash", ct);

        Assert.Equal(1, svc.SessionVersion);
    }

    // ── Test 3: Second SetUIPasswordHashAsync increments monotonically ──────

    /// <summary>
    /// M-SEC-04 — Repeated calls to <c>SetUIPasswordHashAsync</c> increment <c>SessionVersion</c>
    /// monotonically (0 → 1 → 2). Each rotation invalidates every cookie carrying the prior
    /// claim value.
    /// </summary>
    [Fact]
    public async Task SetUIPasswordHashAsync_RepeatedSets_Increment_Monotonically()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetSessionVersionRowAsync(ct);

        var svc = CreateService();
        await svc.WarmAsync(ct);

        await svc.SetUIPasswordHashAsync("hashA", ct);
        Assert.Equal(1, svc.SessionVersion);

        await svc.SetUIPasswordHashAsync("hashB", ct);
        Assert.Equal(2, svc.SessionVersion);

        await svc.SetUIPasswordHashAsync("hashC", ct);
        Assert.Equal(3, svc.SessionVersion);
    }

    // ── Test 4a: First call is null (0 → 1) ─────────────────────────────────

    /// <summary>
    /// M-SEC-04 — Clearing the UI password (passing <c>null</c>) ALSO bumps
    /// <c>SessionVersion</c>. The mitigation is symmetric: setting OR clearing the password is
    /// the operator-driven cookie-invalidation event. First call with null: 0 → 1.
    /// </summary>
    [Fact]
    public async Task SetUIPasswordHashAsync_ClearPath_FirstCall_Bumps_To_One()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetSessionVersionRowAsync(ct);

        var svc = CreateService();
        await svc.WarmAsync(ct);
        Assert.Equal(0, svc.SessionVersion);

        await svc.SetUIPasswordHashAsync(null, ct);

        Assert.Equal(1, svc.SessionVersion);
    }

    // ── Test 4b: Null after a set bumps 1 → 2 ───────────────────────────────

    /// <summary>
    /// M-SEC-04 — Clearing the UI password after it was previously set bumps
    /// <c>SessionVersion</c> n → n+1. The clear path is NOT optimized away when the value
    /// is already the same — the bump is unconditional per the plan contract.
    /// </summary>
    [Fact]
    public async Task SetUIPasswordHashAsync_ClearAfterSet_Bumps_To_Two()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetSessionVersionRowAsync(ct);

        var svc = CreateService();
        await svc.WarmAsync(ct);

        await svc.SetUIPasswordHashAsync("hash", ct);
        Assert.Equal(1, svc.SessionVersion);

        await svc.SetUIPasswordHashAsync(null, ct);
        Assert.Equal(2, svc.SessionVersion);
    }

    // ── Test 5: BumpSessionVersionAsync called directly ─────────────────────

    /// <summary>
    /// M-SEC-04 — Direct calls to <see cref="IAppSettingsService.BumpSessionVersionAsync"/>
    /// increment <c>SessionVersion</c> by exactly 1. This is the building block used
    /// internally by <c>SetUIPasswordHashAsync</c> and is exposed for future call-sites that
    /// rotate sessions without touching the password (e.g., explicit "log everyone out").
    /// </summary>
    [Fact]
    public async Task BumpSessionVersionAsync_Increments_By_One()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetSessionVersionRowAsync(ct);

        var svc = CreateService();
        await svc.WarmAsync(ct);
        Assert.Equal(0, svc.SessionVersion);

        await svc.BumpSessionVersionAsync(ct);
        Assert.Equal(1, svc.SessionVersion);

        await svc.BumpSessionVersionAsync(ct);
        Assert.Equal(2, svc.SessionVersion);
    }

    // ── Test 6: SetSessionVersionAsync sets to literal value ────────────────

    /// <summary>
    /// M-SEC-04 — <see cref="IAppSettingsService.SetSessionVersionAsync"/> assigns a literal
    /// integer value. Production code calls <c>BumpSessionVersionAsync</c>; this explicit
    /// setter exists for tests that simulate specific deployed states (e.g., v0.11 → v0.12
    /// migration where the row already carries a non-zero counter).
    /// </summary>
    [Fact]
    public async Task SetSessionVersionAsync_Sets_Literal_Value()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetSessionVersionRowAsync(ct);

        var svc = CreateService();
        await svc.WarmAsync(ct);

        await svc.SetSessionVersionAsync(42, ct);
        Assert.Equal(42, svc.SessionVersion);

        // Verify durable: the row was persisted with the literal IntValue.
        await using var verify = _fixture.CreateContext();
        var row = await verify.AppSettings.AsNoTracking().FirstAsync(r => r.Key == "SessionVersion", ct);
        Assert.Equal(42, row.IntValue);
    }

    // ── Test 7: Atomicity-of-effect: both UIPasswordHash AND SessionVersion ──

    /// <summary>
    /// M-SEC-04 — After a call to <c>SetUIPasswordHashAsync("h")</c>, BOTH the
    /// <c>UIPasswordHash</c> read-side AND the <c>SessionVersion</c> read-side reflect the
    /// new state. Confirms the caller's perspective: there is no observable window where the
    /// password is updated but the version counter has not yet ratcheted (or vice versa).
    /// Achieved structurally because both upserts go through the same single-writer
    /// <c>IAppSettingsRepo</c> code path and <c>WarmAsync</c> publishes a single new snapshot
    /// per call.
    /// </summary>
    [Fact]
    public async Task SetUIPasswordHashAsync_BothFields_Are_Refreshed_From_Caller_Perspective()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetSessionVersionRowAsync(ct);

        var svc = CreateService();
        await svc.WarmAsync(ct);

        await svc.SetUIPasswordHashAsync("h", ct);

        Assert.Equal("h", svc.UIPasswordHash);
        Assert.Equal(1, svc.SessionVersion);
    }
}
