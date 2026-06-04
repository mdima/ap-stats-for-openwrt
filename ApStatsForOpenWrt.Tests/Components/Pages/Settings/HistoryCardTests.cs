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
/// Phase 18 Plan 06 / M-HIST-04 — bUnit tests for the "Live history" card on /Settings/Index.razor.
/// Renders the page against a mutable StubSettings whose HistoryEnabled is controllable and whose
/// SetHistoryEnabledAsync is recorded (last value + call count).
///
/// Cases (PLAN <acceptance_criteria>):
///   - ON state: #history-enabled-toggle renders checked, label "History enabled", icon bi-clock-history.
///   - OFF state: input unchecked, label "History disabled", icon bi-slash-circle.
///   - Toggle → true invokes SetHistoryEnabledAsync(true) exactly once.
///   - Toggle → false invokes SetHistoryEnabledAsync(false) exactly once.
///   - Card title "Live history" + the "12 hours" / "continuously" explanatory copy are present.
/// </summary>
public sealed class HistoryCardTests
{
    // ── Mutable stub with raisable Changed + recorded SetHistoryEnabledAsync ──
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
        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) { UpdateCheckEnabled = enabled; RaiseChanged(); return Task.CompletedTask; }
        public string? LatestKnownVersion { get; set; }
        public string? LatestReleaseUrl { get; set; }
        public DateTime? LastCheckedAtUtc { get; set; }
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) { LatestKnownVersion = version; RaiseChanged(); return Task.CompletedTask; }
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) { LatestReleaseUrl = url; RaiseChanged(); return Task.CompletedTask; }
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) { LastCheckedAtUtc = checkedAtUtc; RaiseChanged(); return Task.CompletedTask; }
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default) { LatestKnownVersion = version; LatestReleaseUrl = url; LastCheckedAtUtc = checkedAtUtc; RaiseChanged(); return Task.CompletedTask; }

        // ── M-HIST-04 — the card under test ──────────────────────────────────
        public bool HistoryEnabled { get; set; } = true;
        public int SetHistoryEnabledCallCount;
        public bool? LastSetHistoryEnabled;
        public Task SetHistoryEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            HistoryEnabled = enabled;
            SetHistoryEnabledCallCount++;
            LastSetHistoryEnabled = enabled;
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed;
        public void RaiseChanged() => Changed?.Invoke(this);
    }

    private sealed class FakeChecker : IUpdateChecker
    {
        public Task CheckOnceAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingHasher : IUIPasswordHasher
    {
        public string Hash(string plaintext) => "AQAAA-fake-hash";
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

    private static (BunitContext ctx, StubSettings settings) Build(bool historyEnabled = true)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");

        var settings = new StubSettings { HistoryEnabled = historyEnabled };

        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        ctx.Services.AddSingleton<IUIPasswordHasher>(new CapturingHasher());
        ctx.Services.AddSingleton<INodeRepo>(new CapturingNodeRepo());
        ctx.Services.AddSingleton(new ApStatsForOpenWrt.Services.PreferencesService(ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton<PollNotifier>();
        ctx.Services.AddSingleton<IForceEnrichmentService, ForceEnrichmentService>();
        ctx.Services.AddSingleton<IUpdateChecker>(new FakeChecker());
        ctx.Services.AddSingleton(new UpdateNotifier());
        return (ctx, settings);
    }

    private static IRenderedComponent<ApStatsForOpenWrt.Components.Pages.Settings.Index> RenderPage(BunitContext ctx)
        => ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();

    // ── Card presence + copy ──────────────────────────────────────────────────
    [Fact]
    public void HistoryCard_RendersTitleAndExplanatoryCopy()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = RenderPage(ctx);
            Assert.Contains(">Live history</h5>", cut.Markup, StringComparison.Ordinal);
            Assert.NotNull(cut.Find("#history-enabled-toggle"));
            // UI-SPEC explanatory copy — "12 hours" spelled out + the continuous-polling consequence.
            Assert.Contains("12 hours", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("continuously", cut.Markup, StringComparison.Ordinal);
        }
    }

    // ── ON state ──────────────────────────────────────────────────────────────
    [Fact]
    public void Toggle_Checked_AndLabelOn_AndIcon_WhenHistoryEnabled()
    {
        var (ctx, _) = Build(historyEnabled: true);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            var toggle = cut.Find("#history-enabled-toggle");
            Assert.True(toggle.HasAttribute("checked"));

            var label = cut.Find("label[for=\"history-enabled-toggle\"]");
            Assert.Contains("History enabled", label.TextContent, StringComparison.Ordinal);
            Assert.Contains("bi-clock-history", label.InnerHtml, StringComparison.Ordinal);
        }
    }

    // ── OFF state ─────────────────────────────────────────────────────────────
    [Fact]
    public void Toggle_Unchecked_AndLabelOff_AndIcon_WhenHistoryDisabled()
    {
        var (ctx, _) = Build(historyEnabled: false);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            var toggle = cut.Find("#history-enabled-toggle");
            Assert.False(toggle.HasAttribute("checked"));

            var label = cut.Find("label[for=\"history-enabled-toggle\"]");
            Assert.Contains("History disabled", label.TextContent, StringComparison.Ordinal);
            Assert.Contains("bi-slash-circle", label.InnerHtml, StringComparison.Ordinal);
        }
    }

    // ── Toggle persists immediately ─────────────────────────────────────────────
    [Fact]
    public async Task Toggle_Change_ToTrue_CallsSetHistoryEnabledAsync_Once()
    {
        var (ctx, settings) = Build(historyEnabled: false);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            await cut.Find("#history-enabled-toggle")
                .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

            Assert.Equal(1, settings.SetHistoryEnabledCallCount);
            Assert.True(settings.LastSetHistoryEnabled);
            Assert.True(settings.HistoryEnabled);
        }
    }

    [Fact]
    public async Task Toggle_Change_ToFalse_CallsSetHistoryEnabledAsync_Once()
    {
        var (ctx, settings) = Build(historyEnabled: true);
        using (ctx)
        {
            var cut = RenderPage(ctx);
            await cut.Find("#history-enabled-toggle")
                .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });

            Assert.Equal(1, settings.SetHistoryEnabledCallCount);
            Assert.False(settings.LastSetHistoryEnabled);
            Assert.False(settings.HistoryEnabled);
        }
    }
}
