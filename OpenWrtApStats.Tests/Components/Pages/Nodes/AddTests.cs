using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Nodes;
using Xunit;
using AddPage = OpenWrtApStats.Components.Pages.Nodes.Add;

namespace OpenWrtApStats.Tests.Components.Pages.Nodes;

/// <summary>
/// /Nodes/Add page bUnit tests. Covers: form rendering with the verbatim UI-SPEC §Node Add
/// labels and CTA copy, Test Connection success path, and Save persisting an
/// encrypted-by-DataProtection ciphertext (T-3-04-01 mitigation evidence).
/// </summary>
public sealed class AddTests
{
    private sealed class CapturingNodeRepo : INodeRepo
    {
        public WiFiNode? Captured;
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WiFiNode>>(Array.Empty<WiFiNode>());
        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<WiFiNode?>(null);
        public Task AddAsync(WiFiNode node, CancellationToken ct = default)
        {
            node.Id = 99;
            Captured = node;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string h, string u, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePasswordAsync(int id, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastEnrichmentRefreshAtAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubTestConn : ITestConnectionService
    {
        public TestConnectionOutcome NextOutcome { get; set; } = new TestConnectionOutcome.Success(0, 0);
        public int CallCount;
        public Task<TestConnectionOutcome> TestAsync(string h, string u, string p, int? id, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(NextOutcome);
        }
    }

    private static BunitContext Build(out CapturingNodeRepo repo, out StubTestConn conn)
    {
        var ctx = new BunitContext();
        repo = new CapturingNodeRepo();
        conn = new StubTestConn();
        ctx.Services.AddSingleton<INodeRepo>(repo);
        ctx.Services.AddSingleton<ITestConnectionService>(conn);
        ctx.Services.AddDataProtection().SetApplicationName("OpenWrtApStats-Tests-Plan03-04");
        return ctx;
    }

    [Fact]
    public void RendersFormWithNewAccessPointTitle_AndVerbatimLabels()
    {
        using var ctx = Build(out _, out _);
        var cut = ctx.Render<AddPage>();

        Assert.Contains("New access point", cut.Markup);
        Assert.Contains("Hostname or IP address", cut.Markup);
        Assert.Contains("SSH username", cut.Markup);
        Assert.Contains("SSH password", cut.Markup);
        Assert.Contains("Save node", cut.Markup);
        Assert.Contains("Test connection", cut.Markup);
        Assert.Contains("Reachable via SSH from this container.", cut.Markup);
        Assert.Contains("Stored encrypted at rest.", cut.Markup);
    }

    [Fact]
    public void TestConnection_WithEmptyFields_ShowsHelperBanner()
    {
        using var ctx = Build(out _, out var conn);
        var cut = ctx.Render<AddPage>();

        // Click Test connection without filling fields — service must NOT be called.
        var testBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Test connection"));
        testBtn.Click();

        Assert.Contains("Fill hostname and username before testing.", cut.Markup);
        Assert.Equal(0, conn.CallCount);
    }

    [Fact]
    public void TestConnection_SuccessOutcome_RendersAlertSuccess()
    {
        using var ctx = Build(out _, out var conn);
        conn.NextOutcome = new TestConnectionOutcome.Success(3, 7);
        var cut = ctx.Render<AddPage>();

        cut.Find("input[placeholder='192.168.1.1']").Change("192.168.1.1");
        cut.Find("input[placeholder='root']").Change("root");
        cut.Find("input[type='password']").Change("p");

        var testBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Test connection"));
        testBtn.Click();

        Assert.Contains("alert-success", cut.Markup);
        Assert.Contains("Connected. Discovered 3 WLANs", cut.Markup);
        Assert.Equal(1, conn.CallCount);
    }

    [Fact]
    public void TestConnection_FailedOutcome_RendersAlertDanger()
    {
        using var ctx = Build(out _, out var conn);
        conn.NextOutcome = new TestConnectionOutcome.AuthFailed();
        var cut = ctx.Render<AddPage>();

        cut.Find("input[placeholder='192.168.1.1']").Change("h");
        cut.Find("input[placeholder='root']").Change("u");
        cut.Find("input[type='password']").Change("p");

        var testBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Test connection"));
        testBtn.Click();

        Assert.Contains("alert-danger", cut.Markup);
        Assert.Contains("Authentication failed", cut.Markup);
    }

    [Fact]
    public void Save_WithValidFields_PersistsEncryptedPassword()
    {
        using var ctx = Build(out var repo, out _);
        var cut = ctx.Render<AddPage>();

        cut.Find("input[placeholder='192.168.1.1']").Change("192.168.1.1");
        cut.Find("input[placeholder='root']").Change("root");
        cut.Find("input[type='password']").Change("plain-text-pwd");

        var submit = cut.Find("button[type='submit']");
        submit.Click();

        Assert.NotNull(repo.Captured);
        Assert.Equal("192.168.1.1", repo.Captured!.Hostname);
        Assert.Equal("root", repo.Captured.Username);
        // T-3-04-01 mitigation: ciphertext, not plaintext.
        Assert.NotEqual("plain-text-pwd", repo.Captured.Password);
        Assert.StartsWith("CfDJ", repo.Captured.Password);
    }

    // ── Phase 10 SET-03 / SET-04 — D-09 form fields persist through Add flow ─────
    [Fact]
    public void Save_PersistsSubnetScanFields_FromForm()
    {
        // Default toggle state is ON (NodeFormModel.SubnetScanEnabled = true). Operator
        // fills the CIDR field and submits.
        using var ctx = Build(out var repo, out _);
        var cut = ctx.Render<AddPage>();

        cut.Find("input[placeholder='192.168.1.1']").Change("192.168.1.1");
        cut.Find("input[placeholder='root']").Change("root");
        cut.Find("input[type='password']").Change("plain-text-pwd");
        cut.Find("input#subnet-scan-ranges").Change("192.168.1.0/24");

        cut.Find("button[type='submit']").Click();

        Assert.NotNull(repo.Captured);
        Assert.True(repo.Captured!.SubnetScanEnabled);
        Assert.Equal("192.168.1.0/24", repo.Captured.SubnetScanRanges);
    }

    [Fact]
    public void Save_BlankSubnetScanRanges_PersistsAsNull()
    {
        // Blank/whitespace SubnetScanRanges round-trips as null (auto-derive intent — D-09).
        using var ctx = Build(out var repo, out _);
        var cut = ctx.Render<AddPage>();

        cut.Find("input[placeholder='192.168.1.1']").Change("192.168.1.1");
        cut.Find("input[placeholder='root']").Change("root");
        cut.Find("input[type='password']").Change("plain-text-pwd");
        // Leave SubnetScanRanges blank (default).

        cut.Find("button[type='submit']").Click();

        Assert.NotNull(repo.Captured);
        Assert.True(repo.Captured!.SubnetScanEnabled);  // default ON
        Assert.Null(repo.Captured.SubnetScanRanges);
    }

    [Fact]
    public void Save_DisabledToggle_PersistsFalse()
    {
        // Operator unchecks the toggle. The CIDR list value is preserved (D-09 — value is
        // still persisted so the operator can re-enable later without re-entering it).
        using var ctx = Build(out var repo, out _);
        var cut = ctx.Render<AddPage>();

        cut.Find("input[placeholder='192.168.1.1']").Change("192.168.1.1");
        cut.Find("input[placeholder='root']").Change("root");
        cut.Find("input[type='password']").Change("plain-text-pwd");
        cut.Find("input#subnet-scan-ranges").Change("10.0.0.0/24");
        // Uncheck the toggle.
        cut.Find("input#subnet-scan-enabled").Change(false);

        cut.Find("button[type='submit']").Click();

        Assert.NotNull(repo.Captured);
        Assert.False(repo.Captured!.SubnetScanEnabled);
        Assert.Equal("10.0.0.0/24", repo.Captured.SubnetScanRanges);
    }
}
