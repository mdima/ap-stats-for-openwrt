using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Services;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Shared;

/// <summary>
/// Phase 8 D-18 / REQ-UI-30..32, REQ-UI-51 — ThemeToggle component, 4 bUnit-testable
/// visual states locked in 08-UI-SPEC.md §Acceptance Test Fixtures > ThemeToggle.razor:
///   1. Initial render with theme="light" stored in Prefs → bi-sun-fill icon and
///      aria-label="Switch to dark mode".
///   2. Initial render with theme="dark" stored in Prefs → bi-moon-stars-fill icon and
///      aria-label="Switch to light mode".
///   3. Click flips theme: Prefs.SetThemeAsync(value) invoked once via preferences.js
///      `set` (RAW string contract); theme.js `applyTheme` invoked with the new value.
///   4. First render triggers OnAfterRenderAsync(firstRender:true) → reads Prefs and
///      applies through theme.js; markup reflects the persisted value after hydration.
///
/// PreferencesService is the REAL Singleton from Plan 08-01 — D-09 hot-path stays Singleton
/// in tests too. The bUnit JSInterop module mocks for `./js/preferences.js` and `./js/theme.js`
/// give us the same spy semantics PreferencesServiceTests use (Loose mode + module.Invocations
/// inspection). Per CLAUDE.md / Phase 5 test pattern, we do NOT mock PreferencesService.
///
/// Test stack: xUnit v3 + bUnit 2.7.2 + Microsoft.Testing.Extensions.CodeCoverage (NEVER coverlet).
/// Coverage gate per D-18: ≥ 80% line + ≥ 80% branch on ThemeToggle.razor.
/// </summary>
public sealed class ThemeToggleTests
{
    private const string PrefsModuleId = "./js/preferences.js";
    private const string ThemeModuleId = "./js/theme.js";

    private static (BunitContext Ctx, BunitJSModuleInterop PrefsModule, BunitJSModuleInterop ThemeModule)
        CreateContext()
    {
        var ctx = new BunitContext();
        // Loose mode: void invocations (set / setJson / applyTheme) succeed silently and
        // are recorded in module.Invocations. Configured returning invocations (get / getJson /
        // getSystemPreference) match by identifier+args+TValue.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var prefsModule = ctx.JSInterop.SetupModule(PrefsModuleId);
        var themeModule = ctx.JSInterop.SetupModule(ThemeModuleId);

        // Inject the REAL PreferencesService Singleton (D-09 contract) backed by bUnit's IJSRuntime.
        ctx.Services.AddSingleton<PreferencesService>();
        return (ctx, prefsModule, themeModule);
    }

    // 1. Initial render with theme="light" → sun icon + "Switch to dark mode" aria-label. -------
    [Fact]
    public void InitialRender_LightTheme_RendersSunIcon_And_DarkAriaLabel()
    {
        var (ctx, prefsModule, themeModule) = CreateContext();
        using var _ = ctx;
        // PreferencesService.GetThemeAsync uses preferences.js `get` (RAW string contract).
        // Returning "light" (or null with the system-pref fallback) keeps the toggle in light mode.
        prefsModule.Setup<string?>("get", PreferencesService.KeyTheme).SetResult("light");
        themeModule.Setup<string>("getSystemPreference").SetResult("light");

        var cut = ctx.Render<ThemeToggle>();

        // OnAfterRenderAsync(firstRender:true) hydrates _isDark from "light" → false → sun icon.
        cut.WaitForAssertion(() => Assert.Contains("bi-sun-fill", cut.Markup));
        Assert.Contains("Switch to dark mode", cut.Markup);
        Assert.DoesNotContain("bi-moon-stars-fill", cut.Markup);
    }

    // 2. Initial render with theme="dark" → moon icon + "Switch to light mode" aria-label. ------
    [Fact]
    public void InitialRender_DarkTheme_RendersMoonIcon_And_LightAriaLabel()
    {
        var (ctx, prefsModule, themeModule) = CreateContext();
        using var _ = ctx;
        prefsModule.Setup<string?>("get", PreferencesService.KeyTheme).SetResult("dark");
        // theme.js getSystemPreference is irrelevant when the key is present — but bUnit's Loose
        // mode still requires the module to be registered (done in CreateContext).

        var cut = ctx.Render<ThemeToggle>();

        cut.WaitForAssertion(() => Assert.Contains("bi-moon-stars-fill", cut.Markup));
        Assert.Contains("Switch to light mode", cut.Markup);
        Assert.DoesNotContain("bi-sun-fill", cut.Markup);
    }

    // 3. Click flips theme + calls SetTheme + calls applyTheme (both directions). ---------------
    [Fact]
    public void Click_Flips_Theme_And_Calls_SetTheme_And_ApplyTheme()
    {
        var (ctx, prefsModule, themeModule) = CreateContext();
        using var _ = ctx;
        // Start from light. PreferencesService default for an absent `theme` key is "light".
        prefsModule.Setup<string?>("get", PreferencesService.KeyTheme).SetResult("light");

        var cut = ctx.Render<ThemeToggle>();
        cut.WaitForAssertion(() => Assert.Contains("bi-sun-fill", cut.Markup));

        // First click: light → dark. Exercises the "_isDark = !false → true" branch.
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Contains("bi-moon-stars-fill", cut.Markup));

        // Second click: dark → light. Exercises the "_isDark = !true → false" branch
        // (closes the ToggleAsync ternary 50% branch coverage gap).
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Contains("bi-sun-fill", cut.Markup));

        // PreferencesService.SetThemeAsync uses preferences.js `set` (RAW string contract).
        // Both `set("theme", "dark")` and `set("theme", "light")` should have been invoked.
        Assert.Contains(prefsModule.Invocations, i =>
            i.Identifier == "set"
            && i.Arguments.Count >= 2
            && (i.Arguments[0] as string) == PreferencesService.KeyTheme
            && (i.Arguments[1] as string) == "dark");
        Assert.Contains(prefsModule.Invocations, i =>
            i.Identifier == "set"
            && i.Arguments.Count >= 2
            && (i.Arguments[0] as string) == PreferencesService.KeyTheme
            && (i.Arguments[1] as string) == "light");

        // Both applyTheme("dark") and applyTheme("light") should have been invoked from clicks.
        Assert.Contains(themeModule.Invocations, i =>
            i.Identifier == "applyTheme"
            && i.Arguments.Count >= 1
            && (i.Arguments[0] as string) == "dark");
        Assert.Contains(themeModule.Invocations, i =>
            i.Identifier == "applyTheme"
            && i.Arguments.Count >= 1
            && (i.Arguments[0] as string) == "light");
    }

    // 4. OnAfterRenderAsync(firstRender:true) hydrates _isDark from persisted value. ------------
    [Fact]
    public void OnAfterRenderAsync_FirstRender_HydratesFromPersistedDarkValue()
    {
        var (ctx, prefsModule, themeModule) = CreateContext();
        using var _ = ctx;
        // Persisted "dark" — hydration flips _isDark to true and applyTheme("dark") is called.
        prefsModule.Setup<string?>("get", PreferencesService.KeyTheme).SetResult("dark");

        var cut = ctx.Render<ThemeToggle>();

        // Hydrated markup reflects the dark icon (regression: would render sun if _restored
        // never flipped or StateHasChanged was missing after restore).
        cut.WaitForAssertion(() => Assert.Contains("bi-moon-stars-fill", cut.Markup));

        // applyTheme called with "dark" during hydration (anti-FOUC re-apply path).
        Assert.Contains(themeModule.Invocations, i =>
            i.Identifier == "applyTheme"
            && i.Arguments.Count >= 1
            && (i.Arguments[0] as string) == "dark");
    }

    // 5. REQ-UI-31 system-preference fallback when persisted theme is empty/null. ----------------
    // PreferencesService.GetThemeAsync defaults to "light", but if the underlying preferences.js
    // `get` returns an empty string (e.g. localStorage explicitly storing ""), the service
    // returns "" — the IsNullOrEmpty(theme) branch fires and ThemeToggle calls
    // theme.js getSystemPreference() per REQ-UI-31. This test exercises that branch and locks
    // in the "system pref says dark → moon icon" semantics.
    [Fact]
    public void OnAfterRenderAsync_EmptyPersistedTheme_FallsBackToSystemPreference_Dark()
    {
        var (ctx, prefsModule, themeModule) = CreateContext();
        using var _ = ctx;
        // Empty string persisted — triggers IsNullOrEmpty branch (REQ-UI-31).
        prefsModule.Setup<string?>("get", PreferencesService.KeyTheme).SetResult(string.Empty);
        themeModule.Setup<string>("getSystemPreference").SetResult("dark");

        var cut = ctx.Render<ThemeToggle>();

        cut.WaitForAssertion(() => Assert.Contains("bi-moon-stars-fill", cut.Markup));
        // System-pref fallback was actually called — Pitfall 7 lock.
        Assert.Contains(themeModule.Invocations, i => i.Identifier == "getSystemPreference");
        // applyTheme called with "dark" (the value derived from system pref).
        Assert.Contains(themeModule.Invocations, i =>
            i.Identifier == "applyTheme"
            && i.Arguments.Count >= 1
            && (i.Arguments[0] as string) == "dark");
    }
}
