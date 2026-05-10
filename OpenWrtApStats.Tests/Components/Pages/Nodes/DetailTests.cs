using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Nodes;
using Xunit;
using DetailPage = OpenWrtApStats.Components.Pages.Nodes.Detail;

namespace OpenWrtApStats.Tests.Components.Pages.Nodes;

/// <summary>
/// /Nodes/{id} Detail page bUnit tests. Covers Card 1 (Connection summary + Re-trust),
/// Card 2 (WLANs + Discover button + Enabled toggle).
/// </summary>
public sealed class DetailTests
{
    private sealed class StubNodeRepo : INodeRepo
    {
        public WiFiNode Existing = new() { Id = 1, Hostname = "ap-detail", Username = "root", Status = Status.Online, LastPollDate = DateTime.UtcNow, HostKeyFingerprint = "0123456789abcdef0123456789abcdef" };
        public int ClearHostKeyFingerprintCallCount;
        public int? ClearedNodeId;

        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<WiFiNode?>(id == Existing.Id ? Existing : null);
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default)
        {
            ClearHostKeyFingerprintCallCount++;
            ClearedNodeId = nodeId;
            Existing.HostKeyFingerprint = null;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WiFiNode>>(new[] { Existing });
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string h, string u, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePasswordAsync(int id, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastEnrichmentRefreshAtAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubNetworkRepo : INetworkRepo
    {
        public List<WiFiNetwork> Rows = new();
        public List<(int id, bool enabled)> SetEnabledCalls = new();

        public Task<IReadOnlyList<WiFiNetwork>> GetByNodeAsync(int wiFiNodeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WiFiNetwork>>(Rows.Where(r => r.WiFiNodeId == wiFiNodeId).ToList());
        public Task<WiFiNetwork?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<WiFiNetwork?>(Rows.FirstOrDefault(r => r.Id == id));
        public Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
        {
            SetEnabledCalls.Add((id, enabled));
            var row = Rows.FirstOrDefault(r => r.Id == id);
            if (row is not null) row.Enabled = enabled;
            return Task.CompletedTask;
        }
        public Task UpsertManyAsync(int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertManyAsync(OpenWrtApStats.Data.DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAbsentDisabledAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(OpenWrtApStats.Data.DatabaseContext db, int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEnabledAsync(CancellationToken ct = default) => Task.FromResult(Rows.Any(r => r.Enabled));
    }

    private sealed class StubDiscovery : IWlanDiscoveryService
    {
        public DiscoveryOutcome NextOutcome { get; set; } = new DiscoveryOutcome.Success(0, null);
        public int CallCount;
        public int? ObservedNodeId;
        public Task<DiscoveryOutcome> DiscoverAsync(int nodeId, CancellationToken ct)
        {
            CallCount++;
            ObservedNodeId = nodeId;
            return Task.FromResult(NextOutcome);
        }
    }

    private static (BunitContext ctx, StubNodeRepo nodes, StubNetworkRepo nets, StubDiscovery disc) Build()
    {
        var ctx = new BunitContext();
        var nodes = new StubNodeRepo();
        var nets = new StubNetworkRepo();
        var disc = new StubDiscovery();
        ctx.Services.AddSingleton<INodeRepo>(nodes);
        ctx.Services.AddSingleton<INetworkRepo>(nets);
        ctx.Services.AddSingleton<IWlanDiscoveryService>(disc);
        return (ctx, nodes, nets, disc);
    }

    [Fact]
    public void OnLoad_RendersConnectionCard_AndWlansCard_WithEmptyState()
    {
        var (ctx, _, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            Assert.Contains("Connection", cut.Markup);
            Assert.Contains("ap-detail", cut.Markup);
            Assert.Contains("Re-trust this AP", cut.Markup);
            Assert.Contains("WLANs", cut.Markup);
            Assert.Contains("Discover WLANs", cut.Markup);
            // Empty-state pre-discovery (UI-SPEC §Node Detail empty-state copy)
            Assert.Contains("No WLANs discovered yet", cut.Markup);
            Assert.Contains("Click \"Discover WLANs\" to scan this access point.", cut.Markup);
            // Truncated fingerprint with ellipsis
            Assert.Contains("0123456789abcdef…", cut.Markup);
        }
    }

    [Fact]
    public void OnLoad_WithNullFingerprint_RendersNotYetTrusted()
    {
        var (ctx, nodes, _, _) = Build();
        using (ctx)
        {
            nodes.Existing.HostKeyFingerprint = null;

            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            Assert.Contains("Not yet trusted", cut.Markup);
        }
    }

    [Fact]
    public void DiscoverButton_OnSuccess_ShowsAlertSuccess()
    {
        var (ctx, _, _, disc) = Build();
        using (ctx)
        {
            disc.NextOutcome = new DiscoveryOutcome.Success(2, 1);
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            var btn = cut.FindAll("button").First(b => b.TextContent.Contains("Discover WLANs"));
            btn.Click();

            Assert.Equal(1, disc.CallCount);
            Assert.Equal(1, disc.ObservedNodeId);
            Assert.Contains("alert-success", cut.Markup);
            Assert.Contains("Discovery complete", cut.Markup);
            Assert.Contains("2 WLANs are now visible", cut.Markup);
            Assert.Contains("1 previously-discovered WLANs were marked disabled", cut.Markup);
        }
    }

    [Fact]
    public void DiscoverButton_OnBusy_ShowsAlertInfo()
    {
        var (ctx, _, _, disc) = Build();
        using (ctx)
        {
            disc.NextOutcome = new DiscoveryOutcome.Busy();
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            var btn = cut.FindAll("button").First(b => b.TextContent.Contains("Discover WLANs"));
            btn.Click();

            Assert.Contains("alert-info", cut.Markup);
            Assert.Contains("Node is busy with the regular polling cycle", cut.Markup);
        }
    }

    [Fact]
    public void EnabledToggle_CallsSetEnabledAsync()
    {
        var (ctx, _, nets, _) = Build();
        using (ctx)
        {
            nets.Rows.Add(new WiFiNetwork { Id = 11, WiFiNodeId = 1, MacAddress = "AA:BB:CC:00:00:01", SSID = "wlan-1", Enabled = true });
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            // Toggle off the only row.
            var checkbox = cut.Find("input[type='checkbox'][role='switch']");
            checkbox.Change(false);

            Assert.Single(nets.SetEnabledCalls);
            Assert.Equal((11, false), nets.SetEnabledCalls[0]);
        }
    }

    [Fact]
    public void RetrustButton_OpensModal_ConfirmCallsClearHostKeyFingerprintAsync()
    {
        var (ctx, nodes, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            var retrustBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Re-trust this AP"));
            retrustBtn.Click();

            // Modal opens with verbatim UI-SPEC copy.
            Assert.Contains("Re-trust this access point?", cut.Markup);
            Assert.Contains("ap-detail", cut.Markup);
            Assert.Contains("intentionally re-installed", cut.Markup);

            // Click the Confirm button (modal-footer btn-danger labeled "Re-trust").
            var confirm = cut.Find(".modal-footer .btn-danger");
            confirm.Click();

            Assert.Equal(1, nodes.ClearHostKeyFingerprintCallCount);
            Assert.Equal(1, nodes.ClearedNodeId);
        }
    }
}
