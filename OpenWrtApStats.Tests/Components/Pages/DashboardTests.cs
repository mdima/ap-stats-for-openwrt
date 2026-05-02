using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Settings;
using OpenWrtApStats.Services.Vendor;
using Xunit;
using Dashboard = OpenWrtApStats.Components.Pages.Dashboard;

namespace OpenWrtApStats.Tests.Components.Pages;

/// <summary>
/// Dashboard.razor bUnit tests. Covers the three render branches:
///   - DASH-09 (zero nodes empty state, conditional CTA per D-09/ONB-06)
///   - DASH-10 (nodes exist but every WLAN disabled empty state, plural + singular)
///   - DASH-13 (vendor column via IOuiLookup, known + unknown fallback)
/// Plus DASH-11 deferred placeholder + forward-compat anchor presence (T-3-06-04 mitigation).
/// </summary>
public sealed class DashboardTests
{
    // ── Test stubs ─────────────────────────────────────────────────────────
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
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
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
        public Task<bool> AnyEnabledAsync(CancellationToken ct = default) => Task.FromResult(AnyEnabledOverride);
    }

    private sealed class StubStationRepo(IEnumerable<WLANStation> seed) : IStationRepo
    {
        private readonly List<WLANStation> _stations = seed.ToList();
        public Task<IReadOnlyList<WLANStation>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WLANStation>>(_stations);
        public Task<WLANStation?> GetByMacAsync(string macAddress, CancellationToken ct = default) =>
            Task.FromResult<WLANStation?>(_stations.FirstOrDefault(s => s.MacAddress == macAddress));
        public Task SetNameAsync(string macAddress, string? name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteByMacAsync(string macAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(OpenWrtApStats.Data.DatabaseContext db, IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubOuiLookup : IOuiLookup
    {
        public Dictionary<string, string?> Vendors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? GetVendor(string macAddress) => Vendors.TryGetValue(macAddress, out var v) ? v : null;
    }

    private sealed class StubSettings : IAppSettingsService
    {
        public StubSettings(bool wizardDismissed = false) { WizardDismissed = wizardDismissed; }
        public int     PollingIntervalSeconds { get; } = 30;
        public string? UIPasswordHash         { get; } = null;
        public bool    WizardDismissed        { get; }
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private static (BunitContext ctx, StubNodeRepo nodes, StubNetworkRepo nets, StubStationRepo stations, StubOuiLookup oui)
        Build(IEnumerable<WiFiNode> nodes, IEnumerable<WLANStation> stations, bool anyEnabled, Dictionary<int, List<WiFiNetwork>>? networkMap = null, bool wizardDismissed = false)
    {
        var ctx = new BunitContext();
        var nr = new StubNodeRepo(nodes);
        var nws = new StubNetworkRepo { AnyEnabledOverride = anyEnabled };
        if (networkMap is not null)
        {
            foreach (var kvp in networkMap) nws.ByNode[kvp.Key] = kvp.Value;
        }
        var sr = new StubStationRepo(stations);
        var oui = new StubOuiLookup();
        var settings = new StubSettings(wizardDismissed);
        ctx.Services.AddSingleton<INodeRepo>(nr);
        ctx.Services.AddSingleton<INetworkRepo>(nws);
        ctx.Services.AddSingleton<IStationRepo>(sr);
        ctx.Services.AddSingleton<IOuiLookup>(oui);
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        return (ctx, nr, nws, sr, oui);
    }

    // ── DASH-09 ────────────────────────────────────────────────────────────
    [Fact]
    public void ZeroNodes_RendersDash09EmptyState_WithCtaToNodesAdd()
    {
        // WizardDismissed=true: Phase 3 original CTA preserved (Add your first OpenWRT node).
        var (ctx, _, _, _, _) = Build(Array.Empty<WiFiNode>(), Array.Empty<WLANStation>(), anyEnabled: false, wizardDismissed: true);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            Assert.Contains("No access points yet", cut.Markup);
            Assert.Contains("Add your first OpenWRT access point", cut.Markup);
            Assert.Contains("Add your first OpenWRT node", cut.Markup);
            Assert.Contains("href=\"/Nodes/Add\"", cut.Markup);
            Assert.Contains("bi-router-fill", cut.Markup);
        }
    }

    // ── DASH-10 ────────────────────────────────────────────────────────────
    [Fact]
    public void NodesExist_NoEnabledWlans_RendersDash10EmptyState_WithCtaToFirstNodeDetail()
    {
        var nodes = new[]
        {
            new WiFiNode { Id = 7, Hostname = "ap-7", Username = "root" },
            new WiFiNode { Id = 3, Hostname = "ap-3", Username = "root" },   // lowest Id
        };
        var (ctx, _, _, _, _) = Build(nodes, Array.Empty<WLANStation>(), anyEnabled: false);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            Assert.Contains("No WLANs are being monitored", cut.Markup);
            Assert.Contains("You have 2 access points configured", cut.Markup);
            Assert.Contains("Enable a WLAN", cut.Markup);
            Assert.Contains("href=\"/Nodes/3\"", cut.Markup);   // lowest-Id node
            Assert.Contains("bi-wifi-off", cut.Markup);
        }
    }

    [Fact]
    public void OneNode_NoEnabledWlans_DashTenBody_UsesSingularNoun()
    {
        var nodes = new[] { new WiFiNode { Id = 1, Hostname = "ap-1", Username = "root" } };
        var (ctx, _, _, _, _) = Build(nodes, Array.Empty<WLANStation>(), anyEnabled: false);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("You have 1 access point configured", cut.Markup);
            Assert.DoesNotContain("access points configured", cut.Markup);
        }
    }

    // ── DASH-13 vendor column ──────────────────────────────────────────────
    [Fact]
    public void TableRow_VendorColumn_KnownPrefix_ShowsVendorName()
    {
        var nodes = new[] { new WiFiNode { Id = 1, Hostname = "ap-1", Username = "root" } };
        var networks = new Dictionary<int, List<WiFiNetwork>>
        {
            [1] = new() { new WiFiNetwork { Id = 100, WiFiNodeId = 1, MacAddress = "AA:BB:CC:00:00:00", SSID = "main", Enabled = true } }
        };
        var stations = new[]
        {
            new WLANStation { Id = 1, MacAddress = "00:00:0C:DE:AD:BE", WiFiNetworkId = 100, Status = Status.Online, LastPollDate = DateTime.UtcNow }
        };

        var (ctx, _, _, _, oui) = Build(nodes, stations, anyEnabled: true, networks);
        oui.Vendors["00:00:0C:DE:AD:BE"] = "Cisco Systems, Inc";
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("Cisco Systems, Inc", cut.Markup);
        }
    }

    [Fact]
    public void TableRow_VendorColumn_UnknownPrefix_ShowsUnknownVendor_Muted()
    {
        var nodes = new[] { new WiFiNode { Id = 1, Hostname = "ap-1", Username = "root" } };
        var networks = new Dictionary<int, List<WiFiNetwork>>
        {
            [1] = new() { new WiFiNetwork { Id = 100, WiFiNodeId = 1, MacAddress = "AA:BB:CC:00:00:00", SSID = "main", Enabled = true } }
        };
        var stations = new[]
        {
            new WLANStation { Id = 1, MacAddress = "F2:FE:FD:00:00:01", WiFiNetworkId = 100, Status = Status.Online, LastPollDate = DateTime.UtcNow }
        };

        var (ctx, _, _, _, oui) = Build(nodes, stations, anyEnabled: true, networks);
        oui.Vendors["F2:FE:FD:00:00:01"] = null;   // explicit "unknown"
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("Unknown vendor", cut.Markup);
            Assert.Contains("text-body-secondary", cut.Markup);
        }
    }

    // ── DASH-11 deferred placeholder ──────────────────────────────────────
    [Fact]
    public void NodesExist_WlansEnabled_NoStations_RendersPlaceholderRow()
    {
        var nodes = new[] { new WiFiNode { Id = 1, Hostname = "ap-1", Username = "root" } };
        var networks = new Dictionary<int, List<WiFiNetwork>>
        {
            [1] = new() { new WiFiNetwork { Id = 100, WiFiNodeId = 1, MacAddress = "AA:BB:CC:00:00:00", SSID = "main", Enabled = true } }
        };
        var (ctx, _, _, _, _) = Build(nodes, Array.Empty<WLANStation>(), anyEnabled: true, networks);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("No stations associated.", cut.Markup);
            Assert.Contains("colspan=\"9\"", cut.Markup);
        }
    }

    // ── Forward-compat anchor presence (T-3-06-04 structural mitigation) ──
    // Razor strips comments from rendered output, so we assert against the .razor source on disk.
    [Fact]
    public void Dashboard_ContainsAllRequiredForwardCompatAnchors()
    {
        var source = ReadDashboardSource();
        // NOTE: TODO(phase-04) anchor was removed in Plan 04-05 (D-09 CTA wired).
        Assert.Contains("TODO(phase-05): wrap <tbody> in <Virtualize>", source);
        Assert.Contains("TODO(phase-05): RSSI cell", source);
        Assert.Contains("TODO(phase-05): subscribe to PollNotifier", source);
        Assert.Contains("TODO(phase-05): add search input above table", source);
        Assert.Contains("TODO(phase-05): SSID/AP filter chips row", source);
    }

    // ── DASH-09 D-09 conditional CTA (ONB-06) ─────────────────────────────
    [Fact]
    public void ZeroNodes_WizardNotDismissed_RendersContinueSetupCta()
    {
        var (ctx, _, _, _, _) = Build(
            Array.Empty<WiFiNode>(),
            Array.Empty<WLANStation>(),
            anyEnabled: false,
            wizardDismissed: false);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("Continue setup", cut.Markup);
            Assert.Contains("href=\"/setup/password\"", cut.Markup);
            Assert.DoesNotContain("Add your first OpenWRT node", cut.Markup);
        }
    }

    [Fact]
    public void ZeroNodes_WizardDismissed_RendersAddYourFirstNodeCta()
    {
        var (ctx, _, _, _, _) = Build(
            Array.Empty<WiFiNode>(),
            Array.Empty<WLANStation>(),
            anyEnabled: false,
            wizardDismissed: true);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("Add your first OpenWRT node", cut.Markup);
            Assert.Contains("href=\"/Nodes/Add\"", cut.Markup);
            Assert.DoesNotContain("Continue setup", cut.Markup);
        }
    }

    /// <summary>
    /// Walk up from the test binary's BaseDirectory to find the repo root, then load Dashboard.razor.
    /// The test binary lives at OpenWrtApStats.Tests/bin/{Configuration}/net10.0/, so walking up
    /// four parents lands at the repo root that contains both project folders.
    /// </summary>
    private static string ReadDashboardSource()
    {
        // Probe a few candidate roots so the test stays robust to bin-path variations.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "OpenWrtApStats", "Components", "Pages", "Dashboard.razor");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new FileNotFoundException("Could not locate Dashboard.razor by walking up from " + AppContext.BaseDirectory);
    }
}
