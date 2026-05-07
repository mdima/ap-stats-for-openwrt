using Bunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Auth;
using OpenWrtApStats.Services.Settings;
using Xunit;

namespace OpenWrtApStats.Tests.Components.Pages.Settings;

/// <summary>
/// bUnit tests for /Settings/Index.razor (Plan 03-05). Renders the three D-17 cards
/// against stubbed IAppSettingsService / IUIPasswordHasher / INodeRepo seams. The full
/// auth-gate propagation (SET-04 / Pitfall 1) is exercised by SettingsRoundTripTests.
/// </summary>
public sealed class IndexTests
{
    // ── Stubs ───────────────────────────────────────────────────────────────

    private sealed class StubSettings : IAppSettingsService
    {
        public string? UIPasswordHash { get; set; }
        public int PollingIntervalSeconds { get; set; } = 30;
        public bool WizardDismissed => false;
        public bool ChangedFiredOnSetPassword;
        public bool ChangedFiredOnSetInterval;

        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default)
        {
            PollingIntervalSeconds = seconds;
            ChangedFiredOnSetInterval = true;
            return Task.CompletedTask;
        }
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default)
        {
            UIPasswordHash = hash;
            ChangedFiredOnSetPassword = true;
            return Task.CompletedTask;
        }
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    private sealed class CapturingHasher : IUIPasswordHasher
    {
        public string LastInput = "";
        public string Hash(string plaintext) { LastInput = plaintext; return "AQAAA-fake-hash"; }
        public PasswordVerificationResult Verify(string h, string p) => PasswordVerificationResult.Success;
    }

    private sealed class CapturingNodeRepo : INodeRepo
    {
        public bool ClearAllCalled;
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) { ClearAllCalled = true; return Task.CompletedTask; }

        // Every other method returns a default — this stub is only consumed for the bulk-clear path.
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
    }

    private static (BunitContext ctx, StubSettings settings, CapturingHasher hasher, CapturingNodeRepo nodes) Build(string? initialHash = null, int initialInterval = 30)
    {
        var ctx = new BunitContext();
        // Phase 8 Plan 08-07 — Settings/Index.razor now hosts a 4th "UI preferences" card
        // (REQ-UI-44) that injects PreferencesService. Loose-mode JSInterop with a faked
        // ./js/preferences.js module lets the page render end-to-end without a real JS runtime.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");
        var settings = new StubSettings { UIPasswordHash = initialHash, PollingIntervalSeconds = initialInterval };
        var hasher = new CapturingHasher();
        var nodes = new CapturingNodeRepo();
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        ctx.Services.AddSingleton<IUIPasswordHasher>(hasher);
        ctx.Services.AddSingleton<INodeRepo>(nodes);
        ctx.Services.AddSingleton<OpenWrtApStats.Services.PreferencesService>(
            new OpenWrtApStats.Services.PreferencesService(ctx.JSInterop.JSRuntime));
        return (ctx, settings, hasher, nodes);
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public void RendersThreeCards_InOrder()
    {
        var (ctx, _, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
            // D-17 binding: three cards in the locked order.
            var markup = cut.Markup;
            var i1 = markup.IndexOf(">Polling interval</h5>", StringComparison.Ordinal);
            var i2 = markup.IndexOf(">UI password</h5>", StringComparison.Ordinal);
            var i3 = markup.IndexOf(">SSH known-hosts cache</h5>", StringComparison.Ordinal);
            Assert.True(i1 >= 0, "Card 1 (Polling interval) header missing");
            Assert.True(i2 > i1, "Card 2 (UI password) header missing or out of order");
            Assert.True(i3 > i2, "Card 3 (SSH known-hosts cache) header missing or out of order");
        }
    }

    [Fact]
    public void Card2_NoPasswordSet_ShowsNoPasswordIntro_NoClearButton()
    {
        var (ctx, _, _, _) = Build(initialHash: null);
        using (ctx)
        {
            var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
            Assert.Contains("No password is set.", cut.Markup);
            Assert.DoesNotContain(">Clear password</button>", cut.Markup);
        }
    }

    [Fact]
    public void Card2_PasswordSet_ShowsClearButton_AndDifferentIntro()
    {
        var (ctx, _, _, _) = Build(initialHash: "AQAAA-existing");
        using (ctx)
        {
            var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
            Assert.Contains("Sign-in is required.", cut.Markup);
            Assert.Contains(">Clear password</button>", cut.Markup);
        }
    }

    [Fact]
    public void Card3_AlwaysShowsClearAllHostKeysButton()
    {
        var (ctx, _, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
            Assert.Contains("Clear all host keys", cut.Markup);
        }
    }

    [Fact]
    public async Task Card2_MismatchedPasswords_ShowsMismatchError_AndDoesNotHash()
    {
        var (ctx, settings, hasher, _) = Build(initialHash: null);
        using (ctx)
        {
            var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
            // Set the two password fields to different values.
            cut.Find("#new-password").Change("alpha");
            cut.Find("#confirm-password").Change("beta");

            // Submit the password form. The Set password submit lives inside the
            // EditForm rendering Card 2; clicking the matching submit triggers OnValidSubmit.
            var submit = cut.FindAll("button[type='submit']")
                            .First(b => b.TextContent.Contains("Set password", StringComparison.Ordinal));
            await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

            Assert.Contains("Passwords do not match.", cut.Markup);
            Assert.Equal("", hasher.LastInput);                   // Hasher.Hash never called
            Assert.Null(settings.UIPasswordHash);                 // hash never persisted
        }
    }

    [Fact]
    public async Task Card2_EmptyNewPassword_ShowsEnterAPasswordError()
    {
        var (ctx, settings, hasher, _) = Build(initialHash: null);
        using (ctx)
        {
            var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
            // Leave both fields empty (default null/empty), submit.
            var submit = cut.FindAll("button[type='submit']")
                            .First(b => b.TextContent.Contains("Set password", StringComparison.Ordinal));
            await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

            Assert.Contains("Enter a password.", cut.Markup);
            Assert.Equal("", hasher.LastInput);
            Assert.Null(settings.UIPasswordHash);
        }
    }

    [Fact]
    public async Task Card2_MatchingPasswords_HashesAndPersists_FromEmpty()
    {
        var (ctx, settings, hasher, _) = Build(initialHash: null);
        using (ctx)
        {
            var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
            cut.Find("#new-password").Change("hunter2");
            cut.Find("#confirm-password").Change("hunter2");

            var submit = cut.FindAll("button[type='submit']")
                            .First(b => b.TextContent.Contains("Set password", StringComparison.Ordinal));
            await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

            Assert.Equal("hunter2", hasher.LastInput);
            Assert.Equal("AQAAA-fake-hash", settings.UIPasswordHash);
            Assert.True(settings.ChangedFiredOnSetPassword);
            // From-empty banner copy per UI-SPEC §Settings Card 2.
            Assert.Contains("Password set.", cut.Markup);
        }
    }

    [Fact]
    public async Task Card2_MatchingPasswords_HashesAndPersists_FromExistingHash_UpdatedBanner()
    {
        var (ctx, settings, hasher, _) = Build(initialHash: "AQAAA-existing");
        using (ctx)
        {
            var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
            cut.Find("#new-password").Change("brandnew");
            cut.Find("#confirm-password").Change("brandnew");

            var submit = cut.FindAll("button[type='submit']")
                            .First(b => b.TextContent.Contains("Set password", StringComparison.Ordinal));
            await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

            Assert.Equal("brandnew", hasher.LastInput);
            Assert.Equal("AQAAA-fake-hash", settings.UIPasswordHash);
            // Changed-from-non-empty banner copy per UI-SPEC §Settings Card 2.
            Assert.Contains("Password updated.", cut.Markup);
        }
    }

    [Fact]
    public void Card1_RadioGroup_PreSelectsCurrentInterval()
    {
        var (ctx, _, _, _) = Build(initialInterval: 10);
        using (ctx)
        {
            var cut = ctx.Render<OpenWrtApStats.Components.Pages.Settings.Index>();
            // The 10-second radio should carry checked=""; the others should not.
            var r10 = cut.Find("#interval-10");
            Assert.True(r10.HasAttribute("checked"));
            var r5 = cut.Find("#interval-5");
            Assert.False(r5.HasAttribute("checked"));
            var r30 = cut.Find("#interval-30");
            Assert.False(r30.HasAttribute("checked"));
        }
    }
}
