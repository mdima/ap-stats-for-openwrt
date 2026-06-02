using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;
using HistoryPage = ApStatsForOpenWrt.Components.Pages.Setup.History;

namespace ApStatsForOpenWrt.Tests.Components.Pages.Setup;

/// <summary>
/// Phase 19 Plan 05 / M-HIST-08 / D-01 — Setup/History.razor, the new wizard terminus.
/// A deliberate history opt-in: two form-check radios (default-selected Enable, charter-locked
/// copy L-10). Finish persists the choice via the SAME IAppSettingsService.SetHistoryEnabledAsync
/// the Settings card uses (L-08), then sets WizardDismissed = true, then navigates to "/".
///
/// Cases:
///   - Enable radio is checked by default.
///   - The L-10 locked copy strings render verbatim (radio headings/bodies + reversibility note).
///   - Finish with the default selection: SetHistoryEnabledAsync(true), then
///     SetWizardDismissedAsync(true), then navigate to "/" (in that order).
///   - Selecting Skip then Finish: SetHistoryEnabledAsync(false).
/// </summary>
public sealed class HistoryTests
{
    private sealed class StubSettings : IAppSettingsService
    {
        public int PollingIntervalSeconds => 30;
        public string? UIPasswordHash => null;
        public bool WizardDismissed { get; private set; }
        public int RefreshIntervalSeconds => 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; } = Array.Empty<string>();

        // ── Call-tracking for the History terminus Finish handler ──────────────
        public readonly List<string> CallOrder = new();
        public int SetHistoryEnabledCallCount;
        public bool? LastSetHistoryEnabled;
        public int SetWizardDismissedCallCount;

        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;

        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default)
        {
            WizardDismissed = dismissed;
            SetWizardDismissedCallCount++;
            CallOrder.Add($"SetWizardDismissed({dismissed})");
            return Task.CompletedTask;
        }

        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;

        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public bool UpdateCheckEnabled => true;
        public string? LatestKnownVersion => null;
        public string? LatestReleaseUrl => null;
        public DateTime? LastCheckedAtUtc => null;
        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default) => Task.CompletedTask;

        public bool HistoryEnabled => true;
        public Task SetHistoryEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            SetHistoryEnabledCallCount++;
            LastSetHistoryEnabled = enabled;
            CallOrder.Add($"SetHistoryEnabled({enabled})");
            return Task.CompletedTask;
        }

        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private static (BunitContext ctx, StubSettings settings) Build()
    {
        var ctx = new BunitContext();
        var settings = new StubSettings();
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        return (ctx, settings);
    }

    [Fact]
    public void EnableRadio_IsCheckedByDefault()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<HistoryPage>();

            var enable = cut.Find("#history-enable");
            Assert.True(enable.HasAttribute("checked"));

            var skip = cut.Find("#history-skip");
            Assert.False(skip.HasAttribute("checked"));
        }
    }

    [Fact]
    public void LockedCopy_RendersVerbatim()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<HistoryPage>();

            // L-10 — radio headings + bodies + reversibility note, verbatim.
            Assert.Contains("Enable history", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Track online/offline counts and per-station RSSI for the last 12 h. Polling runs continuously.", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Skip history", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("No data recorded. Polling only runs while someone is viewing the dashboard, and pauses otherwise.", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("You can change this later in Settings.", cut.Markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Finish_WithDefaultEnable_PersistsThenDismissesThenNavigatesHome()
    {
        var (ctx, settings) = Build();
        using (ctx)
        {
            var cut = ctx.Render<HistoryPage>();

            var finishBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Finish"));
            await cut.InvokeAsync(() => finishBtn.Click());

            // SetHistoryEnabledAsync(true) then SetWizardDismissedAsync(true), in that order.
            Assert.Equal(1, settings.SetHistoryEnabledCallCount);
            Assert.True(settings.LastSetHistoryEnabled);
            Assert.Equal(1, settings.SetWizardDismissedCallCount);
            Assert.True(settings.WizardDismissed);
            Assert.Equal(new[] { "SetHistoryEnabled(True)", "SetWizardDismissed(True)" }, settings.CallOrder);

            // Navigated to the dashboard root.
            var nav = ctx.Services.GetRequiredService<NavigationManager>();
            Assert.Equal(nav.BaseUri, nav.Uri);
        }
    }

    [Fact]
    public async Task Finish_AfterSelectingSkip_PersistsFalse()
    {
        var (ctx, settings) = Build();
        using (ctx)
        {
            var cut = ctx.Render<HistoryPage>();

            // Select the Skip radio.
            await cut.Find("#history-skip")
                .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

            var finishBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Finish"));
            await cut.InvokeAsync(() => finishBtn.Click());

            Assert.Equal(1, settings.SetHistoryEnabledCallCount);
            Assert.False(settings.LastSetHistoryEnabled);
            Assert.True(settings.WizardDismissed);
        }
    }
}
