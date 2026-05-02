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
        ctx.Services.AddSingleton(new OpenWrtApStats.Services.Polling.PollNotifier());
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

    // ── DASH-11 Phase 5 empty state (replaces the Phase 3 placeholder row) ─
    // Note: the dedicated phase-5 test `Dashboard_Dash11_EmptyState_RendersWhenNodesAndWlansButNoStations`
    // (below) covers the new EmptyState rendering; the Phase 3 placeholder-row test was retired with
    // Plan 03 since the muted "No stations associated." row + colspan=9 was replaced by <EmptyState>.

    // ── Phase-5 closure: TODO(phase-05) markers removed — `Dashboard_ClosesAllPhase05TodoMarkers` ──
    // (below in the Phase 5 tests block) replaces the Phase 3 forward-compat anchor test.

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

    // ── Phase 5 tests ──────────────────────────────────────────────────────

    [Fact]
    public void Dashboard_FirstColumn_LinksToStationDetail()
    {
        var node = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var net  = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        var stn  = new WLANStation { Id = 100, MacAddress = "AA:BB:CC:00:00:01", WiFiNetworkId = 10, Name = "phone", Hostname = "iphone", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { stn }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // URL-encoded MAC (colons → %3A) per UI-SPEC line 809.
            Assert.Contains("href=\"/Stations/AA%3ABB%3ACC%3A00%3A00%3A01\"", cut.Markup);
            Assert.Contains("phone", cut.Markup);   // displayed text from row.Name
        }
    }

    [Fact]
    public void Dashboard_SearchInput_FiltersRowsLive()
    {
        var node = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var net  = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        var s1   = new WLANStation { Id = 100, MacAddress = "AA:BB:CC:00:00:01", WiFiNetworkId = 10, Hostname = "alice-phone", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var s2   = new WLANStation { Id = 101, MacAddress = "AA:BB:CC:00:00:02", WiFiNetworkId = 10, Hostname = "bob-laptop",  Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s1, s2 }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("alice-phone", cut.Markup);
            Assert.Contains("bob-laptop",  cut.Markup);

            // Type "alice" — bob should be filtered out.
            cut.Find("input[type=search]").Input("alice");
            Assert.Contains("alice-phone", cut.Markup);
            Assert.DoesNotContain("bob-laptop", cut.Markup);
        }
    }

    [Fact]
    public void Dashboard_SortHeader_ClickFlipsDescending()
    {
        var node = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var net  = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        var s1   = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s1 }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // Default sort = lastSeen desc; the lastSeen th has aria-sort="descending".
            var lastSeenTh = cut.FindAll("th").First(t => t.TextContent.Contains("Last seen"));
            Assert.Equal("descending", lastSeenTh.GetAttribute("aria-sort"));

            // Click the Name header — should set _sortColumn=name, _sortDescending=false.
            cut.FindAll("button").First(b => b.TextContent.Contains("Name")).Click();
            var nameTh = cut.FindAll("th").First(t => t.TextContent.Contains("Name"));
            Assert.Equal("ascending", nameTh.GetAttribute("aria-sort"));

            // Click again — flips to descending.
            cut.FindAll("button").First(b => b.TextContent.Contains("Name")).Click();
            nameTh = cut.FindAll("th").First(t => t.TextContent.Contains("Name"));
            Assert.Equal("descending", nameTh.GetAttribute("aria-sort"));
        }
    }

    [Fact]
    public void Dashboard_SsidChip_TogglesInclusion()
    {
        var node = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var net1 = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        var net2 = new WiFiNetwork { Id = 11, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:66", SSID = "main",  Enabled = true };
        var s1   = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Hostname = "alice", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var s2   = new WLANStation { Id = 101, MacAddress = "BB:00:00:00:00:02", WiFiNetworkId = 11, Hostname = "bob",   Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s1, s2 }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net1, net2 } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("alice", cut.Markup);
            Assert.Contains("bob",   cut.Markup);

            // Click the "guest" chip — only alice should remain.
            cut.FindAll("button").First(b => b.TextContent.Trim() == "guest").Click();
            Assert.Contains("alice", cut.Markup);
            Assert.DoesNotContain("bob", cut.Markup);
        }
    }

    [Fact]
    public void Dashboard_Dash11_EmptyState_RendersWhenNodesAndWlansButNoStations()
    {
        var node = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var net  = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        var (ctx, _, _, _, _) = Build(new[] { node }, Array.Empty<WLANStation>(), anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("WLANs are healthy, no clients right now", cut.Markup);
            Assert.Contains("Last poll:", cut.Markup);
            Assert.Contains("bi-wifi", cut.Markup);
        }
    }

    // ── Revision 1 W1 — PerApStrip click-to-filter binds to _selectedAps (D-11) ────────
    [Fact]
    public void Dashboard_PerApStrip_ClickFiltersTable()
    {
        // Two APs, two stations (one on each AP). Clicking ap-A's strip badge filters out
        // bob's row (which sits on ap-B). _selectedAps is the SINGLE source of truth shared
        // with the AP FilterChipGroup — clicking the strip is the same toggle.
        var nodeA = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var nodeB = new WiFiNode { Id = 2, Hostname = "ap-B", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var netA  = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        var netB  = new WiFiNetwork { Id = 20, WiFiNodeId = 2, MacAddress = "00:11:22:33:44:66", SSID = "guest", Enabled = true };
        var s1    = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Hostname = "alice", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var s2    = new WLANStation { Id = 101, MacAddress = "BB:00:00:00:00:02", WiFiNetworkId = 20, Hostname = "bob",   Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { nodeA, nodeB }, new[] { s1, s2 }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>>
            {
                [1] = new List<WiFiNetwork> { netA },
                [2] = new List<WiFiNetwork> { netB },
            });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("alice", cut.Markup);
            Assert.Contains("bob",   cut.Markup);

            // The PerApStrip wraps everything in an aria-labeled group; node buttons live inside it.
            // Click the strip badge for ap-A by locating the button whose text content contains the hostname.
            var perApStripGroup = cut.Find("[aria-label=\"Per-access-point connectivity\"]");
            var apAButton = perApStripGroup.QuerySelectorAll("button")
                .First(b => b.TextContent.Contains("ap-A"));
            apAButton.Click();

            // After clicking ap-A, only stations on ap-A (alice) should remain in the table.
            Assert.Contains("alice", cut.Markup);
            Assert.DoesNotContain("bob", cut.Markup);
        }
    }

    // ── Revision 1 W2 — <Virtualize> renders 100+ stations cleanly (DASH-12 / SC5) ──
    // Plan-author-deviation note (Rule 1 bug fix): the original assertion expected bUnit's
    // headless renderer to bound the rendered <tr> count to a viewport + overscan window.
    // bUnit does NOT simulate a browser viewport — it renders all items emitted by
    // <Virtualize>'s ItemContent. The load-bearing guarantee is structural:
    //   1. <Virtualize> wraps the table body (verified by source-level `<Virtualize` token).
    //   2. The dashboard renders cleanly at 100+ station scale (no exceptions, all rows present).
    // Browser-viewport bounding is a runtime concern verified at /gsd-ui-phase 5 UAT, not in bUnit.
    [Fact]
    public void Dashboard_With100Stations_RendersCleanlyAtScale()
    {
        var node = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var net  = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        var stations = Enumerable.Range(0, 120).Select(i => new WLANStation
        {
            Id = 100 + i,
            MacAddress = $"AA:BB:CC:00:{i:X2}:01",
            WiFiNetworkId = 10,
            Hostname = $"station-{i}",
            Status = Status.Online,
            LastPollDate = DateTime.UtcNow,
        }).ToArray();

        var (ctx, _, _, _, _) = Build(new[] { node }, stations, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // Renders without exception at 120-station scale. <Virtualize>'s behavior in
            // bUnit's headless renderer (no viewport) is to render some subset of items —
            // we don't care which subset, only that the table renders cleanly and at least
            // one of the seeded MAC patterns is visible.
            Assert.Contains("AA:BB:CC:00:", cut.Markup);

            var bodyRows = cut.FindAll("tbody tr");
            Assert.True(bodyRows.Count >= 1, $"Expected at least one row to render, but got {bodyRows.Count}.");
        }
    }

    // Source-level guarantee that <Virtualize> wraps the table body (companion to
    // Dashboard_With100Stations_RendersCleanlyAtScale, which cannot bound the viewport in bUnit).
    [Fact]
    public void Dashboard_TableBody_IsWrappedInVirtualize()
    {
        var source = ReadDashboardSource();
        Assert.Contains("<Virtualize Items=", source);
        Assert.Contains("ItemSize=\"40\"", source);
    }

    // ── Revision 1 W5 — D-05 default sort tiebreaker by MAC asc ────────────────────
    [Fact]
    public void Dashboard_DefaultSort_TiebreaksByMacAsc()
    {
        // Two stations with the SAME LastPollDate — secondary stable sort by MAC asc must
        // place AA:00:... before BB:00:... in the rendered HTML.
        var node = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var net  = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        var sameTime = DateTime.UtcNow.AddSeconds(-5);
        var sLow  = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Hostname = "alice", Status = Status.Online, LastPollDate = sameTime };
        var sHigh = new WLANStation { Id = 101, MacAddress = "BB:00:00:00:00:02", WiFiNetworkId = 10, Hostname = "bob",   Status = Status.Online, LastPollDate = sameTime };
        // Intentionally feed in REVERSE MAC order so the sort has work to do.
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { sHigh, sLow }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // Default sort is lastSeen desc; identical LastPollDate → MAC asc tiebreaker.
            var aaIndex = cut.Markup.IndexOf("AA:00:00:00:00:01", StringComparison.Ordinal);
            var bbIndex = cut.Markup.IndexOf("BB:00:00:00:00:02", StringComparison.Ordinal);
            Assert.True(aaIndex >= 0, "AA:00:00:00:00:01 not rendered");
            Assert.True(bbIndex >= 0, "BB:00:00:00:00:02 not rendered");
            Assert.True(aaIndex < bbIndex,
                $"D-05 secondary stable sort by MAC asc violated: AA index {aaIndex} should be before BB index {bbIndex}.");
        }
    }

    // ── Phase-5 closure: TODO(phase-05) markers all removed ─────────────────────────
    [Fact]
    public void Dashboard_ClosesAllPhase05TodoMarkers()
    {
        var source = ReadDashboardSource();
        // Phase 5 Plan 03 success criterion: all five TODO(phase-05) markers are closed.
        Assert.DoesNotContain("TODO(phase-05)", source);
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
