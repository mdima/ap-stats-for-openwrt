using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Services;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Services.UpdateCheck;
using Xunit;

namespace ApStatsForOpenWrt.Tests.UpdateCheck;

/// <summary>
/// Phase 17 Plan 04 / QUAL-02 — the AUTOMATED half of the forced-out-of-date verification the
/// operator LIVE-UAT references. By design the real repo has no public release yet (D-07 graceful
/// 404), so the badge/toast surfaces can only be SEEN by FORCING the out-of-date condition. This
/// suite forces it purely through the EXISTING <c>releases-latest-newer.json</c> fixture (v9.9.9 &gt;
/// the running assembly version) — it edits NO production const, so there is nothing to revert in the
/// codebase.
///
/// <para>
/// This is an integration-flavored proof that chains the as-built plan contracts end-to-end:
/// stubbed <see cref="HttpMessageHandler"/> → test <see cref="IHttpClientFactory"/> → REAL
/// <see cref="UpdateChecker"/> (Plan 02) → grown in-memory <see cref="IAppSettingsService"/> stub
/// (Plan 01) → REAL <see cref="UpdateNotifier"/> (Plan 01); <c>CheckOnceAsync</c> runs the real
/// fetch/parse/compare/persist/raise path; then the resulting settings state drives a bUnit-rendered
/// <see cref="UpdateBadge"/> (Plan 03) and a single discovery toast lands on a mounted
/// <see cref="ToastHost"/> via the real <see cref="UpdateToastBroadcaster"/> (Plan 03).
/// </para>
///
/// <para>
/// Cases:
///   1. Forced out-of-date lights up the badge: after <c>CheckOnceAsync</c> against v9.9.9 the badge
///      IS present (the inverse of Plan 03's DOM-absent-when-current test), <c>href</c> == the fixture
///      release URL, <c>aria-label</c> contains "9.9.9".
///   2. The same forced advance raises <see cref="UpdateNotifier.UpdateAvailable"/> exactly once and a
///      single toast renders on a mounted <see cref="ToastHost"/> (end-to-end transition proof).
///   3. Toggle-off inertness (D-03/e): with <c>UpdateCheckEnabled == false</c> the badge stays
///      DOM-absent even though the forced <c>LatestKnownVersion</c> &gt; the running version (the
///      air-gapped "toggle off ⇒ nothing renders" path).
/// </para>
///
/// Test stack per CLAUDE.md: xUnit v3 + bUnit, real notifier / real ToastService / real broadcaster
/// (NOT mocks). The fixture is the only forcing lever — no production const is touched.
/// </summary>
public sealed class ForcedOutOfDateTests
{
    // The release URL baked into releases-latest-newer.json (v9.9.9). The checker validates and
    // persists this verbatim to LatestReleaseUrl, and the badge renders it into the <a href>.
    private const string FixtureReleaseUrl =
        "https://github.com/mdima/ap-stats-for-openwrt/releases/tag/v9.9.9";

    // ── Test doubles (mirror UpdateCheckerTests + UpdateBadgeTests harnesses) ──

    /// <summary>Stubbed handler returning the recorded GitHub releases/latest body verbatim.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeGate : INotificationsGate
    {
        public bool IsEnabled { get; init; } = true;
    }

    /// <summary>
    /// A gate whose <c>IsEnabled</c> getter throws — makes the singleton <see cref="ToastService.Show"/>
    /// throw, which exercises the <see cref="UpdateToastBroadcaster"/>'s defensive catch path (the
    /// "a bad toast emission must never detach the subscription or bubble into the notifier's raise
    /// loop" branch). Plan-04 coverage close: the broadcaster's catch was the only net-new line below
    /// the ≥80% gate.
    /// </summary>
    private sealed class ThrowingGate : INotificationsGate
    {
        public bool IsEnabled => throw new InvalidOperationException("gate boom");
    }

    /// <summary>
    /// Mutable in-memory IAppSettingsService stub — the SAME state the checker writes through and the
    /// badge reads from. <c>SetLatestReleaseAsync</c> raises <c>Changed</c> exactly once (Plan 01
    /// contract) so the badge re-renders reactively.
    /// </summary>
    private sealed class StubSettings : IAppSettingsService
    {
        public int PollingIntervalSeconds => 30;
        public string? UIPasswordHash => null;
        public bool WizardDismissed => true;
        public int RefreshIntervalSeconds => 3600;
        public IReadOnlyList<string> LocalDomainSuffixes => Array.Empty<string>();
        public int SessionVersion => 0;
        public event Action<IAppSettingsService>? Changed;

        public bool UpdateCheckEnabled { get; set; } = true;
        public string? LatestKnownVersion { get; set; }
        public string? LatestReleaseUrl { get; set; }
        public DateTime? LastCheckedAtUtc { get; set; }

        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) { UpdateCheckEnabled = enabled; Changed?.Invoke(this); return Task.CompletedTask; }
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) { LatestKnownVersion = version; return Task.CompletedTask; }
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) { LatestReleaseUrl = url; return Task.CompletedTask; }
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) { LastCheckedAtUtc = checkedAtUtc; return Task.CompletedTask; }
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default)
        {
            LatestKnownVersion = version;
            LatestReleaseUrl = url;
            LastCheckedAtUtc = checkedAtUtc;
            Changed?.Invoke(this);
            return Task.CompletedTask;
        }
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── Fixture loading (same path convention UpdateCheckerTests uses) ─────────

    private static string LoadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UpdateCheck", "Fixtures", name));

    /// <summary>
    /// Build the REAL checker over a stubbed handler + the supplied settings, with a real notifier.
    /// </summary>
    private static (UpdateChecker checker, UpdateNotifier notifier) BuildRealChecker(StubSettings settings)
    {
        var handler = new StubHandler(HttpStatusCode.OK, LoadFixture("releases-latest-newer.json"));
        var factory = new StubHttpClientFactory(handler);
        var notifier = new UpdateNotifier();
        var checker = new UpdateChecker(
            factory, settings, notifier, NullLogger<UpdateChecker>.Instance);
        return (checker, notifier);
    }

    // ── 1. Forced out-of-date → the badge LIGHTS UP (checker → settings → badge) ──
    [Fact]
    public async Task ForcedOutOfDate_LightsUpBadge_EndToEnd()
    {
        // Fully-qualified: this file imports Bunit (Bunit.TestContext) AND xUnit (Xunit.TestContext),
        // so the bare TestContext is ambiguous (CS0104) — the cancellation token comes from xUnit.
        var ct = Xunit.TestContext.Current.CancellationToken;

        // Toggle ON; no known version yet → up-to-date posture (badge would be DOM-absent today).
        var settings = new StubSettings { UpdateCheckEnabled = true, LatestKnownVersion = null };
        var (checker, notifier) = BuildRealChecker(settings);

        // FORCE the out-of-date condition through the fixture only — the REAL checker fetches,
        // parses v9.9.9, finds it newer than the running assembly version, and persists.
        await checker.CheckOnceAsync(ct);

        // The checker advanced the shared settings state (no production const edited).
        Assert.Equal("9.9.9", settings.LatestKnownVersion);
        Assert.Equal(FixtureReleaseUrl, settings.LatestReleaseUrl);
        Assert.NotNull(settings.LastCheckedAtUtc);

        // Now render the badge bound to that SAME settings state → it must light up (inverse of
        // Plan 03's DOM-absent-when-current case).
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        ctx.Services.AddSingleton(notifier);

        var cut = ctx.Render<UpdateBadge>();

        var a = cut.Find("a");
        Assert.Equal(FixtureReleaseUrl, a.GetAttribute("href"));
        Assert.Equal("_blank", a.GetAttribute("target"));
        Assert.Equal("noopener", a.GetAttribute("rel"));
        Assert.Contains("9.9.9", a.GetAttribute("aria-label"));
        Assert.Contains("text-bg-info", a.GetAttribute("class"));
    }

    // ── 2. The forced advance raises ONCE → a single toast on a mounted ToastHost ──
    [Fact]
    public async Task ForcedOutOfDate_RaisesOnce_AndRendersSingleToast()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        var settings = new StubSettings { UpdateCheckEnabled = true, LatestKnownVersion = null };
        var (checker, notifier) = BuildRealChecker(settings);

        // Real ToastService + real singleton broadcaster (subscribes in its ctor). The forced
        // advance the checker performs raises UpdateNotifier.UpdateAvailable; the broadcaster emits
        // exactly one toast through the singleton ToastService.
        var toasts = new ToastService(new FakeGate { IsEnabled = true }, NullLogger<ToastService>.Instance);
        using var broadcaster = new UpdateToastBroadcaster(
            notifier, toasts, NullLogger<UpdateToastBroadcaster>.Instance);

        // Also confirm the notifier raises exactly once on the N→N+1 advance (D-05).
        var raised = 0;
        notifier.UpdateAvailable += _ => { Interlocked.Increment(ref raised); return Task.CompletedTask; };

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton(toasts);
        var host = ctx.Render<ToastHost>();

        await checker.CheckOnceAsync(ct);

        host.WaitForAssertion(
            () => Assert.Single(host.FindAll(".toast.show")), TimeSpan.FromSeconds(2));
        Assert.Contains("Version 9.9.9 available", host.Markup);
        Assert.Equal(1, raised);
    }

    // ── 3. Toggle OFF ⇒ inert: badge stays DOM-absent even after the forced advance (D-03/e) ──
    [Fact]
    public async Task ForcedOutOfDate_ButToggleOff_KeepsBadgeDomAbsent()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        // Toggle OFF — the air-gapped posture. The checker STILL writes the advanced state
        // (the loop guard, not the checker, gates the outbound call in production), so this case
        // pins that the BADGE render gate alone suppresses the element when the toggle is off,
        // even though LatestKnownVersion > CurrentVersion.
        var settings = new StubSettings { UpdateCheckEnabled = false, LatestKnownVersion = null };
        var (checker, notifier) = BuildRealChecker(settings);

        await checker.CheckOnceAsync(ct);

        // The forced advance happened (the state IS out-of-date)…
        Assert.Equal("9.9.9", settings.LatestKnownVersion);

        // …yet with the toggle off the badge renders NOTHING (D-02 gate AND UpdateCheckEnabled).
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        ctx.Services.AddSingleton(notifier);

        var cut = ctx.Render<UpdateBadge>();

        Assert.Empty(cut.FindAll("a"));
        Assert.DoesNotContain("text-bg-info", cut.Markup);
        Assert.DoesNotContain("Update available", cut.Markup);
    }

    // ── 4. Broadcaster swallows a toast-emission failure (defensive catch path) ──
    [Fact]
    public async Task BroadcasterCatchesToastFailure_DoesNotBubbleIntoRaiseLoop()
    {
        // A throwing gate makes ToastService.Show throw; the broadcaster's catch must swallow it so
        // the failure never detaches the subscription or bubbles into UpdateNotifier.RaiseAsync.
        // This pins the broadcaster's only net-new line that the happy-path tests do not exercise
        // (Plan-04 ≥80%-line coverage close).
        var notifier = new UpdateNotifier();
        var toasts = new ToastService(new ThrowingGate(), NullLogger<ToastService>.Instance);
        using var broadcaster = new UpdateToastBroadcaster(
            notifier, toasts, NullLogger<UpdateToastBroadcaster>.Instance);

        var raised = 0;
        notifier.UpdateAvailable += _ => { Interlocked.Increment(ref raised); return Task.CompletedTask; };

        // The raise completes without throwing even though Show throws inside the broadcaster.
        await notifier.RaiseAsync(new UpdateInfo(new Version(9, 9, 9), FixtureReleaseUrl));

        Assert.Equal(1, raised);
        // A second raise still works — the subscription was not detached by the swallowed failure.
        await notifier.RaiseAsync(new UpdateInfo(new Version(9, 9, 9), FixtureReleaseUrl));
        Assert.Equal(2, raised);
    }
}
