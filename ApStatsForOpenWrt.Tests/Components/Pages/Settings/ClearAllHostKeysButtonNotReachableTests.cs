using Bunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Auth;
using ApStatsForOpenWrt.Services.Enrichment;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Components.Pages.Settings;

/// <summary>
/// M-SEC-06 (plan 16-04) deprecation gate. The bulk "Clear all host keys" UI surface
/// (Card 3 "SSH known-hosts cache" + button + modal + handler) was REMOVED from
/// Settings/Index.razor per CONTEXT.md Decision 6 — the per-node Re-trust button on
/// Nodes/Detail.razor is the sole remaining re-trust path. These tests fail loud if a
/// future commit accidentally re-introduces the button or the card heading. v0.13 will
/// delete the obsolete repo method + these tests in a single mechanical sweep.
/// </summary>
[Trait("Category", "SecurityFix")]
public sealed class ClearAllHostKeysButtonNotReachableTests
{
    private sealed class StubSettings : IAppSettingsService
    {
        public string? UIPasswordHash { get; set; }
        public int PollingIntervalSeconds { get; set; } = 30;
        public bool WizardDismissed => false;
        public int RefreshIntervalSeconds { get; set; } = 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; set; } = Array.Empty<string>();
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private sealed class StubHasher : IUIPasswordHasher
    {
        public string Hash(string plaintext) => "AQAAA-fake-hash";
        public PasswordVerificationResult Verify(string h, string p) => PasswordVerificationResult.Success;
    }

    private sealed class StubNodeRepo : INodeRepo
    {
#pragma warning disable CS0618 // exercising the [Obsolete] deprecation gate from the interface contract
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
#pragma warning restore CS0618
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WiFiNode>>(Array.Empty<WiFiNode>());
        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<WiFiNode?>(null);
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string h, string u, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePasswordAsync(int id, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastEnrichmentRefreshAtAsync(DatabaseContext db, int nodeId, DateTimeOffset ts, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateScanSettingsAsync(int id, bool subnetScanEnabled, string? subnetScanRanges, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static BunitContext Build()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");
        ctx.Services.AddSingleton<IAppSettingsService>(new StubSettings());
        ctx.Services.AddSingleton<IUIPasswordHasher>(new StubHasher());
        ctx.Services.AddSingleton<INodeRepo>(new StubNodeRepo());
        ctx.Services.AddSingleton<ApStatsForOpenWrt.Services.PreferencesService>(
            new ApStatsForOpenWrt.Services.PreferencesService(ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton<PollNotifier>();
        ctx.Services.AddSingleton<IForceEnrichmentService, ForceEnrichmentService>();
        return ctx;
    }

    [Fact]
    [Trait("Category", "SecurityFix")]
    public void ClearAllHostKeysButton_NotReachable_M_SEC_06()
    {
        var ctx = Build();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();

            var matchingButtons = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("Clear all host keys"))
                .ToList();
            Assert.Empty(matchingButtons);
        }
    }

    [Fact]
    [Trait("Category", "SecurityFix")]
    public void SshKnownHostsCacheCard_Heading_NotReachable_M_SEC_06()
    {
        var ctx = Build();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();

            Assert.DoesNotContain("SSH known-hosts cache", cut.Markup);
        }
    }
}
