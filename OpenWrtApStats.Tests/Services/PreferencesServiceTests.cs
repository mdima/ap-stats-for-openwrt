using Bunit;
using OpenWrtApStats.Services;
using Xunit;

namespace OpenWrtApStats.Tests.Services;

/// <summary>
/// Phase 8 Plan 08-01 Task 2 — exercises <see cref="PreferencesService"/> against a
/// bUnit-faked JS module loaded via <c>JSRuntime.InvokeAsync&lt;IJSObjectReference&gt;("import",
/// "./js/preferences.js")</c>. Locks in the storage-shape contract (theme + search-text RAW;
/// everything else JSON), the four-key carve-out of <see cref="PreferencesService.ClearViewStateAsync"/>,
/// the cached IsNotificationsEnabled hot-path (D-09), and prerender safety (every Get* returns
/// the declared default when the JS module fails to load).
///
/// Test stack: xUnit v3 + bUnit 2.7.2 + Microsoft.Testing.Extensions.CodeCoverage (NEVER coverlet).
/// Coverage gate per D-18: ≥ 80% line + ≥ 80% branch on PreferencesService.cs.
/// </summary>
public sealed class PreferencesServiceTests
{
    private const string ModuleId = "./js/preferences.js";

    private static (BunitContext Ctx, BunitJSModuleInterop Module, PreferencesService Svc) CreateService()
    {
        var ctx = new BunitContext();
        // Loose mode: unconfigured void invocations (set / setJson / clearKeys) succeed silently
        // and are recorded in module.Invocations. Configured returning invocations (get / getJson)
        // still match by identifier+args+TValue. This gives us the "spy" semantics we need.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = ctx.JSInterop.SetupModule(ModuleId);
        var svc = new PreferencesService(ctx.JSInterop.JSRuntime);
        return (ctx, module, svc);
    }

    // 1. Theme defaults to "light" when key absent ---------------------------------------------
    [Fact]
    public async Task GetThemeAsync_DefaultsToLight_WhenKeyAbsent()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;
        // Module returns null for `get('theme')`.
        module.Setup<string?>("get", PreferencesService.KeyTheme).SetResult(null);

        var result = await svc.GetThemeAsync();

        Assert.Equal("light", result);
    }

    // 2. Theme round-trips as RAW string (not JSON-encoded) ------------------------------------
    [Fact]
    public async Task SetThemeAsync_RoundTrips_AsRawString()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;

        await svc.SetThemeAsync("dark");

        var setInvocation = Assert.Single(
            module.Invocations,
            i => i.Identifier == "set");
        Assert.Equal(PreferencesService.KeyTheme, setInvocation.Arguments[0]);
        var stored = setInvocation.Arguments[1] as string;
        Assert.NotNull(stored);
        Assert.Equal("dark", stored);                  // literal 4 chars
        Assert.DoesNotContain('"', stored);            // no embedded quote chars (anti-FOUC contract)
        Assert.Equal(4, stored.Length);
    }

    // 3. Theme NEVER invokes setJson (regression guard against future drift) --------------------
    [Fact]
    public async Task SetThemeAsync_DoesNotInvokeSetJson()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;

        await svc.SetThemeAsync("dark");

        Assert.DoesNotContain(module.Invocations, i =>
            i.Identifier == "setJson"
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeyTheme);
    }

    // 4a. Sidebar default false / 4b. round-trips ----------------------------------------------
    [Fact]
    public async Task GetSidebarCollapsedAsync_DefaultsFalse_WhenKeyAbsent()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;
        // Default value of bool ⇒ false. With Loose mode + no Setup, getJson returns default.
        // (We could Setup<bool>(...).SetResult(false) but that double-asserts the default path.)
        var result = await svc.GetSidebarCollapsedAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task SetSidebarCollapsedAsync_PersistsViaSetJson()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;

        await svc.SetSidebarCollapsedAsync(true);

        var inv = Assert.Single(module.Invocations, i => i.Identifier == "setJson");
        Assert.Equal(PreferencesService.KeySidebarCollapsed, inv.Arguments[0]);
        Assert.Equal(true, inv.Arguments[1]);
    }

    // 5. Notifications default true ------------------------------------------------------------
    [Fact]
    public async Task IsNotificationsEnabledAsync_DefaultsTrue_WhenKeyAbsent()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;
        // Service calls mod.InvokeAsync<bool?>("getJson", key); bUnit's TValue matches the
        // Nullable<bool>. Setting up with `null` lets the service take its "missing → default"
        // branch and return its declared default (true).
        module.Setup<bool?>("getJson", PreferencesService.KeyNotificationsEnabled).SetResult(null);

        var result = await svc.IsNotificationsEnabledAsync();

        Assert.True(result);
    }

    // 6. Notifications cached after first read -------------------------------------------------
    [Fact]
    public async Task IsNotificationsEnabledAsync_CachesAfterFirstRead()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;
        module.Setup<bool?>("getJson", PreferencesService.KeyNotificationsEnabled).SetResult(true);

        await svc.IsNotificationsEnabledAsync();
        await svc.IsNotificationsEnabledAsync();
        await svc.IsNotificationsEnabledAsync();

        var getJsonNotifInvocations = module.Invocations.Count(i =>
            i.Identifier == "getJson"
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeyNotificationsEnabled);
        Assert.Equal(1, getJsonNotifInvocations);
    }

    // 7. Sync IsNotificationsEnabled defaults true before any async read -----------------------
    [Fact]
    public void IsNotificationsEnabled_SyncProperty_DefaultsTrue_BeforeFirstRead()
    {
        var (ctx, _, svc) = CreateService();
        using var _disp = ctx;

        Assert.True(svc.IsNotificationsEnabled);
    }

    // 8. SetNotificationsEnabledAsync updates cache + persists ---------------------------------
    [Fact]
    public async Task SetNotificationsEnabledAsync_UpdatesCacheAndPersists()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;

        await svc.SetNotificationsEnabledAsync(false);

        Assert.False(svc.IsNotificationsEnabled);
        var inv = Assert.Single(module.Invocations, i => i.Identifier == "setJson");
        Assert.Equal(PreferencesService.KeyNotificationsEnabled, inv.Arguments[0]);
        Assert.Equal(false, inv.Arguments[1]);
    }

    // 9. ActiveFilters default empty -----------------------------------------------------------
    [Fact]
    public async Task GetActiveFiltersAsync_ReturnsEmpty_WhenKeyAbsent()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;
        module.Setup<DashboardFilters?>("getJson", PreferencesService.KeyActiveFilters).SetResult(null);

        var result = await svc.GetActiveFiltersAsync();

        Assert.NotNull(result);
        Assert.Empty(result.Ssids);
        Assert.Empty(result.Aps);
        Assert.Empty(result.Statuses);
    }

    // 10. ActiveFilters round-trips all 3 axes -------------------------------------------------
    [Fact]
    public async Task SetActiveFiltersAsync_RoundTrips_AllThreeAxes()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;
        var filters = new DashboardFilters(
            new[] { "guest", "main" },
            new[] { "ap-A" },
            new[] { "Online", "Stale" });

        await svc.SetActiveFiltersAsync(filters);

        var inv = Assert.Single(module.Invocations, i => i.Identifier == "setJson");
        Assert.Equal(PreferencesService.KeyActiveFilters, inv.Arguments[0]);
        var stored = Assert.IsType<DashboardFilters>(inv.Arguments[1]);
        Assert.Equal(new[] { "guest", "main" }, stored.Ssids);
        Assert.Equal(new[] { "ap-A" }, stored.Aps);
        Assert.Equal(new[] { "Online", "Stale" }, stored.Statuses);
    }

    // 11. KpiSelection null when key absent ----------------------------------------------------
    [Fact]
    public async Task GetKpiSelectionAsync_ReturnsNull_WhenKeyAbsent()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;
        module.Setup<StatusFilter?>("getJson", PreferencesService.KeyKpiSelection).SetResult(null);

        var result = await svc.GetKpiSelectionAsync();

        Assert.Null(result);
    }

    // 12. KpiSelection persists enum value -----------------------------------------------------
    [Fact]
    public async Task SetKpiSelectionAsync_PersistsEnumValue()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;

        await svc.SetKpiSelectionAsync(StatusFilter.Online);

        var inv = Assert.Single(module.Invocations, i => i.Identifier == "setJson");
        Assert.Equal(PreferencesService.KeyKpiSelection, inv.Arguments[0]);
        Assert.Equal(StatusFilter.Online, inv.Arguments[1]);
    }

    // 13. SearchText round-trips as RAW string (no JSON wrapping) ------------------------------
    [Fact]
    public async Task SetSearchTextAsync_RoundTrips_AsRawString()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;
        // Free-form needle containing a literal `"` — the round-trip parity case.
        var needle = "guest \"vip\"";

        await svc.SetSearchTextAsync(needle);

        var inv = Assert.Single(module.Invocations, i => i.Identifier == "set");
        Assert.Equal(PreferencesService.KeySearchText, inv.Arguments[0]);
        Assert.Equal(needle, inv.Arguments[1] as string);
        // setJson MUST NOT be invoked for search-text.
        Assert.DoesNotContain(module.Invocations, i =>
            i.Identifier == "setJson"
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeySearchText);
    }

    // 14. ClearViewStateAsync — exactly the 4 view-state keys ----------------------------------
    [Fact]
    public async Task ClearViewStateAsync_ClearsExactlyFourKeys()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;

        await svc.ClearViewStateAsync();

        var inv = Assert.Single(module.Invocations, i => i.Identifier == "clearKeys");
        var keys = Assert.IsAssignableFrom<IReadOnlyList<string>>(inv.Arguments[0]);
        Assert.Equal(
            new[] { "search-text", "active-filters", "sort-state", "kpi-selection" },
            keys.ToArray());
    }

    // 15. ClearViewStateAsync — does NOT touch theme / sidebar / notifications -----------------
    [Fact]
    public async Task ClearViewStateAsync_DoesNotClearTheme_OrSidebar_OrNotifications()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;

        await svc.ClearViewStateAsync();

        foreach (var inv in module.Invocations.Where(i => i.Identifier == "clearKeys"))
        {
            var keys = (IReadOnlyList<string>?)inv.Arguments[0];
            Assert.NotNull(keys);
            Assert.DoesNotContain(PreferencesService.KeyTheme, keys);
            Assert.DoesNotContain(PreferencesService.KeySidebarCollapsed, keys);
            Assert.DoesNotContain(PreferencesService.KeyNotificationsEnabled, keys);
        }
    }

    // 16. Prerender safety — interop unavailable ⇒ every Get* returns its declared default ----
    [Fact]
    public async Task EnsureModuleAsync_SwallowsPrerenderJSException_ReturnsDefaults()
    {
        // Custom IJSRuntime that throws JSException on every call — the prerender shape (the
        // SignalR circuit / JS bridge not yet connected). EnsureModuleAsync must swallow this
        // and every Get* must return its declared default rather than propagate.
        var svc = new PreferencesService(new ThrowingJSRuntime());

        Assert.Equal("light", await svc.GetThemeAsync());
        Assert.False(await svc.GetSidebarCollapsedAsync());
        Assert.True(await svc.IsNotificationsEnabledAsync());
        Assert.Null(await svc.GetSearchTextAsync());
        var filters = await svc.GetActiveFiltersAsync();
        Assert.NotNull(filters);
        Assert.Empty(filters.Ssids);
        Assert.Null(await svc.GetSortStateAsync());
        Assert.Null(await svc.GetKpiSelectionAsync());
    }

    /// <summary>
    /// IJSRuntime stub that always throws JSException — emulates Blazor prerender / interop unavailable.
    /// </summary>
    private sealed class ThrowingJSRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => ValueTask.FromException<TValue>(new Microsoft.JSInterop.JSException("interop unavailable (prerender)"));

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, System.Threading.CancellationToken cancellationToken, object?[]? args)
            => ValueTask.FromException<TValue>(new Microsoft.JSInterop.JSException("interop unavailable (prerender)"));
    }

    // 17. Concurrent ensure-module — semaphore guarantees a single import call -----------------
    [Fact]
    public async Task EnsureModuleAsync_LoadsModuleOnce_ConcurrentCalls()
    {
        var (ctx, module, svc) = CreateService();
        using var _ = ctx;
        module.Setup<string?>("get", PreferencesService.KeyTheme).SetResult("dark");

        // Fire two parallel reads at once.
        var t1 = svc.GetThemeAsync();
        var t2 = svc.GetThemeAsync();
        await Task.WhenAll(t1, t2);

        // Exactly one `import("./js/preferences.js")` should have been issued.
        // bUnit records the import as an invocation on the JSRuntime root with identifier "import".
        var importInvocations = ctx.JSInterop.Invocations
            .Count(i => i.Identifier == "import");
        Assert.Equal(1, importInvocations);
    }
}
