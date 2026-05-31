using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Services;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Services.UpdateCheck;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 17 Plan 03 / M-UPD-03 + M-UPD-04 (toast surface). bUnit tests for the navbar
/// <see cref="UpdateBadge"/> + the MainLayout toast wiring via
/// <see cref="UpdateToastSubscriber"/>.
///
/// Cases (PLAN <behavior>):
///   1. DOM-absent when current (LatestKnownVersion null / not greater) even with toggle on.
///   2. DOM-absent when toggle OFF even though a newer version exists (D-02).
///   3. Visible + link: &lt;a href&gt; == LatestReleaseUrl, target=_blank, rel=noopener,
///      aria-label == "Update available: version 9.9.9".
///   4. Two-line tooltip: "Version 9.9.9 available" + "discovered Xh ago" (RelativeTime).
///   5. Reactive: raising UpdateNotifier.UpdateAvailable re-renders without a reload.
///   6. Transition-fires-once toast: one raise → exactly one toast; a second raise with the
///      SAME version → still exactly one toast (the wiring never double-emits per raise).
///   7. Multi-circuit broadcast: two renders sharing one singleton notifier + one ToastService
///      both receive the toast on a single raise.
///
/// Test stack per CLAUDE.md: bUnit, NOT mocks of the notifier/toast. Real ToastService +
/// a mounted ToastHost exercise the toast path end-to-end.
/// </summary>
public sealed class UpdateBadgeTests
{
    private const string CurrentReleaseUrl = "https://github.com/mdima/ap-stats-for-openwrt/releases/tag/v9.9.9";

    // ── Mutable stub (badge reads four getters + the Changed event) ──────────
    private sealed class StubSettings : IAppSettingsService
    {
        public int PollingIntervalSeconds { get; } = 30;
        public string? UIPasswordHash { get; } = null;
        public bool WizardDismissed { get; } = true;
        public int RefreshIntervalSeconds { get; } = 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; } = Array.Empty<string>();
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public bool UpdateCheckEnabled { get; set; } = true;
        public string? LatestKnownVersion { get; set; }
        public string? LatestReleaseUrl { get; set; }
        public DateTime? LastCheckedAtUtc { get; set; }
        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) { UpdateCheckEnabled = enabled; RaiseChanged(); return Task.CompletedTask; }
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) { LatestKnownVersion = version; RaiseChanged(); return Task.CompletedTask; }
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) { LatestReleaseUrl = url; RaiseChanged(); return Task.CompletedTask; }
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) { LastCheckedAtUtc = checkedAtUtc; RaiseChanged(); return Task.CompletedTask; }
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default) { LatestKnownVersion = version; LatestReleaseUrl = url; LastCheckedAtUtc = checkedAtUtc; RaiseChanged(); return Task.CompletedTask; }
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed;
        public void RaiseChanged() => Changed?.Invoke(this);
    }

    private sealed class FakeGate : INotificationsGate
    {
        public bool IsEnabled { get; init; } = true;
    }

    // ── Harness ──────────────────────────────────────────────────────────────
    private static (BunitContext ctx, StubSettings settings, UpdateNotifier notifier) BuildBadgeContext(
        bool updateCheckEnabled = true,
        string? latestKnownVersion = null,
        string? latestReleaseUrl = null,
        DateTime? lastCheckedAtUtc = null)
    {
        var ctx = new BunitContext();
        var settings = new StubSettings
        {
            UpdateCheckEnabled = updateCheckEnabled,
            LatestKnownVersion = latestKnownVersion,
            LatestReleaseUrl = latestReleaseUrl,
            LastCheckedAtUtc = lastCheckedAtUtc,
        };
        var notifier = new UpdateNotifier();
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        ctx.Services.AddSingleton(notifier);
        return (ctx, settings, notifier);
    }

    // ── 1. DOM-absent when current ─────────────────────────────────────────
    [Fact]
    public void DomAbsent_WhenNoNewerVersion_EvenWithToggleOn()
    {
        var (ctx, _, _) = BuildBadgeContext(updateCheckEnabled: true, latestKnownVersion: null);
        using (ctx)
        {
            var cut = ctx.Render<UpdateBadge>();
            // No anchor, no info badge — the navbar slot is empty.
            Assert.Empty(cut.FindAll("a"));
            Assert.DoesNotContain("text-bg-info", cut.Markup);
            Assert.DoesNotContain("Update available", cut.Markup);
        }
    }

    // ── 2. DOM-absent when toggle OFF (D-02) ────────────────────────────────
    [Fact]
    public void DomAbsent_WhenToggleOff_EvenWithNewerVersion()
    {
        var (ctx, _, _) = BuildBadgeContext(
            updateCheckEnabled: false,
            latestKnownVersion: "9.9.9",
            latestReleaseUrl: CurrentReleaseUrl);
        using (ctx)
        {
            var cut = ctx.Render<UpdateBadge>();
            Assert.Empty(cut.FindAll("a"));
            Assert.DoesNotContain("text-bg-info", cut.Markup);
        }
    }

    // ── 3. Visible + link + aria-label ───────────────────────────────────────
    [Fact]
    public void Visible_WhenNewerVersionAndToggleOn_RendersLinkTargetRelAria()
    {
        var (ctx, _, _) = BuildBadgeContext(
            updateCheckEnabled: true,
            latestKnownVersion: "9.9.9",
            latestReleaseUrl: CurrentReleaseUrl,
            lastCheckedAtUtc: DateTime.UtcNow.AddHours(-3));
        using (ctx)
        {
            var cut = ctx.Render<UpdateBadge>();
            var a = cut.Find("a");
            Assert.Equal(CurrentReleaseUrl, a.GetAttribute("href"));
            Assert.Equal("_blank", a.GetAttribute("target"));
            Assert.Equal("noopener", a.GetAttribute("rel"));
            Assert.Equal("Update available: version 9.9.9", a.GetAttribute("aria-label"));
            Assert.Contains("text-bg-info", a.GetAttribute("class"));
            // No pulse/animation class (M-UPD-03 calm cue).
            Assert.DoesNotContain("pulse", a.GetAttribute("class"));
        }
    }

    // ── 4. Two-line tooltip ──────────────────────────────────────────────────
    [Fact]
    public void Tooltip_HasTwoLines_VersionAvailable_AndDiscoveredRelativeTime()
    {
        var (ctx, _, _) = BuildBadgeContext(
            updateCheckEnabled: true,
            latestKnownVersion: "9.9.9",
            latestReleaseUrl: CurrentReleaseUrl,
            lastCheckedAtUtc: DateTime.UtcNow.AddHours(-3));
        using (ctx)
        {
            var cut = ctx.Render<UpdateBadge>();
            var a = cut.Find("a");
            var title = a.GetAttribute("title") ?? string.Empty;
            Assert.Contains("Version 9.9.9 available", title);
            Assert.Contains("discovered 3h ago", title);
            // Native `title` tooltip — two lines split by a real newline (NOT a <br>: this app
            // initialises no Bootstrap tooltip JS, so a <br> renders literally). 260531 UAT fix.
            Assert.Contains("available\ndiscovered", title);
            Assert.DoesNotContain("<br>", title);
            Assert.Null(a.GetAttribute("data-bs-html"));
        }
    }

    // ── 5. Reactive re-render on UpdateAvailable ────────────────────────────
    [Fact]
    public async Task Reactive_BadgeAppears_AfterUpdateAvailableRaise()
    {
        // Start with no known version (badge absent), then advance the setting and raise
        // the notifier — the badge must appear without a reload.
        var (ctx, settings, notifier) = BuildBadgeContext(updateCheckEnabled: true, latestKnownVersion: null);
        using (ctx)
        {
            var cut = ctx.Render<UpdateBadge>();
            Assert.Empty(cut.FindAll("a"));

            settings.LatestKnownVersion = "9.9.9";
            settings.LatestReleaseUrl = CurrentReleaseUrl;
            settings.LastCheckedAtUtc = DateTime.UtcNow;
            await notifier.RaiseAsync(new UpdateInfo(new Version(9, 9, 9), CurrentReleaseUrl));

            cut.WaitForState(() => cut.FindAll("a").Count == 1, TimeSpan.FromSeconds(2));
            var a = cut.Find("a");
            Assert.Equal(CurrentReleaseUrl, a.GetAttribute("href"));
        }
    }

    // ── 5b. Reactive hide on Settings.Changed (toggle off) ──────────────────
    [Fact]
    public async Task Reactive_BadgeHides_AfterToggleOff()
    {
        var (ctx, settings, _) = BuildBadgeContext(
            updateCheckEnabled: true,
            latestKnownVersion: "9.9.9",
            latestReleaseUrl: CurrentReleaseUrl,
            lastCheckedAtUtc: DateTime.UtcNow);
        using (ctx)
        {
            var cut = ctx.Render<UpdateBadge>();
            Assert.Single(cut.FindAll("a"));

            await settings.SetUpdateCheckEnabledAsync(false);

            cut.WaitForState(() => cut.FindAll("a").Count == 0, TimeSpan.FromSeconds(2));
            Assert.Empty(cut.FindAll("a"));
        }
    }

    // ── Toast harness (real ToastService + singleton UpdateToastBroadcaster + mounted ToastHost) ──
    // M-UPD-04 broadcast model (Plan-03 deviation #1): the discovery toast is emitted by a
    // SINGLETON UpdateToastBroadcaster that subscribes ONCE to UpdateNotifier.UpdateAvailable and
    // calls the singleton ToastService.Show exactly once per raise. The singleton ToastService then
    // broadcasts to every mounted ToastHost. A per-circuit Blazor subscriber was rejected (N×N
    // fan-out). These tests construct the broadcaster directly (its ctor subscribes) and mount a
    // ToastHost per circuit, mirroring the production singleton + per-circuit-ToastHost shape.

    // ── 6. Transition-fires-once toast ───────────────────────────────────────
    [Fact]
    public async Task DiscoveryToast_FiresExactlyOnce_PerRaise()
    {
        var notifier = new UpdateNotifier();
        var toasts = new ToastService(new FakeGate { IsEnabled = true }, NullLogger<ToastService>.Instance);
        // The singleton broadcaster subscribes to the notifier in its constructor.
        using var broadcaster = new UpdateToastBroadcaster(
            notifier, toasts, NullLogger<UpdateToastBroadcaster>.Instance);

        var ctx = new BunitContext();
        ctx.Services.AddSingleton(toasts);
        using (ctx)
        {
            var host = ctx.Render<ToastHost>();

            await notifier.RaiseAsync(new UpdateInfo(new Version(9, 9, 9), CurrentReleaseUrl));

            host.WaitForAssertion(
                () => Assert.Single(host.FindAll(".toast.show")), TimeSpan.FromSeconds(2));
            Assert.Contains("Version 9.9.9 available", host.Markup);
            // D-04 — no link in the toast.
            Assert.DoesNotContain(CurrentReleaseUrl, host.Markup);

            // A second raise with the SAME version still produces exactly one MORE toast
            // (the wiring never double-emits per raise; same-version dedup is the checker's job).
            await notifier.RaiseAsync(new UpdateInfo(new Version(9, 9, 9), CurrentReleaseUrl));
            host.WaitForAssertion(
                () => Assert.Equal(2, host.FindAll(".toast.show").Count), TimeSpan.FromSeconds(2));
        }
    }

    // ── 7. Multi-circuit broadcast ───────────────────────────────────────────
    [Fact]
    public async Task DiscoveryToast_BroadcastsToAllCircuits_OnSingleRaise()
    {
        // ONE singleton notifier + ONE ToastService + ONE singleton broadcaster, shared by two
        // circuits (each circuit mounts its own ToastHost). A single raise must land exactly one
        // toast in EACH host (broadcast), not N toasts (the N×N fan-out a per-circuit subscriber
        // would cause).
        var notifier = new UpdateNotifier();
        var toasts = new ToastService(new FakeGate { IsEnabled = true }, NullLogger<ToastService>.Instance);
        using var broadcaster = new UpdateToastBroadcaster(
            notifier, toasts, NullLogger<UpdateToastBroadcaster>.Instance);

        var ctx1 = new BunitContext();
        ctx1.Services.AddSingleton(toasts);
        var ctx2 = new BunitContext();
        ctx2.Services.AddSingleton(toasts);
        using (ctx1)
        using (ctx2)
        {
            var host1 = ctx1.Render<ToastHost>();
            var host2 = ctx2.Render<ToastHost>();

            await notifier.RaiseAsync(new UpdateInfo(new Version(9, 9, 9), CurrentReleaseUrl));

            host1.WaitForAssertion(
                () => Assert.Single(host1.FindAll(".toast.show")), TimeSpan.FromSeconds(2));
            host2.WaitForAssertion(
                () => Assert.Single(host2.FindAll(".toast.show")), TimeSpan.FromSeconds(2));
        }
    }
}
