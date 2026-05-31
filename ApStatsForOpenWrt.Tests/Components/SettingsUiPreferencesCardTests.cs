using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services;
using ApStatsForOpenWrt.Services.Auth;
using ApStatsForOpenWrt.Services.Enrichment;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components;

/// <summary>
/// Phase 8 Plan 08-07 Task 2 — exercises the new "UI preferences" card on
/// <see cref="ApStatsForOpenWrt.Components.Pages.Settings.Index"/>. The card uses
/// <see cref="PreferencesService"/> ONLY (REQ-UI-44 lockdown — never IAppSettingsService).
///
/// Test stack: xUnit v3 + bUnit 2.7.2 + Microsoft.Testing.Extensions.CodeCoverage
/// (NEVER coverlet). Coverage gate per D-18: ≥ 80% line + ≥ 80% branch on the new
/// card's @code block.
///
/// The Settings/Index.razor page hosts FOUR cards: Polling interval, UI password,
/// SSH known-hosts, and (new) UI preferences. To render the page, bUnit must supply
/// dependencies for all four cards' @inject directives. Stubs below mirror the test
/// patterns in ApStatsForOpenWrt.Tests.Integration.SettingsRoundTripTests.
/// </summary>
public sealed class SettingsUiPreferencesCardTests
{
    // ── Stubs for the existing 3 cards' DI ─────────────────────────────────
    private sealed class StubAppSettingsService : IAppSettingsService
    {
        public int     PollingIntervalSeconds { get; private set; } = 30;
        public string? UIPasswordHash         { get; private set; }
        public bool    WizardDismissed        { get; private set; } = true;
        public int     RefreshIntervalSeconds { get; private set; } = 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; private set; } = Array.Empty<string>();
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) { PollingIntervalSeconds = seconds; return Task.CompletedTask; }
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) { UIPasswordHash = hash; return Task.CompletedTask; }
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) { WizardDismissed = dismissed; return Task.CompletedTask; }
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) { RefreshIntervalSeconds = seconds; return Task.CompletedTask; }
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) { LocalDomainSuffixes = suffixes.ToArray(); return Task.CompletedTask; }
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        // Phase 17 Plan 17-01 M-UPD-02 — minimal mutable update-check stub members.
        public bool UpdateCheckEnabled { get; private set; } = true;
        public string? LatestKnownVersion { get; private set; }
        public string? LatestReleaseUrl { get; private set; }
        public DateTime? LastCheckedAtUtc { get; private set; }
        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) { UpdateCheckEnabled = enabled; return Task.CompletedTask; }
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) { LatestKnownVersion = version; return Task.CompletedTask; }
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) { LatestReleaseUrl = url; return Task.CompletedTask; }
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) { LastCheckedAtUtc = checkedAtUtc; return Task.CompletedTask; }
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default) { LatestKnownVersion = version; LatestReleaseUrl = url; LastCheckedAtUtc = checkedAtUtc; return Task.CompletedTask; }
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private sealed class StubUIPasswordHasher : IUIPasswordHasher
    {
        public string Hash(string password) => "hashed:" + password;
        public Microsoft.AspNetCore.Identity.PasswordVerificationResult Verify(string storedHash, string providedPassword) =>
            storedHash == "hashed:" + providedPassword
                ? Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success
                : Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed;
    }

    // Phase 17 M-UPD-05 — no-op checker so the Settings page renders (Update-check card dep).
    private sealed class NoOpUpdateChecker : ApStatsForOpenWrt.Services.UpdateCheck.IUpdateChecker
    {
        public Task CheckOnceAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubNodeRepo : INodeRepo
    {
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WiFiNode>>(Array.Empty<WiFiNode>());
        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<WiFiNode?>(null);
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string hostname, string username, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePasswordAsync(int id, string encryptedPassword, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(ApStatsForOpenWrt.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastEnrichmentRefreshAtAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default) => Task.CompletedTask;
    }

    private const string ModuleId = "./js/preferences.js";

    private static (BunitContext Ctx, BunitJSModuleInterop Module, PreferencesService Svc) BuildContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = ctx.JSInterop.SetupModule(ModuleId);

        // Existing 3-card dependencies
        ctx.Services.AddSingleton<IAppSettingsService>(new StubAppSettingsService());
        ctx.Services.AddSingleton<IUIPasswordHasher>(new StubUIPasswordHasher());
        ctx.Services.AddSingleton<INodeRepo>(new StubNodeRepo());

        // v0.11 ENRICH-02 (Plan 14-02) — Settings/Index.razor now injects IForceEnrichmentService
        // + PollNotifier for the "Run now" button. This test focuses on the UI preferences card,
        // but the page render fails fast if either dep is missing. Real services with no-op subscribers
        // are sufficient — these tests do not exercise the Run-now button path.
        ctx.Services.AddSingleton<PollNotifier>();
        ctx.Services.AddSingleton<IForceEnrichmentService, ForceEnrichmentService>();
        // Phase 17 M-UPD-05 — Settings/Index.razor now injects IUpdateChecker + UpdateNotifier
        // for the Update-check card. Register no-op seams so the page renders.
        ctx.Services.AddSingleton<ApStatsForOpenWrt.Services.UpdateCheck.IUpdateChecker, NoOpUpdateChecker>();
        ctx.Services.AddSingleton<ApStatsForOpenWrt.Services.UpdateCheck.UpdateNotifier>();

        // The card under test — real PreferencesService over the bUnit-faked JS module.
        var prefs = new PreferencesService(ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton<PreferencesService>(prefs);

        return (ctx, module, prefs);
    }

    // 1. Initial render — checkbox reflects PreferencesService default (true → checked). -------
    [Fact]
    public void Renders_NotificationsToggle_Checked_WhenDefault()
    {
        var (ctx, module, _) = BuildContext();
        using var _disp = ctx;
        // PreferencesService default for IsNotificationsEnabled is true (bool? null → true).
        module.Setup<bool?>("getJson", PreferencesService.KeyNotificationsEnabled).SetResult(null);

        var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input#notifications-toggle");
            // Toggle is a checkbox — when checked, the rendered <input> has the checked attribute.
            Assert.True(input.HasAttribute("checked"));
            Assert.Contains("bi-bell-fill", cut.Markup);
            Assert.Contains("UI preferences", cut.Markup);
        });
    }

    // 2. Toggle OFF — calls PreferencesService.SetNotificationsEnabledAsync(false); icon flips. -
    [Fact]
    public void Toggle_Off_PersistsFalse_AndFlipsIcon()
    {
        var (ctx, module, prefs) = BuildContext();
        using var _disp = ctx;
        module.Setup<bool?>("getJson", PreferencesService.KeyNotificationsEnabled).SetResult(true);

        var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
        cut.WaitForAssertion(() => Assert.Contains("bi-bell-fill", cut.Markup));

        // Act — flip the toggle off.
        cut.Find("input#notifications-toggle").Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("bi-bell-slash-fill", cut.Markup);
            // setJson('notifications-enabled', false) was invoked — the wiring call.
            Assert.Contains(module.Invocations, i =>
                i.Identifier == "setJson"
                && i.Arguments.Count >= 2
                && (i.Arguments[0] as string) == PreferencesService.KeyNotificationsEnabled
                && i.Arguments[1] is bool b && b == false);
        });

        // Side effect — PreferencesService now reports IsNotificationsEnabled=false (cached).
        Assert.False(prefs.IsNotificationsEnabled);
    }

    // 3. Toggle ON — calls PreferencesService.SetNotificationsEnabledAsync(true); icon flips. ---
    [Fact]
    public void Toggle_On_PersistsTrue_AndFlipsIcon()
    {
        var (ctx, module, prefs) = BuildContext();
        using var _disp = ctx;
        module.Setup<bool?>("getJson", PreferencesService.KeyNotificationsEnabled).SetResult(false);

        var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
        cut.WaitForAssertion(() => Assert.Contains("bi-bell-slash-fill", cut.Markup));

        cut.Find("input#notifications-toggle").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("bi-bell-fill", cut.Markup);
            Assert.Contains(module.Invocations, i =>
                i.Identifier == "setJson"
                && i.Arguments.Count >= 2
                && (i.Arguments[0] as string) == PreferencesService.KeyNotificationsEnabled
                && i.Arguments[1] is bool b && b == true);
        });

        Assert.True(prefs.IsNotificationsEnabled);
    }

    // 3b. Coverage tightener — fire the @onchange handler with a non-boolean payload so the
    //     `e.Value is bool b && b` pattern's "not-a-bool" branch lights up. In practice no
    //     real <input type="checkbox"> emits non-boolean change values, but the pattern was
    //     written defensively, and exercising the branch is needed to clear the D-18 gate.
    [Fact]
    public void Toggle_WithNonBooleanChangeValue_DefaultsFalse()
    {
        var (ctx, module, prefs) = BuildContext();
        using var _disp = ctx;
        module.Setup<bool?>("getJson", PreferencesService.KeyNotificationsEnabled).SetResult(true);

        var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
        cut.WaitForAssertion(() => Assert.Contains("bi-bell-fill", cut.Markup));

        // Fire @onchange with a string value — exercises the non-bool branch of
        // `e.Value is bool b && b` (defaults to false). In practice this never happens
        // for a checkbox <input>; we are exclusively raising branch coverage here.
        cut.Find("input#notifications-toggle").Change("not-a-bool");

        cut.WaitForAssertion(() =>
        {
            // Defaulted to false → icon flipped to bi-bell-slash-fill.
            Assert.Contains("bi-bell-slash-fill", cut.Markup);
            Assert.Contains(module.Invocations, i =>
                i.Identifier == "setJson"
                && i.Arguments.Count >= 2
                && (i.Arguments[0] as string) == PreferencesService.KeyNotificationsEnabled
                && i.Arguments[1] is bool b && b == false);
        });

        Assert.False(prefs.IsNotificationsEnabled);
    }

    // 4. Structural lockdown — the rendered Razor source contains the new card markers and
    //    wires through PreferencesService.SetNotificationsEnabledAsync only (REQ-UI-44 lockdown).
    [Fact]
    public void Card_StructuralAssertion_UsesPreferencesService()
    {
        var razorPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "ApStatsForOpenWrt", "Components", "Pages", "Settings", "Index.razor");
        var src = File.ReadAllText(razorPath);

        Assert.Contains("UI preferences", src);
        Assert.Contains("notifications-toggle", src);
        Assert.Contains("SetNotificationsEnabledAsync", src);
        Assert.Contains("bi-bell-fill", src);
        Assert.Contains("bi-bell-slash-fill", src);
        // The new card MUST inject PreferencesService — REQ-UI-44 lockdown contract.
        Assert.Contains("PreferencesService", src);
    }
}
