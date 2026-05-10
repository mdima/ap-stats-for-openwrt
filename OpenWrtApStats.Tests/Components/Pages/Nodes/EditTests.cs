using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Nodes;
using Xunit;
using EditPage = OpenWrtApStats.Components.Pages.Nodes.Edit;

namespace OpenWrtApStats.Tests.Components.Pages.Nodes;

/// <summary>
/// /Nodes/{id}/Edit page bUnit tests. Covers D-08 leave-blank-password contract +
/// D-09 split-method routing (UpdateMetadataAsync always; UpdatePasswordAsync only
/// when form Password is non-empty).
///
/// <para>
/// T-3-04-02 evidence: SaveWithEmptyPassword_OnlyCallsUpdateMetadataAsync proves that
/// the empty-password branch never invokes UpdatePasswordAsync, so the encrypted
/// ciphertext is preserved end-to-end.
/// </para>
/// <para>
/// T-3-04-03 evidence: OnLoad_PreFillsHostnameAndUsername_PasswordEmpty proves that
/// the encrypted ciphertext NEVER reaches the browser's Password input.
/// </para>
/// </summary>
public sealed class EditTests
{
    private sealed class TrackingNodeRepo : INodeRepo
    {
        public WiFiNode Existing = new()
        {
            Id = 5,
            Hostname = "old-host",
            Username = "old-user",
            Password = "OLD-CIPHERTEXT",
            HostKeyFingerprint = "FP"
        };
        public string? CapturedNewHostname;
        public string? CapturedNewUsername;
        public string? CapturedNewPasswordEncrypted;

        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<WiFiNode?>(id == Existing.Id ? Existing : null);
        public Task UpdateMetadataAsync(int id, string h, string u, CancellationToken ct = default)
        {
            CapturedNewHostname = h; CapturedNewUsername = u;
            return Task.CompletedTask;
        }
        public Task UpdatePasswordAsync(int id, string p, CancellationToken ct = default)
        {
            CapturedNewPasswordEncrypted = p;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WiFiNode>>(new[] { Existing });
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastEnrichmentRefreshAtAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestConnectionStub : ITestConnectionService
    {
        public Task<TestConnectionOutcome> TestAsync(string h, string u, string p, int? id, CancellationToken ct) =>
            Task.FromResult<TestConnectionOutcome>(new TestConnectionOutcome.Success(0, 0));
    }

    private static (BunitContext ctx, TrackingNodeRepo repo) Build()
    {
        var ctx = new BunitContext();
        var repo = new TrackingNodeRepo();
        ctx.Services.AddSingleton<INodeRepo>(repo);
        ctx.Services.AddSingleton<ITestConnectionService>(new TestConnectionStub());
        ctx.Services.AddDataProtection().SetApplicationName("OpenWrtApStats-Tests-Plan03-04");
        return (ctx, repo);
    }

    [Fact]
    public void OnLoad_PreFillsHostnameAndUsername_PasswordEmpty()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<EditPage>(p => p.Add(c => c.Id, 5));

            var hostInput = cut.Find("input[placeholder='192.168.1.1']");
            var userInput = cut.Find("input[placeholder='root']");
            var pwdInput = cut.Find("input[type='password']");

            Assert.Equal("old-host", hostInput.GetAttribute("value"));
            Assert.Equal("old-user", userInput.GetAttribute("value"));
            // T-3-04-03 mitigation: encrypted ciphertext NEVER reaches the browser.
            Assert.Equal("", pwdInput.GetAttribute("value") ?? "");
        }
    }

    [Fact]
    public void RendersEditAccessPointTitle_AndSaveChangesLabel()
    {
        var (ctx, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<EditPage>(p => p.Add(c => c.Id, 5));

            Assert.Contains("Edit access point", cut.Markup);
            Assert.Contains("Save changes", cut.Markup);
            Assert.Contains("Leave blank to keep existing password", cut.Markup);
        }
    }

    [Fact]
    public void SaveWithEmptyPassword_OnlyCallsUpdateMetadataAsync()
    {
        var (ctx, repo) = Build();
        using (ctx)
        {
            var cut = ctx.Render<EditPage>(p => p.Add(c => c.Id, 5));
            cut.Find("input[placeholder='192.168.1.1']").Change("new-host");
            cut.Find("input[placeholder='root']").Change("new-user");
            // Password field intentionally left empty.

            cut.Find("button[type='submit']").Click();

            Assert.Equal("new-host", repo.CapturedNewHostname);
            Assert.Equal("new-user", repo.CapturedNewUsername);
            // T-3-04-02 mitigation: D-08 — no UpdatePasswordAsync call for empty Password.
            Assert.Null(repo.CapturedNewPasswordEncrypted);
        }
    }

    [Fact]
    public void SaveWithNonEmptyPassword_CallsUpdatePasswordAsyncWithEncryptedValue()
    {
        var (ctx, repo) = Build();
        using (ctx)
        {
            var cut = ctx.Render<EditPage>(p => p.Add(c => c.Id, 5));
            cut.Find("input[type='password']").Change("new-plain");

            cut.Find("button[type='submit']").Click();

            Assert.NotNull(repo.CapturedNewPasswordEncrypted);
            Assert.NotEqual("new-plain", repo.CapturedNewPasswordEncrypted);
            Assert.StartsWith("CfDJ", repo.CapturedNewPasswordEncrypted!);
        }
    }
}
