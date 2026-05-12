using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services;
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

        public Task<IReadOnlyList<WLANStation>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WLANStation>>(Existing is null ? Array.Empty<WLANStation>() : new[] { Existing });
        public Task<WLANStation?> GetByMacAsync(string macAddress, CancellationToken ct = default)
        {
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
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private sealed class StubNotificationsGate : INotificationsGate
    {
        public bool IsEnabled { get; set; } = true;
    }

    private static (BunitContext ctx, StubStationRepo stations, StubNetworkRepo nets, StubNodeRepo nodes, StubOuiLookup oui, EventDetectionService events) Build()
    {
        var ctx = new BunitContext();
        var stations = new StubStationRepo();
        var nets = new StubNetworkRepo();
        var nodes = new StubNodeRepo();
        var oui = new StubOuiLookup();
        ctx.Services.AddSingleton<IStationRepo>(stations);
        ctx.Services.AddSingleton<INetworkRepo>(nets);
        ctx.Services.AddSingleton<INodeRepo>(nodes);
        ctx.Services.AddSingleton<IOuiLookup>(oui);
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings());

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
        var events = new EventDetectionService(notifier, toasts, gate, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EventDetectionService>.Instance);
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddSingleton<INotificationsGate>(gate);
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(events);

        return (ctx, stations, nets, nodes, oui, events);
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
        var (ctx, stations, nets, nodes, oui, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, events) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
        var (ctx, stations, _, _, _, _) = Build();
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
}
