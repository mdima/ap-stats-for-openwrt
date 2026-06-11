using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Nodes;
using ApStatsForOpenWrt.Services.Polling;
using Xunit;
using DetailPage = ApStatsForOpenWrt.Components.Pages.Nodes.Detail;

namespace ApStatsForOpenWrt.Tests.Components.Pages.Nodes;

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
        // 260517-ndr: pin live-update + post-dispose unsubscribe contracts for Nodes/Detail.razor.
        public int GetByIdCallCount { get; private set; }

        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            GetByIdCallCount++;
            return Task.FromResult<WiFiNode?>(id == Existing.Id ? Existing : null);
        }
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
        public Task MarkAllOfflineAsync(ApStatsForOpenWrt.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastEnrichmentRefreshAtAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default) => Task.CompletedTask;
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
        public Task UpsertManyAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAbsentDisabledAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEnabledAsync(CancellationToken ct = default) => Task.FromResult(Rows.Any(r => r.Enabled));
        // M-HRD-10: not exercised by these tests — empty batched read satisfies the contract.
        public Task<IReadOnlyDictionary<int, IReadOnlyList<WiFiNetwork>>> GetAllGroupedByNodeAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<WiFiNetwork>>>(new Dictionary<int, IReadOnlyList<WiFiNetwork>>());
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
        // 260517-ndr — Detail.razor now injects PollNotifier for live-update on Cycled.
        // bUnit harness never raises Cycled for unrelated tests, so the singleton is idle.
        ctx.Services.AddSingleton(new PollNotifier());
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
            // M-SEC-06 (plan 16-04) — full fingerprint, no truncation/ellipsis. The
            // full DetailFullFingerprintTests suite pins the user-select-all markup + recipe;
            // here we just confirm the un-truncated value renders and the old ellipsis is gone.
            Assert.Contains("0123456789abcdef0123456789abcdef", cut.Markup);
            Assert.DoesNotContain("…", cut.Markup);
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

            // M-SEC-06 (plan 16-04) — empty-state copy updated to the CONTEXT.md Decision 4 phrasing.
            Assert.Contains("Not yet captured — first poll cycle establishes trust", cut.Markup);
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

    // ── 260517-ndr — Nodes/Detail.razor subscribes to PollNotifier.Cycled (mirror of 260516-dlu for Stations/Detail) ──
    //
    // Operator-flagged 2026-05-17 (before testing 260517-hcd): *"After the polling, if I am in the details
    // page I must manually refresh the page in order to get the updated data of the station. I see that
    // the dashboard gets refreshed after the pull, but not the details page."* 260516-dlu wired
    // Stations/Detail.razor for live-update but not Nodes/Detail.razor; this quick task closes the parallel
    // gap by applying the same subscribe-in-Init / unsubscribe-in-Dispose pattern with W4 double-guard.

    [Fact]
    public async Task OnPollCycled_RefreshesNodeData_FromRepo()
    {
        // Pin the live-update contract: when PollNotifier.Cycled fires, Nodes/Detail.razor re-loads the
        // node from the repo so any DB-side status flip (e.g., a node transitioning Online -> Offline as
        // a poll-cycle outcome) becomes visible without manual F5. Bug shape (pre-260517-ndr): no
        // subscription exists; raising Cycled is a no-op for the page; the StatusBadge stays on whatever
        // it loaded at OnInitializedAsync time.
        var (ctx, nodes, _, _) = Build();
        nodes.Existing.Status = Status.Online;
        nodes.Existing.LastPollDate = DateTime.UtcNow.AddHours(-1);

        using (ctx)
        {
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));
            // Initial render: StatusBadge shows Online. Match the badge's text-content + closing tag
            // as the discriminator (same pattern as Stations/DetailTests post-260516-dlu).
            Assert.Contains(">Online</span>", cut.Markup);
            var initialLoadCount = nodes.GetByIdCallCount;

            // Simulate a DB-side change between cycles: the next cycle should observe Offline.
            nodes.Existing.Status = Status.Offline;

            // Raise a cycle event via the renderer's dispatcher so the handler runs on the
            // correct sync context (Pitfall 7 + Phase 5 D-12 paired-adjacency pattern).
            var notifier = ctx.Services.GetRequiredService<PollNotifier>();
            await cut.InvokeAsync(async () =>
                await notifier.RaiseAsync(new PollSnapshot(
                    TotalNodes: 1, OnlineNodes: 0, FailedNodes: 1, StationCount: 0, CycleEndUtc: DateTime.UtcNow)));

            // Page must have re-loaded -- repo call count strictly greater than the initial load.
            Assert.True(nodes.GetByIdCallCount > initialLoadCount,
                $"GetByIdCallCount stayed at {nodes.GetByIdCallCount} after Cycled raise; expected > {initialLoadCount} (live subscription missing).");

            // Badge now reflects the new state (Offline, not Online).
            Assert.Contains(">Offline</span>", cut.Markup);
            Assert.DoesNotContain(">Online</span>", cut.Markup);
        }
    }

    [Fact]
    public async Task OnDispose_UnsubscribesFromCycled_NoRefreshAfterDispose()
    {
        // Pattern 2 -- captured `this` leak guard. After the component is disposed, raising Cycled on
        // the still-live notifier singleton must NOT call back into LoadAsync. Unsubscribe-in-Dispose
        // removes the handler; the W4 _disposed flag is belt-and-braces if a handler is somehow in-
        // flight at the moment of dispose.
        var (ctx, nodes, _, _) = Build();
        var notifier = ctx.Services.GetRequiredService<PollNotifier>();
        int countAtDispose;
        using (ctx)
        {
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));
            countAtDispose = nodes.GetByIdCallCount;
        }   // bUnit disposes the rendered component here.

        // Raise Cycled AFTER dispose -- must NOT cause a further LoadAsync call AND must not throw.
        await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 0, DateTime.UtcNow));

        Assert.Equal(countAtDispose, nodes.GetByIdCallCount);
    }
}
