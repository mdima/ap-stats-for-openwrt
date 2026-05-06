using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Settings;
using OpenWrtApStats.Services.Vendor;
using Xunit;
using Detail = OpenWrtApStats.Components.Pages.Stations.Detail;

namespace OpenWrtApStats.Tests.Components.Pages.Stations;

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

        public Task<IReadOnlyList<WLANStation>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WLANStation>>(Existing is null ? Array.Empty<WLANStation>() : new[] { Existing });
        public Task<WLANStation?> GetByMacAsync(string macAddress, CancellationToken ct = default)
        {
            RequestedMac = macAddress;
            return Task.FromResult(Existing?.MacAddress == macAddress ? Existing : null);
        }
        public Task SetNameAsync(string macAddress, string? name, CancellationToken ct = default)
        {
            SetNameCallCount++;
            LastSetNameValue = name;
            if (Existing is not null && Existing.MacAddress == macAddress) Existing.Name = name;
            return Task.CompletedTask;
        }
        public Task DeleteByMacAsync(string macAddress, CancellationToken ct = default)
        {
            DeleteCallCount++;
            DeletedMac = macAddress;
            return Task.CompletedTask;
        }
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(OpenWrtApStats.Data.DatabaseContext db, IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubNetworkRepo : INetworkRepo
    {
        public WiFiNetwork? Existing { get; set; }
        public Task<IReadOnlyList<WiFiNetwork>> GetByNodeAsync(int wiFiNodeId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WiFiNetwork>>(Array.Empty<WiFiNetwork>());
        public Task<WiFiNetwork?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(Existing?.Id == id ? Existing : null);
        public Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertManyAsync(int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertManyAsync(OpenWrtApStats.Data.DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAbsentDisabledAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(OpenWrtApStats.Data.DatabaseContext db, int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
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
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
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
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private static (BunitContext ctx, StubStationRepo stations, StubNetworkRepo nets, StubNodeRepo nodes, StubOuiLookup oui) Build()
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
        return (ctx, stations, nets, nodes, oui);
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
        var (ctx, stations, nets, nodes, oui) = Build();
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
        var (ctx, stations, _, _, _) = Build();
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
        var (ctx, stations, _, _, _) = Build();
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
        var (ctx, stations, _, _, _) = Build();
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
        var (ctx, stations, _, _, _) = Build();
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
        var (ctx, stations, _, _, _) = Build();
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

    [Fact]
    public void DeleteModal_Confirm_CallsDeleteByMacAsync()
    {
        var (ctx, stations, _, _, _) = Build();
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
        var (ctx, stations, _, _, _) = Build();
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
        var (ctx, stations, _, _, _) = Build();
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
        var (ctx, stations, _, _, _) = Build();
        var stn = MakeStation();
        stn.RSS = null;
        stations.Existing = stn;

        using (ctx)
        {
            var cut = ctx.Render<Detail>(p => p.Add(c => c.Mac, "AA:BB:CC:00:00:01"));

            Assert.Contains("Signal unknown", cut.Markup);
        }
    }

    [Fact]
    public void DeleteModal_Title_ContainsStationName()
    {
        var (ctx, stations, _, _, _) = Build();
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
