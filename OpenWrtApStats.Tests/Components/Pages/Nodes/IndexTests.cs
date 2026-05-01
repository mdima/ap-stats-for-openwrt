using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using Xunit;
using IndexPage = OpenWrtApStats.Components.Pages.Nodes.Index;

namespace OpenWrtApStats.Tests.Components.Pages.Nodes;

/// <summary>
/// /Nodes Index page bUnit tests. Covers the empty-state branch (DASH-09 visual mirrored)
/// and the table-rendering branch (UI-SPEC §Nodes Index lines 318-329 column headers + the
/// 3-action button surface).
/// </summary>
public sealed class IndexTests
{
    private sealed class StubNodeRepo(IEnumerable<WiFiNode> seed) : INodeRepo
    {
        private readonly List<WiFiNode> _nodes = seed.ToList();
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WiFiNode>>(_nodes);
        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<WiFiNode?>(_nodes.FirstOrDefault(n => n.Id == id));
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) { _nodes.Add(node); return Task.CompletedTask; }
        public Task DeleteAsync(int id, CancellationToken ct = default) { _nodes.RemoveAll(n => n.Id == id); return Task.CompletedTask; }
        public Task UpdateMetadataAsync(int id, string hostname, string username, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePasswordAsync(int id, string encryptedPassword, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => Task.FromResult(_nodes.Any(n => !string.IsNullOrEmpty(n.Password)));
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static BunitContext BuildContext(INodeRepo repo)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(repo);
        return ctx;
    }

    [Fact]
    public void EmptyList_RendersEmptyState()
    {
        using var ctx = BuildContext(new StubNodeRepo(Array.Empty<WiFiNode>()));

        var cut = ctx.Render<IndexPage>();

        Assert.Contains("No access points yet", cut.Markup);
        Assert.Contains("Add your first OpenWRT node", cut.Markup);
        Assert.Contains("href=\"/Nodes/Add\"", cut.Markup);
    }

    [Fact]
    public void TwoNodes_RendersTableWithStatusBadge_AndActions()
    {
        using var ctx = BuildContext(new StubNodeRepo(new[]
        {
            new WiFiNode { Id = 1, Hostname = "ap-1", Username = "root", Status = Status.Online, LastPollDate = DateTime.UtcNow },
            new WiFiNode { Id = 2, Hostname = "ap-2", Username = "root", Status = Status.Offline, LastPollDate = default }
        }));

        var cut = ctx.Render<IndexPage>();

        Assert.Contains("ap-1", cut.Markup);
        Assert.Contains("ap-2", cut.Markup);
        Assert.Contains("Online", cut.Markup);
        Assert.Contains("Offline", cut.Markup);
        Assert.Contains("Never", cut.Markup);   // Last polled cell for ap-2
        Assert.Contains("href=\"/Nodes/1/Edit\"", cut.Markup);
        Assert.Contains("href=\"/Nodes/2\"", cut.Markup);
    }

    [Fact]
    public void DeleteButton_OpensConfirmationModal_WithHostnameSubstituted()
    {
        using var ctx = BuildContext(new StubNodeRepo(new[]
        {
            new WiFiNode { Id = 7, Hostname = "to-delete-me", Username = "root", Status = Status.Online, LastPollDate = DateTime.UtcNow }
        }));

        var cut = ctx.Render<IndexPage>();

        // Modal starts hidden — find the Delete button and click.
        var deleteBtn = cut.FindAll("button.btn-outline-danger").First();
        deleteBtn.Click();

        // Verbatim UI-SPEC §Confirmation Modal §Delete node copy with hostname substituted.
        Assert.Contains("Delete this node?", cut.Markup);
        Assert.Contains("to-delete-me", cut.Markup);
        Assert.Contains("from the polling list", cut.Markup);
    }
}
