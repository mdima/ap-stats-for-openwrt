using System.Text.Json;
using Microsoft.JSInterop;

namespace ApStatsForOpenWrt.Services;

/// <summary>
/// Phase 8 Plan 08-06 D-10 — minimal abstraction <see cref="NotificationsGate"/> implements
/// to expose the cached <c>IsEnabled</c> boolean to <see cref="ToastService"/> and
/// <see cref="EventDetectionService"/> without dragging the full PreferencesService surface
/// into their constructor or test fixtures.
///
/// Hot-path gate ONLY (D-09): <c>IsEnabled</c> reads a volatile field on the Singleton
/// <see cref="NotificationsGate"/> — no JS interop, no async cost. Tests construct
/// ToastService with a hand-rolled FakeGate; production wires
/// <see cref="NotificationsGate"/> as a Singleton and routes <see cref="INotificationsGate"/>
/// to it via DI forwarding in Program.cs.
///
/// Phase 8 deviation note (2026-05-08): the original D-09 line said "PreferencesService is
/// Singleton". That was a captive-dep bug — PreferencesService captures <see cref="IJSRuntime"/>
/// (Scoped per Blazor circuit). The fix splits the gate (Singleton) from the JS-touching
/// service (Scoped). See <c>.planning/debug/resolved/phase8-prefs-captive-dep.md</c>.
/// </summary>
public interface INotificationsGate
{
    bool IsEnabled { get; }
}

/// <summary>
/// Phase 8 D-09 (post-fix) — Singleton holder of the cached
/// <c>notifications-enabled</c> boolean. Hot-path read is volatile and lock-free; writes
/// arrive from the Scoped <see cref="PreferencesService"/> on cache-seed
/// (<see cref="PreferencesService.IsNotificationsEnabledAsync"/>) and on every
/// <see cref="PreferencesService.SetNotificationsEnabledAsync"/>.
///
/// Why split from PreferencesService: PreferencesService captures
/// <see cref="IJSRuntime"/> (Scoped per Blazor circuit), so it CANNOT itself be Singleton
/// without a captive-dependency violation. The Singleton seam moves here; the Scoped
/// service mirrors its cached value into this gate so consumers (ToastService,
/// EventDetectionService) keep their lock-free Singleton-typed dependency.
///
/// Default = <c>true</c> (notifications-on) until any circuit seeds otherwise.
/// </summary>
public sealed class NotificationsGate : INotificationsGate
{
    // Volatile read/write so cross-circuit visibility is guaranteed without a lock.
    private volatile bool _isEnabled = true;

    /// <inheritdoc />
    public bool IsEnabled => _isEnabled;

    /// <summary>
    /// Mirror the per-circuit cached value into the Singleton gate. Called from
    /// <see cref="PreferencesService.IsNotificationsEnabledAsync"/> on first seed and
    /// from <see cref="PreferencesService.SetNotificationsEnabledAsync"/> on every write.
    /// </summary>
    public void Set(bool value) => _isEnabled = value;
}

/// <summary>
/// Phase 8 D-12 (post-fix): Scoped wrapper over wwwroot/js/preferences.js.
/// Strongly-typed methods per preference (NOT generic Get&lt;T&gt;/Set&lt;T&gt;).
/// Internally prepends 'ap-stats-for-openwrt:' to every localStorage key.
/// IsNotificationsEnabled boolean is cached in memory after first read for
/// fast hot-path access; the value is mirrored into the Singleton
/// <see cref="NotificationsGate"/> so cross-circuit consumers (ToastService,
/// EventDetectionService — both Singletons) see the same flag without ever
/// touching PreferencesService directly (D-09 / D-10).
///
/// Lifetime: Scoped (per Blazor circuit). Was Singleton in the original Plan 08-01
/// registration; that was a captive-dep bug — see class-doc on
/// <see cref="NotificationsGate"/>.
///
/// MUST NOT touch IAppSettingsService, IDbContextFactory, or any repository —
/// preferences are per-browser, never persisted to the database (Phase 8 hard constraint).
///
/// Storage shape contract (closes plan-checker BLOCKER 1):
/// - 'theme'                → RAW string ('dark' | 'light'). Uses set/get NOT setJson/getJson
///                            so the App.razor anti-FOUC inline script can do
///                            `localStorage.getItem(...) === 'dark'` without JSON.parse.
/// - 'search-text'          → RAW string (nullable). Free-form user text — double-encoding
///                            would lose round-trip parity for needles containing `"`.
/// - 'sidebar-collapsed'    → JSON-encoded bool. JSON.stringify(true) === 'true' so the
///                            inline script's `=== 'true'` compare still matches.
/// - 'notifications-enabled'→ JSON-encoded bool (inline script doesn't read this key).
/// - 'active-filters'       → JSON-encoded DashboardFilters (object).
/// - 'sort-state'           → JSON-encoded SortState? (object/null).
/// - 'kpi-selection'        → JSON-encoded StatusFilter? (string/null).
/// </summary>
public sealed class PreferencesService
{
    // Key constants (D-12 lockdown)
    public const string KeyTheme = "theme";
    public const string KeySidebarCollapsed = "sidebar-collapsed";
    public const string KeyNotificationsEnabled = "notifications-enabled";
    public const string KeySearchText = "search-text";
    public const string KeyActiveFilters = "active-filters";
    public const string KeySortState = "sort-state";
    public const string KeyKpiSelection = "kpi-selection";

    /// <summary>
    /// The four "view-state" keys cleared by <see cref="ClearViewStateAsync"/>.
    /// Theme / sidebar-collapsed / notifications-enabled are intentionally absent
    /// (REQ-UI-23, D-12 — shell-state survives "Reset filters").
    /// </summary>
    public static readonly IReadOnlyList<string> ViewStateKeys = new[]
    {
        KeySearchText, KeyActiveFilters, KeySortState, KeyKpiSelection
    };

    private readonly IJSRuntime _js;
    private readonly NotificationsGate _gate;
    private IJSObjectReference? _module;
    private readonly SemaphoreSlim _moduleLock = new(1, 1);
    private bool _notificationsCached;
    private bool _notificationsEnabled = true;

    /// <summary>
    /// Production constructor — DI provides both the per-circuit <see cref="IJSRuntime"/>
    /// and the Singleton <see cref="NotificationsGate"/> mirror target.
    /// </summary>
    public PreferencesService(IJSRuntime js, NotificationsGate gate)
    {
        _js = js;
        _gate = gate;
    }

    /// <summary>
    /// Test convenience constructor — fabricates a private <see cref="NotificationsGate"/>
    /// when one is not supplied. Tests that hold a <see cref="PreferencesService"/>
    /// reference and assert <see cref="IsNotificationsEnabled"/> after a Set continue to
    /// see the same per-instance value because the constructed gate is mirrored on every
    /// write. Production code paths NEVER hit this overload — the Program.cs registration
    /// always passes the Singleton gate.
    /// </summary>
    public PreferencesService(IJSRuntime js) : this(js, new NotificationsGate()) { }

    /// <summary>
    /// Synchronous fast-path access used by tests + the Settings UI checkbox. Returns the
    /// cached value once <see cref="IsNotificationsEnabledAsync"/> has seeded it; otherwise
    /// the spec-default <c>true</c>. ToastService / EventDetectionService consume the
    /// Singleton <see cref="INotificationsGate"/> instead — they MUST NOT depend on the
    /// per-circuit instance (D-09 hot-path).
    /// </summary>
    public bool IsNotificationsEnabled => _notificationsCached ? _notificationsEnabled : true;

    // --- Theme: RAW string (anti-FOUC contract — see class doc) ---
    // Theme is persisted as the literal 4-character string 'dark' (or 'light') so
    // the App.razor inline anti-FOUC script can compare via
    // `localStorage.getItem('ap-stats-for-openwrt:theme') === 'dark'`. NEVER use
    // setJson/getJson here — JSON-encoding would store '"dark"' (6 chars with embedded
    // quote characters) and silently break the inline pre-paint compare.
    public Task<string> GetThemeAsync() => GetRawOrDefaultAsync(KeyTheme, "light");
    public Task SetThemeAsync(string value) => SetRawAsync(KeyTheme, value);

    // --- Sidebar: JSON-encoded bool (string 'true'/'false' accidentally matches) ---
    public Task<bool> GetSidebarCollapsedAsync() => GetJsonStructOrDefaultAsync<bool>(KeySidebarCollapsed, false);
    public Task SetSidebarCollapsedAsync(bool value) => SetJsonAsync(KeySidebarCollapsed, value);

    // --- Search: RAW string (free-form user text, see class doc) ---
    public async Task<string?> GetSearchTextAsync()
    {
        var mod = await EnsureModuleAsync();
        if (mod is null) return null;
        try
        {
            return await mod.InvokeAsync<string?>("get", KeySearchText);
        }
        catch (JSException) { return null; }
    }
    public Task SetSearchTextAsync(string? value) => SetRawAsync(KeySearchText, value);

    // --- Notifications (cached for hot path) — JSON-encoded bool ---
    public async Task<bool> IsNotificationsEnabledAsync()
    {
        if (_notificationsCached) return _notificationsEnabled;
        _notificationsEnabled = await GetJsonStructOrDefaultAsync<bool>(KeyNotificationsEnabled, true);
        _notificationsCached = true;
        // Mirror to the Singleton gate on first seed so cross-circuit consumers see the
        // operator's persisted preference rather than the spec-default (D-10).
        _gate.Set(_notificationsEnabled);
        return _notificationsEnabled;
    }
    public async Task SetNotificationsEnabledAsync(bool value)
    {
        await SetJsonAsync(KeyNotificationsEnabled, value);
        _notificationsEnabled = value;
        _notificationsCached = true;
        // Mirror to the Singleton gate on every write — this is the hot-path update path
        // for ToastService / EventDetectionService (D-10 defense-in-depth).
        _gate.Set(value);
    }

    // --- Filters / Sort / KPI: JSON-encoded (inline script never reads these) ---
    public Task<DashboardFilters> GetActiveFiltersAsync()
        => GetJsonClassOrDefaultAsync(KeyActiveFilters, DashboardFilters.Empty);
    public Task SetActiveFiltersAsync(DashboardFilters value) => SetJsonAsync(KeyActiveFilters, value);

    public Task<SortState?> GetSortStateAsync() => GetJsonClassOrNullAsync<SortState>(KeySortState);
    public Task SetSortStateAsync(SortState? value) => SetJsonAsync(KeySortState, value);

    public Task<StatusFilter?> GetKpiSelectionAsync() => GetJsonStructOrNullAsync<StatusFilter>(KeyKpiSelection);
    public Task SetKpiSelectionAsync(StatusFilter? value) => SetJsonAsync(KeyKpiSelection, value);

    // --- Carve-out clear (REQ-UI-23, D-12) ---
    /// <summary>
    /// Clears the four view-state keys (search-text / active-filters / sort-state /
    /// kpi-selection) by delegating to preferences.js <c>clearKeys</c>. Theme,
    /// sidebar-collapsed, and notifications-enabled are intentionally preserved
    /// — they are shell-state, not view-state (REQ-UI-23).
    /// </summary>
    public async Task ClearViewStateAsync()
    {
        var mod = await EnsureModuleAsync();
        if (mod is null) return;
        await mod.InvokeVoidAsync("clearKeys", new object[] { ViewStateKeys });
        // theme / sidebar-collapsed / notifications-enabled left intact by contract.
    }

    // --- Internals ---
    private async Task<IJSObjectReference?> EnsureModuleAsync()
    {
        if (_module is not null) return _module;
        await _moduleLock.WaitAsync();
        try
        {
            if (_module is not null) return _module;
            try
            {
                _module = await _js.InvokeAsync<IJSObjectReference>("import", "./js/preferences.js");
            }
            catch (JSException) { return null; }                // prerender / interop unavailable
            catch (InvalidOperationException) { return null; }  // prerender (no JS runtime yet)
            catch (TaskCanceledException) { return null; }
            return _module;
        }
        finally { _moduleLock.Release(); }
    }

    // RAW string accessor — uses preferences.js `get`, NOT `getJson`. Anti-FOUC contract.
    private async Task<string> GetRawOrDefaultAsync(string key, string @default)
    {
        var mod = await EnsureModuleAsync();
        if (mod is null) return @default;
        try
        {
            var raw = await mod.InvokeAsync<string?>("get", key);
            return raw ?? @default;
        }
        catch (JSException) { return @default; }
    }

    // Value-type accessor — uses Nullable<T> to round-trip JS `null` losslessly.
    // Without `where T : struct`, `T?` for an unconstrained T compiles to `T` (the nullable
    // annotation, NOT Nullable<T>), so JS-null deserializes to default(T) (false / 0), and
    // we cannot distinguish "absent key" from "explicitly false / zero". The struct constraint
    // forces `T?` to mean Nullable<T>, restoring the null sentinel.
    private async Task<T> GetJsonStructOrDefaultAsync<T>(string key, T @default) where T : struct
    {
        var mod = await EnsureModuleAsync();
        if (mod is null) return @default;
        try
        {
            var value = await mod.InvokeAsync<T?>("getJson", key);
            return value ?? @default;
        }
        catch (JsonException) { return @default; }
        catch (JSException) { return @default; }
    }

    private async Task<T?> GetJsonStructOrNullAsync<T>(string key) where T : struct
    {
        var mod = await EnsureModuleAsync();
        if (mod is null) return null;
        try { return await mod.InvokeAsync<T?>("getJson", key); }
        catch (JsonException) { return null; }
        catch (JSException) { return null; }
    }

    // Reference-type accessor — `T?` already means a nullable reference, so round-trips JS null.
    private async Task<T> GetJsonClassOrDefaultAsync<T>(string key, T @default) where T : class
    {
        var mod = await EnsureModuleAsync();
        if (mod is null) return @default;
        try
        {
            var value = await mod.InvokeAsync<T?>("getJson", key);
            return value ?? @default;
        }
        catch (JsonException) { return @default; }
        catch (JSException) { return @default; }
    }

    private async Task<T?> GetJsonClassOrNullAsync<T>(string key) where T : class
    {
        var mod = await EnsureModuleAsync();
        if (mod is null) return null;
        try { return await mod.InvokeAsync<T?>("getJson", key); }
        catch (JsonException) { return null; }
        catch (JSException) { return null; }
    }

    private async Task SetJsonAsync<T>(string key, T value)
    {
        var mod = await EnsureModuleAsync();
        if (mod is null) return;
        try
        {
            await mod.InvokeVoidAsync("setJson", key, value);
        }
        catch (JSException) { /* swallow — best-effort persistence */ }
    }

    // RAW string setter — uses preferences.js `set` (NOT `setJson`). Anti-FOUC contract.
    private async Task SetRawAsync(string key, string? value)
    {
        var mod = await EnsureModuleAsync();
        if (mod is null) return;
        try
        {
            await mod.InvokeVoidAsync("set", key, value);
        }
        catch (JSException) { /* swallow — best-effort persistence */ }
    }
}

/// <summary>
/// Phase 8 D-12 — dashboard filter selection persisted via PreferencesService.
/// Inline script never reads this key, so JSON-encoding is safe.
/// </summary>
public sealed record DashboardFilters(
    IReadOnlyList<string> Ssids,
    IReadOnlyList<string> Aps,
    IReadOnlyList<string> Statuses)
{
    public static DashboardFilters Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Phase 8 D-12 — sort column + direction for the dashboard table.
/// </summary>
public sealed record SortState(string Column, bool Descending);

/// <summary>
/// Phase 8 D-12 — KPI tile selection (status filter). Null means "All".
/// </summary>
public enum StatusFilter { Online, Stale, Offline }
