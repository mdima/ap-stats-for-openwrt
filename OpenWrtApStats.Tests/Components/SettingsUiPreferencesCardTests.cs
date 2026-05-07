using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services;
using OpenWrtApStats.Services.Auth;
using OpenWrtApStats.Services.Settings;
using Xunit;

namespace OpenWrtApStats.Tests.Components;

/// <summary>
/// Phase 8 Plan 08-07 Task 2 — exercises the new "UI preferences" card on
/// <see cref="OpenWrtApStats.Components.Pages.Settings.Index"/>. The card uses
/// <see cref="PreferencesService"/> ONLY (REQ-UI-44 lockdown — never IAppSettingsService).
///
/// Test stack: xUnit v3 + bUnit 2.7.2 + Microsoft.Testing.Extensions.CodeCoverage
/// (NEVER coverlet). Coverage gate per D-18: ≥ 80% line + ≥ 80% branch on the new
/// card's @code block.
///
/// The Settings/Index.razor page hosts FOUR cards: Polling interval, UI password,
/// SSH known-hosts, and (new) UI preferences. To render the page, bUnit must supply
/// dependencies for all four cards' @inject directives. Stubs below mirror the test
/// patterns in OpenWrtApStats.Tests.Integration.SettingsRoundTripTests.
/// </summary>
public sealed class SettingsUiPreferencesCardTests
{
    // ── Stubs for the existing 3 cards' DI ─────────────────────────────────
    private sealed class StubAppSettingsService : IAppSettingsService
    {
        public int     PollingIntervalSeconds { get; private set; } = 30;
        public string? UIPasswordHash         { get; private set; }
        public bool    WizardDismissed        { get; private set; } = true;
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) { PollingIntervalSeconds = seconds; return Task.CompletedTask; }
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) { UIPasswordHash = hash; return Task.CompletedTask; }
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) { WizardDismissed = dismissed; return Task.CompletedTask; }
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
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
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

        var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();

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

        var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
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

        var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
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

        var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
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
            "OpenWrtApStats", "Components", "Pages", "Settings", "Index.razor");
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
