using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Bootstrap;
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

    // 2. Promote Serilog to the host's primary logger.
    //    Two-stage init means appsettings.json overrides are applied here while
    //    the bootstrap logger above still catches any earlier startup exceptions.
    builder.Host.UseSerilog((ctx, services, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console()
        .WriteTo.Async(a => a.File(
            formatter: new CompactJsonFormatter(),
            path: "/logs/openwrt-ap-stats-.log",            // hardcoded per D-13; trailing hyphen mandatory (INF-04, INF-05)
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,                       // INF-05 — REQUIREMENTS-pinned 7 wins over STACK.md's 14
            shared: false)));

    // 3. EF Core — factory only, NEVER scoped DbContext (PER-05; PITFALL: captive dependency).
    builder.Services.AddDbContextFactory<DatabaseContext>(options =>
        options.UseSqlite("Data Source=/data/OpenWRT-AP-Stats.db;Cache=Shared;Pooling=True"));

    // 4. ASP.NET Data Protection — file-system key ring + verbatim application name (D-02 / SEC-03).
    //    On Linux/WSL2 keys are NOT encrypted at rest (DPAPI is Windows-only) —
    //    the /data volume is the encryption boundary (acceptable per threat model).
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo("/data/keys"))   // SEC-02
        .SetApplicationName("OpenWrtApStats")                       // SEC-03, D-02 — VERBATIM string
        .SetDefaultKeyLifetime(TimeSpan.FromDays(90));              // framework default — RESEARCH Pattern 3 §3

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

    // 6. Bootstrap order (registration order = StartAsync order — Pitfall 10):
    //    Migrations -> D-12 probe -> AppSettings warm-up -> Polling.
    //    Phase 2 closes Phase 1's "DO NOT add a fourth here" reservation by adding
    //    PollingHostedService LAST.
    builder.Services.AddHostedService<MigrationsHostedService>();
    // Registered after MigrationsHostedService (schema must exist before the D-12 probe queries
    // WiFiNodes via INodeRepo) and before AppSettingsBootstrapHostedService (must fail-fast
    // before settings warm-up so a key-ring/DB mismatch surfaces BEFORE any settings consumer
    // observes a partially-bootstrapped app). D-12 / Pitfall 10.
    builder.Services.AddHostedService<DataProtectionHealthHostedService>(); // D-12
    builder.Services.AddHostedService<AppSettingsBootstrapHostedService>();
    builder.Services.AddHostedService<PollingHostedService>();              // 4th — Phase 2

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
