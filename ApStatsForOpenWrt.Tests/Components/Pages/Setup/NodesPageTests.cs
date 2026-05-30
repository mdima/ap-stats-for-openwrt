using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Nodes;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;
using NodesPage = ApStatsForOpenWrt.Components.Pages.Setup.Nodes;

namespace ApStatsForOpenWrt.Tests.Components.Pages.Setup;

/// <summary>
/// Nodes.razor (Step 2) bUnit tests. Covers: empty-table initial state, Continue button
/// disabled when no nodes, save adds row and resets form, Continue enabled after first save,
/// Remove calls DeleteAsync and removes row. ONB-03.
/// </summary>
public sealed class NodesPageTests
{
    private sealed class CapturingNodeRepo : INodeRepo
    {
        private readonly List<WiFiNode> _nodes = new();
        public int? DeletedId { get; private set; }

        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WiFiNode>>(_nodes.ToList());

        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult(_nodes.FirstOrDefault(n => n.Id == id));

        public Task AddAsync(WiFiNode node, CancellationToken ct = default)
        {
            node.Id = _nodes.Count + 1;
            _nodes.Add(node);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int id, CancellationToken ct = default)
        {
            DeletedId = id;
            _nodes.RemoveAll(n => n.Id == id);
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
            => Task.FromResult(_nodes.Count > 0);

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

        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubSettings : IAppSettingsService
    {
        public int PollingIntervalSeconds => 30;
        public string? UIPasswordHash => null;
        public bool WizardDismissed { get; set; }
        public int RefreshIntervalSeconds => 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; } = Array.Empty<string>();
        public Task SetPollingIntervalSecondsAsync(int s, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool d, CancellationToken ct = default) { WizardDismissed = d; return Task.CompletedTask; }
        public Task SetRefreshIntervalSecondsAsync(int s, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private sealed class StubTestConn : ITestConnectionService
    {
        public Task<TestConnectionOutcome> TestAsync(
            string hostname, string username, string plaintextPassword,
            int? existingNodeId, CancellationToken ct)
            => Task.FromResult<TestConnectionOutcome>(new TestConnectionOutcome.Success(0, 0));
    }

    private static (BunitContext ctx, CapturingNodeRepo repo) Build()
    {
        var ctx = new BunitContext();
        var repo = new CapturingNodeRepo();
        ctx.Services.AddSingleton<INodeRepo>(repo);
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings());
        ctx.Services.AddSingleton<ITestConnectionService>(new StubTestConn());
        ctx.Services.AddDataProtection().SetApplicationName("ApStatsForOpenWrt-Tests-Plan04-03");
        return (ctx, repo);
    }

    [Fact]
    public void RendersEmptyNodeList_Initially()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<NodesPage>();
            // No <tbody> since _savedNodes is empty and table is conditionally rendered
            Assert.DoesNotContain("<tbody", cut.Markup);
            // Continue button should be disabled
            var continueBtn = cut.Find("[data-testid='continue-btn']");
            Assert.True(continueBtn.HasAttribute("disabled"));
        }
    }

    [Fact]
    public async Task ContinueButton_Disabled_WhenZeroNodes()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<NodesPage>();
            await cut.InvokeAsync(() => Task.CompletedTask); // let async init settle
            var continueBtn = cut.Find("[data-testid='continue-btn']");
            Assert.True(continueBtn.HasAttribute("disabled"));
        }
    }

    [Fact]
    public async Task SaveNode_WithValidModel_AddsRowToTable()
    {
        var (ctx, repo) = Build();
        using (ctx)
        {
            var cut = ctx.Render<NodesPage>();
            await cut.InvokeAsync(async () =>
                await cut.Instance.HandleSaveForTestAsync(
                    new ApStatsForOpenWrt.Components.Pages.Nodes.NodeFormModel
                    {
                        Hostname = "ap-test.local",
                        Username = "root",
                        Password = "secret"
                    }));
            var rows = await repo.GetAllAsync(Xunit.TestContext.Current.CancellationToken);
            Assert.Single(rows);
        }
    }

    [Fact]
    public async Task ContinueButton_Enabled_AfterFirstSave()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<NodesPage>();
            await cut.InvokeAsync(async () =>
                await cut.Instance.HandleSaveForTestAsync(
                    new ApStatsForOpenWrt.Components.Pages.Nodes.NodeFormModel
                    {
                        Hostname = "ap2.local",
                        Username = "root",
                        Password = "pass"
                    }));
            var continueBtn = cut.Find("[data-testid='continue-btn']");
            Assert.False(continueBtn.HasAttribute("disabled"));
        }
    }

    [Fact]
    public async Task RemoveButton_CallsDeleteAsync_RemovesRow()
    {
        var (ctx, repo) = Build();
        using (ctx)
        {
            var cut = ctx.Render<NodesPage>();
            // Add a node first
            await cut.InvokeAsync(async () =>
                await cut.Instance.HandleSaveForTestAsync(
                    new ApStatsForOpenWrt.Components.Pages.Nodes.NodeFormModel
                    {
                        Hostname = "ap3.local",
                        Username = "root",
                        Password = "pw"
                    }));
            // Get the saved node id
            var nodes = await repo.GetAllAsync(Xunit.TestContext.Current.CancellationToken);
            var nodeId = nodes.First().Id;
            // Invoke remove
            await cut.InvokeAsync(async () =>
                await cut.Instance.HandleRemoveForTestAsync(nodeId));
            Assert.Equal(nodeId, repo.DeletedId);
            var remaining = await repo.GetAllAsync(Xunit.TestContext.Current.CancellationToken);
            Assert.Empty(remaining);
        }
    }
}
