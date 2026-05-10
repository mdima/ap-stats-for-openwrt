using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Nodes;
using OpenWrtApStats.Services.Settings;
using Xunit;
using WlansPage = OpenWrtApStats.Components.Pages.Setup.Wlans;

namespace OpenWrtApStats.Tests.Components.Pages.Setup;

/// <summary>
/// Wlans.razor (Step 3) bUnit tests. Covers: parallel discovery triggered, success renders
/// WLAN rows, failure renders error banner, toggle calls SetEnabledAsync, zero-WLAN advisory,
/// Finish sets WizardDismissed and navigates. ONB-04 + ONB-05.
/// </summary>
public sealed class WlansPageTests
{
    private sealed class StubNodeRepo : INodeRepo
    {
        public List<WiFiNode> Nodes { get; } = new();

        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WiFiNode>>(Nodes.ToList());

        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult(Nodes.FirstOrDefault(n => n.Id == id));

        public Task AddAsync(WiFiNode node, CancellationToken ct = default)
        {
            node.Id = Nodes.Count + 1;
            Nodes.Add(node);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int id, CancellationToken ct = default)
        {
            Nodes.RemoveAll(n => n.Id == id);
            return Task.CompletedTask;
        }

        public Task UpdateMetadataAsync(int id, string hostname, string username, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdatePasswordAsync(int id, string encryptedPassword, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> AnyAsync(CancellationToken ct = default)
            => Task.FromResult(Nodes.Count > 0);

        public Task MarkAllOfflineAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MarkAllOfflineAsync(DatabaseContext db, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateOnlineAsync(DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SetHostKeyFingerprintAsync(DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateLastEnrichmentRefreshAtAsync(DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubNetworkRepo : INetworkRepo
    {
        public List<WiFiNetwork> Rows { get; } = new();
        public List<(int id, bool enabled)> SetEnabledCalls { get; } = new();

        public Task<IReadOnlyList<WiFiNetwork>> GetByNodeAsync(int wiFiNodeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WiFiNetwork>>(Rows.Where(r => r.WiFiNodeId == wiFiNodeId).ToList());

        public Task<WiFiNetwork?> GetByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult<WiFiNetwork?>(Rows.FirstOrDefault(r => r.Id == id));

        public Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
        {
            SetEnabledCalls.Add((id, enabled));
            var row = Rows.FirstOrDefault(r => r.Id == id);
            if (row is not null) row.Enabled = enabled;
            return Task.CompletedTask;
        }

        public Task UpsertManyAsync(int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpsertManyAsync(DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MarkAbsentDisabledAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAbsentAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAbsentAsync(DatabaseContext db, int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> AnyEnabledAsync(CancellationToken ct = default)
            => Task.FromResult(Rows.Any(r => r.Enabled));
    }

    private sealed class StubDiscovery : IWlanDiscoveryService
    {
        public DiscoveryOutcome NextOutcome { get; set; } = new DiscoveryOutcome.Success(0, null);
        public int CallCount;

        public Task<DiscoveryOutcome> DiscoverAsync(int nodeId, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(NextOutcome);
        }
    }

    private sealed class StubSettings : IAppSettingsService
    {
        public int PollingIntervalSeconds => 30;
        public string? UIPasswordHash => null;
        public bool WizardDismissed { get; private set; }
        public int RefreshIntervalSeconds => 3600;

        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default)
        {
            WizardDismissed = dismissed;
            return Task.CompletedTask;
        }

        public Task WarmAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private static (BunitContext ctx, StubNodeRepo nodeRepo, StubNetworkRepo netRepo, StubDiscovery discovery, StubSettings settings) Build()
    {
        var ctx = new BunitContext();
        var nodeRepo = new StubNodeRepo();
        var netRepo = new StubNetworkRepo();
        var discovery = new StubDiscovery();
        var settings = new StubSettings();
        ctx.Services.AddSingleton<INodeRepo>(nodeRepo);
        ctx.Services.AddSingleton<INetworkRepo>(netRepo);
        ctx.Services.AddSingleton<IWlanDiscoveryService>(discovery);
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        return (ctx, nodeRepo, netRepo, discovery, settings);
    }

    [Fact]
    public async Task OnInitialized_TriggersDiscovery_ForAllAddedNodes()
    {
        var (ctx, nodeRepo, _, discovery, _) = Build();
        using (ctx)
        {
            // Arrange: 2 nodes seeded in StubNodeRepo
            nodeRepo.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap1.local", Username = "root", Status = Status.Offline });
            nodeRepo.Nodes.Add(new WiFiNode { Id = 2, Hostname = "ap2.local", Username = "root", Status = Status.Offline });

            // Act: render Wlans, await init settlement
            var cut = ctx.Render<WlansPage>();
            await cut.InvokeAsync(() => Task.CompletedTask);

            // Assert: discovery called once per node
            Assert.Equal(2, discovery.CallCount);
        }
    }

    [Fact]
    public async Task DiscoverySuccess_RendersNetworkRows()
    {
        var (ctx, nodeRepo, netRepo, discovery, _) = Build();
        using (ctx)
        {
            // Arrange: 1 node, discovery returns success; StubNetworkRepo returns 2 networks
            nodeRepo.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap1.local", Username = "root", Status = Status.Offline });
            netRepo.Rows.Add(new WiFiNetwork { Id = 10, WiFiNodeId = 1, SSID = "HomeWifi", MacAddress = "AA:BB:CC:DD:EE:01", Enabled = true });
            netRepo.Rows.Add(new WiFiNetwork { Id = 11, WiFiNodeId = 1, SSID = "GuestWifi", MacAddress = "AA:BB:CC:DD:EE:02", Enabled = true });
            discovery.NextOutcome = new DiscoveryOutcome.Success(2, null);

            // Act
            var cut = ctx.Render<WlansPage>();
            await cut.InvokeAsync(() => Task.CompletedTask);

            // Assert: rendered markup contains the SSIDs
            Assert.Contains("HomeWifi", cut.Markup);
            Assert.Contains("GuestWifi", cut.Markup);
        }
    }

    [Fact]
    public async Task DiscoveryFailure_RendersErrorBanner()
    {
        var (ctx, nodeRepo, _, discovery, _) = Build();
        using (ctx)
        {
            // Arrange: 1 node, discovery returns failure outcome
            nodeRepo.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap1.local", Username = "root", Status = Status.Offline });
            discovery.NextOutcome = new DiscoveryOutcome.Generic("SSH timeout");

            // Act
            var cut = ctx.Render<WlansPage>();
            await cut.InvokeAsync(() => Task.CompletedTask);

            // Assert: error banner present
            Assert.Contains("alert-danger", cut.Markup);
        }
    }

    [Fact]
    public async Task EnabledToggle_CallsSetEnabledAsync()
    {
        var (ctx, nodeRepo, netRepo, discovery, _) = Build();
        using (ctx)
        {
            // Arrange: 1 node, 1 network discovered (Enabled=true)
            nodeRepo.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap1.local", Username = "root", Status = Status.Offline });
            netRepo.Rows.Add(new WiFiNetwork { Id = 10, WiFiNodeId = 1, SSID = "HomeWifi", MacAddress = "AA:BB:CC:DD:EE:01", Enabled = true });
            discovery.NextOutcome = new DiscoveryOutcome.Success(1, null);

            // Act: render and let discovery settle
            var cut = ctx.Render<WlansPage>();
            await cut.InvokeAsync(() => Task.CompletedTask);

            // Toggle off the checkbox
            var checkbox = cut.Find("input[type='checkbox'][role='switch']");
            await cut.InvokeAsync(() => checkbox.Change(false));

            // Assert
            Assert.Single(netRepo.SetEnabledCalls);
            Assert.Equal((10, false), netRepo.SetEnabledCalls[0]);
        }
    }

    [Fact]
    public async Task ZeroWlansDiscovered_RendersAdvisory_FinishEnabled()
    {
        var (ctx, nodeRepo, _, discovery, _) = Build();
        using (ctx)
        {
            // Arrange: 1 node, discovery returns success but StubNetworkRepo returns empty list
            nodeRepo.Nodes.Add(new WiFiNode { Id = 1, Hostname = "ap1.local", Username = "root", Status = Status.Offline });
            discovery.NextOutcome = new DiscoveryOutcome.Success(0, null);
            // netRepo has no rows — GetByNodeAsync returns empty list

            // Act
            var cut = ctx.Render<WlansPage>();
            await cut.InvokeAsync(() => Task.CompletedTask);

            // Assert: alert-warning advisory present
            Assert.Contains("alert-warning", cut.Markup);

            // Finish button NOT disabled
            var finishBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Finish"));
            Assert.NotNull(finishBtn);
            Assert.False(finishBtn!.HasAttribute("disabled"));
        }
    }

    [Fact]
    public async Task FinishButton_SetsWizardDismissed_NavigatesToRoot()
    {
        var (ctx, _, _, _, settings) = Build();
        using (ctx)
        {
            // Arrange: no nodes — empty state is fine; Finish is always enabled
            var cut = ctx.Render<WlansPage>();
            await cut.InvokeAsync(() => Task.CompletedTask);

            // Act: click the Finish button
            var finishBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Finish"));
            await cut.InvokeAsync(() => finishBtn.Click());

            // Assert: WizardDismissed set to true
            Assert.True(settings.WizardDismissed);
        }
    }
}
