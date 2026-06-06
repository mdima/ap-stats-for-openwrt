using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Data.Repositories.History;
using ApStatsForOpenWrt.Services;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Services.Vendor;
using Xunit;
using Dashboard = ApStatsForOpenWrt.Components.Pages.Dashboard;

namespace ApStatsForOpenWrt.Tests.Components.Pages;

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

        // M-HRD-10: batched dashboard read. Derive from the same ByNode map the per-node
        // GetByNodeAsync stub serves so both paths render identical rows; nodes with zero
        // networks contribute no key (matches the production GroupBy semantics).
        public int GetAllGroupedByNodeCallCount { get; private set; }
        public Task<IReadOnlyDictionary<int, IReadOnlyList<WiFiNetwork>>> GetAllGroupedByNodeAsync(CancellationToken ct = default)
        {
            GetAllGroupedByNodeCallCount++;
            var dict = ByNode
                .Where(kvp => kvp.Value.Count > 0)
                .ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<WiFiNetwork>)kvp.Value);
            return Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<WiFiNetwork>>>(dict);
        }
    }

    private sealed class StubStationRepo(IEnumerable<WLANStation> seed) : IStationRepo
    {
        private readonly List<WLANStation> _stations = seed.ToList();
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
        public Dictionary<string, string?> Vendors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? GetVendor(string macAddress) => Vendors.TryGetValue(macAddress, out var v) ? v : null;
    }

    private sealed class StubSettings : IAppSettingsService
    {
        public StubSettings(bool wizardDismissed = false, IReadOnlyList<string>? localDomainSuffixes = null, string? uiPasswordHash = null, bool historyEnabled = true)
        {
            WizardDismissed = wizardDismissed;
            LocalDomainSuffixes = localDomainSuffixes ?? Array.Empty<string>();
            UIPasswordHash = uiPasswordHash;
            HistoryEnabled = historyEnabled;
        }
        public int     PollingIntervalSeconds { get; } = 30;
        public string? UIPasswordHash         { get; }
        public bool    WizardDismissed        { get; }
        public int     RefreshIntervalSeconds { get; } = 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; }
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        // Phase 17 Plan 17-01 M-UPD-02 — minimal mutable update-check stub members.
        public bool UpdateCheckEnabled { get; private set; } = true;
        public bool HistoryEnabled { get; }
        public Task SetHistoryEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
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

    // Phase 19 stub-churn (RESEARCH §Pitfall 5): Dashboard.razor now injects both history query
    // services. Hand-rolled stubs (TESTING.md — no mocking framework) returning empty results so
    // the existing render branches are unaffected and the sparkline column renders the em-dash.
    private sealed class StubKpiHistory : IKpiHistoryQueryService
    {
        public Task<KpiHistoryDto> GetLast12hAsync(CancellationToken ct) =>
            Task.FromResult(new KpiHistoryDto(Array.Empty<KpiHistoryBucket>(), IsEmpty: true));
    }

    private sealed class StubRssiHistory : IRssiHistoryQueryService
    {
        public Dictionary<string, RssiSparklineSeries> Map { get; } = new();
        public int SparklineCallCount { get; private set; }

        public Task<IReadOnlyDictionary<string, RssiSparklineSeries>> GetSparklineSeriesAsync(IEnumerable<string> macs, CancellationToken ct)
        {
            SparklineCallCount++;
            return Task.FromResult<IReadOnlyDictionary<string, RssiSparklineSeries>>(Map);
        }

        public Task<RssiDetailDto> GetDetailSeriesAsync(string mac, CancellationToken ct) =>
            Task.FromResult(new RssiDetailDto(Array.Empty<DateTime>(), Array.Empty<RssiDetailSeries>(), IsEmpty: true));
    }

    private static (BunitContext ctx, StubNodeRepo nodes, StubNetworkRepo nets, StubStationRepo stations, StubOuiLookup oui)
        Build(IEnumerable<WiFiNode> nodes, IEnumerable<WLANStation> stations, bool anyEnabled, Dictionary<int, List<WiFiNetwork>>? networkMap = null, bool wizardDismissed = false, IReadOnlyList<string>? localDomainSuffixes = null, string? uiPasswordHash = null, bool historyEnabled = true)
    {
        var ctx = new BunitContext();
        // Phase 8: Dashboard.razor injects PreferencesService for view-state restore + persistence.
        // Loose JS interop mode + a fake `./js/preferences.js` module covers the localStorage path
        // (OnAfterRenderAsync(firstRender:true) + Set*Async fire-and-forget calls).
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");
        var nr = new StubNodeRepo(nodes);
        var nws = new StubNetworkRepo { AnyEnabledOverride = anyEnabled };
        if (networkMap is not null)
        {
            foreach (var kvp in networkMap) nws.ByNode[kvp.Key] = kvp.Value;
        }
        var sr = new StubStationRepo(stations);
        var oui = new StubOuiLookup();
        var settings = new StubSettings(wizardDismissed, localDomainSuffixes, uiPasswordHash, historyEnabled);
        ctx.Services.AddSingleton<INodeRepo>(nr);
        ctx.Services.AddSingleton<INetworkRepo>(nws);
        ctx.Services.AddSingleton<IStationRepo>(sr);
        ctx.Services.AddSingleton<IOuiLookup>(oui);
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        ctx.Services.AddSingleton(new ApStatsForOpenWrt.Services.Polling.PollNotifier());
        ctx.Services.AddSingleton(new PreferencesService(ctx.JSInterop.JSRuntime));
        // Phase 19 stub-churn — register the two history query services Dashboard now injects.
        ctx.Services.AddSingleton<IKpiHistoryQueryService>(new StubKpiHistory());
        ctx.Services.AddSingleton<IRssiHistoryQueryService>(new StubRssiHistory());
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

    [Fact]
    public void TableRow_VendorColumn_ClampedWithTwoLineEllipsis_FullNameInOverflowTooltip()
    {
        // 260604-a5u — vendor cell markup contract: the vendor name renders inside a span
        // carrying .two-line-ellipsis (site.css clamps to 2 lines, ellipsis only on overflow)
        // that opts in to the overflow-gated Bootstrap tooltip instead of a native title.
        // bUnit has no layout engine and JSInterop is stubbed here, so the scrollHeight >
        // clientHeight gating is JS-only and is live-verified; this test pins only the markup
        // OPT-IN CONTRACT (the attributes initOverflowTooltips reads), not the gating itself:
        // data-bs-toggle="tooltip-overflow", full vendor name on data-bs-title, the shared
        // .app-tooltip skin via data-bs-custom-class, and NO native title attribute.
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
        oui.Vendors["00:00:0C:DE:AD:BE"] = "Telefonaktiebolaget LM Ericsson";
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            var vendorSpan = cut.Find("span.two-line-ellipsis");
            Assert.Equal("tooltip-overflow", vendorSpan.GetAttribute("data-bs-toggle"));
            Assert.Equal("Telefonaktiebolaget LM Ericsson", vendorSpan.GetAttribute("data-bs-title"));
            Assert.Equal("app-tooltip", vendorSpan.GetAttribute("data-bs-custom-class"));
            Assert.Null(vendorSpan.GetAttribute("title"));  // no native title — AngleSharp returns null for an absent attribute
            Assert.Contains("Telefonaktiebolaget LM Ericsson", vendorSpan.TextContent);
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
    public void ZeroNodes_WizardDismissed_RendersAddYourFirstNodeCta_AndSecondaryContinueSetupLink()
    {
        // Phase 7 row 4.1 follow-up: SetupLayout's "Skip for now" footer promises "you can always
        // finish setup later from the dashboard." After skip, primary CTA remains "Add your first
        // OpenWRT node" (D-09 / ONB-06) but a secondary "Continue setup" ghost-link is rendered
        // below it so the promise holds.
        var (ctx, _, _, _, _) = Build(
            Array.Empty<WiFiNode>(),
            Array.Empty<WLANStation>(),
            anyEnabled: false,
            wizardDismissed: true);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // Primary CTA unchanged.
            Assert.Contains("Add your first OpenWRT node", cut.Markup);
            Assert.Contains("href=\"/Nodes/Add\"", cut.Markup);

            // Secondary "Continue setup" link MUST be present pointing to /setup/password.
            Assert.Contains("Continue setup", cut.Markup);
            Assert.Contains("href=\"/setup/password\"", cut.Markup);

            // Order matters: the secondary link must render AFTER the primary CTA in the DOM.
            var primaryIdx = cut.Markup.IndexOf("Add your first OpenWRT node", StringComparison.Ordinal);
            var secondaryIdx = cut.Markup.IndexOf("Continue setup", StringComparison.Ordinal);
            Assert.True(primaryIdx >= 0 && secondaryIdx > primaryIdx,
                $"secondary 'Continue setup' link must render after the primary CTA. primaryIdx={primaryIdx}, secondaryIdx={secondaryIdx}");
        }
    }

    // ── Smarter resume (wizard-resume-pw-reset-404) ───────────────────────────
    // When the wizard was dismissed and a password is already set (but no nodes exist),
    // the "Continue setup" link must resume at the first INCOMPLETE step — /setup/nodes
    // (step 2) — instead of dumping the returning operator back onto /setup/password to
    // re-enter a password they already set. With NO password it still targets
    // /setup/password (covered by the test above, which uses the default null hash).

    [Fact]
    public void ZeroNodes_WizardDismissed_HasPassword_ContinueSetupResumesAtNodesStep()
    {
        var (ctx, _, _, _, _) = Build(
            Array.Empty<WiFiNode>(),
            Array.Empty<WLANStation>(),
            anyEnabled: false,
            wizardDismissed: true,
            uiPasswordHash: "AQAAA-fake-hash");
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // Primary CTA unchanged (add your first node).
            Assert.Contains("Add your first OpenWRT node", cut.Markup);
            Assert.Contains("href=\"/Nodes/Add\"", cut.Markup);

            // Secondary "Continue setup" resumes at the first incomplete step: /setup/nodes.
            Assert.Contains("Continue setup", cut.Markup);
            Assert.Contains("href=\"/setup/nodes\"", cut.Markup);

            // Must NOT send the operator back to step 1 to re-enter the existing password.
            Assert.DoesNotContain("href=\"/setup/password\"", cut.Markup);
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

            // Default sort = name asc (260508-dns); the Name th has aria-sort="ascending".
            var nameTh = cut.FindAll("th").First(t => t.TextContent.Contains("Name"));
            Assert.Equal("ascending", nameTh.GetAttribute("aria-sort"));

            // Click the Name header — Name is the active column, so direction flips to descending.
            cut.FindAll("button").First(b => b.TextContent.Contains("Name")).Click();
            nameTh = cut.FindAll("th").First(t => t.TextContent.Contains("Name"));
            Assert.Equal("descending", nameTh.GetAttribute("aria-sort"));

            // Click MAC header — column switches; new column starts ascending and Name clears its aria-sort.
            cut.FindAll("button").First(b => b.TextContent.Contains("MAC")).Click();
            var macTh = cut.FindAll("th").First(t => t.TextContent.Contains("MAC"));
            Assert.Equal("ascending", macTh.GetAttribute("aria-sort"));
            nameTh = cut.FindAll("th").First(t => t.TextContent.Contains("Name"));
            Assert.NotEqual("ascending", nameTh.GetAttribute("aria-sort"));
            Assert.NotEqual("descending", nameTh.GetAttribute("aria-sort"));
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

    // ── Phase 8 NOTE — Dashboard_PerApStrip_ClickFiltersTable removed ──────────────────
    // The PerApStrip component (and the Phase 5 Revision-1 W1 test that drove it) was
    // intentionally removed in Phase 8 Plan 08-08 (REQ-UI-10 — PerApStrip lockdown). Per-AP
    // click-to-filter is now provided by the AP FilterChipGroup whose hostname options bind
    // to `_selectedApLabels` — coverage moves to the new DashboardPhase8Tests fixture.

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

    // ── Revision 1 W5 — D-05 lastSeen-desc tiebreaker by MAC asc ───────────────────
    [Fact]
    public void Dashboard_LastSeenDescSort_TiebreaksByMacAsc()
    {
        // Two stations with the SAME LastPollDate — when sorted by lastSeen desc, the
        // secondary stable sort by MAC asc must place AA:00:... before BB:00:... in the
        // rendered HTML. (Default sort is now name asc per 260508-dns; this test routes
        // through the UI to switch to lastSeen desc, where the MAC tiebreaker is the
        // load-bearing behavior under test.)
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

            // Switch sort column to lastSeen desc via the UI (twice = column switch then flip).
            cut.FindAll("button").First(b => b.TextContent.Contains("Last seen")).Click();
            cut.FindAll("button").First(b => b.TextContent.Contains("Last seen")).Click();
            var lastSeenTh = cut.FindAll("th").First(t => t.TextContent.Contains("Last seen"));
            Assert.Equal("descending", lastSeenTh.GetAttribute("aria-sort"));

            // Identical LastPollDate → primary key ties → MAC asc tiebreaker engages.
            var aaIndex = cut.Markup.IndexOf("AA:00:00:00:00:01", StringComparison.Ordinal);
            var bbIndex = cut.Markup.IndexOf("BB:00:00:00:00:02", StringComparison.Ordinal);
            Assert.True(aaIndex >= 0, "AA:00:00:00:00:01 not rendered");
            Assert.True(bbIndex >= 0, "BB:00:00:00:00:02 not rendered");
            Assert.True(aaIndex < bbIndex,
                $"D-05 secondary stable sort by MAC asc violated: AA index {aaIndex} should be before BB index {bbIndex}.");
        }
    }

    // ── Quick task 260517-ips — IP column sorts numerically (IPv4 value), not lexicographically ──
    [Fact]
    public void Dashboard_IpAscSort_OrdersNumerically_NotLexicographically()
    {
        // Pre-fix Dashboard.razor:435 sorted IPs as raw strings, so 192.168.1.100
        // ranked BEFORE 192.168.1.21 (ordinal '1' < '2' at the fourth-octet position).
        // Post-fix IPs sort by full IPv4 numeric value across all four octets and
        // null/unparseable IPs sort LAST in both ascending and descending directions
        // (mirrors the RSSI-null convention at Dashboard.razor:446-451).
        var node = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var net  = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        // Feed in REVERSE-numeric order so the sort has work to do; include one
        // null-IP row to pin the null-last invariant.
        var sHundred = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, IPAddress = "192.168.1.100", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var sTwentyTwo = new WLANStation { Id = 101, MacAddress = "AA:00:00:00:00:02", WiFiNetworkId = 10, IPAddress = "192.168.1.22",  Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var sTwentyOne = new WLANStation { Id = 102, MacAddress = "AA:00:00:00:00:03", WiFiNetworkId = 10, IPAddress = "192.168.1.21",  Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var sNullIp    = new WLANStation { Id = 103, MacAddress = "ZZ:00:00:00:00:04", WiFiNetworkId = 10, IPAddress = null,            Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { sHundred, sTwentyTwo, sTwentyOne, sNullIp }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // Switch sort column to IP asc via the UI (single click = column switch; default sort is name asc per 260508-dns).
            cut.FindAll("button").First(b => b.TextContent.Trim() == "IP").Click();
            var ipTh = cut.FindAll("th").First(t => t.TextContent.Contains("IP"));
            Assert.Equal("ascending", ipTh.GetAttribute("aria-sort"));

            var idx21  = cut.Markup.IndexOf("AA:00:00:00:00:03", StringComparison.Ordinal); // .21
            var idx22  = cut.Markup.IndexOf("AA:00:00:00:00:02", StringComparison.Ordinal); // .22
            var idx100 = cut.Markup.IndexOf("AA:00:00:00:00:01", StringComparison.Ordinal); // .100
            var idxNull = cut.Markup.IndexOf("ZZ:00:00:00:00:04", StringComparison.Ordinal); // null IP
            Assert.True(idx21  >= 0, "192.168.1.21 row not rendered");
            Assert.True(idx22  >= 0, "192.168.1.22 row not rendered");
            Assert.True(idx100 >= 0, "192.168.1.100 row not rendered");
            Assert.True(idxNull >= 0, "null-IP row not rendered");

            Assert.True(idx21 < idx22,
                $"IP numeric sort violated: 192.168.1.21 (index {idx21}) should precede 192.168.1.22 (index {idx22}).");
            Assert.True(idx22 < idx100,
                $"IP numeric sort violated: 192.168.1.22 (index {idx22}) should precede 192.168.1.100 (index {idx100}).");
            Assert.True(idx100 < idxNull,
                $"Null-IP last invariant violated: 192.168.1.100 (index {idx100}) should precede null-IP row (index {idxNull}).");
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

    // ── Phase 10 tests (Plan 10-04 Task 2) ─────────────────────────────────

    private static (WiFiNode node, WiFiNetwork net) MakeApAndNet()
    {
        var node = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var net  = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        return (node, net);
    }

    [Fact]
    public void Dashboard_IpColumn_RendersIpForRow_AndEmDashForNullIp()
    {
        // DASH-17 — IP column populated from WLANStation.IPAddress; null shows "—".
        var (node, net) = MakeApAndNet();
        var s1 = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, IPAddress = "10.0.0.5",  Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var s2 = new WLANStation { Id = 101, MacAddress = "AA:00:00:00:00:02", WiFiNetworkId = 10, IPAddress = null,        Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s1, s2 }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            // Header
            Assert.Contains(">IP<", cut.Markup);
            // Populated row
            Assert.Contains("10.0.0.5", cut.Markup);
            // Null IP renders em-dash
            Assert.Contains("—", cut.Markup);
        }
    }

    [Fact]
    public void Dashboard_Cascade_StripsLocalSuffix_WhenSettingsHasMatchingSuffix()
    {
        // DASH-18 / DASH-19 — Hostname branch goes through HostnameDisplay.StripLocalSuffix.
        var (node, net) = MakeApAndNet();
        var s = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Name = null, Hostname = "dev.michelecasa", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } },
            localDomainSuffixes: new[] { "michelecasa" });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            // Anchor text is the suffix-stripped short form (cascade only — full FQDN may still appear in the row tooltip).
            var anchor = cut.FindAll("a").First(a => (a.GetAttribute("href") ?? "").Contains("AA%3A00%3A00%3A00%3A00%3A01"));
            Assert.Equal("dev", anchor.TextContent.Trim());
        }
    }

    [Fact]
    public void Dashboard_Cascade_RendersFullFqdn_WhenNoSuffixMatches()
    {
        var (node, net) = MakeApAndNet();
        var s = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Name = null, Hostname = "dev.michelecasa", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } },
            localDomainSuffixes: new[] { "other" });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            var anchor = cut.FindAll("a").First(a => (a.GetAttribute("href") ?? "").Contains("AA%3A00%3A00%3A00%3A00%3A01"));
            Assert.Equal("dev.michelecasa", anchor.TextContent.Trim());
        }
    }

    [Fact]
    public void Dashboard_Cascade_RendersMac_WhenNameAndHostnameAreNull()
    {
        var (node, net) = MakeApAndNet();
        var s = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Name = null, Hostname = null, Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            var anchor = cut.FindAll("a").First(a => (a.GetAttribute("href") ?? "").Contains("AA%3A00%3A00%3A00%3A00%3A01"));
            Assert.Equal("AA:00:00:00:00:01", anchor.TextContent.Trim());
        }
    }

    [Fact]
    public void Dashboard_Cascade_RendersName_WhenNameIsSet_NoSuffixStrippingOnNameBranch()
    {
        var (node, net) = MakeApAndNet();
        var s = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Name = "Operator-Pi", Hostname = "dev.michelecasa", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } },
            localDomainSuffixes: new[] { "michelecasa" });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            var anchor = cut.FindAll("a").First(a => (a.GetAttribute("href") ?? "").Contains("AA%3A00%3A00%3A00%3A00%3A01"));
            Assert.Equal("Operator-Pi", anchor.TextContent.Trim());
        }
    }

    [Fact]
    public void Dashboard_RandomMacBadge_RendersWhenIsRandomMacTrue()
    {
        // DASH-21 — bi-shuffle pill rendered next to MAC value when IsRandomMac=true.
        var (node, net) = MakeApAndNet();
        var s = new WLANStation { Id = 100, MacAddress = "BA:00:00:00:00:01", WiFiNetworkId = 10, IsRandomMac = true, Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Contains("bi bi-shuffle", cut.Markup);
            Assert.Contains("Locally administered MAC", cut.Markup);
        }
    }

    [Fact]
    public void Dashboard_RandomMacBadge_HiddenWhenIsRandomMacFalse()
    {
        var (node, net) = MakeApAndNet();
        var s = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, IsRandomMac = false, Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.DoesNotContain("bi-shuffle", cut.Markup);
        }
    }

    [Fact]
    public void Dashboard_BodyRows_CarryNoTitleTooltip()
    {
        // DASH-20 retired (260604 cosmetic tune) — the row-level title tooltip was removed; body
        // rows must carry NO title attribute even when every identifier (Name, Hostname, FQDN,
        // MAC, Random MAC) is populated. Identifiers live in the visible columns instead.
        var (node, net) = MakeApAndNet();
        var s = new WLANStation { Id = 100, MacAddress = "AA:BB:CC:DD:EE:FF", WiFiNetworkId = 10, Name = "Pi", Hostname = "dev.michelecasa", IsRandomMac = true, Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } },
            localDomainSuffixes: new[] { "michelecasa" });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            var bodyRow = cut.FindAll("tbody tr").First();
            Assert.False(bodyRow.HasAttribute("title"),
                "Body rows must not carry a title tooltip (DASH-20 tooltip removed in the pre-v0.14-close cosmetic tune).");
        }
    }

    // ── Quick task 260511-osd — orphan WLANStations rendered with "—" placeholders ────────
    //
    // When a WiFiNetwork is hard-deleted (cascade SetNull on WLANStation.WiFiNetworkId
    // after BSSID rotation / AP outage), the dependent station rows become "orphan"
    // (FK = null) or point at a now-missing network id. Prior behavior: BuildRowsAsync
    // silently filtered them out, hiding e.g. cameras that connected briefly then powered
    // off. New behavior: include them with `Ssid = "—"`, `ApHostname = "—"`, `ApNodeId = 0`
    // so operators see them. Disabled-WLAN exclusion is preserved unchanged.

    [Fact]
    public void BuildRows_IncludesOrphanStations_WithDashPlaceholders()
    {
        // Two orphan shapes: (a) WiFiNetworkId IS NULL (the cascade-SetNull outcome),
        // (b) WiFiNetworkId points at a network id that no longer exists in networkLookup
        // (the lookup-miss case — happens if the network is gone but the FK hasn't yet
        // been nulled, or if the lookup is built from a different node set).
        var (node, net) = MakeApAndNet();
        var normal  = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10,   Hostname = "alice", Status = Status.Online,  LastPollDate = DateTime.UtcNow };
        var orphanA = new WLANStation { Id = 101, MacAddress = "BB:00:00:00:00:02", WiFiNetworkId = null, Hostname = "bob",   Status = Status.Offline, LastPollDate = DateTime.UtcNow };
        var orphanB = new WLANStation { Id = 102, MacAddress = "CC:00:00:00:00:03", WiFiNetworkId = 999,  Hostname = "carol", Status = Status.Offline, LastPollDate = DateTime.UtcNow };

        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { normal, orphanA, orphanB }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // All three rows are present (MAC visible in column 2 monospace cell for each).
            Assert.Contains("AA:00:00:00:00:01", cut.Markup);
            Assert.Contains("BB:00:00:00:00:02", cut.Markup);
            Assert.Contains("CC:00:00:00:00:03", cut.Markup);

            // Per-row placeholder check: locate the <tr> by MAC and assert its SSID + AP cells
            // (columns 5 and 6, 0-indexed 4 and 5) render the em-dash placeholder.
            foreach (var orphanMac in new[] { "BB:00:00:00:00:02", "CC:00:00:00:00:03" })
            {
                var row = cut.FindAll("tbody tr")
                             .First(tr => tr.TextContent.Contains(orphanMac));
                var cells = row.QuerySelectorAll("td");
                Assert.Equal("—", cells[4].TextContent.Trim());   // SSID
                Assert.Equal("—", cells[5].TextContent.Trim());   // AP
            }

            // Sanity: the non-orphan row still renders its real SSID + AP.
            var aliceRow = cut.FindAll("tbody tr")
                              .First(tr => tr.TextContent.Contains("AA:00:00:00:00:01"));
            var aliceCells = aliceRow.QuerySelectorAll("td");
            Assert.Equal("guest", aliceCells[4].TextContent.Trim());
            Assert.Equal("ap-A",  aliceCells[5].TextContent.Trim());
        }
    }

    [Fact]
    public void BuildRows_OrphanStation_RespectsStatusFilter()
    {
        // Orphan with Status=Offline must inherit the standard Status-filter semantics:
        // selecting "Online" hides it (its persisted Status is not Online). The orphan
        // remains visible when no Status filter is active (the initial render).
        //
        // Note: the Offline / Stale partition in production splits on the IsStale predicate,
        // which depends on `_cycleEndUtc`. That field is only populated by a PollNotifier.Cycled
        // event, which this bUnit harness does not raise — so an offline row's Offline-vs-Stale
        // classification under bUnit defaults to Stale (elapsed is negative against MinValue
        // and thus < threshold). The Online-excludes-Offline-orphan direction is sufficient
        // to lock the orphan-respects-Status-filter regression; the Offline-vs-Stale partition
        // is independently covered by the existing IsStale tests.
        var (node, net) = MakeApAndNet();
        var onlineNormal = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10,   Hostname = "alice",  Status = Status.Online,  LastPollDate = DateTime.UtcNow };
        var offlineOrphan = new WLANStation { Id = 101, MacAddress = "BB:00:00:00:00:02", WiFiNetworkId = null, Hostname = "ghost", Status = Status.Offline, LastPollDate = DateTime.UtcNow };

        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { onlineNormal, offlineOrphan }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // No filter: both rows visible.
            Assert.Contains("AA:00:00:00:00:01", cut.Markup);
            Assert.Contains("BB:00:00:00:00:02", cut.Markup);

            // Click the "Online" Status chip — only the online normal row should remain.
            // Status filter chips have no StatusBadgeFor decorator so their button TextContent
            // is just the label.
            cut.FindAll("button").First(b => b.TextContent.Trim() == "Online").Click();
            Assert.Contains("AA:00:00:00:00:01", cut.Markup);
            Assert.DoesNotContain("BB:00:00:00:00:02", cut.Markup);
        }
    }

    [Fact]
    public void BuildRows_OrphanStation_HiddenByApFilter()
    {
        // AP filter selects by hostname label. Orphan rows have ApHostname="—" so they don't
        // match any real AP label and stay hidden when an AP filter is active.
        var (node, net) = MakeApAndNet();
        var normal = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10,   Hostname = "alice", Status = Status.Online,  LastPollDate = DateTime.UtcNow };
        var orphan = new WLANStation { Id = 101, MacAddress = "BB:00:00:00:00:02", WiFiNetworkId = null, Hostname = "ghost", Status = Status.Online,  LastPollDate = DateTime.UtcNow };

        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { normal, orphan }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // No filter: both visible.
            Assert.Contains("AA:00:00:00:00:01", cut.Markup);
            Assert.Contains("BB:00:00:00:00:02", cut.Markup);

            // Click the "ap-A" AP chip. AP chips compose Phase 8's StatusBadgeFor, so the
            // <button>'s TextContent contains both the label and the nested badge text
            // (e.g. "ap-A Online"). Match by prefix.
            cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith("ap-A")).Click();
            Assert.Contains("AA:00:00:00:00:01", cut.Markup);
            Assert.DoesNotContain("BB:00:00:00:00:02", cut.Markup);
        }
    }

    // ── Quick task 260517-fpd — SSID/AP filter chips omit the "—" orphan placeholder ──────
    //
    // 260511-osd promotes orphan WLANStations (FK = null after cascade-SetNull, or FK pointing
    // at a missing network id) into _rows with Ssid="—" and ApHostname="—" so operators see
    // them. The placeholder must stay in the rendered table cells (operator visibility) but
    // must NOT propagate into the SSID/AP filter chip option lists — clicking a "—" chip is
    // not a meaningful operator action. The fix lives at Dashboard.razor:307-308 (a `.Where(x
    // => x != "—")` step before `.Distinct()` on _allSsids and _allApLabels).

    [Fact]
    public void Dashboard_SsidAndApFilterChips_OmitEmDashPlaceholder()
    {
        var (node, net) = MakeApAndNet();
        var normal = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10,   Hostname = "alice", Status = Status.Online,  LastPollDate = DateTime.UtcNow };
        var orphan = new WLANStation { Id = 101, MacAddress = "BB:00:00:00:00:02", WiFiNetworkId = null, Hostname = "ghost", Status = Status.Offline, LastPollDate = DateTime.UtcNow };

        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { normal, orphan }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // Sanity: the orphan row IS in the table (260511-osd invariant — row-level
            // placeholder rendering is unchanged by this quick task).
            Assert.Contains("BB:00:00:00:00:02", cut.Markup);

            // Scope to each FilterChipGroup via its aria-label wrapper (FilterChipGroup.razor:15).
            // Chip option buttons live as direct <button> children of that wrapper. The SSID
            // group has no StatusBadgeFor decorator so its button TextContent is just the label;
            // the AP group composes StatusBadgeFor (Phase 8) so its button TextContent prefixes
            // the label (e.g. "ap-A Online"). Both shapes are handled by trimming + StartsWith.
            var ssidGroup = cut.Find("[aria-label='Filter by SSID']");
            var apGroup   = cut.Find("[aria-label='Filter by AP']");
            var ssidChips = ssidGroup.QuerySelectorAll("button");
            var apChips   = apGroup.QuerySelectorAll("button");

            // The fix is regression-locked here: neither group exposes a chip whose label is "—".
            Assert.DoesNotContain(ssidChips, b => b.TextContent.Trim() == "—");
            Assert.DoesNotContain(apChips,   b => b.TextContent.Trim().StartsWith("—"));

            // Forward guard: the legitimate SSID + AP options ARE still present (so we haven't
            // accidentally dropped real labels alongside the "—" placeholder).
            Assert.Contains(ssidChips, b => b.TextContent.Trim() == "guest");
            Assert.Contains(apChips,   b => b.TextContent.Trim().StartsWith("ap-A"));
        }
    }

    // ── Phase 19 M-HIST-05 / M-HIST-06 (D-03 / D-04) — merged "RSSI" column (RSSI + 1 h sparkline) ──

    [Fact]
    public void Dashboard_RssiColumn_MergesSparkline_WhenHistoryEnabled()
    {
        var (node, net) = MakeApAndNet();
        var s = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } },
            historyEnabled: true);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            // RSSI + RSSI(1h) are merged into ONE sortable "RSSI" column — no separate sparkline header.
            Assert.DoesNotContain("RSSI (1h)", cut.Markup);
            Assert.Contains(cut.FindAll("th").Select(t => t.TextContent), h => h.Contains("RSSI"));
            // The sparkline lives inside the merged Signal cell (link target covered by the click-through test).
            Assert.NotEmpty(cut.FindAll("a[data-testid=\"sparkline-link\"]"));
            // The KPI history chart card still mounts beneath the KPI tiles.
            Assert.Contains("Activity — last 12 h", cut.Markup);
        }
    }

    [Fact]
    public void Dashboard_SparklineCell_LinksToStationDetail_WhenHistoryEnabled()
    {
        // QUAL-02 #5 — the whole sparkline cell is a click-through to /Stations/{mac}.
        var (node, net) = MakeApAndNet();
        var s = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } },
            historyEnabled: true);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            var link = cut.Find("a[data-testid=\"sparkline-link\"]");
            Assert.Equal($"/Stations/{Uri.EscapeDataString("AA:00:00:00:00:01")}", link.GetAttribute("href"));
        }
    }

    [Fact]
    public void Dashboard_RssiColumn_OmitsSparkline_WhenHistoryDisabled()
    {
        // D-03 — history off → the RSSI column shows ONLY the RSSI cell (no sparkline, no link), no chart.
        var (node, net) = MakeApAndNet();
        var s = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var (ctx, _, _, _, _) = Build(new[] { node }, new[] { s }, anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>> { [1] = new List<WiFiNetwork> { net } },
            historyEnabled: false);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            // The RSSI column header is still present (sortable by current RSSI)...
            Assert.Contains(cut.FindAll("th").Select(t => t.TextContent), h => h.Contains("RSSI"));
            // ...but with history off there is no sparkline (no link, no tier band) and no KPI chart.
            Assert.Empty(cut.FindAll("a[data-testid=\"sparkline-link\"]"));
            Assert.Empty(cut.FindAll("rect.rssi-sparkline-tier"));
            Assert.DoesNotContain("Activity — last 12 h", cut.Markup);
        }
    }

    // ── M-HRD-10 — batched node→networks read (BuildRowsAsync uses GetAllGroupedByNodeAsync) ──

    [Fact]
    public void Dashboard_BuildRows_UsesBatchedGroupedRead_RowsIdenticalToPerNodeLoop()
    {
        // M-HRD-10 render-neutral refactor guard. Multi-node, multi-WLAN fixed seed; the
        // rendered rows (Name, MAC, SSID, AP-hostname cells) MUST be byte-identical to the
        // pre-change per-node GetByNodeAsync output. The StubNetworkRepo serves the batched
        // read from the SAME ByNode map, so any divergence in BuildRowsAsync's consumption
        // (wrong key, dropped node, swapped tuple field) surfaces as a cell mismatch here.
        var nodeA = new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var nodeB = new WiFiNode { Id = 2, Hostname = "ap-B", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var netA1 = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:01", SSID = "guest", Enabled = true };
        var netA2 = new WiFiNetwork { Id = 11, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:02", SSID = "main",  Enabled = true };
        var netB1 = new WiFiNetwork { Id = 20, WiFiNodeId = 2, MacAddress = "00:11:22:33:55:01", SSID = "iot",   Enabled = true };
        var sA1 = new WLANStation { Id = 100, MacAddress = "AA:00:00:00:00:01", WiFiNetworkId = 10, Name = "alice", Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var sA2 = new WLANStation { Id = 101, MacAddress = "AA:00:00:00:00:02", WiFiNetworkId = 11, Name = "bob",   Status = Status.Online, LastPollDate = DateTime.UtcNow };
        var sB1 = new WLANStation { Id = 102, MacAddress = "BB:00:00:00:00:01", WiFiNetworkId = 20, Name = "carol", Status = Status.Online, LastPollDate = DateTime.UtcNow };

        var (ctx, _, nws, _, _) = Build(
            new[] { nodeA, nodeB },
            new[] { sA1, sA2, sB1 },
            anyEnabled: true,
            networkMap: new Dictionary<int, List<WiFiNetwork>>
            {
                [1] = new List<WiFiNetwork> { netA1, netA2 },
                [2] = new List<WiFiNetwork> { netB1 },
            });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // The batched read was consulted (the N+1 loop was replaced).
            Assert.True(nws.GetAllGroupedByNodeCallCount >= 1,
                "BuildRowsAsync must call GetAllGroupedByNodeAsync (the batched M-HRD-10 read).");

            // Per-station row identity: each station resolves to the SSID + AP hostname of its
            // network, exactly as the per-node loop produced. Columns: 0=Name, 1=MAC, 4=SSID, 5=AP.
            var expected = new (string Mac, string Ssid, string Ap)[]
            {
                ("AA:00:00:00:00:01", "guest", "ap-A"),
                ("AA:00:00:00:00:02", "main",  "ap-A"),
                ("BB:00:00:00:00:01", "iot",   "ap-B"),
            };
            foreach (var (mac, ssid, ap) in expected)
            {
                var row = cut.FindAll("tbody tr").First(tr => tr.TextContent.Contains(mac));
                var cells = row.QuerySelectorAll("td");
                Assert.Equal(ssid, cells[4].TextContent.Trim());
                Assert.Equal(ap,   cells[5].TextContent.Trim());
            }
        }
    }

    /// <summary>
    /// Walk up from the test binary's BaseDirectory to find the repo root, then load Dashboard.razor.
    /// The test binary lives at ApStatsForOpenWrt.Tests/bin/{Configuration}/net10.0/, so walking up
    /// four parents lands at the repo root that contains both project folders.
    /// </summary>
    private static string ReadDashboardSource()
    {
        // Probe a few candidate roots so the test stays robust to bin-path variations.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "ApStatsForOpenWrt", "Components", "Pages", "Dashboard.razor");
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
