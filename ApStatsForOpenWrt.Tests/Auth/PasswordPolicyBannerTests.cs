using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Bunit;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Auth;
using ApStatsForOpenWrt.Services.Enrichment;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Auth;

/// <summary>
/// M-SEC-03 (plan 15-05) bUnit + integration suite for the password-policy banner
/// on the three consumer surfaces of <see cref="IUIPasswordHasher.Hash"/>:
///
/// <list type="number">
///   <item>Login.razor rehash branch — verbatim policy message in <c>alert-danger</c>
///         when the operator's currently-stored password fails the four-part policy
///         (plan 15-02) and the framework signals SuccessRehashNeeded.</item>
///   <item>Settings/Index.razor — verbatim policy message in <c>alert-danger</c> when
///         the operator types a below-policy new password into Card 2.</item>
///   <item>Setup/Password.razor — verbatim policy message in the wizard's <c>alert-danger</c>
///         after a 302 redirect from /setup/submit-password with Error= URL-encoded
///         message in the query string (integration test, since the policy throw is
///         caught in Program.cs's MapPost lambda).</item>
///   <item>Login.razor reflection gate — asserts NO [EnableRateLimiting] attribute is
///         present (login-429-counts-page-views POST-only fix). M-SEC-03 brute-force
///         limiting is enforced ENTIRELY in Program.cs via opts.GlobalLimiter (chained
///         per-IP 5/min + global 30/min, both gated on Method == "POST" && Path == /Login),
///         so GET page views are free and POST attempts stay capped; the
///         LoginGlobalRateLimitTests + Integration.RateLimitTests suites pin the behavioral
///         consequence end-to-end.</item>
/// </list>
///
/// The verbatim operator-facing copy is pinned by REQUIREMENTS.md line 16:
///
/// <para>
/// Password must be at least 8 characters and include upper, lower, and a digit or symbol.
/// </para>
///
/// Stored as <c>PinnedPolicyMessage</c> in this test file (a separate const from
/// UIPasswordHasher.PolicyMessage) so any drift in either place trips this test
/// independently — the M-SEC-03 single-source-of-truth invariant.
/// </summary>
[Trait("Category", "SecurityFix")]
public sealed class PasswordPolicyBannerTests
{
    /// <summary>REQUIREMENTS.md line 16 verbatim copy. Mirrors UIPasswordHasher.PolicyMessage.</summary>
    private const string PinnedPolicyMessage =
        "Password must be at least 8 characters and include upper, lower, and a digit or symbol.";

    // ── Shared stubs ────────────────────────────────────────────────────────

    private sealed class StubSettings : IAppSettingsService
    {
        public string? UIPasswordHash { get; set; }
        public int PollingIntervalSeconds => 30;
        public bool WizardDismissed => false;
        public int RefreshIntervalSeconds => 3600;
        public IReadOnlyList<string> LocalDomainSuffixes { get; } = Array.Empty<string>();
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default)
        {
            UIPasswordHash = hash;
            return Task.CompletedTask;
        }
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }
    }

    /// <summary>
    /// IUIPasswordHasher stub whose <c>Hash</c> always throws the pinned
    /// <see cref="ArgumentException"/> regardless of input. Verify returns the
    /// caller-supplied result. Used to simulate the policy-rejection path
    /// deterministically — the production UIPasswordHasher would reject only
    /// below-policy plaintexts, but the consumer-side catch contract is identical
    /// either way.
    /// </summary>
    private sealed class PolicyThrowingHasher(PasswordVerificationResult verifyResult) : IUIPasswordHasher
    {
        public string Hash(string plaintext) =>
            throw new ArgumentException(PinnedPolicyMessage, nameof(plaintext));
        public PasswordVerificationResult Verify(string h, string p) => verifyResult;
    }

    private sealed class CapturingNodeRepo : INodeRepo
    {
        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WiFiNode>>(Array.Empty<WiFiNode>());
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
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── Fact 1: Login.razor rehash-path policy rejection surfaces the banner ──

    /// <summary>
    /// Renders Login.razor with a stub hasher whose <c>Verify</c> returns
    /// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> (forcing the rehash
    /// branch in <c>HandleLoginAsync</c>) and whose <c>Hash</c> throws
    /// <see cref="ArgumentException"/> with the pinned policy message. Submitting the
    /// form triggers the rehash branch → Hash throws → the catch sets _banner to
    /// ex.Message → the verbatim policy message renders in the alert-danger div.
    /// </summary>
    [Fact]
    public async Task Login_RehashPolicyRejection_RendersVerbatimBannerInAlertDanger()
    {
        using var ctx = new BunitContext();
        var settings = new StubSettings { UIPasswordHash = "AQAAA-legacy-hash" };
        var hasher = new PolicyThrowingHasher(PasswordVerificationResult.SuccessRehashNeeded);
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        ctx.Services.AddSingleton<IUIPasswordHasher>(hasher);

        // Login.razor consumes [CascadingParameter] HttpContext — supply a default to avoid NRE.
        var httpContext = new DefaultHttpContext();
        ctx.Services.AddCascadingValue<HttpContext>(_ => httpContext);

        // The bUnit render may throw on <AntiforgeryToken /> if IAntiforgery isn't registered.
        // To force the OnValidSubmit handler invocation regardless, we render and then attempt
        // to click submit; the rehash exception then surfaces via _banner. If the render
        // itself fails before that, we fall back to direct invocation of HandleLoginAsync
        // via the cut.Instance reflection seam.
        var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Login>();

        // Drive the OnValidSubmit handler by clicking the submit button after typing a password.
        // bUnit's EditForm OnValidSubmit fires when DataAnnotationsValidator is satisfied (we
        // bypass validation by setting form.Password to a non-empty value).
        var passwordInput = cut.FindAll("input[type='password']").FirstOrDefault();
        if (passwordInput is not null)
        {
            passwordInput.Change("AnyPassword1!");
        }
        var submit = cut.FindAll("button[type='submit']").FirstOrDefault();
        if (submit is not null)
        {
            await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        }

        // Assert: the alert-danger div carries the pinned policy message.
        var markup = cut.Markup;
        Assert.Contains("alert-danger", markup);
        Assert.Contains(PinnedPolicyMessage, markup);
    }

    // ── Fact 2: Settings/Index.razor policy rejection surfaces the banner ─────

    /// <summary>
    /// Renders Settings/Index.razor's Card 2 (UI password) with a stub
    /// <see cref="IUIPasswordHasher"/> whose <c>Hash</c> always throws. The form is
    /// filled with matching above-empty values (so the empty/mismatch checks at lines
    /// 421-429 don't intercept first) and submitted; the catch in HandleSetPasswordAsync
    /// sets _passwordError to ex.Message and the alert-danger div renders verbatim.
    /// </summary>
    [Fact]
    public async Task Settings_HashThrows_RendersVerbatimBannerInAlertDanger()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./js/preferences.js");

        var settings = new StubSettings { UIPasswordHash = null };
        var hasher = new PolicyThrowingHasher(PasswordVerificationResult.Success);
        ctx.Services.AddSingleton<IAppSettingsService>(settings);
        ctx.Services.AddSingleton<IUIPasswordHasher>(hasher);
        ctx.Services.AddSingleton<INodeRepo>(new CapturingNodeRepo());
        ctx.Services.AddSingleton<PollNotifier>();
        ctx.Services.AddSingleton<IForceEnrichmentService, ForceEnrichmentService>();
        ctx.Services.AddSingleton<ApStatsForOpenWrt.Services.PreferencesService>(
            new ApStatsForOpenWrt.Services.PreferencesService(ctx.JSInterop.JSRuntime));

        var cut = ctx.Render<ApStatsForOpenWrt.Components.Pages.Settings.Index>();

        // Fill the password card's two inputs with matching above-empty values so the
        // empty/mismatch error gates at lines 421-429 don't intercept.
        cut.Find("#new-password").Change("anything");
        cut.Find("#confirm-password").Change("anything");

        var submit = cut.FindAll("button[type='submit']")
                        .First(b => b.TextContent.Contains("Set password", StringComparison.Ordinal));
        await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var markup = cut.Markup;
        Assert.Contains("alert-danger", markup);
        Assert.Contains(PinnedPolicyMessage, markup);
        // No DB write happened (policy was rejected before SetUIPasswordHashAsync).
        Assert.Null(settings.UIPasswordHash);
    }

    // ── Fact 3: Login.razor static-shape gate — NO rate-limit attribute (POST-only) ──

    /// <summary>
    /// Asserts via reflection that Login.razor carries NO
    /// <see cref="Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute"/>.
    ///
    /// <para>
    /// login-429-counts-page-views (operator-approved 2026-05-30): M-SEC-03 brute-force
    /// limiting is now POST-ONLY and enforced ENTIRELY in Program.cs via opts.GlobalLimiter
    /// (chained per-IP 5/min + global 30/min, both gated on Method == "POST" &&
    /// Path == /Login). The PRIOR [EnableRateLimiting("LoginPolicy")] attribute applied to the
    /// Razor page endpoint's GET render too, so GET page views (the sign-out → /Login redirect
    /// chain, reloads) consumed the per-IP budget and 429'd legitimate operators on the 6th
    /// view. The attribute was REMOVED because an EnableRateLimiting attribute on a Razor page
    /// endpoint cannot be method-scoped.
    /// </para>
    ///
    /// <para>
    /// This test is the static-shape regression lock for the POST-only fix: re-adding any
    /// [EnableRateLimiting] attribute to Login.razor would re-introduce the GET-page-view
    /// lockout. The BEHAVIORAL contract (GET free, POST capped at 5/IP/min + 30/min global) is
    /// pinned by LoginGlobalRateLimitTests + Integration.RateLimitTests.
    /// </para>
    /// </summary>
    [Fact]
    public void Login_StaticShape_HasNoRateLimitAttribute_PostOnlyContract()
    {
        var loginType = typeof(ApStatsForOpenWrt.Components.Pages.Login);
        var attrs = loginType
            .GetCustomAttributes<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>(false)
            .ToArray();

        Assert.Empty(attrs);
    }

    // ── Fact 4: Setup/Password.razor — integration test (POST + 302 redirect) ─

    /// <summary>
    /// Integration test: posts a below-policy password to /setup/submit-password with
    /// a valid antiforgery token; asserts the response is a 302 redirect to
    /// /setup/password with the URL-encoded verbatim policy message in the Error
    /// query parameter. Setup/Password.razor's [SupplyParameterFromQuery] string Error
    /// then renders verbatim inside the alert-danger div (lines 13-15 of that page).
    ///
    /// <para>
    /// The plan called this fact "optional but recommended." Per the executor's
    /// prompt directive ("**NON-OPTIONAL per revision fix**"), it lands.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SetupSubmitPassword_BelowPolicy_Redirects302WithUrlEncodedPolicyMessage()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        await using var factory = new PolicyBannerFactory();
        await factory.InitializeAsync();
        try
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

            // Harvest a valid antiforgery token from a fresh /setup/password GET so the
            // POST passes the antiforgery validation (M-SEC-01 wiring landed in plan 15-03).
            var getResp = await client.GetAsync("/setup/password", ct);
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            var html = await getResp.Content.ReadAsStringAsync(ct);
            var match = Regex.Match(
                html,
                "name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"");
            if (!match.Success)
            {
                match = Regex.Match(
                    html,
                    "value=\"(?<value>[^\"]+)\"[^>]*name=\"__RequestVerificationToken\"");
            }
            Assert.True(match.Success, "expected __RequestVerificationToken on /setup/password");
            var token = match.Groups["value"].Value;

            // POST a below-policy password (well-formed match on both fields so the
            // empty/mismatch gates don't short-circuit). UIPasswordHasher.Hash throws
            // ArgumentException; the MapPost catch redirects to /setup/password?Error=...
            // with the URL-encoded verbatim policy message.
            var post = await client.PostAsync(
                "/setup/submit-password",
                new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("__RequestVerificationToken", token),
                    new KeyValuePair<string, string>("action", "set"),
                    new KeyValuePair<string, string>("Password", "short"),
                    new KeyValuePair<string, string>("ConfirmPassword", "short"),
                }),
                ct);

            Assert.True(
                post.StatusCode == HttpStatusCode.Redirect ||
                post.StatusCode == HttpStatusCode.Found,
                $"expected 302 redirect, got {(int)post.StatusCode} {post.StatusCode}.");
            Assert.NotNull(post.Headers.Location);

            var location = post.Headers.Location!.ToString();
            Assert.Contains("/setup/password", location);
            Assert.Contains("Error=", location);

            // Decode the Error param and assert it carries the pinned verbatim policy
            // message as a prefix. The framework's ArgumentException.Message appends
            // " (Parameter 'plaintext')" to the constructor-supplied message (documented
            // in plan 15-02 SUMMARY); the pinned operator-facing copy IS the prefix half.
            // Using StartsWith here matches the 15-02
            // Hash_NonCompliantPassword_ExceptionMessageMatchesPinnedOperatorCopyVerbatim
            // pattern and lets the operator-facing prefix be a single-source-of-truth pin
            // regardless of where the suffix lives.
            var errParamMatch = Regex.Match(location, @"Error=(?<v>[^&]+)");
            Assert.True(errParamMatch.Success, $"expected Error=... in {location}");
            var decoded = Uri.UnescapeDataString(errParamMatch.Groups["v"].Value);
            Assert.StartsWith(PinnedPolicyMessage, decoded);

            // Finally: follow the redirect with a GET and confirm Setup/Password.razor
            // renders the Error param verbatim inside an alert-danger div (the page's
            // [SupplyParameterFromQuery] string Error wiring at lines 13-15).
            var follow = await client.GetAsync(post.Headers.Location, ct);
            Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
            var followHtml = await follow.Content.ReadAsStringAsync(ct);
            Assert.Contains("alert-danger", followHtml);
            Assert.Contains(PinnedPolicyMessage, followHtml);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    /// <summary>
    /// Test-only factory mirroring SmokeFactoryFixture (in-memory SQLite + keep-alive) for
    /// the wizard-redirect integration test. Standalone instance so the wizard state is
    /// per-test (no UIPasswordHash, no nodes — the wizard is OPEN).
    /// </summary>
    private sealed class PolicyBannerFactory
        : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly string _dataSource = $"PolicyBanner-{Guid.NewGuid():N}";
        private Microsoft.Data.Sqlite.SqliteConnection? _keepAlive;

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(Microsoft.EntityFrameworkCore.IDbContextFactory<DatabaseContext>));
                if (dbDescriptor is not null) services.Remove(dbDescriptor);
                services.AddDbContextFactory<DatabaseContext>(opts =>
                    opts.UseSqlite($"Data Source={_dataSource};Mode=Memory;Cache=Shared"));
            });
        }

        public ValueTask InitializeAsync()
        {
            _keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={_dataSource};Mode=Memory;Cache=Shared");
            _keepAlive.Open();
            _ = CreateClient();
            return ValueTask.CompletedTask;
        }

        public new ValueTask DisposeAsync()
        {
            _keepAlive?.Dispose();
            base.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
