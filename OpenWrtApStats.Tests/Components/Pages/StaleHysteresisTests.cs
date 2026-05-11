using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Settings;
using OpenWrtApStats.Services.Vendor;
using Xunit;
using Dashboard = OpenWrtApStats.Components.Pages.Dashboard;

namespace OpenWrtApStats.Tests.Components.Pages;

/// <summary>
/// Phase 5 D-08 / POLL-09 stale-row hysteresis. Drives a real PollNotifier and asserts
/// the Stale badge flips after the second missed cycle (1.5 × PollingIntervalSeconds = 15s
/// with default 10s interval — second cycle's CycleEndUtc - LastPollDate ≥ 15s).
/// Closes architectural gate `StaleHysteresis_FiresOnSecondMissedCycle_NoFlicker`
/// (CONTEXT lines 220-225).
/// </summary>
public sealed class StaleHysteresisTests
{
    // Reuse the stub shapes from DashboardTests.cs by re-declaring locally — bUnit isolation.
    private sealed class StubNodeRepo : INodeRepo
    {
        public List<WiFiNode> Nodes { get; } = new();
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WiFiNode>>(Nodes);
        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<WiFiNode?>(Nodes.FirstOrDefault(n => n.Id == id));
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string h, string u, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePasswordAsync(int id, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastEnrichmentRefreshAtAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => Task.CompletedTask;
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
        public Task UpsertManyAsync(OpenWrtApStats.Data.DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAbsentDisabledAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(OpenWrtApStats.Data.DatabaseContext db, int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEnabledAsync(CancellationToken ct = default) => Task.FromResult(AnyEnabledOverride);
    }

    private sealed class StubStationRepo : IStationRepo
    {
        public List<WLANStation> Stations { get; } = new();
        public Task<IReadOnlyList<WLANStation>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WLANStation>>(Stations);
        public Task<WLANStation?> GetByMacAsync(string macAddress, CancellationToken ct = default) => Task.FromResult<WLANStation?>(Stations.FirstOrDefault(s => s.MacAddress == macAddress));
        public Task<IReadOnlyList<string>> GetAllMacsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Stations.Select(s => s.MacAddress).Distinct().ToList());
        public Task SetNameAsync(string macAddress, string? name, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetIsRandomMacAsync(string macAddress, bool isRandomMac, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteByMacAsync(string macAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(OpenWrtApStats.Data.DatabaseContext db, IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetMacsByNodeAsync(int nodeId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Stations.Select(s => s.MacAddress).Distinct().ToList());
    }

    private sealed class StubOuiLookup : IOuiLookup { public string? GetVendor(string macAddress) => null; }

    private sealed class StubSettings : IAppSettingsService
    {
        public int     PollingIntervalSeconds { get; set; } = 10;
        public string? UIPasswordHash         { get; } = null;
        public bool    WizardDismissed        { get; } = true;
        public int     RefreshIntervalSeconds { get; } = 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; } = Array.Empty<string>();
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    [Fact]
    public async Task StaleHysteresis_SecondMissedCycle_FlipsBadgeToStale()
    {
        var nodeRepo = new StubNodeRepo();
        nodeRepo.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow });

        var netRepo = new StubNetworkRepo { AnyEnabledOverride = true };
        netRepo.ByNode[1] = new List<WiFiNetwork> { new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true } };

        // Station's LastPollDate is "T0" — we advance _cycleEndUtc via Cycled events.
        var t0 = DateTime.UtcNow;
        var stationRepo = new StubStationRepo();
        stationRepo.Stations.Add(new WLANStation
        {
            Id = 100, MacAddress = "AA:BB:CC:00:00:01", WiFiNetworkId = 10,
            Status = Status.Offline,    // already offline in this test (station missed first cycle)
            LastPollDate = t0,
        });

        var notifier = new PollNotifier();
        using var ctx = new BunitContext();
        // Phase 8: Dashboard.razor injects PreferencesService — register a Loose-mode
        // preferences.js module + Singleton PreferencesService so the Phase 5 hysteresis
        // tests survive the new @inject without DI failure.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");
        ctx.Services.AddSingleton<INodeRepo>(nodeRepo);
        ctx.Services.AddSingleton<INetworkRepo>(netRepo);
        ctx.Services.AddSingleton<IStationRepo>(stationRepo);
        ctx.Services.AddSingleton<IOuiLookup>(new StubOuiLookup());
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings { PollingIntervalSeconds = 10 });
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton(new PreferencesService(ctx.JSInterop.JSRuntime));

        var cut = ctx.Render<Dashboard>();

        // Quick-fix 260506-001: corrected derivation is `elapsed < 2*interval` (=20s) → Stale.
        // First cycle ends 5s after station's last poll — within the 2×10=20s window → Stale.
        // Assertions target the badge by class (text-bg-warning is exclusive to StatusBadge's
        // Stale variant; text-bg-secondary is exclusive to its Offline variant). Loose text
        // matching against "Stale"/"Offline" would also match the dashboard's Status filter
        // chip group, which always renders those literal labels.
        await notifier.RaiseAsync(new PollSnapshot(1, 0, 0, 1, t0.AddSeconds(5)));
        Assert.Contains("text-bg-warning", cut.Markup);

        // Second cycle ends 20s after station's last poll — at-or-past the 20s threshold → Offline.
        // (The badge "flips" away from Stale on the second-missed-cycle boundary, demonstrating
        // hysteresis: short-gap rows render Stale, long-gap rows render Offline.)
        await notifier.RaiseAsync(new PollSnapshot(1, 0, 0, 1, t0.AddSeconds(20)));
        Assert.Contains("text-bg-secondary", cut.Markup);
        Assert.DoesNotContain("text-bg-warning", cut.Markup);
    }

    [Fact]
    public async Task StaleHysteresis_RecoveryToOnline_FlipsBadgeBack()
    {
        var nodeRepo = new StubNodeRepo();
        nodeRepo.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow });

        var netRepo = new StubNetworkRepo { AnyEnabledOverride = true };
        netRepo.ByNode[1] = new List<WiFiNetwork> { new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true } };

        var t0 = DateTime.UtcNow;
        var stationRepo = new StubStationRepo();
        var stn = new WLANStation
        {
            Id = 100, MacAddress = "AA:BB:CC:00:00:01", WiFiNetworkId = 10,
            Status = Status.Offline,
            LastPollDate = t0,
        };
        stationRepo.Stations.Add(stn);

        var notifier = new PollNotifier();
        using var ctx = new BunitContext();
        // Phase 8: Dashboard.razor injects PreferencesService — register a Loose-mode
        // preferences.js module + Singleton PreferencesService so the Phase 5 hysteresis
        // tests survive the new @inject without DI failure.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");
        ctx.Services.AddSingleton<INodeRepo>(nodeRepo);
        ctx.Services.AddSingleton<INetworkRepo>(netRepo);
        ctx.Services.AddSingleton<IStationRepo>(stationRepo);
        ctx.Services.AddSingleton<IOuiLookup>(new StubOuiLookup());
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings { PollingIntervalSeconds = 10 });
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton(new PreferencesService(ctx.JSInterop.JSRuntime));

        var cut = ctx.Render<Dashboard>();

        // Quick-fix 260506-001: corrected derivation is `elapsed < 2*interval` (=20s) → Stale.
        // Cycle ends 10s after station's last poll — within the 20s window → Stale (warning).
        // Class-based assertion — see note above.
        await notifier.RaiseAsync(new PollSnapshot(1, 0, 0, 1, t0.AddSeconds(10)));
        Assert.Contains("text-bg-warning", cut.Markup);

        // Simulate a successful subsequent poll: the orchestrator's mark-Offline-then-upsert
        // would set Status=Online and LastPollDate=now. Mutate the stub's row in place.
        stn.Status = Status.Online;
        stn.LastPollDate = t0.AddSeconds(30);

        await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 1, t0.AddSeconds(30)));
        Assert.DoesNotContain("text-bg-warning", cut.Markup);
        Assert.Contains("text-bg-success", cut.Markup);
    }

    /// <summary>
    /// Quick-fix 260506-001 (DASH-09 / D-08 / D-09): pins the corrected IsStale derivation
    /// for the transient window — Offline station with elapsed &lt; 2× polling interval
    /// renders Stale (text-bg-warning). Regression-pin against the inverted-comparison
    /// defect caught in LIVE-UAT row 5.2.
    /// </summary>
    [Fact]
    public async Task StatusBadge_RendersStale_WhenOffline_AndElapsedLessThan2Cycles()
    {
        var nodeRepo = new StubNodeRepo();
        nodeRepo.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow });

        var netRepo = new StubNetworkRepo { AnyEnabledOverride = true };
        netRepo.ByNode[1] = new List<WiFiNetwork> { new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true } };

        // interval=10s → threshold=2×10=20s. LastPollDate at cycleEndUtc - 15s → elapsed=15 < 20 → Stale.
        var cycleEndUtc = DateTime.UtcNow;
        var stationRepo = new StubStationRepo();
        stationRepo.Stations.Add(new WLANStation
        {
            Id = 100, MacAddress = "AA:BB:CC:00:00:01", WiFiNetworkId = 10,
            Status = Status.Offline,
            LastPollDate = cycleEndUtc.AddSeconds(-15),
        });

        var notifier = new PollNotifier();
        using var ctx = new BunitContext();
        // Phase 8: Dashboard.razor injects PreferencesService — register a Loose-mode
        // preferences.js module + Singleton PreferencesService so the Phase 5 hysteresis
        // tests survive the new @inject without DI failure.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");
        ctx.Services.AddSingleton<INodeRepo>(nodeRepo);
        ctx.Services.AddSingleton<INetworkRepo>(netRepo);
        ctx.Services.AddSingleton<IStationRepo>(stationRepo);
        ctx.Services.AddSingleton<IOuiLookup>(new StubOuiLookup());
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings { PollingIntervalSeconds = 10 });
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton(new PreferencesService(ctx.JSInterop.JSRuntime));

        var cut = ctx.Render<Dashboard>();
        await notifier.RaiseAsync(new PollSnapshot(1, 0, 0, 1, cycleEndUtc));

        Assert.Contains("text-bg-warning", cut.Markup);
        Assert.Contains("Stale", cut.Markup);
    }

    /// <summary>
    /// Quick-fix 260506-001 (DASH-09 / D-08 / D-09): pins the corrected IsStale derivation
    /// for the long-gap case — Offline station with elapsed ≥ 2× polling interval renders
    /// Offline (text-bg-secondary), NOT Stale. This is the regression-pin for the operator's
    /// 8 stuck-Stale rows from LIVE-UAT row 5.2 (stations absent from every AP for hours).
    /// </summary>
    [Fact]
    public async Task StatusBadge_RendersOffline_WhenOffline_AndElapsedAtLeast2Cycles()
    {
        var nodeRepo = new StubNodeRepo();
        nodeRepo.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow });

        var netRepo = new StubNetworkRepo { AnyEnabledOverride = true };
        netRepo.ByNode[1] = new List<WiFiNetwork> { new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true } };

        // interval=10s → threshold=20s. LastPollDate at cycleEndUtc - 30s → elapsed=30 ≥ 20 → Offline.
        var cycleEndUtc = DateTime.UtcNow;
        var stationRepo = new StubStationRepo();
        stationRepo.Stations.Add(new WLANStation
        {
            Id = 100, MacAddress = "AA:BB:CC:00:00:01", WiFiNetworkId = 10,
            Status = Status.Offline,
            LastPollDate = cycleEndUtc.AddSeconds(-30),
        });

        var notifier = new PollNotifier();
        using var ctx = new BunitContext();
        // Phase 8: Dashboard.razor injects PreferencesService — register a Loose-mode
        // preferences.js module + Singleton PreferencesService so the Phase 5 hysteresis
        // tests survive the new @inject without DI failure.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");
        ctx.Services.AddSingleton<INodeRepo>(nodeRepo);
        ctx.Services.AddSingleton<INetworkRepo>(netRepo);
        ctx.Services.AddSingleton<IStationRepo>(stationRepo);
        ctx.Services.AddSingleton<IOuiLookup>(new StubOuiLookup());
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings { PollingIntervalSeconds = 10 });
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton(new PreferencesService(ctx.JSInterop.JSRuntime));

        var cut = ctx.Render<Dashboard>();
        await notifier.RaiseAsync(new PollSnapshot(1, 0, 0, 1, cycleEndUtc));

        Assert.Contains("text-bg-secondary", cut.Markup);
        Assert.DoesNotContain("text-bg-warning", cut.Markup);
    }

    /// <summary>
    /// Quick-fix 260506-001 (DASH-09 / D-08 / D-09): pins the `Status != Offline` short-circuit
    /// in IsStale — an Online station never renders Stale, regardless of how ancient
    /// LastPollDate looks. Guards against future refactors that drop the short-circuit.
    /// </summary>
    [Fact]
    public async Task StatusBadge_RendersOnline_WhenStationStatusIsOnline_RegardlessOfElapsed()
    {
        var nodeRepo = new StubNodeRepo();
        nodeRepo.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow });

        var netRepo = new StubNetworkRepo { AnyEnabledOverride = true };
        netRepo.ByNode[1] = new List<WiFiNetwork> { new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true } };

        // interval=10s. LastPollDate at cycleEndUtc - 99×10s (deliberately ancient) → would be
        // Offline if elapsed mattered — but Status=Online short-circuits IsStale before the
        // threshold check. Expect Online (text-bg-success), never Stale.
        var cycleEndUtc = DateTime.UtcNow;
        var stationRepo = new StubStationRepo();
        stationRepo.Stations.Add(new WLANStation
        {
            Id = 100, MacAddress = "AA:BB:CC:00:00:01", WiFiNetworkId = 10,
            Status = Status.Online,
            LastPollDate = cycleEndUtc.AddSeconds(-99 * 10),
        });

        var notifier = new PollNotifier();
        using var ctx = new BunitContext();
        // Phase 8: Dashboard.razor injects PreferencesService — register a Loose-mode
        // preferences.js module + Singleton PreferencesService so the Phase 5 hysteresis
        // tests survive the new @inject without DI failure.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");
        ctx.Services.AddSingleton<INodeRepo>(nodeRepo);
        ctx.Services.AddSingleton<INetworkRepo>(netRepo);
        ctx.Services.AddSingleton<IStationRepo>(stationRepo);
        ctx.Services.AddSingleton<IOuiLookup>(new StubOuiLookup());
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings { PollingIntervalSeconds = 10 });
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton(new PreferencesService(ctx.JSInterop.JSRuntime));

        var cut = ctx.Render<Dashboard>();
        await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 1, cycleEndUtc));

        Assert.Contains("text-bg-success", cut.Markup);
        Assert.DoesNotContain("text-bg-warning", cut.Markup);
    }
}
