using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services;
using ApStatsForOpenWrt.Services.Admin;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Services.Vendor;
using Xunit;
using Detail = ApStatsForOpenWrt.Components.Pages.Stations.Detail;

namespace ApStatsForOpenWrt.Tests.Components.Pages.Stations;

/// <summary>
/// Phase 5 D-13 / D-14 / DASH-15 / DASH-16 — /Stations/{mac} detail page bUnit tests.
/// Closes architectural gates StationsDetailPage_RendersAllFields and
/// StationsDetailPage_DeleteModal_ConfirmInvokesRepo (CONTEXT lines 220-225).
/// </summary>
public sealed class DetailTests
{
    // ── Stubs ──────────────────────────────────────────────────────────────
    private sealed class StubStationRepo : IStationRepo
    {
        public WLANStation? Existing { get; set; }
        public int    SetNameCallCount { get; private set; }
        public string? LastSetNameValue { get; private set; }
        public int    DeleteCallCount { get; private set; }
        public string? DeletedMac { get; private set; }
        public string? RequestedMac { get; private set; }
        public int    SetIsRandomMacCallCount { get; private set; }
        public bool?  LastSetIsRandomMacValue { get; private set; }
        public string? LastSetIsRandomMacForMac { get; private set; }
        public int    GetByMacCallCount { get; private set; }   // 260516-dlu: pin live-update + post-dispose unsubscribe contracts.

        public Task<IReadOnlyList<WLANStation>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WLANStation>>(Existing is null ? Array.Empty<WLANStation>() : new[] { Existing });
        public Task<WLANStation?> GetByMacAsync(string macAddress, CancellationToken ct = default)
        {
            GetByMacCallCount++;
            RequestedMac = macAddress;
            return Task.FromResult(Existing?.MacAddress == macAddress ? Existing : null);
        }
        public Task<IReadOnlyList<string>> GetAllMacsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Existing is null ? Array.Empty<string>() : new[] { Existing.MacAddress });
        public Task SetNameAsync(string macAddress, string? name, CancellationToken ct = default)
        {
            SetNameCallCount++;
            LastSetNameValue = name;
            if (Existing is not null && Existing.MacAddress == macAddress) Existing.Name = name;
            return Task.CompletedTask;
        }
        public Task SetIsRandomMacAsync(string macAddress, bool isRandomMac, CancellationToken ct = default)
        {
            SetIsRandomMacCallCount++;
            LastSetIsRandomMacValue = isRandomMac;
            LastSetIsRandomMacForMac = macAddress;
            if (Existing is not null && Existing.MacAddress == macAddress) Existing.IsRandomMac = isRandomMac;
            return Task.CompletedTask;
        }
        public Task DeleteByMacAsync(string macAddress, CancellationToken ct = default)
        {
            DeleteCallCount++;
            DeletedMac = macAddress;
            return Task.CompletedTask;
        }
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(ApStatsForOpenWrt.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(ApStatsForOpenWrt.Data.DatabaseContext db, IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetMacsByNodeAsync(int nodeId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Existing is null ? Array.Empty<string>() : new[] { Existing.MacAddress });
    }

    private sealed class StubNetworkRepo : INetworkRepo
    {
        public WiFiNetwork? Existing { get; set; }
        public Task<IReadOnlyList<WiFiNetwork>> GetByNodeAsync(int wiFiNodeId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WiFiNetwork>>(Array.Empty<WiFiNetwork>());
        public Task<WiFiNetwork?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(Existing?.Id == id ? Existing : null);
        public Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertManyAsync(int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertManyAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAbsentDisabledAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEnabledAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

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
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(ApStatsForOpenWrt.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastEnrichmentRefreshAtAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubOuiLookup : IOuiLookup
    {
        public Dictionary<string, string?> Vendors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? GetVendor(string macAddress) => Vendors.TryGetValue(macAddress, out var v) ? v : null;
    }

    private sealed class StubSettings : IAppSettingsService
    {
        public int     PollingIntervalSeconds { get; } = 10;
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
        // Phase 17 Plan 17-01 M-UPD-02 — minimal mutable update-check stub members.
        public bool UpdateCheckEnabled { get; private set; } = true;
        public bool HistoryEnabled => true;
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

    private sealed class StubNotificationsGate : INotificationsGate
    {
        public bool IsEnabled { get; set; } = true;
    }

    // v0.11 Plan 14-04 -- programmable stub for INodeAdminService. Returns a canned
    // DisconnectResult so each Disconnect-button test pins one variant of the D-12
    // result-mapping matrix.
    private sealed class StubNodeAdminService : INodeAdminService
    {
        public DisconnectResult Result { get; set; } = new DisconnectResult.Success();
        public int CallCount { get; private set; }
        public string? LastMac { get; private set; }
        public string? LastIface { get; private set; }

        public Task<DisconnectResult> DisconnectStationAsync(WiFiNode node, string mac, string iface, CancellationToken ct)
        {
            CallCount++;
            LastMac = mac;
            LastIface = iface;
            return Task.FromResult(Result);
        }
    }

    private static (BunitContext ctx, StubStationRepo stations, StubNetworkRepo nets, StubNodeRepo nodes, StubOuiLookup oui, EventDetectionService events, StubNodeAdminService nodeAdmin) Build()
    {
        var ctx = new BunitContext();
        var stations = new StubStationRepo();
        var nets = new StubNetworkRepo();
        var nodes = new StubNodeRepo();
        var oui = new StubOuiLookup();
        var nodeAdmin = new StubNodeAdminService();
        ctx.Services.AddSingleton<IStationRepo>(stations);
        ctx.Services.AddSingleton<INetworkRepo>(nets);
        ctx.Services.AddSingleton<INodeRepo>(nodes);
        ctx.Services.AddSingleton<IOuiLookup>(oui);
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings());
        // v0.11 Plan 14-04 DISC-01..07 -- Detail.razor now injects INodeAdminService.
        ctx.Services.AddSingleton<INodeAdminService>(nodeAdmin);

        // Quick task 260511-cmi — Detail.razor injects EventDetectionService for the
        // ForgetMac call in HandleDeleteAsync. Register a real service with stub deps; the
        // bUnit harness never raises PollNotifier so the service is idle for unrelated tests.
        var gate = new StubNotificationsGate();
        var notifier = new PollNotifier();
        var toasts = new ToastService(gate, NullLogger<ToastService>.Instance);
        var sc = new ServiceCollection();
        sc.AddSingleton<INodeRepo>(nodes);
        sc.AddSingleton<IStationRepo>(stations);
        var sp = sc.BuildServiceProvider();
        // Phase 18 18-05 — EventDetectionService now takes a concrete CircuitTracker (D-03 visitor
        // signal). This bUnit harness never raises PollNotifier, so the tracker is idle/Paused;
        // dispose responsibility stays with the ctx Services container lifetime.
        var tracker = new ApStatsForOpenWrt.Services.Gating.CircuitTracker();
        var events = new EventDetectionService(notifier, toasts, gate, tracker, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EventDetectionService>.Instance);
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton<INotificationsGate>(gate);
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(events);

        return (ctx, stations, nets, nodes, oui, events, nodeAdmin);
    }

    private static WLANStation MakeStation(string mac = "AA:BB:CC:00:00:01") => new()
    {
        Id = 100, MacAddress = mac, Hostname = "phone-1",
        Name = "Alice's phone", IPAddress = "192.168.1.42",
        WiFiNetworkId = 10, TxBitrate = 144.4, RxBitrate = 130.0, RSS = -65,
        Status = Status.Online, LastPollDate = DateTime.UtcNow.AddSeconds(-3),
    };

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_AllFields_ForExistingStation()
    {
        var (ctx, stations, nets, nodes, oui, _, _) = Build();
        stations.Existing = MakeStation();
        nets.Existing     = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "guest", Enabled = true };
        nodes.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x", Status = Status.Online, LastPollDate = DateTime.UtcNow });
        oui.Vendors["AA:BB:CC:00:00:01"] = "Apple, Inc.";

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));

            // Required field labels (UI-SPEC dt list):
            Assert.Contains("MAC", cut.Markup);
            Assert.Contains("Hostname", cut.Markup);
            Assert.Contains("Vendor", cut.Markup);
            Assert.Contains("IP address", cut.Markup);
            Assert.Contains("SSID", cut.Markup);
            Assert.Contains("Access point", cut.Markup);
            Assert.Contains("Tx bitrate", cut.Markup);
            Assert.Contains("Rx bitrate", cut.Markup);
            Assert.Contains("Signal", cut.Markup);
            Assert.Contains("Last seen", cut.Markup);
            Assert.Contains("Status", cut.Markup);

            // Field values:
            Assert.Contains("AA:BB:CC:00:00:01", cut.Markup);
            Assert.Contains("phone-1", cut.Markup);
            Assert.Contains("Apple, Inc.", cut.Markup);
            Assert.Contains("192.168.1.42", cut.Markup);
            Assert.Contains("guest", cut.Markup);
            Assert.Contains("ap-A", cut.Markup);
            Assert.Contains("144.4 Mb/s", cut.Markup);
            Assert.Contains("130 Mb/s", cut.Markup);   // RxBitrate
            Assert.Contains("-65 dBm", cut.Markup);    // RssiCell
            Assert.Contains("Online", cut.Markup);
        }
    }

    [Fact]
    public void Renders_404_WhenStationNotFound()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        stations.Existing = null;

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "ZZ:ZZ:ZZ:ZZ:ZZ:ZZ"));

            Assert.Contains("Station not found", cut.Markup);
            Assert.Contains("Back to dashboard", cut.Markup);
            Assert.Contains("href=\"/\"", cut.Markup);
        }
    }

    [Fact]
    public void MacRouteParam_IsUrlDecoded_AndUppercased()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        stations.Existing = MakeStation();

        using (ctx)
        {
            // Lowercase + URL-encoded colons in the route
            ctx.Render<Detail>(p => p.Add(c => c.Mac, "aa%3Abb%3Acc%3A00%3A00%3A01"));

            Assert.Equal("AA:BB:CC:00:00:01", stations.RequestedMac);
        }
    }

    [Fact]
    public void SaveNameButton_CallsSetNameAsync_WithTrimmedValue()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        stations.Existing = MakeStation();

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));

            cut.Find("input#station-name").Change("  Alice's iPhone  ");
            cut.Find("form").Submit();

            Assert.Equal(1, stations.SetNameCallCount);
            Assert.Equal("Alice's iPhone", stations.LastSetNameValue);
        }
    }

    [Fact]
    public void SaveNameButton_EmptyValue_CallsSetNameAsyncWithNull()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        stations.Existing = MakeStation();

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));

            cut.Find("input#station-name").Change("");
            cut.Find("form").Submit();

            Assert.Equal(1, stations.SetNameCallCount);
            Assert.Null(stations.LastSetNameValue);
        }
    }

    [Fact]
    public void DeleteButton_OpensModal_NotYetCallingRepo()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        stations.Existing = MakeStation();

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));

            // Modal closed initially.
            Assert.DoesNotContain("show", cut.Find(".modal").GetAttribute("class") ?? "");

            cut.FindAll("button").First(b => b.TextContent.Contains("Delete this station")).Click();

            Assert.Contains("show", cut.Find(".modal").GetAttribute("class") ?? "");
            Assert.Contains("If it reconnects to a monitored WLAN", cut.Markup);
            Assert.Equal(0, stations.DeleteCallCount);
        }
    }

    // Quick task 260511-cmi — operator-initiated delete must invalidate the in-memory
    // MAC cache held by EventDetectionService. Before this fix, `_knownMacs` was leaky:
    // a deleted MAC stayed "shadow-known" until app restart and the rebid produced no
    // new-station toast. Asserts the post-delete state of `_knownMacs` directly via
    // reflection (the field is private; no public surface exposes the set).
    [Fact]
    public void DeleteModal_Confirm_InvalidatesEventDetectionKnownMacs()
    {
        var (ctx, stations, _, _, _, events, _) = Build();
        stations.Existing = MakeStation();

        // Seed _knownMacs directly so we test the cache-invalidation path without
        // requiring a real PollNotifier cycle (the Build harness does not raise events).
        var knownMacsField = typeof(EventDetectionService).GetField(
            "_knownMacs",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var knownMacs = (HashSet<string>)knownMacsField.GetValue(events)!;
        knownMacs.Add("AA:BB:CC:00:00:01");
        Assert.Contains("AA:BB:CC:00:00:01", knownMacs);   // pre-condition

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            cut.FindAll("button").First(b => b.TextContent.Contains("Delete this station")).Click();

            try
            {
                cut.Find(".modal-footer .btn-danger").Click();
            }
            catch
            {
                // Nav.NavigateTo("/") side-effect — ForgetMac + DeleteByMacAsync ran before this.
            }

            Assert.Equal(1, stations.DeleteCallCount);
            Assert.DoesNotContain("AA:BB:CC:00:00:01", knownMacs);   // ForgetMac flushed it
        }
    }

    [Fact]
    public void DeleteModal_Confirm_CallsDeleteByMacAsync()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        stations.Existing = MakeStation();

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            cut.FindAll("button").First(b => b.TextContent.Contains("Delete this station")).Click();

            // Modal-footer Confirm button (red Delete). HandleDeleteAsync calls
            // DeleteByMacAsync BEFORE Nav.NavigateTo, so the repo assertion holds even
            // if bUnit's NavigationManager throws on the post-delete redirect.
            try
            {
                cut.Find(".modal-footer .btn-danger").Click();
            }
            catch
            {
                // Nav.NavigateTo("/") side-effect — repo call already happened.
            }

            Assert.Equal(1, stations.DeleteCallCount);
            Assert.Equal("AA:BB:CC:00:00:01", stations.DeletedMac);
        }
    }

    [Fact]
    public void DeleteModal_Cancel_DoesNotCallDeleteByMacAsync()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        stations.Existing = MakeStation();

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            cut.FindAll("button").First(b => b.TextContent.Contains("Delete this station")).Click();

            // Modal-footer Cancel button
            cut.Find(".modal-footer .btn-secondary").Click();

            Assert.Equal(0, stations.DeleteCallCount);
        }
    }

    [Fact]
    public void SignalCell_RendersRssiBar_ForKnownSignal()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        stations.Existing = MakeStation();

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));

            Assert.Contains("-65 dBm", cut.Markup);
            Assert.Contains("rssi-seg-filled", cut.Markup);
            Assert.Contains("text-bg-success", cut.Markup);   // -65 → success threshold
        }
    }

    [Fact]
    public void SignalCell_RendersEmDash_WhenRssNull()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        var stn = MakeStation();
        stn.RSS = null;
        stations.Existing = stn;

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));

            Assert.Contains("Signal unknown", cut.Markup);
        }
    }

    // ── Phase 10 Plan 10-04 Task 3 — random-MAC toggle (DASH-22 / DASH-23) ──

    [Fact]
    public void RandomMacToggle_Prefills_FromIsRandomMac_True()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        var s = MakeStation();
        s.IsRandomMac = true;
        stations.Existing = s;

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            var toggle = cut.Find("input#random-mac-toggle");
            Assert.NotNull(toggle.GetAttribute("checked"));
        }
    }

    [Fact]
    public void RandomMacToggle_Prefills_FromIsRandomMac_False()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        var s = MakeStation();
        s.IsRandomMac = false;
        stations.Existing = s;

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            var toggle = cut.Find("input#random-mac-toggle");
            Assert.Null(toggle.GetAttribute("checked"));
        }
    }

    [Fact]
    public void RandomMacToggle_Change_InvokesSetIsRandomMacAsync_WithNewValue()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        var s = MakeStation();
        s.IsRandomMac = false;
        stations.Existing = s;

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            var toggle = cut.Find("input#random-mac-toggle");
            toggle.Change(true);

            Assert.Equal(1, stations.SetIsRandomMacCallCount);
            Assert.Equal(true, stations.LastSetIsRandomMacValue);
            Assert.Equal("AA:BB:CC:00:00:01", stations.LastSetIsRandomMacForMac);
        }
    }

    [Fact]
    public void RandomMacToggle_Change_ShowsSavedFlash()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        var s = MakeStation();
        s.IsRandomMac = false;
        stations.Existing = s;

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            // Sanity: no "Saved" inside the random-MAC toggle div before change.
            // Trigger the change.
            var toggle = cut.Find("input#random-mac-toggle");
            toggle.Change(true);
            // After the synchronous portion of OnRandomMacToggleChangedAsync, _randomMacFlash=true
            // and StateHasChanged has run via the framework's automatic re-render.
            var toggleDiv = cut.Find("input#random-mac-toggle").ParentElement!;
            Assert.Contains("Saved", toggleDiv.InnerHtml);
        }
    }

    // ── v0.9.2 — "First seen" row appears above "Last seen" with correct UTC datetime attrs ──

    [Fact]
    public void Render_Surfaces_FirstSeen_Above_LastSeen()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        var firstSeen = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var lastSeen  = new DateTime(2026, 5, 11,  9, 0, 0, DateTimeKind.Utc);
        var stn = MakeStation();
        stn.FirstSeen = firstSeen;
        stn.LastPollDate = lastSeen;
        stations.Existing = stn;

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));

            // Two <time data-local-time> elements rendered — one per row.
            var times = cut.FindAll("time[data-local-time]");
            Assert.Equal(2, times.Count);

            // DOM order: First seen first, Last seen second.
            Assert.Equal(
                DateTime.SpecifyKind(firstSeen, DateTimeKind.Utc).ToString("o"),
                times[0].GetAttribute("datetime"));
            Assert.Equal(
                DateTime.SpecifyKind(lastSeen, DateTimeKind.Utc).ToString("o"),
                times[1].GetAttribute("datetime"));

            // Label sanity check — "First seen" <dt> precedes "Last seen" <dt>.
            var dts = cut.FindAll("dt").Select(e => e.TextContent.Trim()).ToList();
            var firstIdx = dts.IndexOf("First seen");
            var lastIdx = dts.IndexOf("Last seen");
            Assert.True(
                firstIdx >= 0 && lastIdx > firstIdx,
                $"Expected 'First seen' dt to precede 'Last seen' dt; got order: {string.Join(", ", dts)}");
        }
    }

    [Fact]
    public void DeleteModal_Title_ContainsStationName()
    {
        var (ctx, stations, _, _, _, _, _) = Build();
        var stn = MakeStation();
        stn.Name = "Alice's phone";
        stations.Existing = stn;

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            cut.FindAll("button").First(b => b.TextContent.Contains("Delete this station")).Click();

            Assert.Contains("Delete station", cut.Markup);
            Assert.Contains("Alice's phone", cut.Markup);
        }
    }

    // ── v0.11 Plan 14-04 DISC-03/DISC-04/DISC-05/DISC-07 — Disconnect button + ConfirmationModal + result mapping ──

    // Helper: seed station + network (with IfaceName populated, D-10) + node so HandleDisconnectAsync
    // has all the data it needs to invoke INodeAdminService. Set the stub's canned result via
    // nodeAdmin.Result before clicking confirm.
    private static (BunitContext ctx, StubNodeAdminService nodeAdmin) BuildDisconnectContext(DisconnectResult cannedResult)
    {
        var (ctx, stations, nets, nodes, _, _, nodeAdmin) = Build();
        stations.Existing = MakeStation();
        nets.Existing = new WiFiNetwork
        {
            Id = 10,
            WiFiNodeId = 1,
            MacAddress = "00:11:22:33:44:55",
            SSID = "guest",
            Enabled = true,
            IfaceName = "wlan0",   // D-10 — IfaceName now persisted; required for HandleDisconnectAsync.
        };
        nodes.Nodes.Add(new WiFiNode
        {
            Id = 1,
            Hostname = "ap-A",
            Username = "root",
            Password = "x",
            Status = Status.Online,
            LastPollDate = DateTime.UtcNow,
        });
        nodeAdmin.Result = cannedResult;
        return (ctx, nodeAdmin);
    }

    [Fact]
    public void DisconnectButton_IsAlwaysVisible_NoCapabilityGating()
    {
        // D-11 + D-14: button is rendered unconditionally on a station page that has a resolvable
        // network + node. No capability probe, no role check.
        var (ctx, _) = BuildDisconnectContext(new DisconnectResult.Success());

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            var btn = cut.Find("#disconnect-station-button");
            Assert.NotNull(btn);
            // Initial render: not disabled (no disconnect in flight).
            Assert.False(btn.HasAttribute("disabled"));
            Assert.Contains("Disconnect station", btn.TextContent);
        }
    }

    [Fact]
    public void DisconnectButton_Click_OpensConfirmationModal()
    {
        var (ctx, _) = BuildDisconnectContext(new DisconnectResult.Success());

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            cut.Find("#disconnect-station-button").Click();
            // Modal class includes "show" when open.
            var modals = cut.FindAll(".modal.show");
            Assert.True(modals.Any(), "Expected at least one .modal.show after Disconnect click.");
            // The disconnect modal body copy:
            Assert.Contains("Issues a ubus deauth", cut.Markup);
        }
    }

    [Fact]
    public void DisconnectModal_Confirm_OnSuccess_ShowsSuccessBanner()
    {
        var (ctx, nodeAdmin) = BuildDisconnectContext(new DisconnectResult.Success());

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            cut.Find("#disconnect-station-button").Click();
            // The Disconnect modal is the SECOND .modal-footer .btn-danger on the page (the first
            // is the Delete modal's Confirm). Find the one whose textContent reads "Disconnect".
            var confirmBtns = cut.FindAll(".modal-footer .btn-danger");
            var disconnectConfirm = confirmBtns.First(b => b.TextContent.Trim() == "Disconnect");
            disconnectConfirm.Click();

            Assert.Equal(1, nodeAdmin.CallCount);
            Assert.Equal("AA:BB:CC:00:00:01", nodeAdmin.LastMac);
            Assert.Equal("wlan0", nodeAdmin.LastIface);
            Assert.Contains("Disconnect issued — station should leave the WLAN on the next poll cycle.", cut.Markup);
            Assert.Contains("alert-success", cut.Markup);
        }
    }

    [Fact]
    public void DisconnectModal_Confirm_OnTimeout_ShowsTimeoutErrorBanner()
    {
        var (ctx, nodeAdmin) = BuildDisconnectContext(new DisconnectResult.Timeout());

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            cut.Find("#disconnect-station-button").Click();
            cut.FindAll(".modal-footer .btn-danger").First(b => b.TextContent.Trim() == "Disconnect").Click();

            Assert.Equal(1, nodeAdmin.CallCount);
            Assert.Contains("Disconnect failed — AP unreachable within 5s.", cut.Markup);
            Assert.Contains("alert-danger", cut.Markup);
        }
    }

    [Fact]
    public void DisconnectModal_Confirm_OnNotSupported_ShowsCapabilityMissingBanner()
    {
        // 260517-hcd — D-08 fourth amendment: hostapd_cli is the authoritative step. NotSupported
        // now fires when the shell-glob socket-discovery loop emits `exit 127 / no hostapd socket
        // for <iface>` (no socket file under /var/run/hostapd-* or /var/run/hostapd matching the
        // iface) OR when hostapd_cli's own `Failed to connect to hostapd` diagnostic fires (socket
        // exists but daemon unreachable). Banner text updated from the 260516-iwo "wireless
        // interface unavailable" wording to "hostapd control socket unavailable" to match the new
        // failure surface.
        var (ctx, nodeAdmin) = BuildDisconnectContext(new DisconnectResult.NotSupported("no hostapd socket for wlan0"));

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            cut.Find("#disconnect-station-button").Click();
            cut.FindAll(".modal-footer .btn-danger").First(b => b.TextContent.Trim() == "Disconnect").Click();

            Assert.Equal(1, nodeAdmin.CallCount);
            Assert.Contains("Disconnect not supported on this AP — hostapd control socket unavailable.", cut.Markup);
            Assert.Contains("alert-danger", cut.Markup);
        }
    }

    [Fact]
    public void DisconnectModal_Confirm_OnNoSuchStation_ShowsInformationalBanner()
    {
        var (ctx, nodeAdmin) = BuildDisconnectContext(new DisconnectResult.NoSuchStation("no such station"));

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            cut.Find("#disconnect-station-button").Click();
            cut.FindAll(".modal-footer .btn-danger").First(b => b.TextContent.Trim() == "Disconnect").Click();

            Assert.Equal(1, nodeAdmin.CallCount);
            Assert.Contains("Station is no longer associated with this AP.", cut.Markup);
            // D-12 case 3 — informational tone, not error.
            Assert.Contains("alert-success", cut.Markup);
        }
    }

    [Fact]
    public void DisconnectModal_Confirm_OnUnknownError_ShowsCheckLogsBanner()
    {
        var (ctx, nodeAdmin) = BuildDisconnectContext(new DisconnectResult.UnknownError("kaboom"));

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            cut.Find("#disconnect-station-button").Click();
            cut.FindAll(".modal-footer .btn-danger").First(b => b.TextContent.Trim() == "Disconnect").Click();

            Assert.Equal(1, nodeAdmin.CallCount);
            Assert.Contains("Disconnect failed — check logs.", cut.Markup);
            Assert.Contains("alert-danger", cut.Markup);
        }
    }

    // ── 260516-dlu — Detail.razor lives on PollNotifier.Cycled + optimistic Offline flip on Disconnect Success ──
    //
    // Operator's v0.11 QUAL-02 LIVE-UAT request (2026-05-16): "after the disconnection I expect that
    // the station data will be updated as reported offline." The backend already does this via the
    // mark-offline-then-upsert cycle invariant; the UI gap is that Detail.razor was deliberately a
    // *snapshot* page (per Phase 5 UI-SPEC line 947) and did not subscribe to PollNotifier. This
    // quick task amends that: Detail.razor now subscribes to PollNotifier.Cycled with W4 double-guard
    // disposal discipline mirroring PollCountdown.razor, AND optimistically flips the local station
    // status to Offline on DisconnectResult.Success for instant operator feedback (bounded by the
    // next ≤ PollingIntervalSeconds cycle, which is authoritative).

    [Fact]
    public async Task OnPollCycled_RefreshesStationData_FromRepo()
    {
        // Pin the live-update contract: when PollNotifier.Cycled fires, Detail.razor re-loads the
        // station from the repo so any DB-side status flip (e.g., the station leaving the WLAN
        // association table after a Disconnect) becomes visible without manual navigation/F5.
        // Bug shape (pre-260516-dlu): no subscription exists, so raising Cycled is a no-op for
        // the page; the badge stays on whatever it loaded at OnParametersSetAsync time.
        var (ctx, stations, nets, nodes, _, _, _) = Build();
        var initial = MakeStation();
        initial.Status = Status.Online;
        // LastPollDate well outside the 2× stale window so a flip to Offline renders as
        // Offline (text-bg-secondary) rather than Stale.
        initial.LastPollDate = DateTime.UtcNow.AddHours(-1);
        stations.Existing = initial;
        nets.Existing = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "g", IfaceName = "wlan0" };
        nodes.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x" });

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            // Initial render: StatusBadge shows Online. (Use the badge's text-content +
            // closing tag as the specific marker -- `text-bg-success` also appears on
            // the RSSI signal bar segments so it's not a precise enough discriminator.)
            Assert.Contains(">Online</span>", cut.Markup);
            var initialLoadCount = stations.GetByMacCallCount;

            // Simulate a DB-side change between cycles: the next cycle should observe Offline.
            stations.Existing!.Status = Status.Offline;

            // Raise a cycle event via the renderer's dispatcher so the handler runs on the
            // correct sync context (matches Pitfall 7 + Phase 5 D-12 paired-adjacency pattern).
            var notifier = ctx.Services.GetRequiredService<PollNotifier>();
            await cut.InvokeAsync(async () =>
                await notifier.RaiseAsync(new PollSnapshot(
                    TotalNodes: 1, OnlineNodes: 1, FailedNodes: 0, StationCount: 1, CycleEndUtc: DateTime.UtcNow)));

            // The page must have re-loaded -- repo call count strictly greater than the initial load.
            Assert.True(stations.GetByMacCallCount > initialLoadCount,
                $"GetByMacCallCount stayed at {stations.GetByMacCallCount} after Cycled raise; expected > {initialLoadCount} (live subscription missing).");

            // And the badge must now reflect the new state (Offline, not Online).
            Assert.Contains(">Offline</span>", cut.Markup);
            Assert.DoesNotContain(">Online</span>", cut.Markup);
        }
    }

    [Fact]
    public async Task OnDispose_UnsubscribesFromCycled_NoRefreshAfterDispose()
    {
        // Pattern 2 -- captured `this` leak guard. After the component is disposed, raising Cycled
        // on the still-live notifier singleton must NOT call back into LoadAsync. The unsubscribe
        // in Dispose() removes the handler; the W4 _disposed flag is a belt-and-braces guard if
        // the handler is somehow still invoked in flight.
        var (ctx, stations, nets, nodes, _, _, _) = Build();
        stations.Existing = MakeStation();
        nets.Existing = new WiFiNetwork { Id = 10, WiFiNodeId = 1, MacAddress = "00:11:22:33:44:55", SSID = "g", IfaceName = "wlan0" };
        nodes.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap-A", Username = "root", Password = "x" });

        var notifier = ctx.Services.GetRequiredService<PollNotifier>();
        int countAtDispose;
        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            countAtDispose = stations.GetByMacCallCount;
        }   // bUnit disposes the rendered component here (BunitContext.Dispose -> renderer dispose).

        // Raise Cycled AFTER dispose. With unsubscribe-in-Dispose in place, this must NOT cause
        // a further LoadAsync call. Must also not throw (W4 double-guard belt-and-braces).
        await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 1, DateTime.UtcNow));

        Assert.Equal(countAtDispose, stations.GetByMacCallCount);
    }

    [Fact]
    public void OnDisconnect_Success_OptimisticallyFlipsStatusToOffline_BeforeAnyCycledEvent()
    {
        // 260516-dlu — operator's verbatim expectation 2026-05-16: "after the disconnection I
        // expect that the station data will be updated as reported offline." On a successful
        // Disconnect, the local _station.Status is flipped to Offline IMMEDIATELY (no Cycled
        // event raised in this test), so the StatusBadge in the rendered DOM reflects Offline
        // before the next poll cycle arrives. The next real Cycled is authoritative (it either
        // confirms Offline if the station truly left, or corrects to Online if it re-associated).
        var (ctx, nodeAdmin) = BuildDisconnectContext(new DisconnectResult.Success());
        // Override Existing's LastPollDate to keep _isStale = false (well outside 2× window),
        // so the Offline flip renders as Offline (text-bg-secondary), not Stale.
        var stations = (StubStationRepo)ctx.Services.GetRequiredService<IStationRepo>();
        stations.Existing!.Status = Status.Online;
        stations.Existing.LastPollDate = DateTime.UtcNow.AddHours(-1);

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            // Pre-click: StatusBadge shows Online. (Targeting `>Online</span>` rather than
            // `text-bg-success` because the latter also matches the RSSI signal bar.)
            Assert.Contains(">Online</span>", cut.Markup);

            cut.Find("#disconnect-station-button").Click();
            cut.FindAll(".modal-footer .btn-danger").First(b => b.TextContent.Trim() == "Disconnect").Click();

            // Success banner present AND status immediately flipped to Offline -- no Cycled raised.
            Assert.Equal(1, nodeAdmin.CallCount);
            Assert.Contains("Disconnect issued — station should leave the WLAN on the next poll cycle.", cut.Markup);
            Assert.Contains(">Offline</span>", cut.Markup);
            Assert.DoesNotContain(">Online</span>", cut.Markup);
        }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("not-supported")]
    [InlineData("no-such-station")]
    [InlineData("unknown")]
    public void OnDisconnect_NonSuccess_DoesNotFlipStatus_BadgeStaysOnline(string kind)
    {
        // 260516-dlu forward guard: the optimistic flip MUST be gated on DisconnectResult.Success.
        // Any non-Success branch (Timeout / NotSupported / NoSuchStation / UnknownError) must
        // leave the local status untouched -- a failed SSH command means the station was NOT
        // (verifiably) disconnected, and flipping the badge to Offline would mislead the operator.
        DisconnectResult canned = kind switch
        {
            "timeout"         => new DisconnectResult.Timeout(),
            "not-supported"   => new DisconnectResult.NotSupported("Command failed: Not found"),
            "no-such-station" => new DisconnectResult.NoSuchStation("no such station"),
            "unknown"         => new DisconnectResult.UnknownError("kaboom"),
            _ => throw new ArgumentException("invalid kind", nameof(kind)),
        };
        var (ctx, _) = BuildDisconnectContext(canned);
        var stations = (StubStationRepo)ctx.Services.GetRequiredService<IStationRepo>();
        stations.Existing!.Status = Status.Online;
        stations.Existing.LastPollDate = DateTime.UtcNow.AddHours(-1);

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));
            Assert.Contains(">Online</span>", cut.Markup);   // pre: Online

            cut.Find("#disconnect-station-button").Click();
            cut.FindAll(".modal-footer .btn-danger").First(b => b.TextContent.Trim() == "Disconnect").Click();

            // StatusBadge unchanged (NoSuchStation may render an informational banner but
            // MUST NOT flip the status -- the station could legitimately still be there).
            Assert.Contains(">Online</span>", cut.Markup);
        }
    }
}
