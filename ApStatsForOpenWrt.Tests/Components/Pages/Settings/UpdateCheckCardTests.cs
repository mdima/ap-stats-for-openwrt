using Bunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Auth;
using ApStatsForOpenWrt.Services.Enrichment;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Services.UpdateCheck;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Pages.Settings;

/// <summary>
/// Phase 17 Plan 03 / M-UPD-05 — bUnit tests for the Update-check card on /Settings/Index.razor.
/// Renders the page against a mutable StubSettings (the four Plan-01 update-check members) + a
/// recording fake IUpdateChecker + a real UpdateNotifier.
///
/// Cases (PLAN <behavior>):
///   - Toggle persists: toggling the checkbox calls SetUpdateCheckEnabledAsync (immediate persist).
///   - Check now disabled while in-flight: a throwing checker still re-enables in finally.
///   - Check now disabled when toggle OFF (D-03) + diagnostics greyed.
///   - Diagnostic copy: Current version / Latest known / Last checked (RelativeTime / "never").
///   - Reload-free refresh: raising UpdateNotifier.UpdateAvailable re-renders the diagnostics.
/// </summary>
public sealed class UpdateCheckCardTests
{
    // ── Mutable stub with raisable Changed + the four Plan-01 members ────────
    private sealed class StubSettings : IAppSettingsService
    {
        public string? UIPasswordHash { get; set; }
        public int PollingIntervalSeconds { get; set; } = 30;
        public bool WizardDismissed => false;
        public int RefreshIntervalSeconds { get; set; } = 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; set; } = Array.Empty<string>();

        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public bool UpdateCheckEnabled { get; set; } = true;
        public bool HistoryEnabled => true;
        public Task SetHistoryEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public string? LatestKnownVersion { get; set; }
        public string? LatestReleaseUrl { get; set; }
        public DateTime? LastCheckedAtUtc { get; set; }

        public int SetUpdateCheckEnabledCallCount;
        public bool? LastSetUpdateCheckEnabled;

        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            UpdateCheckEnabled = enabled;
            SetUpdateCheckEnabledCallCount++;
            LastSetUpdateCheckEnabled = enabled;
            RaiseChanged();
            return Task.CompletedTask;
        }
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) { LatestKnownVersion = version; RaiseChanged(); return Task.CompletedTask; }
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) { LatestReleaseUrl = url; RaiseChanged(); return Task.CompletedTask; }
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) { LastCheckedAtUtc = checkedAtUtc; RaiseChanged(); return Task.CompletedTask; }
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default) { LatestKnownVersion = version; LatestReleaseUrl = url; LastCheckedAtUtc = checkedAtUtc; RaiseChanged(); return Task.CompletedTask; }
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed;
        public void RaiseChanged() => Changed?.Invoke(this);
    }

    // ── Recording fake checker (optionally throws to prove finally-re-enable) ──
    private sealed class FakeChecker : IUpdateChecker
    {
        public int CallCount;
        public bool Throw;
        public TaskCompletionSource? Gate;   // when set, CheckOnceAsync awaits this before returning

        public async Task CheckOnceAsync(CancellationToken ct = default)
        {
            CallCount++;
            if (Gate is not null) await Gate.Task;
            if (Throw) throw new InvalidOperationException("simulated check failure");
        }
    }

    private sealed class CapturingHasher : IUIPasswordHasher
    {
        public string LastInput = "";
        public string Hash(string plaintext) { LastInput = plaintext; return "AQAAA-fake-hash"; }
        public PasswordVerificationResult Verify(string h, string p) => PasswordVerificationResult.Success;
    }

    private sealed class CapturingNodeRepo : INodeRepo
    {
#pragma warning disable CS0618
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
#pragma warning restore CS0618
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WiFiNode>>(Array.Empty<WiFiNode>());
        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<WiFiNode?>(null);
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string h, string u, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePasswordAsync(int id, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastEnrichmentRefreshAtAsync(DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (BunitContext ctx, StubSettings settings, FakeChecker checker, UpdateNotifier notifier) Build(
        bool updateCheckEnabled = true,
        string? latestKnownVersion = null,
        DateTime? lastCheckedAtUtc = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");

        var settings = new StubSettings
        {
            UpdateCheckEnabled = updateCheckEnabled,
            LatestKnownVersion = latestKnownVersion,
            LastCheckedAtUtc = lastCheckedAtUtc,
        };
        var checker = new FakeChecker();
        var notifier = new UpdateNotifier();

        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        ctx.Services.AddSingleton<IUIPasswordHasher>(new CapturingHasher());
        ctx.Services.AddSingleton<INodeRepo>(new CapturingNodeRepo());
        ctx.Services.AddSingleton(new ApStatsForOpenWrt.Services.PreferencesService(ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton<PollNotifier>();
        ctx.Services.AddSingleton<IForceEnrichmentService, ForceEnrichmentService>();
        ctx.Services.AddSingleton<IUpdateChecker>(checker);
        ctx.Services.AddSingleton(notifier);
        return (ctx, settings, checker, notifier);
    }

    private static IRenderedComponent<ApStatsForOpenWrt.Components.Pages.Settings.Index> RenderPage(BunitContext ctx)
        => ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();

    // ── Card presence ────────────────────────────────────────────────────────
    [Fact]
    public void UpdateCheckCard_RendersWithHeaderAndControls()
    {
        var (ctx, _, _, _) = Build();
        using (ctx)
        {
            var cut = RenderPage(ctx);
            Assert.Contains(">Update check</h5>", cut.Markup, StringComparison.Ordinal);
            Assert.NotNull(cut.Find("#update-check-toggle"));
            Assert.NotNull(cut.Find("#check-now-button"));
            Assert.NotNull(cut.Find("#update-check-diagnostics"));
        }
    }

    // ── Toggle persists immediately ──────────────────────────────────────────
    [Fact]
    public async Task Toggle_Change_CallsSetUpdateCheckEnabledAsync_WithNewValue()
    {
        var (ctx, settings, _, _) = Build(updateCheckEnabled: true);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            // Toggling the switch OFF must immediately persist false.
            await cut.Find("#update-check-toggle").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });

            Assert.Equal(1, settings.SetUpdateCheckEnabledCallCount);
            Assert.False(settings.LastSetUpdateCheckEnabled);
            Assert.False(settings.UpdateCheckEnabled);
        }
    }

    // ── Check now disabled when toggle OFF (D-03) + diagnostics greyed ───────
    [Fact]
    public void CheckNow_DisabledWhenToggleOff_AndDiagnosticsGreyed()
    {
        var (ctx, _, _, _) = Build(updateCheckEnabled: false);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            Assert.True(cut.Find("#check-now-button").HasAttribute("disabled"));
            // D-03 — diagnostics muted/greyed when OFF.
            var diag = cut.Find("#update-check-diagnostics");
            var cls = diag.GetAttribute("class") ?? string.Empty;
            Assert.Contains("text-body-secondary", cls);
            Assert.Contains("opacity-50", cls);
        }
    }

    [Fact]
    public void CheckNow_EnabledWhenToggleOn_AndDiagnosticsNotGreyed()
    {
        var (ctx, _, _, _) = Build(updateCheckEnabled: true);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            Assert.False(cut.Find("#check-now-button").HasAttribute("disabled"));
            var cls = cut.Find("#update-check-diagnostics").GetAttribute("class") ?? string.Empty;
            Assert.DoesNotContain("opacity-50", cls);
        }
    }

    // ── Check now disabled while in-flight; re-enables in finally on failure ──
    [Fact]
    public async Task CheckNow_DisablesWhileInFlight_ThenReEnables_OnThrownFailure()
    {
        var (ctx, _, checker, _) = Build(updateCheckEnabled: true);
        checker.Throw = true;
        checker.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            Assert.False(cut.Find("#check-now-button").HasAttribute("disabled"));

            // Start the check; it blocks on the gate so we can observe the in-flight disabled state.
            var click = cut.Find("#check-now-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
            cut.WaitForState(() => cut.Find("#check-now-button").HasAttribute("disabled"), TimeSpan.FromSeconds(2));
            Assert.True(cut.Find("#check-now-button").HasAttribute("disabled"));
            Assert.Contains("Checking…", cut.Markup);

            // Release the gate — the checker throws, but HandleCheckNowAsync's finally must
            // re-enable the button (M-UPD-05: recover on success OR failure).
            checker.Gate.SetResult();
            await click;

            cut.WaitForState(() => !cut.Find("#check-now-button").HasAttribute("disabled"), TimeSpan.FromSeconds(2));
            Assert.False(cut.Find("#check-now-button").HasAttribute("disabled"));
            Assert.Equal(1, checker.CallCount);
        }
    }

    [Fact]
    public async Task CheckNow_Click_InvokesCheckOnceAsync_OnSuccess()
    {
        var (ctx, _, checker, _) = Build(updateCheckEnabled: true);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            await cut.Find("#check-now-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

            Assert.Equal(1, checker.CallCount);
            cut.WaitForState(() => !cut.Find("#check-now-button").HasAttribute("disabled"), TimeSpan.FromSeconds(2));
            Assert.False(cut.Find("#check-now-button").HasAttribute("disabled"));
        }
    }

    // ── Diagnostic copy ──────────────────────────────────────────────────────
    [Fact]
    public void Diagnostics_ShowDashAndNever_WhenNothingDiscovered()
    {
        var (ctx, _, _, _) = Build(updateCheckEnabled: true, latestKnownVersion: null, lastCheckedAtUtc: null);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            var diag = cut.Find("#update-check-diagnostics");
            Assert.Contains("Current version", diag.TextContent);
            Assert.Contains("Latest known", diag.TextContent);
            Assert.Contains("—", diag.TextContent);          // Latest known placeholder
            Assert.Contains("Last checked", diag.TextContent);
            Assert.Contains("never", diag.TextContent);       // Last checked placeholder
        }
    }

    [Fact]
    public void Diagnostics_ShowLatestKnown_AndRelativeLastChecked_WhenDiscovered()
    {
        var (ctx, _, _, _) = Build(
            updateCheckEnabled: true,
            latestKnownVersion: "9.9.9",
            lastCheckedAtUtc: DateTime.UtcNow.AddHours(-2));
        using (ctx)
        {
            var cut = RenderPage(ctx);
            var diag = cut.Find("#update-check-diagnostics");
            Assert.Contains("9.9.9", diag.TextContent);
            Assert.Contains("2h ago", diag.TextContent);
        }
    }

    // ── Reload-free refresh ──────────────────────────────────────────────────
    [Fact]
    public async Task Diagnostics_RefreshWithoutReload_AfterUpdateAvailableRaise()
    {
        var (ctx, settings, _, notifier) = Build(
            updateCheckEnabled: true, latestKnownVersion: null, lastCheckedAtUtc: null);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            Assert.Contains("never", cut.Find("#update-check-diagnostics").TextContent);

            // Simulate a successful check advancing the stored state, then raising the notifier.
            settings.LatestKnownVersion = "9.9.9";
            settings.LastCheckedAtUtc = DateTime.UtcNow;
            await notifier.RaiseAsync(new UpdateInfo(new Version(9, 9, 9),
                "https://github.com/mdima/ap-stats-for-openwrt/releases/tag/v9.9.9"));

            cut.WaitForState(
                () => cut.Find("#update-check-diagnostics").TextContent.Contains("9.9.9", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));
            Assert.Contains("9.9.9", cut.Find("#update-check-diagnostics").TextContent);
            Assert.DoesNotContain("never", cut.Find("#update-check-diagnostics").TextContent);
        }
    }
}
