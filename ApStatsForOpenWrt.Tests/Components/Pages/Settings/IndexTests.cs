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
        public int RefreshIntervalSeconds { get; set; } = 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; set; } = Array.Empty<string>();
        public bool ChangedFiredOnSetPassword;
        public bool ChangedFiredOnSetInterval;
        public int? LastSetRefreshIntervalSeconds;
        public IReadOnlyList<string>? LastSetLocalDomainSuffixes;
        public int SetLocalDomainSuffixesCallCount;
        public int SetRefreshIntervalSecondsCallCount;

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
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default)
        {
            RefreshIntervalSeconds = seconds;
            LastSetRefreshIntervalSeconds = seconds;
            SetRefreshIntervalSecondsCallCount++;
            return Task.CompletedTask;
        }
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default)
        {
            var arr = suffixes.ToArray();
            LocalDomainSuffixes = arr;
            LastSetLocalDomainSuffixes = arr;
            SetLocalDomainSuffixesCallCount++;
            return Task.CompletedTask;
        }
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
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
        // M-SEC-06 (plan 16-04): the bulk-clear UI surface was removed; this stub no longer
        // captures a Clear-all invocation. The obsolete contract method is still implemented
        // (warning-suppressed) so the stub satisfies INodeRepo until the v0.13 method removal.
#pragma warning disable CS0618
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
#pragma warning restore CS0618

        // Every other method returns a default — this stub is only consumed by the Settings page render.
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
        ctx.Services.AddSingleton<ApStatsForOpenWrt.Services.PreferencesService>(
            new ApStatsForOpenWrt.Services.PreferencesService(ctx.JSInterop.JSRuntime));
        // v0.11 ENRICH-02 — register PollNotifier + IForceEnrichmentService for the Run-now button.
        // Existing tests that don't exercise the Run-now path simply don't touch these services.
        ctx.Services.AddSingleton<PollNotifier>();
        ctx.Services.AddSingleton<IForceEnrichmentService, ForceEnrichmentService>();
        return (ctx, settings, hasher, nodes);
    }

    // v0.11 ENRICH-04 — overload returning the new services for the Run-now button tests.
    private static (BunitContext ctx, StubSettings settings, PollNotifier notifier, IForceEnrichmentService forceEnrichment) BuildWithForceEnrichment(string? initialHash = null, int initialInterval = 30)
    {
        var (ctx, settings, _, _) = Build(initialHash, initialInterval);
        var notifier = ctx.Services.GetRequiredService<PollNotifier>();
        var forceEnrichment = ctx.Services.GetRequiredService<IForceEnrichmentService>();
        return (ctx, settings, notifier, forceEnrichment);
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public void RendersAllCards_InOrder()
    {
        var (ctx, _, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            // D-17 + Phase 10 D-05/D-03/D-04 — cards in the locked order.
            // M-SEC-06 (plan 16-04): the "SSH known-hosts cache" card was REMOVED; the
            // remaining order is Live polling / Active enrichment / Local domain suffixes /
            // UI password / UI preferences.
            var markup = cut.Markup;
            var i1 = markup.IndexOf(">Live polling interval</h5>", StringComparison.Ordinal);
            var i2 = markup.IndexOf(">Active enrichment interval</h5>", StringComparison.Ordinal);
            var i3 = markup.IndexOf(">Local domain suffixes</h5>", StringComparison.Ordinal);
            var i4 = markup.IndexOf(">UI password</h5>", StringComparison.Ordinal);
            var i5 = markup.IndexOf(">UI preferences</h5>", StringComparison.Ordinal);
            Assert.True(i1 >= 0, "Card 1 (Live polling interval) header missing");
            Assert.True(i2 > i1, "Card 2 (Active enrichment interval) header missing or out of order");
            Assert.True(i3 > i2, "Card 3 (Local domain suffixes) header missing or out of order");
            Assert.True(i4 > i3, "Card 4 (UI password) header missing or out of order");
            Assert.True(i5 > i4, "Card 5 (UI preferences) header missing or out of order");

            // M-SEC-06 — the bulk SSH known-hosts cache card is gone.
            Assert.DoesNotContain(">SSH known-hosts cache</h5>", markup, StringComparison.Ordinal);

            // D-05 verbatim: the OLD bare "Polling interval" title is gone.
            Assert.DoesNotContain(">Polling interval</h5>", markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Card1_HelperTextRewritten_PerD05()
    {
        var (ctx, _, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            // D-05 verbatim helper text.
            Assert.Contains("How often each AP is polled for connected stations.", cut.Markup);
            // Old v0.9 helper copy is gone.
            Assert.DoesNotContain("How often the application SSHes into each access point.", cut.Markup);
        }
    }

    [Fact]
    public void Card2_NoPasswordSet_ShowsNoPasswordIntro_NoClearButton()
    {
        var (ctx, _, _, _) = Build(initialHash: null);
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
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
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            Assert.Contains("Sign-in is required.", cut.Markup);
            Assert.Contains(">Clear password</button>", cut.Markup);
        }
    }

    // M-SEC-06 (plan 16-04): the "Clear all host keys" bulk surface was REMOVED. The
    // deprecation-gate assertions live in ClearAllHostKeysButtonNotReachableTests; this
    // test pins the negative here too so the IndexTests render-contract stays self-consistent.
    [Fact]
    public void Card3_SshKnownHostsCache_RemovedEntirely_M_SEC_06()
    {
        var (ctx, _, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            Assert.DoesNotContain("Clear all host keys", cut.Markup);
            Assert.DoesNotContain("SSH known-hosts cache", cut.Markup);
        }
    }

    [Fact]
    public async Task Card2_MismatchedPasswords_ShowsMismatchError_AndDoesNotHash()
    {
        var (ctx, settings, hasher, _) = Build(initialHash: null);
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
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
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
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
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
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
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
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
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            // The 10-second radio should carry checked=""; the others should not.
            var r10 = cut.Find("#interval-10");
            Assert.True(r10.HasAttribute("checked"));
            var r5 = cut.Find("#interval-5");
            Assert.False(r5.HasAttribute("checked"));
            var r30 = cut.Find("#interval-30");
            Assert.False(r30.HasAttribute("checked"));
        }
    }

    // ── Phase 10 Plan 10-02 — Active enrichment interval (D-03) ─────────────

    [Fact]
    public void ActiveEnrichmentCard_RendersAllFivePresets()
    {
        var (ctx, _, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            // D-03 verbatim — five presets: 15min/30min/1h/6h/24h.
            Assert.NotNull(cut.Find("#refresh-900"));
            Assert.NotNull(cut.Find("#refresh-1800"));
            Assert.NotNull(cut.Find("#refresh-3600"));
            Assert.NotNull(cut.Find("#refresh-21600"));
            Assert.NotNull(cut.Find("#refresh-86400"));
        }
    }

    [Fact]
    public void ActiveEnrichmentCard_PreSelectsCurrentRefreshInterval()
    {
        var (ctx, settings, _, _) = Build();
        settings.RefreshIntervalSeconds = 21600;     // 6 hours
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            var r6h = cut.Find("#refresh-21600");
            Assert.True(r6h.HasAttribute("checked"));
            var r1h = cut.Find("#refresh-3600");
            Assert.False(r1h.HasAttribute("checked"));
        }
    }

    [Fact]
    public void ActiveEnrichmentCard_SubmitsSelectedPreset_PersistsViaService()
    {
        // Pre-seed a non-default selection (24h = 86400) so OnInitializedAsync pulls it
        // into _refreshForm.Seconds. Then submit and assert the service was called with
        // the seeded value. This bypasses the bUnit-vs-InputRadioGroup change-event
        // marshaling complexity (radios under InputRadioGroup don't propagate checked-bit
        // changes through bUnit's .Change() in the same way <input> + @bind-Value does).
        var (ctx, settings, _, _) = Build();
        settings.RefreshIntervalSeconds = 86400;
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();

            // Submit the refresh form by triggering the form's submit event directly.
            var refreshForm = cut.Find("#refresh-86400").Closest("form")
                ?? throw new InvalidOperationException("Could not locate refresh form ancestor.");
            refreshForm.Submit();

            Assert.Equal(1, settings.SetRefreshIntervalSecondsCallCount);
            Assert.Equal(86400, settings.LastSetRefreshIntervalSeconds);
            Assert.Contains("Active enrichment interval saved.", cut.Markup);
        }
    }

    // ── Phase 10 Plan 10-02 — Local domain suffixes (D-04) ──────────────────

    [Fact]
    public void SuffixesCard_PreFillsFromCurrentLocalDomainSuffixes()
    {
        var (ctx, settings, _, _) = Build();
        settings.LocalDomainSuffixes = new[] { "michelecasa", "lab.local" };
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            var input = cut.Find("#suffixes-input");
            Assert.Equal("michelecasa, lab.local", input.GetAttribute("value"));
        }
    }

    [Fact]
    public async Task SuffixesCard_SubmitWithInvalidToken_RejectsAndShowsBanner()
    {
        var (ctx, settings, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            // Second token has a space — invalid per ^[A-Za-z0-9.\-]+$
            cut.Find("#suffixes-input").Change("foo, bar baz");

            // Submit the suffixes form by walking up from the input to its form ancestor.
            var suffixesInput = cut.Find("#suffixes-input");
            var suffixesForm = suffixesInput.Closest("form")
                ?? throw new InvalidOperationException("Could not locate suffixes form ancestor.");
            suffixesForm.Submit();

            // Error banner appears, success banner does not, and the service was NOT called.
            Assert.Contains("Invalid suffix", cut.Markup);
            Assert.DoesNotContain("Local domain suffixes saved.", cut.Markup);
            Assert.Equal(0, settings.SetLocalDomainSuffixesCallCount);
            Assert.Null(settings.LastSetLocalDomainSuffixes);
        }
    }

    [Fact]
    public async Task SuffixesCard_SubmitWithLeadingDot_RejectsAndShowsBanner()
    {
        var (ctx, settings, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            cut.Find("#suffixes-input").Change(".lab.local");

            // Submit the suffixes form by walking up from the input to its form ancestor.
            var suffixesInput = cut.Find("#suffixes-input");
            var suffixesForm = suffixesInput.Closest("form")
                ?? throw new InvalidOperationException("Could not locate suffixes form ancestor.");
            suffixesForm.Submit();

            Assert.Contains("must not start with a dot", cut.Markup);
            Assert.Equal(0, settings.SetLocalDomainSuffixesCallCount);
        }
    }

    [Fact]
    public async Task SuffixesCard_SubmitWithValidTokens_PersistsCleanedList()
    {
        var (ctx, settings, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            cut.Find("#suffixes-input").Change("valid, also-valid");

            // Submit the suffixes form by walking up from the input to its form ancestor.
            var suffixesInput = cut.Find("#suffixes-input");
            var suffixesForm = suffixesInput.Closest("form")
                ?? throw new InvalidOperationException("Could not locate suffixes form ancestor.");
            suffixesForm.Submit();

            Assert.Equal(1, settings.SetLocalDomainSuffixesCallCount);
            Assert.NotNull(settings.LastSetLocalDomainSuffixes);
            Assert.Equal(2, settings.LastSetLocalDomainSuffixes!.Count);
            Assert.Equal("valid", settings.LastSetLocalDomainSuffixes[0]);
            Assert.Equal("also-valid", settings.LastSetLocalDomainSuffixes[1]);
            Assert.Contains("Local domain suffixes saved.", cut.Markup);
        }
    }

    [Fact]
    public async Task SuffixesCard_SubmitWithEmptyInput_PersistsEmptyList()
    {
        // Operator clearing the suffixes — D-04: split-on-comma yields zero tokens, all valid by vacuous truth,
        // service is called with an empty list, persisted as JSON `[]`.
        var (ctx, settings, _, _) = Build();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            cut.Find("#suffixes-input").Change("");

            // Submit the suffixes form by walking up from the input to its form ancestor.
            var suffixesInput = cut.Find("#suffixes-input");
            var suffixesForm = suffixesInput.Closest("form")
                ?? throw new InvalidOperationException("Could not locate suffixes form ancestor.");
            suffixesForm.Submit();

            Assert.Equal(1, settings.SetLocalDomainSuffixesCallCount);
            Assert.NotNull(settings.LastSetLocalDomainSuffixes);
            Assert.Empty(settings.LastSetLocalDomainSuffixes!);
        }
    }

    // ── v0.11 ENRICH-04 — Run now button state machine (CONTEXT 14 §D-05/D-06) ──

    [Fact]
    public void RunNowButton_RendersInsideActiveEnrichmentCard_Enabled_OnPageLoad()
    {
        var (ctx, _, _, forceEnrichment) = BuildWithForceEnrichment();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();

            // Button exists, is enabled, and renders the "Run now" label (no spinner yet).
            var button = cut.Find("#run-now-button");
            Assert.False(button.HasAttribute("disabled"));
            Assert.Contains("Run now", button.TextContent);
            Assert.False(forceEnrichment.IsQueued);
        }
    }

    [Fact]
    public async Task RunNowButton_Click_InvokesForceEnrichmentRequest_AndShowsBanner()
    {
        var (ctx, _, _, forceEnrichment) = BuildWithForceEnrichment();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            Assert.False(forceEnrichment.IsQueued);

            await cut.Find("#run-now-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

            Assert.True(forceEnrichment.IsQueued);
            Assert.Contains("Enrichment queued", cut.Markup);
            Assert.Contains("will run on the next polling cycle", cut.Markup);
        }
    }

    // D-06 OR-condition arm: (cycleInFlight=true, IsQueued=false) → button disabled.
    [Fact]
    public async Task Settings_RunNow_DisabledDuring_CycleInFlight()
    {
        var (ctx, _, notifier, forceEnrichment) = BuildWithForceEnrichment();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            Assert.False(cut.Find("#run-now-button").HasAttribute("disabled"));

            // Simulate CycleStarted (cycle in flight) — button must disable.
            await notifier.RaiseCycleStartedAsync();
            cut.WaitForState(() => cut.Find("#run-now-button").HasAttribute("disabled"), TimeSpan.FromSeconds(2));
            Assert.True(cut.Find("#run-now-button").HasAttribute("disabled"));
            Assert.False(forceEnrichment.IsQueued);
            Assert.Contains("Queued…", cut.Markup);

            // After Cycled, button re-enables (since IsQueued was never set in this test).
            await notifier.RaiseAsync(new PollSnapshot(1, 1, 0, 0, DateTime.UtcNow));
            cut.WaitForState(() => !cut.Find("#run-now-button").HasAttribute("disabled"), TimeSpan.FromSeconds(2));
            Assert.False(cut.Find("#run-now-button").HasAttribute("disabled"));
        }
    }

    // D-06 OR-condition arm: (cycleInFlight=false, IsQueued=true) → button disabled.
    [Fact]
    public async Task Settings_RunNow_DisabledDuring_FlagQueued()
    {
        var (ctx, _, _, forceEnrichment) = BuildWithForceEnrichment();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();
            Assert.False(cut.Find("#run-now-button").HasAttribute("disabled"));

            // Click the button: ForceEnrichment.Request() sets IsQueued=true. The same click
            // also runs StateHasChanged via HandleRunNowAsync's flow (banner update), so the
            // disabled state is re-evaluated.
            await cut.Find("#run-now-button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
            cut.WaitForState(() => cut.Find("#run-now-button").HasAttribute("disabled"), TimeSpan.FromSeconds(2));
            Assert.True(cut.Find("#run-now-button").HasAttribute("disabled"));
            Assert.True(forceEnrichment.IsQueued);
        }
    }

    // D-06 OR-condition arm: (cycleInFlight=true, IsQueued=true) → button disabled.
    [Fact]
    public async Task Settings_RunNow_DisabledDuring_BothConditions()
    {
        var (ctx, _, notifier, forceEnrichment) = BuildWithForceEnrichment();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();

            // Set both arms.
            forceEnrichment.Request();
            await notifier.RaiseCycleStartedAsync();

            cut.WaitForState(() => cut.Find("#run-now-button").HasAttribute("disabled"), TimeSpan.FromSeconds(2));
            Assert.True(cut.Find("#run-now-button").HasAttribute("disabled"));
            Assert.True(forceEnrichment.IsQueued);
        }
    }

    // D-06 OR-condition arm: (cycleInFlight=false, IsQueued=false) → button enabled.
    [Fact]
    public void Settings_RunNow_Enabled_WhenIdleAndUnqueued()
    {
        var (ctx, _, _, forceEnrichment) = BuildWithForceEnrichment();
        using (ctx)
        {
            var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();

            // Initial state: no cycle in flight, no flag queued.
            Assert.False(forceEnrichment.IsQueued);
            Assert.False(cut.Find("#run-now-button").HasAttribute("disabled"));
            Assert.Contains("Run now", cut.Find("#run-now-button").TextContent);
        }
    }
}
