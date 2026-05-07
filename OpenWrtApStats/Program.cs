using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Bootstrap;
using OpenWrtApStats.Services.Onboarding;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Polling.Ssh;
using OpenWrtApStats.Services.Settings;
using Serilog;
using Serilog.Formatting.Compact;

// 1. Bootstrap logger — captures startup exceptions before host is built (RESEARCH Pattern 4).
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("OpenWrtApStats starting");

    var builder = WebApplication.CreateBuilder(args);

    // DATA_ROOT / LOGS_ROOT env vars allow local Windows dev to redirect storage without
    // touching Docker deployment (unset → /data and /logs as before, D-13 still holds for prod).
    var dataRoot = Environment.GetEnvironmentVariable("DATA_ROOT") ?? "/data";
    var logsRoot = Environment.GetEnvironmentVariable("LOGS_ROOT") ?? "/logs";

    // 2. Promote Serilog to the host's primary logger.
    //    Two-stage init means appsettings.json overrides are applied here while
    //    the bootstrap logger above still catches any earlier startup exceptions.
    builder.Host.UseSerilog((ctx, services, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        // Phase 6 D-10 — drop /health from the request log entirely. UseSerilogRequestLogging
        // attaches RequestPath as a property; ByExcluding evaluates against e.Properties.
        // The doubled-quote literal `"\"/health\""` is intentional — Serilog stores string
        // ScalarValue.ToString() with surrounding double quotes (quirk of LogEventPropertyValue
        // formatting). Runtime regression coverage: HealthSerilogFilterTests via TestCorrelator.
        // The filter is on RequestPath only; logs emitted FROM the health checks themselves
        // (DbHealthCheck / DataProtectionHealthCheck) are NOT excluded — they have a different
        // logger category, not a RequestPath property.
        .Filter.ByExcluding(e =>
            e.Properties.TryGetValue("RequestPath", out var p) &&
            p.ToString() == "\"/health\"")
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console()
        .WriteTo.Async(a => a.File(
            formatter: new CompactJsonFormatter(),
            path: $"{logsRoot}/openwrt-ap-stats-.log",       // D-13: no appsettings indirection; override via LOGS_ROOT env var
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,                        // INF-05 — REQUIREMENTS-pinned 7 wins over STACK.md's 14
            shared: false)));

    // 3. EF Core — factory only, NEVER scoped DbContext (PER-05; PITFALL: captive dependency).
    //    Pooling=True for connection reuse; Default Timeout=5 -> PRAGMA busy_timeout=5000
    //    so transient SQLITE_BUSY (file-lock) is retried instead of erroring.
    //    NO Cache=Shared on a file DB: shared cache forces table-level cache locks that
    //    bypass WAL's file-level reader/writer concurrency, producing SQLITE_LOCKED (error 6)
    //    when the orchestrator transaction holds WiFiNodes while the dashboard / health checks
    //    read it. WAL itself is enabled by MigrationsHostedService via PRAGMA journal_mode=WAL
    //    and is persisted on the DB file.
    builder.Services.AddDbContextFactory<DatabaseContext>(options =>
        options.UseSqlite($"Data Source={dataRoot}/OpenWRT-AP-Stats.db;Pooling=True;Default Timeout=5"));

    // 4. ASP.NET Data Protection — file-system key ring + verbatim application name (D-02 / SEC-03).
    //    On Linux/WSL2 keys are NOT encrypted at rest (DPAPI is Windows-only) —
    //    the /data volume is the encryption boundary (acceptable per threat model).
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo($"{dataRoot}/keys"))  // SEC-02
        .SetApplicationName("OpenWrtApStats")                            // SEC-03, D-02 — VERBATIM string
        .SetDefaultKeyLifetime(TimeSpan.FromDays(90));                   // framework default — RESEARCH Pattern 3 §3

    // 5. Repositories (Transient — RESEARCH lifetimes table) + Settings service (Singleton — D-11).
    //    Repository registrations come first because AppSettingsService consumes IAppSettingsRepo
    //    (PER-04 — only repositories under OpenWrtApStats.Data.Repositories may inject IDbContextFactory).
    builder.Services.AddTransient<INodeRepo,        NodeRepo>();
    builder.Services.AddTransient<INetworkRepo,     NetworkRepo>();
    builder.Services.AddTransient<IStationRepo,     StationRepo>();
    builder.Services.AddTransient<IAppSettingsRepo, AppSettingsRepo>();
    builder.Services.AddSingleton<IAppSettingsService, AppSettingsService>();

    // 5b. Polling subsystem (Phase 2). PollOrchestrator is the PER-04 carve-out #3 — see ArchitectureTests.
    //     ISshSession is transient (one fresh session per poll, per Pitfall 5).
    //     NodePoller is constructed per-cycle via the Func<WiFiNode, CancellationToken, Task<NodeProbeResult>>
    //     probe factory so the orchestrator never holds a baked-in WiFiNode reference.
    //     PollNotifier is registered as a Singleton even though Phase 2 has no subscribers — Phase 5
    //     adds Dashboard subscribers; raising the event from day 1 makes Phase 5 additive only.
    builder.Services.AddSingleton<OpenWrtAdapterCache>();
    builder.Services.AddSingleton<PollNotifier>();
    // Phase 8 D-09: PreferencesService is Singleton — wraps wwwroot/js/preferences.js
    // (per-browser localStorage) with a thin async interop layer + cached
    // IsNotificationsEnabled hot-path. Registered AFTER PollNotifier per CONTEXT.md
    // integration-points, BEFORE Wave 2 services (ToastService, EventDetectionService)
    // which will land in subsequent plans.
    builder.Services.AddSingleton<OpenWrtApStats.Services.PreferencesService>();
    // Phase 8 Plan 08-06 D-10 — INotificationsGate forwards to the same PreferencesService
    // Singleton. ToastService consumes the interface (NOT PreferencesService directly) so
    // its tests can substitute a FakeGate without bringing the JS interop machinery along.
    builder.Services.AddSingleton<OpenWrtApStats.Services.INotificationsGate>(sp =>
        sp.GetRequiredService<OpenWrtApStats.Services.PreferencesService>());
    // Phase 8 Plan 08-06 D-08 — ToastService is the in-process event bus consumed by
    // ToastHost (Wave 3 mount in MainLayout) and emitted to by EventDetectionService.
    // Registered as Singleton AFTER PreferencesService + INotificationsGate so the DI
    // graph resolves cleanly at startup.
    builder.Services.AddSingleton<OpenWrtApStats.Services.ToastService>();
    // Phase 8 Plan 08-07 D-08/D-11 — EventDetectionService subscribes to PollNotifier.Cycled
    // in its constructor. Singletons are constructed lazily on first resolve, so we register
    // an eager-start IHostedService shim that resolves the Singleton at app startup; without
    // it, a session with no UI activity would never instantiate the detector. Ordered AFTER
    // ToastService (its dependency) and BEFORE the polling pipeline registrations so the DI
    // graph remains acyclic.
    builder.Services.AddSingleton<OpenWrtApStats.Services.EventDetectionService>();
    builder.Services.AddSingleton<PollOrchestrator>();
    builder.Services.AddTransient<ISshSession, RealSshSession>();
    // NodePoller is INTENTIONALLY NOT in the DI container. Its primary-ctor takes
    // a runtime WiFiNode positional arg that is not a DI service; registering it
    // would cause WebApplicationBuilder's eager service-provider validation
    // (Development default) to abort with "Unable to resolve WiFiNode" — see
    // GAP-02-A in 02-HUMAN-UAT.md. NodePoller is constructed per-cycle via
    // ActivatorUtilities.CreateInstance below, which works without registration.
    // B3 LOCK -- probe factory is the SOLE way the orchestrator drives a poll. No
    // Func<WiFiNode, NodePoller> exists in DI; PollOrchestrator's primary-ctor takes this Func
    // directly, never a NodePoller factory.
    builder.Services.AddSingleton<Func<WiFiNode, CancellationToken, Task<NodeProbeResult>>>(sp => (node, ct) =>
    {
        var poller = ActivatorUtilities.CreateInstance<NodePoller>(sp, node);
        return poller.PollAsync(ct);
    });
    // PollingHostedService consumes a runCycleAsync delegate (not PollOrchestrator directly) so the
    // host stays unit-testable. The runtime delegate resolves PollOrchestrator from the host scope.
    builder.Services.AddSingleton<Func<CancellationToken, Task>>(sp => ct =>
        sp.GetRequiredService<PollOrchestrator>().RunCycleAsync(ct));

    // 5c. Phase 3 — Cookie auth + conditional fallback policy + rate limiter +
    //     ForwardedHeaders + UI password hasher + OUI lookup + UI services.
    //     RESEARCH §Code Examples §1; Pitfalls 3 + 4 + 8 + 9 binding.
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(opts =>
    {
        opts.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                              | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        opts.KnownIPNetworks.Clear();  // D-04 — operator's job to harden if exposed publicly (KnownNetworks is deprecated in .NET 10 — ASPDEPR005)
        opts.KnownProxies.Clear();
    });

    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.Cookie.Name = "OpenWrtApStats.Auth";   // D-05 verbatim
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
            o.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
            o.LoginPath = "/Login";
            o.LogoutPath = "/Logout";
            o.ExpireTimeSpan = TimeSpan.FromDays(7);
            o.SlidingExpiration = true;
        });

    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddAuthorization(o =>
    {
        o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .AddRequirements(new OpenWrtApStats.Services.Auth.ConditionalAuthRequirement())
            .Build();
    });
    builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
        OpenWrtApStats.Services.Auth.ConditionalAuthHandler>();

    builder.Services.AddRateLimiter(opts =>
    {
        opts.AddPolicy("LoginPolicy", ctx =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,                                    // SEC-06 / D-03
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

    // SEC-04 password hasher; DASH-13 OUI singleton; D-07 + D-12 UI services.
    builder.Services.AddTransient<OpenWrtApStats.Services.Auth.IUIPasswordHasher,
                                  OpenWrtApStats.Services.Auth.UIPasswordHasher>();
    builder.Services.AddSingleton<OpenWrtApStats.Services.Vendor.IOuiLookup,
                                  OpenWrtApStats.Services.Vendor.OuiLookup>();
    builder.Services.AddTransient<OpenWrtApStats.Services.Nodes.ITestConnectionService,
                                  OpenWrtApStats.Services.Nodes.TestConnectionService>();
    builder.Services.AddTransient<OpenWrtApStats.Services.Nodes.IWlanDiscoveryService,
                                  OpenWrtApStats.Services.Nodes.WlanDiscoveryService>();

    // Phase 6 INF-06 / POLL-05 / POLL-06 — real visitor gate.
    // CircuitTracker holds the Interlocked counter + re-armable TCS; CircuitTrackerHandler is
    // scoped (one per circuit) and forwards open/close to the singleton tracker; CircuitTrackerGate
    // is the singleton ICircuitGate forwarding adapter consumed by LivePausedBadge / PollCountdown
    // (Pattern 2 subscribers — Plans 05-03 / 05-05 unchanged).
    // CircuitOptions.DisconnectedCircuitRetentionPeriod = 3 min (D-03 — hard-coded, NOT App Settings
    // in v1; total grace = 3 min + 30s in CircuitTracker semantics).
    builder.Services.AddSingleton<OpenWrtApStats.Services.Gating.CircuitTracker>();
    builder.Services.AddSingleton<OpenWrtApStats.Services.Gating.ICircuitGate,
                                  OpenWrtApStats.Services.Gating.CircuitTrackerGate>();
    builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler,
                               OpenWrtApStats.Services.Gating.CircuitTrackerHandler>();
    builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o =>
    {
        o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);  // D-03
    });

    // Phase 6 INF-06 / D-08 — request-time health checks. Two named checks; status code
    // (200 / 503) is auto-derived from HealthReport.Status by MapHealthChecks below.
    builder.Services.AddHealthChecks()
        .AddCheck<OpenWrtApStats.Services.Health.DbHealthCheck>("db")
        .AddCheck<OpenWrtApStats.Services.Health.DataProtectionHealthCheck>("dataProtection");

    // 6. Bootstrap order (registration order = StartAsync order — Pitfall 10):
    //    Writability -> Migrations -> D-12 probe -> AppSettings warm-up -> Polling.
    //    Phase 7 prepends WritabilityCheckHostedService so /data + /logs writability is
    //    fail-fast verified BEFORE EF migrations run (INF-07; D-01). Phase 2 closed Phase 1's
    //    "DO NOT add a fourth here" reservation by adding PollingHostedService LAST.
    builder.Services.AddHostedService<WritabilityCheckHostedService>();     // 1st — Phase 7 (INF-07)
    builder.Services.AddHostedService<MigrationsHostedService>();           // 2nd
    // Registered after MigrationsHostedService (schema must exist before the D-12 probe queries
    // WiFiNodes via INodeRepo) and before AppSettingsBootstrapHostedService (must fail-fast
    // before settings warm-up so a key-ring/DB mismatch surfaces BEFORE any settings consumer
    // observes a partially-bootstrapped app). D-12 / Pitfall 10.
    builder.Services.AddHostedService<DataProtectionHealthHostedService>(); // 3rd — D-12
    builder.Services.AddHostedService<AppSettingsBootstrapHostedService>(); // 4th
    builder.Services.AddHostedService<PollingHostedService>();              // 5th — Phase 2
    // Phase 8 Plan 08-07 — eager-start shim that forces the EventDetectionService Singleton
    // to instantiate (and therefore subscribe to PollNotifier.Cycled) at app startup, before
    // any UI page is opened. Order is irrelevant within the hosted-service group: this shim
    // does no work in StartAsync — DI resolution alone wires up the Cycled subscription.
    builder.Services.AddHostedService<OpenWrtApStats.Services.EventDetectionEagerStartHostedService>(); // 6th — Phase 8

    // 7. Razor components (Phase 3 starts here). Auth + ratelimit + forwarded headers are
    //    added in Plan 03-03; this Plan 03-01 only lights up the Razor seam so the smoke
    //    spike (AdminLteAssetSmokeTests) can boot the host without 404s on the placeholder
    //    Index.razor route. D-06: AddInteractiveServerComponents + AddInteractiveServerRenderMode.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    var app = builder.Build();

    // Middleware pipeline — order is LOAD-BEARING (RESEARCH §Pitfall 4):
    // UseForwardedHeaders MUST run before UseAuthentication (so cookie sees the rewritten
    // RemoteIpAddress) AND before UseRateLimiter (so the partition key is correct).
    app.UseForwardedHeaders();
    app.UseSerilogRequestLogging();
    app.UseStaticFiles();           // serves /lib/adminlte/* /lib/bootstrap-icons/* /lib/source-sans-3/* /app.css /favicon.ico
    app.UseAntiforgery();
    // D-07: Auto-redirect first-run operator to the onboarding wizard.
    // Placed after UseStaticFiles (assets bypass it) and after UseAntiforgery,
    // but BEFORE UseAuthentication so anonymous GET / is handled before the auth pipeline.
    app.UseMiddleware<WizardRedirectMiddleware>();

    // D-07 / D-08 / W4 — /health response writer + FormatAge helper. Hand-written
    // static local functions defined in top-level statements scope, lexically ABOVE the
    // app.MapHealthChecks call below so the method-group reference resolves.
    // Shape: { status, components: { db, dataProtection }, lastPollAge }.
    // gateState is INTENTIONALLY OMITTED (D-07 — UI concern, not /health concern).
    static async Task WriteJsonResponse(
        HttpContext ctx,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";

        // D-09 — read PollOrchestrator.LastSuccessfulCycleUtc via Volatile.Read (non-blocking).
        var orchestrator = ctx.RequestServices
            .GetRequiredService<OpenWrtApStats.Services.Polling.PollOrchestrator>();
        var lastSuccess = orchestrator.LastSuccessfulCycleUtc;
        string? lastPollAge = lastSuccess == default
            ? null
            : FormatAge(DateTime.UtcNow - lastSuccess);

        var payload = new
        {
            status     = report.Status.ToString(),                           // "Healthy" | "Unhealthy"
            components = report.Entries.ToDictionary(
                             e => e.Key,
                             e => e.Value.Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy
                                 ? "ok" : "fail"),
            lastPollAge,
        };

        await System.Text.Json.JsonSerializer.SerializeAsync(ctx.Response.Body, payload);
    }

    static string FormatAge(TimeSpan t)
    {
        if (t.TotalSeconds < 0) t = TimeSpan.Zero;     // defensive: clock skew shouldn't render "-3s".
        return t.TotalMinutes < 1
            ? $"{(int)t.TotalSeconds}s"
            : $"{(int)t.TotalMinutes}m{t.Seconds:D2}s";
    }

    // D-11 — /health is reachable regardless of UI-password state (ROADMAP success #3).
    // Mapped BEFORE UseAuthentication so the source-order discipline matches CONTEXT.md (Phase 6
    // documented the lexical placement as the contract). The endpoint metadata also carries
    // .AllowAnonymous() so the AuthorizationMiddleware fallback policy (which engages once a
    // UI password is set) does not redirect /health to /Login — endpoint matching happens during
    // the implicit UseRouting at WebApplication start, so middleware after UseRouting still runs
    // for matched endpoints. Without AllowAnonymous, the cookie middleware's OnRedirectToLogin
    // would fire on the protected endpoint even though MapHealthChecks itself is short-circuit.
    // Status code (200 / 503) auto-derived from HealthReport.Status by the framework.
    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = WriteJsonResponse,
    }).AllowAnonymous();

    app.UseAuthentication();        // reads "OpenWrtApStats.Auth" cookie
    app.UseAuthorization();         // applies fallback ConditionalAuthRequirement
    app.UseRateLimiter();           // sees rewritten IP (Pitfall 4)

    // /Logout — minimal endpoint; SignOut + redirect (RESEARCH §Open Question #1 recommendation).
    // The user-menu form posts the antiforgery token; we validate it by default.
    app.MapPost("/Logout", async (HttpContext ctx) =>
    {
        await ctx.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
        return Microsoft.AspNetCore.Http.Results.Redirect("/");
    });

    // D-08: "Skip for now" POST handler. Sets WizardDismissed = true and redirects to dashboard.
    // Works from all three wizard steps (even Step 1 static SSR via the SetupLayout form POST).
    app.MapPost("/setup/skip", async (
        IAppSettingsService settings,
        HttpContext ctx) =>
    {
        await settings.SetWizardDismissedAsync(true, ctx.RequestAborted);
        return Microsoft.AspNetCore.Http.Results.Redirect("/");
    }).AllowAnonymous();

    // Step 1 password submission — auth needs the HTTP response pipeline, not the Blazor circuit.
    // Password.razor is a normal InteractiveServer page; this endpoint handles the POST.
    app.MapPost("/setup/submit-password", async (
        OpenWrtApStats.Services.Auth.IUIPasswordHasher hasher,
        IAppSettingsService settings,
        HttpContext ctx) =>
    {
        var form   = ctx.Request.Form;
        var action = form["action"].ToString();

        if (action != "set")
            return Microsoft.AspNetCore.Http.Results.Redirect("/setup/nodes");

        var password = form["Password"].ToString();
        var confirm  = form["ConfirmPassword"].ToString();

        if (string.IsNullOrEmpty(password))
            return Microsoft.AspNetCore.Http.Results.Redirect(
                "/setup/password?Error=" + Uri.EscapeDataString("Password is required."));

        if (password != confirm)
            return Microsoft.AspNetCore.Http.Results.Redirect(
                "/setup/password?Error=" + Uri.EscapeDataString("Passwords do not match."));

        var hash = hasher.Hash(password);
        await settings.SetUIPasswordHashAsync(hash, ctx.RequestAborted);

        var claims   = new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "operator") };
        var identity = new System.Security.Claims.ClaimsIdentity(
            claims,
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
            new System.Security.Claims.ClaimsPrincipal(identity));

        return Microsoft.AspNetCore.Http.Results.Redirect("/setup/nodes");
    }).AllowAnonymous();   // wizard skip must be reachable regardless of auth state

    app.MapRazorComponents<OpenWrtApStats.Components.App>()
        .AddInteractiveServerRenderMode();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "OpenWrtApStats terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

// Plan 03-01 Task 3: WebApplicationFactory<Program> needs Program to be a named, accessible
// type. Top-level statements emit it as internal-partial; making it public-partial here is
// the canonical opt-in for ASP.NET Core integration testing per
// https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests.
public partial class Program { }
