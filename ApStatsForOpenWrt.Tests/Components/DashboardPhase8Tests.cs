using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Components.Shared;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Services.Vendor;
using Xunit;
using Dashboard = ApStatsForOpenWrt.Components.Pages.Dashboard;

namespace ApStatsForOpenWrt.Tests.Components;

/// <summary>
/// Phase 8 Plan 08-08 Task 3 — covers the NET-NEW Dashboard wiring added in Task 1:
///   * REQ-UI-02 bidirectional KPI ↔ Status-chip sync (Tests 1-3, including the
///     INFO #7 multi-select-collapses-to-null asymmetry).
///   * REQ-UI-23 Reset filters carve-out (Test 4): clears search/filters/sort/KPI but
///     LEAVES theme/sidebar/notifications-enabled untouched in localStorage.
///   * REQ-UI-22 prerender-safe view-state restore (Test 5).
///   * REQ-UI-11 AP chip status badge mapping (Tests 6-9).
///   * REQ-UI-53 row-status-changed CSS hook (Test 10).
///
/// D-18 ≥ 80% line + branch gate applies to the net-new code paths only — the Phase 5
/// table mechanics keep their coverage from DashboardTests.cs.
///
/// Reflection used for OnKpiSelectedAsync / OnToggleStatusAsync / ResetFiltersAsync /
/// MapNodeStatus / DetectRowStatusChanges / OnAfterRenderAsync because they are private
/// instance methods on the Dashboard component — bUnit's documented escape hatch for
/// private-method coverage when surface tests would be too indirect.
/// </summary>
public sealed class DashboardPhase8Tests
{
    private const string ModuleId = "./js/preferences.js";

    // ── Stub repos (mirror DashboardTests.cs Phase 5 pattern) ─────────────────
    private sealed class StubNodeRepo(IEnumerable<WiFiNode> seed) : INodeRepo
    {
        private readonly List<WiFiNode> _nodes = seed.ToList();
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WiFiNode>>(_nodes);
        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<WiFiNode?>(_nodes.FirstOrDefault(n => n.Id == id));
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string h, string u, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePasswordAsync(int id, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => Task.FromResult(_nodes.Any());
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(ApStatsForOpenWrt.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastEnrichmentRefreshAtAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubNetworkRepo : INetworkRepo
    {
        public Dictionary<int, List<WiFiNetwork>> ByNode { get; } = new();
        public bool AnyEnabledOverride { get; set; }
        public Task<IReadOnlyList<WiFiNetwork>> GetByNodeAsync(int wiFiNodeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WiFiNetwork>>(ByNode.TryGetValue(wiFiNodeId, out var list) ? list : new List<WiFiNetwork>());
        public Task<WiFiNetwork?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<WiFiNetwork?>(null);
        public Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertManyAsync(int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertManyAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAbsentDisabledAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEnabledAsync(CancellationToken ct = default) => Task.FromResult(AnyEnabledOverride);
    }

    private sealed class StubStationRepo(IEnumerable<WLANStation> seed) : IStationRepo
    {
        private readonly List<WLANStation> _stations = seed.ToList();
        public void Replace(IEnumerable<WLANStation> rows)
        {
            _stations.Clear();
            _stations.AddRange(rows);
        }
        public Task<IReadOnlyList<WLANStation>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WLANStation>>(_stations);
        public Task<WLANStation?> GetByMacAsync(string macAddress, CancellationToken ct = default) =>
            Task.FromResult<WLANStation?>(_stations.FirstOrDefault(s => s.MacAddress == macAddress));
        public Task<IReadOnlyList<string>> GetAllMacsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(_stations.Select(s => s.MacAddress).Distinct().ToList());
        public Task SetNameAsync(string macAddress, string? name, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetIsRandomMacAsync(string macAddress, bool isRandomMac, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteByMacAsync(string macAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(ApStatsForOpenWrt.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(ApStatsForOpenWrt.Data.DatabaseContext db, IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetMacsByNodeAsync(int nodeId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(_stations.Select(s => s.MacAddress).Distinct().ToList());
    }

    private sealed class StubOuiLookup : IOuiLookup
    {
        public string? GetVendor(string macAddress) => null;
    }

    private sealed class StubSettings : IAppSettingsService
    {
        public int     PollingIntervalSeconds { get; } = 30;
        public string? UIPasswordHash         { get; } = null;
        public bool    WizardDismissed        { get; } = true;
        public int     RefreshIntervalSeconds { get; } = 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; } = Array.Empty<string>();
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    // ── Test harness ─────────────────────────────────────────────────────────
    private sealed record Harness(
        BunitContext Ctx,
        BunitJSModuleInterop Module,
        StubNodeRepo NodeRepo,
        StubNetworkRepo NetRepo,
        StubStationRepo StationRepo,
        PollNotifier Notifier,
        PreferencesService Prefs);

    private static Harness Build(
        IEnumerable<WiFiNode>? nodes = null,
        IEnumerable<WLANStation>? stations = null,
        Dictionary<int, List<WiFiNetwork>>? networks = null,
        bool anyEnabled = true)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = ctx.JSInterop.SetupModule(ModuleId);

        var nodeList = nodes?.ToList() ?? new List<WiFiNode>();
        var stationList = stations?.ToList() ?? new List<WLANStation>();
        var nr = new StubNodeRepo(nodeList);
        var nws = new StubNetworkRepo { AnyEnabledOverride = anyEnabled };
        if (networks is not null)
        {
            foreach (var kvp in networks) nws.ByNode[kvp.Key] = kvp.Value;
        }
        var sr = new StubStationRepo(stationList);
        var notifier = new PollNotifier();
        var prefs = new PreferencesService(ctx.JSInterop.JSRuntime);

        ctx.Services.AddSingleton<INodeRepo>(nr);
        ctx.Services.AddSingleton<INetworkRepo>(nws);
        ctx.Services.AddSingleton<IStationRepo>(sr);
        ctx.Services.AddSingleton<IOuiLookup>(new StubOuiLookup());
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings());
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton(prefs);
        return new Harness(ctx, module, nr, nws, sr, notifier, prefs);
    }

    // Helpers to invoke private Dashboard methods via reflection. bUnit documented escape
    // hatch for private-method coverage when surface tests would be too indirect.
    // Async invocations must dispatch via the rendered component's InvokeAsync so the
    // Blazor renderer dispatcher is on the calling thread when StateHasChanged() fires.
    private static Task InvokeAsync(IRenderedComponent<Dashboard> cut, string name, params object?[] args)
    {
        var mi = typeof(Dashboard).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Dashboard.{name} not found via reflection");
        return cut.InvokeAsync(async () =>
        {
            var task = (Task)mi.Invoke(cut.Instance, args)!;
            await task;
        });
    }

    private static T? GetField<T>(Dashboard cut, string name)
    {
        var fi = typeof(Dashboard).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Dashboard.{name} field not found via reflection");
        var raw = fi.GetValue(cut);
        return raw is null ? default : (T)raw;
    }

    private static void SetField(Dashboard cut, string name, object? value)
    {
        var fi = typeof(Dashboard).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Dashboard.{name} field not found via reflection");
        fi.SetValue(cut, value);
    }

    private static T InvokeFunc<T>(Dashboard cut, string name, params object?[] args)
    {
        var mi = typeof(Dashboard).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Dashboard.{name} not found via reflection");
        return (T)mi.Invoke(cut, args)!;
    }

    // Convenience: render a populated dashboard with at least one node + station so the
    // "else" branch (PerApStrip-removed; KPI bar + filter card + table) executes.
    private static (Harness h, IRenderedComponent<Dashboard> cut) RenderPopulated(
        IEnumerable<WiFiNode>? nodes = null,
        IEnumerable<WLANStation>? stations = null,
        Dictionary<int, List<WiFiNetwork>>? networks = null)
    {
        var defaultNode = new WiFiNode
        {
            Id = 1, Hostname = "ap-1", Username = "root", Password = "x",
            Status = Status.Online, LastPollDate = DateTime.UtcNow,
        };
        var defaultNet = new WiFiNetwork
        {
            Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55",
            SSID = "guest", Enabled = true,
        };
        var defaultStation = new WLANStation
        {
            Id = 100, MacAddress = "AA:BB:CC:00:00:01", WiFiNetworkId = 10,
            Hostname = "alice", Status = Status.Online, LastPollDate = DateTime.UtcNow,
        };

        var h = Build(
            nodes ?? new[] { defaultNode },
            stations ?? new[] { defaultStation },
            networks ?? new Dictionary<int, List<WiFiNetwork>>
            {
                [1] = new List<WiFiNetwork> { defaultNet },
            });
        var cut = h.Ctx.Render<Dashboard>();
        return (h, cut);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 1 — KPI tile click syncs Status chip (REQ-UI-02 forward direction)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task KpiTileClick_SyncsStatusChip()
    {
        var (h, cut) = RenderPopulated();
        using var _ = h.Ctx;

        // Invoke the private OnKpiSelectedAsync(StatusFilter?.Online) — equivalent to a click
        // on the Online tile when no tile is currently active.
        await InvokeAsync(cut,"OnKpiSelectedAsync", StatusFilter.Online);

        var statusFilter = GetField<StatusFilter?>(cut.Instance, "_statusFilter");
        Assert.Equal(StatusFilter.Online, statusFilter);

        var selectedStatuses = GetField<HashSet<string>>(cut.Instance, "_selectedStatuses");
        Assert.NotNull(selectedStatuses);
        Assert.Single(selectedStatuses!);
        Assert.Contains("Online", selectedStatuses!);

        // PreferencesService persisted both the KPI selection and the active filters.
        Assert.Contains(h.Module.Invocations, i =>
            i.Identifier == "setJson"
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeyKpiSelection);
        Assert.Contains(h.Module.Invocations, i =>
            i.Identifier == "setJson"
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeyActiveFilters);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 2 — Status chip single-select syncs KPI (REQ-UI-02 reverse direction)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task StatusChipClick_SyncsKpi_SingleSelect()
    {
        var (h, cut) = RenderPopulated();
        using var _ = h.Ctx;

        await InvokeAsync(cut,"OnToggleStatusAsync", "Online");

        var statusFilter = GetField<StatusFilter?>(cut.Instance, "_statusFilter");
        Assert.Equal(StatusFilter.Online, statusFilter);

        var selectedStatuses = GetField<HashSet<string>>(cut.Instance, "_selectedStatuses");
        Assert.Single(selectedStatuses!);
        Assert.Contains("Online", selectedStatuses!);

        Assert.Contains(h.Module.Invocations, i =>
            i.Identifier == "setJson"
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeyKpiSelection);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 3 — Multi-select Status chips collapse KPI to null (INFO #7 asymmetry)
    // The "All" KPI tile shows active even though the chip group filters to two values.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task StatusChipClick_MultiSelect_KpiBecomesNull()
    {
        var (h, cut) = RenderPopulated();
        using var _ = h.Ctx;

        await InvokeAsync(cut,"OnToggleStatusAsync", "Online");
        await InvokeAsync(cut,"OnToggleStatusAsync", "Stale");

        var selectedStatuses = GetField<HashSet<string>>(cut.Instance, "_selectedStatuses");
        Assert.NotNull(selectedStatuses);
        Assert.Equal(2, selectedStatuses!.Count);
        Assert.Contains("Online", selectedStatuses!);
        Assert.Contains("Stale", selectedStatuses!);

        // INFO #7 asymmetry: no single tile represents the {Online, Stale} compound, so
        // _statusFilter collapses to null. The "All" tile renders active even though the
        // chip group is filtering to two values.
        var statusFilter = GetField<StatusFilter?>(cut.Instance, "_statusFilter");
        Assert.Null(statusFilter);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 4 — Reset filters clears view-state, leaves shell-state untouched (REQ-UI-23)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ResetFiltersAsync_ClearsViewState_DoesNotTouchTheme_OrSidebar_OrNotifications()
    {
        var (h, cut) = RenderPopulated();
        using var _ = h.Ctx;

        // Pre-set: search + filters + KPI + custom sort.
        SetField(cut.Instance, "_searchText", "alice");
        SetField(cut.Instance, "_selectedSsids", new HashSet<string> { "guest" });
        SetField(cut.Instance, "_selectedApLabels", new HashSet<string> { "ap-1" });
        SetField(cut.Instance, "_selectedStatuses", new HashSet<string> { "Online" });
        SetField(cut.Instance, "_statusFilter", StatusFilter.Online);
        SetField(cut.Instance, "_sortColumn", "name");
        SetField(cut.Instance, "_sortDescending", false);

        // Track invocations from this point onward — pre-render setup invocations are
        // already recorded but we want to assert ResetFiltersAsync's behavior in isolation.
        var invocationsBefore = h.Module.Invocations.Count;

        await InvokeAsync(cut,"ResetFiltersAsync");

        // Capture invocations made by ResetFiltersAsync only.
        var newInvocations = h.Module.Invocations.Skip(invocationsBefore).ToList();

        // ClearViewStateAsync invoked exactly once.
        Assert.Single(newInvocations, i => i.Identifier == "clearKeys");

        // Theme / sidebar / notifications keys NEVER touched by ResetFiltersAsync.
        Assert.DoesNotContain(newInvocations, i =>
            i.Identifier == "set"
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeyTheme);
        Assert.DoesNotContain(newInvocations, i =>
            (i.Identifier == "set" || i.Identifier == "setJson")
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeySidebarCollapsed);
        Assert.DoesNotContain(newInvocations, i =>
            (i.Identifier == "set" || i.Identifier == "setJson")
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeyNotificationsEnabled);

        // In-memory state cleared: search empty, filter sets empty, _statusFilter null,
        // sort defaults restored (default = name asc, 260508-dns).
        Assert.Equal(string.Empty, GetField<string>(cut.Instance, "_searchText"));
        Assert.Empty(GetField<HashSet<string>>(cut.Instance, "_selectedSsids")!);
        Assert.Empty(GetField<HashSet<string>>(cut.Instance, "_selectedApLabels")!);
        Assert.Empty(GetField<HashSet<string>>(cut.Instance, "_selectedStatuses")!);
        Assert.Empty(GetField<HashSet<int>>(cut.Instance, "_selectedAps")!);
        Assert.Null(GetField<StatusFilter?>(cut.Instance, "_statusFilter"));
        Assert.Equal("name", GetField<string>(cut.Instance, "_sortColumn"));
        Assert.False(GetField<bool>(cut.Instance, "_sortDescending"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 5 — OnAfterRenderAsync(firstRender) restores view-state (REQ-UI-22)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task OnAfterRenderAsync_FirstRender_RestoresFromPreferences()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = ctx.JSInterop.SetupModule(ModuleId);

        // Configure the module to return persisted view state for each Get* call.
        // Theme and search are RAW strings (anti-FOUC contract); the rest are JSON.
        module.Setup<string?>("get", PreferencesService.KeySearchText).SetResult("persisted-needle");
        module.Setup<DashboardFilters?>("getJson", PreferencesService.KeyActiveFilters)
            .SetResult(new DashboardFilters(
                Ssids: new[] { "guest" },
                Aps: new[] { "ap-1" },
                Statuses: new[] { "Online" }));
        module.Setup<SortState?>("getJson", PreferencesService.KeySortState)
            .SetResult(new SortState("name", false));
        module.Setup<StatusFilter?>("getJson", PreferencesService.KeyKpiSelection)
            .SetResult(StatusFilter.Online);

        var nr = new StubNodeRepo(new[] { new WiFiNode { Id = 1, Hostname = "ap-1", Username = "root" } });
        var nws = new StubNetworkRepo { AnyEnabledOverride = true };
        nws.ByNode[1] = new List<WiFiNetwork>
        {
            new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true }
        };
        var sr = new StubStationRepo(new[]
        {
            new WLANStation { Id = 100, MacAddress = "AA:BB:CC:00:00:01", WiFiNetworkId = 10, Hostname = "alice", Status = Status.Online, LastPollDate = DateTime.UtcNow }
        });

        ctx.Services.AddSingleton<INodeRepo>(nr);
        ctx.Services.AddSingleton<INetworkRepo>(nws);
        ctx.Services.AddSingleton<IStationRepo>(sr);
        ctx.Services.AddSingleton<IOuiLookup>(new StubOuiLookup());
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings());
        ctx.Services.AddSingleton(new PollNotifier());
        ctx.Services.AddSingleton(new PreferencesService(ctx.JSInterop.JSRuntime));

        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // bUnit waits for OnAfterRenderAsync to complete on first render. Assert that
            // the restore-block has flipped _restored = true and applied each persisted value.
            cut.WaitForAssertion(() =>
            {
                Assert.True(GetField<bool>(cut.Instance, "_restored"));
            });

            Assert.Equal("persisted-needle", GetField<string>(cut.Instance, "_searchText"));

            var ssids = GetField<HashSet<string>>(cut.Instance, "_selectedSsids");
            Assert.Single(ssids!);
            Assert.Contains("guest", ssids!);

            var apLabels = GetField<HashSet<string>>(cut.Instance, "_selectedApLabels");
            Assert.Single(apLabels!);
            Assert.Contains("ap-1", apLabels!);

            // _selectedStatuses comes from ApplyKpiToStatusFilter's mirror of _statusFilter
            // (the persisted KPI selection), NOT directly from the persisted filters.Statuses
            // — the KPI restore line runs AFTER the filters restore, mirrors KPI -> chips.
            var statuses = GetField<HashSet<string>>(cut.Instance, "_selectedStatuses");
            Assert.Single(statuses!);
            Assert.Contains("Online", statuses!);

            Assert.Equal("name", GetField<string>(cut.Instance, "_sortColumn"));
            Assert.False(GetField<bool>(cut.Instance, "_sortDescending"));
            Assert.Equal(StatusFilter.Online, GetField<StatusFilter?>(cut.Instance, "_statusFilter"));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tests 6-9 — MapNodeStatus mapping (REQ-UI-11)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void MapNodeStatus_Returns_Hidden_When_Hostname_Unknown()
    {
        var (h, cut) = RenderPopulated();
        using var _ = h.Ctx;

        var state = InvokeFunc<FilterChipGroup.BadgeState>(cut.Instance, "MapNodeStatus", "ap-DOES-NOT-EXIST");
        Assert.Equal(FilterChipGroup.BadgeState.Hidden, state);
    }

    [Fact]
    public void MapNodeStatus_Returns_Online_When_Node_Online()
    {
        var node = new WiFiNode
        {
            Id = 1, Hostname = "ap-1", Username = "root", Password = "x",
            Status = Status.Online, LastPollDate = DateTime.UtcNow,
        };
        var (h, cut) = RenderPopulated(nodes: new[] { node });
        using var _ = h.Ctx;

        var state = InvokeFunc<FilterChipGroup.BadgeState>(cut.Instance, "MapNodeStatus", "ap-1");
        Assert.Equal(FilterChipGroup.BadgeState.Online, state);
    }

    [Fact]
    public void MapNodeStatus_Returns_Stale_When_Offline_And_Within_Threshold()
    {
        // PollingIntervalSeconds = 30 (StubSettings); threshold = 60s.
        // Node Offline + LastPollDate 5s before _cycleEndUtc -> elapsed < threshold -> Stale.
        var now = DateTime.UtcNow;
        var node = new WiFiNode
        {
            Id = 1, Hostname = "ap-1", Username = "root", Password = "x",
            Status = Status.Offline,
            LastPollDate = now.AddSeconds(-5),   // within 60s threshold
        };
        var (h, cut) = RenderPopulated(nodes: new[] { node });
        using var _ = h.Ctx;

        // Force _cycleEndUtc to a known anchor so the elapsed math is deterministic.
        SetField(cut.Instance, "_cycleEndUtc", now);

        var state = InvokeFunc<FilterChipGroup.BadgeState>(cut.Instance, "MapNodeStatus", "ap-1");
        Assert.Equal(FilterChipGroup.BadgeState.Stale, state);
    }

    [Fact]
    public void MapNodeStatus_Returns_Offline_When_Offline_And_Beyond_Threshold()
    {
        // PollingIntervalSeconds = 30; threshold = 60s.
        // Node Offline + LastPollDate 5 minutes before _cycleEndUtc -> elapsed > threshold -> Offline.
        var now = DateTime.UtcNow;
        var node = new WiFiNode
        {
            Id = 1, Hostname = "ap-1", Username = "root", Password = "x",
            Status = Status.Offline,
            LastPollDate = now.AddMinutes(-5),   // way past 60s threshold
        };
        var (h, cut) = RenderPopulated(nodes: new[] { node });
        using var _ = h.Ctx;

        SetField(cut.Instance, "_cycleEndUtc", now);

        var state = InvokeFunc<FilterChipGroup.BadgeState>(cut.Instance, "MapNodeStatus", "ap-1");
        Assert.Equal(FilterChipGroup.BadgeState.Offline, state);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 12 — Coverage tightener for OnToggleStatusAsync's toggle-OFF + parse-fail branches.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task OnToggleStatusAsync_ToggleOff_AndParseFail_BothBranchesCovered()
    {
        var (h, cut) = RenderPopulated();
        using var _ = h.Ctx;

        // Add then remove "Online" — exercises HashSet.Add returning FALSE on the second
        // call (toggle-off branch). After removal, Count==0 → ternary's else branch:
        // _statusFilter = null.
        await InvokeAsync(cut, "OnToggleStatusAsync", "Online");
        await InvokeAsync(cut, "OnToggleStatusAsync", "Online");
        Assert.Empty(GetField<HashSet<string>>(cut.Instance, "_selectedStatuses")!);
        Assert.Null(GetField<StatusFilter?>(cut.Instance, "_statusFilter"));

        // Pre-set _selectedStatuses to a single bogus value so Count==1 but Enum.TryParse
        // FAILS — exercises the documented `(StatusFilter?)null` fallback in the ternary.
        SetField(cut.Instance, "_selectedStatuses", new HashSet<string> { "NotARealStatus" });
        // Toggling "Bogus" inserts it, leaving Count==2, then we toggle it off so Count==1
        // and the surviving entry "NotARealStatus" forces TryParse to return false.
        await InvokeAsync(cut, "OnToggleStatusAsync", "Bogus");
        await InvokeAsync(cut, "OnToggleStatusAsync", "Bogus");

        // _statusFilter must be null because TryParse failed on "NotARealStatus".
        Assert.Null(GetField<StatusFilter?>(cut.Instance, "_statusFilter"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 11 — Coverage tightener for SSID / AP / Search toggle handlers.
    // OnToggleSsidAsync, OnToggleApAsync, OnSearchChangedAsync are net-new in Plan 08-08
    // but trivially branch on HashSet add/remove + persist; D-18 gate needs them lit.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ToggleHandlers_PersistFiltersAndSearchText()
    {
        var (h, cut) = RenderPopulated();
        using var _ = h.Ctx;

        // SSID toggle ON: adds to set, persists active-filters JSON.
        await InvokeAsync(cut, "OnToggleSsidAsync", "guest");
        var ssids = GetField<HashSet<string>>(cut.Instance, "_selectedSsids");
        Assert.Single(ssids!);
        Assert.Contains("guest", ssids!);

        // SSID toggle OFF: removes from set.
        await InvokeAsync(cut, "OnToggleSsidAsync", "guest");
        ssids = GetField<HashSet<string>>(cut.Instance, "_selectedSsids");
        Assert.Empty(ssids!);

        // AP toggle ON: adds hostname to _selectedApLabels, persists.
        await InvokeAsync(cut, "OnToggleApAsync", "ap-1");
        var aps = GetField<HashSet<string>>(cut.Instance, "_selectedApLabels");
        Assert.Single(aps!);
        Assert.Contains("ap-1", aps!);

        // AP toggle OFF.
        await InvokeAsync(cut, "OnToggleApAsync", "ap-1");
        aps = GetField<HashSet<string>>(cut.Instance, "_selectedApLabels");
        Assert.Empty(aps!);

        // Search-changed: persists raw search-text via Set("search-text", ...)
        SetField(cut.Instance, "_searchText", "needle");
        await InvokeAsync(cut, "OnSearchChangedAsync");
        Assert.Contains(h.Module.Invocations, i =>
            i.Identifier == "set"
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeySearchText
            && (i.Arguments[1] as string) == "needle");

        // SetActiveFiltersAsync was invoked at least 4 times (2 SSID + 2 AP toggles).
        var activeFilterWrites = h.Module.Invocations.Count(i =>
            i.Identifier == "setJson"
            && i.Arguments.Count > 0
            && (i.Arguments[0] as string) == PreferencesService.KeyActiveFilters);
        Assert.True(activeFilterWrites >= 4, $"Expected ≥4 active-filters writes; got {activeFilterWrites}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 10 — row-status-changed class added on Status diff (REQ-UI-53 hook).
    // Asserts the in-memory state contract directly — the field _changedRowMacs is the
    // SINGLE source of truth that the <tr class="..."> binding reads from. Verifying the
    // field is more robust than racing the ClearChangedAfterFrameAsync microtask that
    // intentionally clears the set after the render commits (the documented one-frame
    // lifecycle that the Plan 10 CSS animation rides on).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RowStatusChanged_AddsClassForOneFrame()
    {
        var (h, cut) = RenderPopulated();
        using var _ = h.Ctx;

        // Seed the previous-status dictionary with the same MAC the renderer is showing,
        // mapped to a DIFFERENT status than what _rows currently holds. Then call the
        // private DetectRowStatusChanges() — it diffs row.StationStatus (Online from the
        // station fixture) against _prevRowStatus[mac] (Offline) and stamps the MAC into
        // _changedRowMacs.
        var prev = new Dictionary<string, Status>
        {
            ["AA:BB:CC:00:00:01"] = Status.Offline,   // current row.StationStatus is Online
        };
        SetField(cut.Instance, "_prevRowStatus", prev);
        // Pre-populate _changedRowMacs so we can prove it gets re-populated (rather than
        // observing an initial state).
        SetField(cut.Instance, "_changedRowMacs", new HashSet<string>());

        var mi = typeof(Dashboard).GetMethod("DetectRowStatusChanges", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Race-free read: DetectRowStatusChanges fires-and-forgets ClearChangedAfterFrameAsync,
        // which awaits Task.Yield() then InvokeAsync(...) to clear _changedRowMacs on the
        // renderer dispatcher. We invoke + snapshot inside the SAME cut.InvokeAsync block so
        // the clear-microtask's dispatcher work item is queued AFTER our snapshot. Reading
        // the field outside the dispatcher slot is racy and was the source of the
        // intermittent "set is empty" failure (the microtask wins on faster machines).
        HashSet<string>? changedSnapshot = null;
        Dictionary<string, Status>? prevSnapshot = null;
        await cut.InvokeAsync(() =>
        {
            mi.Invoke(cut.Instance, null);
            // Snapshot a copy of _changedRowMacs while still on the dispatcher — any later
            // .Clear() from ClearChangedAfterFrameAsync runs on a queued continuation that
            // cannot interleave with this synchronous block.
            var live = GetField<HashSet<string>>(cut.Instance, "_changedRowMacs");
            changedSnapshot = live is null ? null : new HashSet<string>(live);
            var livePrev = GetField<Dictionary<string, Status>>(cut.Instance, "_prevRowStatus");
            prevSnapshot = livePrev is null ? null : new Dictionary<string, Status>(livePrev);
        });

        Assert.NotNull(changedSnapshot);
        Assert.Contains("AA:BB:CC:00:00:01", changedSnapshot!);
        // Exactly one MAC flagged for the diff this cycle (the one whose status changed).
        Assert.Single(changedSnapshot!);

        // _prevRowStatus is updated to the current state so the next call won't re-flag
        // the same MAC unless its status changes again.
        Assert.NotNull(prevSnapshot);
        Assert.Equal(Status.Online, prevSnapshot!["AA:BB:CC:00:00:01"]);

        // Second call with no Status change — set is empty after the call itself clears at
        // the start of the body and finds no Status flips to re-add. Same dispatcher-slot
        // snapshot pattern to avoid racing the ClearChangedAfterFrameAsync microtask.
        HashSet<string>? changedAgainSnapshot = null;
        await cut.InvokeAsync(() =>
        {
            mi.Invoke(cut.Instance, null);
            var live = GetField<HashSet<string>>(cut.Instance, "_changedRowMacs");
            changedAgainSnapshot = live is null ? null : new HashSet<string>(live);
        });
        Assert.NotNull(changedAgainSnapshot);
        Assert.Empty(changedAgainSnapshot!);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 13 — Reset filters button mounts in PageHeader.HeaderActions, not the
    // filter card-header (operator-driven move; supersedes the original REQ-UI-12
    // "Reset button at right" of the filter card placement).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ResetFiltersButton_RendersInPageHeader_NotInFilterCardHeader()
    {
        var (h, cut) = RenderPopulated();
        using var _ = h.Ctx;

        // The Reset filters button is rendered exactly once.
        var resetButtons = cut.FindAll("button[aria-label='Reset filters']");
        Assert.Single(resetButtons);

        // Markup-order assertion: PageHeader renders above the filter card, so the
        // Reset button's aria-label must appear BEFORE the "Filters" card-header in
        // the rendered HTML. This proves the button moved out of the card.
        var markup = cut.Markup;
        var resetIdx = markup.IndexOf("aria-label=\"Reset filters\"", StringComparison.Ordinal);
        var filterCardTitleIdx = markup.IndexOf("<h6 class=\"card-sub-title mb-0\">Filters</h6>", StringComparison.Ordinal);
        Assert.True(resetIdx >= 0, "Reset filters button should be rendered.");
        Assert.True(filterCardTitleIdx >= 0, "Filter card 'Filters' card-sub-title should be rendered.");
        Assert.True(resetIdx < filterCardTitleIdx,
            $"Reset filters should render BEFORE the filter card header — it now lives in PageHeader.HeaderActions. " +
            $"resetIdx={resetIdx}, filterCardTitleIdx={filterCardTitleIdx}.");
    }
}
