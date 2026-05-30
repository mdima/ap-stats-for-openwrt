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
/// M-SEC-06 (plan 16-04) — Nodes/Detail.razor full-fingerprint display UX.
/// Pins the CONTEXT.md Decision 4 markup: full SHA256 fingerprint inside a
/// <c>code.font-monospace.user-select-all</c>, the out-of-band verification recipe
/// below it, the "Not yet captured" empty-state copy, no truncation/ellipsis, and
/// the per-node Re-trust button preserved.
/// </summary>
[Trait("Category", "SecurityFix")]
public sealed class DetailFullFingerprintTests
{
    private const string FullFingerprint = "SHA256:abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

    private sealed class StubNodeRepo : INodeRepo
    {
        public WiFiNode Existing = new()
        {
            Id = 1,
            Hostname = "ap-1.local",
            Username = "root",
            Status = Status.Online,
            LastPollDate = DateTime.UtcNow,
            HostKeyFingerprint = FullFingerprint,
        };

        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<WiFiNode?>(id == Existing.Id ? Existing : null);
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default)
        {
            Existing.HostKeyFingerprint = null;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WiFiNode>>(new[] { Existing });
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string h, string u, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePasswordAsync(int id, string p, CancellationToken ct = default) => Task.CompletedTask;
#pragma warning disable CS0618 // exercising the [Obsolete] deprecation gate from the interface contract
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
#pragma warning restore CS0618
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
        public Task<IReadOnlyList<WiFiNetwork>> GetByNodeAsync(int wiFiNodeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WiFiNetwork>>(Array.Empty<WiFiNetwork>());
        public Task<WiFiNetwork?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<WiFiNetwork?>(null);
        public Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertManyAsync(int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertManyAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int wiFiNodeId, IEnumerable<WiFiNetwork> discovered, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAbsentDisabledAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAbsentAsync(ApStatsForOpenWrt.Data.DatabaseContext db, int wiFiNodeId, IReadOnlyCollection<string> presentMacs, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEnabledAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class StubDiscovery : IWlanDiscoveryService
    {
        public Task<DiscoveryOutcome> DiscoverAsync(int nodeId, CancellationToken ct) =>
            Task.FromResult<DiscoveryOutcome>(new DiscoveryOutcome.Success(0, null));
    }

    private static (BunitContext ctx, StubNodeRepo nodes) Build()
    {
        var ctx = new BunitContext();
        var nodes = new StubNodeRepo();
        ctx.Services.AddSingleton<INodeRepo>(nodes);
        ctx.Services.AddSingleton<INetworkRepo>(new StubNetworkRepo());
        ctx.Services.AddSingleton<IWlanDiscoveryService>(new StubDiscovery());
        ctx.Services.AddSingleton(new PollNotifier());
        return (ctx, nodes);
    }

    [Fact]
    public void HostKeyFingerprint_Populated_RendersFullStringInsideUserSelectAllCode()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            var code = cut.Find("code.font-monospace.user-select-all");
            Assert.Equal(FullFingerprint, code.TextContent);
        }
    }

    [Fact]
    public void HostKeyFingerprint_Populated_RecipeRowVisibleWithHostnameInterpolated()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            Assert.Contains("ssh-keyscan ap-1.local | ssh-keygen -lf -", cut.Markup);
            // The recipe carries its own user-select-all <code> span alongside the fingerprint code.
            var selectableCodes = cut.FindAll("code.user-select-all");
            Assert.True(selectableCodes.Count >= 2,
                $"Expected at least 2 user-select-all <code> elements (fingerprint + recipe); found {selectableCodes.Count}.");
        }
    }

    [Fact]
    public void HostKeyFingerprint_Empty_RendersNotYetCapturedCopy()
    {
        var (ctx, nodes) = Build();
        using (ctx)
        {
            nodes.Existing.HostKeyFingerprint = null;

            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            Assert.Contains("Not yet captured — first poll cycle establishes trust", cut.Markup);
            Assert.Empty(cut.FindAll("code.font-monospace.user-select-all"));
        }
    }

    [Fact]
    public void HostKeyFingerprint_Populated_NoEllipsisInHostKeyBlock()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            // The U+2026 ellipsis was the old truncation marker — it must be gone entirely.
            Assert.DoesNotContain("…", cut.Markup);
        }
    }

    [Fact]
    public void HostKeyFingerprint_Populated_RetrustButtonStillPresent()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<DetailPage>(p => p.Add(c => c.Id, 1));

            var retrust = cut.FindAll("button").Where(b => b.TextContent.Contains("Re-trust this AP")).ToList();
            Assert.Single(retrust);
        }
    }
}
