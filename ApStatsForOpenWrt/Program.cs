using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Bootstrap;
using ApStatsForOpenWrt.Services.Onboarding;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Services.UpdateCheck;
using Serilog;
using Serilog.Formatting.Compact;
using System.Net;
using System.Net.Sockets;

// 1. Bootstrap logger — captures startup exceptions before host is built (RESEARCH Pattern 4).
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

// M-SEC-05 / plan 16-01 — ForwardedHeaders conditional registration. Default LAN-HTTP case:
// middleware NOT registered; RemoteIpAddress reflects the actual TCP peer. When at least one of
// ForwardedHeaders__KnownProxies (comma-separated IPs) or ForwardedHeaders__KnownNetworks
// (comma-separated CIDRs) is set, the middleware is registered with that allow-list.
//
// FAIL-FAST (CONTEXT.md Decision 2: "Invalid entries throw at startup — fail-fast over
// skip-and-warn"). The env vars are parsed + VALIDATED EAGERLY here, ABOVE the host try/catch,
// so a bad IP/CIDR throws BEFORE the host is even built and the raw InvalidOperationException
// propagates straight out of Main (it is intentionally NOT inside the try/catch that converts
// fatal startup errors into a logged exit-1 — a misconfigured proxy allow-list must surface the
// offending entry to the operator, not be masked behind a generic "terminated unexpectedly").
// The validated lists are reused by both the Configure<> lambda AND the startup-count log so the
// two can never diverge (the previous bug validated lazily inside the Configure lambda — which
// only ran on the first request — while the startup log counted via TryParse, which never throws,
// so a typo'd allow-list booted healthy, logged trustedProxies=0, then 500'd on first request).
//
// DEVIATION (plan 16-01 important_implementation_note): the CONTEXT.md pinned shape called
// Microsoft.AspNetCore.HttpOverrides.IPNetwork.TryParse, but on .NET 10
// ForwardedHeadersOptions.KnownIPNetworks is IList<System.Net.IPNetwork> (the BCL type), NOT the
// HttpOverrides type. We use System.Net.IPNetwork.TryParse (BCL) — identical parse-CIDR-then-add
// behavior; matches the existing test factories that already construct System.Net.IPNetwork.
var knownProxiesRaw = Environment.GetEnvironmentVariable("ForwardedHeaders__KnownProxies");
var knownNetworksRaw = Environment.GetEnvironmentVariable("ForwardedHeaders__KnownNetworks");
var forwardedHeadersEnabled =
    !string.IsNullOrWhiteSpace(knownProxiesRaw) ||
    !string.IsNullOrWhiteSpace(knownNetworksRaw);

var validatedProxies = new List<System.Net.IPAddress>();
var validatedNetworks = new List<System.Net.IPNetwork>();

if (forwardedHeadersEnabled)
{
    foreach (var entry in (knownProxiesRaw ?? "")
        .Split(',')
        .Select(s => s.Trim())
        .Where(s => s.Length > 0))
    {
        if (!System.Net.IPAddress.TryParse(entry, out var ip))
        {
            throw new InvalidOperationException(
                $"ForwardedHeaders__KnownProxies contains invalid IP: '{entry}'");
        }
        validatedProxies.Add(ip);
    }

    foreach (var entry in (knownNetworksRaw ?? "")
        .Split(',')
        .Select(s => s.Trim())
        .Where(s => s.Length > 0))
    {
        if (!System.Net.IPNetwork.TryParse(entry, out var net))
        {
            throw new InvalidOperationException(
                $"ForwardedHeaders__KnownNetworks contains invalid CIDR: '{entry}'");
        }
        validatedNetworks.Add(net);
    }
}

// Startup-log counts reflect the EAGERLY-VALIDATED set (same lists the Configure lambda below
// consumes). Because invalid entries already threw above, these counts can never silently
// under-count a misconfigured allow-list.
var forwardedProxyCount = validatedProxies.Count;
var forwardedNetworkCount = validatedNetworks.Count;

try
{
    Log.Information("ApStatsForOpenWrt starting");

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
            path: $"{logsRoot}/ap-stats-for-openwrt-.log",   // D-13: no appsettings indirection; override via LOGS_ROOT env var
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,                        // INF-05 — REQUIREMENTS-pinned 7 wins over STACK.md's 14
            shared: false)));

    // 3. EF Core — factory only, NEVER scoped DbContext (PER-05; PITFALL: captive dependency).
    //    Pooling=True for connection reuse; Default Timeout=30 -> PRAGMA busy_timeout=30000
    //    so transient SQLITE_BUSY (file-lock) is retried instead of erroring. 260517-stp
    //    bumped from 5 -> 30: the cold-start poll cycle takes ~5-10s on the operator's fleet
    //    (SSH ALG-negotiation + AP iwinfo warm-up + DHCP-table population), and 5s wasn't
    //    enough headroom for the Setup/Wlans.razor auto-discovery write to wait out the
    //    orchestrator's tx -- the discovery's BeginTransaction would exhaust the 5s and
    //    surface SQLITE_BUSY (Error 5), killing the Blazor circuit (see 260517-stp PLAN).
    //    NO Cache=Shared on a file DB: shared cache forces table-level cache locks that
    //    bypass WAL's file-level reader/writer concurrency, producing SQLITE_LOCKED (error 6)
    //    when the orchestrator transaction holds WiFiNodes while the dashboard / health checks
    //    read it. WAL itself is enabled by MigrationsHostedService via PRAGMA journal_mode=WAL
    //    and is persisted on the DB file.
    builder.Services.AddDbContextFactory<DatabaseContext>(options =>
        options.UseSqlite($"Data Source={dataRoot}/AP-Stats-for-OpenWrt.db;Pooling=True;Default Timeout=30"));

    // 4. ASP.NET Data Protection — file-system key ring + verbatim application name (D-02 / SEC-03).
    //    On Linux/WSL2 keys are NOT encrypted at rest (DPAPI is Windows-only) —
    //    the /data volume is the encryption boundary (acceptable per threat model).
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo($"{dataRoot}/keys"))  // SEC-02
        .SetApplicationName("ApStatsForOpenWrt")                            // SEC-03, D-02 — VERBATIM string
        .SetDefaultKeyLifetime(TimeSpan.FromDays(90));                   // framework default — RESEARCH Pattern 3 §3

    // 5. Repositories (Transient — RESEARCH lifetimes table) + Settings service (Singleton — D-11).
    //    Repository registrations come first because AppSettingsService consumes IAppSettingsRepo
    //    (PER-04 — only repositories under ApStatsForOpenWrt.Data.Repositories may inject IDbContextFactory).
    builder.Services.AddTransient<INodeRepo,        NodeRepo>();
    builder.Services.AddTransient<INetworkRepo,     NetworkRepo>();
    builder.Services.AddTransient<IStationRepo,     StationRepo>();
    builder.Services.AddTransient<IAppSettingsRepo, AppSettingsRepo>();
    builder.Services.AddSingleton<IAppSettingsService, AppSettingsService>();

    // v0.11 ENRICH-01 / CONTEXT 14 §D-05 — one-shot force-enrichment flag for the
    // "Run now" button on Settings/Index.razor. Singleton-lifetime; backed by a single
    // int + Interlocked CAS (lock-free). Consumed once-per-cycle by PollOrchestrator at
    // the entry to the Phase 10 enrichment block. PollOrchestrator constructor now takes
    // IForceEnrichmentService — registered HERE so the DI graph resolves the new ctor param
    // without per-test boilerplate. Plan 14-02.
    builder.Services.AddSingleton<ApStatsForOpenWrt.Services.Enrichment.IForceEnrichmentService,
                                  ApStatsForOpenWrt.Services.Enrichment.ForceEnrichmentService>();

    // v0.11 DISC-01 / CONTEXT 14 §D-09 — write-side SSH responsibility, dedicated from
    // NodePoller. Stateless service; lifetime = Transient. Constructor takes a
    // Func<ISshSession> factory so each call gets a fresh session (matches NodePoller's
    // per-call lifecycle). The ISshSession factory resolves the existing Transient
    // ISshSession registration. Plan 14-04.
    builder.Services.AddTransient<ApStatsForOpenWrt.Services.Admin.INodeAdminService,
                                  ApStatsForOpenWrt.Services.Admin.NodeAdminService>(sp =>
        new ApStatsForOpenWrt.Services.Admin.NodeAdminService(
            sessionFactory: () => sp.GetRequiredService<ApStatsForOpenWrt.Services.Polling.Ssh.ISshSession>(),
            dpp: sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
            log: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ApStatsForOpenWrt.Services.Admin.NodeAdminService>>()));

    // 5b. Polling subsystem (Phase 2). PollOrchestrator is the PER-04 carve-out #3 — see ArchitectureTests.
    //     ISshSession is transient (one fresh session per poll, per Pitfall 5).
    //     NodePoller is constructed per-cycle via the Func<WiFiNode, CancellationToken, Task<NodeProbeResult>>
    //     probe factory so the orchestrator never holds a baked-in WiFiNode reference.
    //     PollNotifier is registered as a Singleton even though Phase 2 has no subscribers — Phase 5
    //     adds Dashboard subscribers; raising the event from day 1 makes Phase 5 additive only.
    builder.Services.AddSingleton<OpenWrtAdapterCache>();
    builder.Services.AddSingleton<PollNotifier>();
    // Phase 17 M-UPD-01 / M-UPD-04 — update-check singletons, registered next to PollNotifier.
    // UpdateNotifier is the PollNotifier twin (Plan 17-01 created the class but did NOT register it
    // — registered here). IUpdateChecker is the first IHttpClientFactory consumer (the "github"
    // named client below). The hosted-service loop AND the Settings "Check now" button both resolve
    // IUpdateChecker — one code path.
    builder.Services.AddSingleton<UpdateNotifier>();
    builder.Services.AddSingleton<IUpdateChecker, UpdateChecker>();
    // First and only AddHttpClient in the app — a named "github" client matching
    // CreateClient("github") in UpdateChecker. Registers IHttpClientFactory in the DI graph.
    builder.Services.AddHttpClient("github");
    // Phase 8 D-09 (post-fix 2026-05-08): the gate is the Singleton seam; PreferencesService
    // itself is Scoped because it captures IJSRuntime (Scoped per Blazor circuit). The
    // original Plan 08-01 Singleton registration of PreferencesService was a captive-dep
    // bug — see .planning/debug/resolved/phase8-prefs-captive-dep.md.
    //
    // Registration order (load-bearing — DI graph must be acyclic at root):
    //   1) NotificationsGate (Singleton)            — hot-path bool, no captures.
    //   2) INotificationsGate factory (Singleton)   — forwards to NotificationsGate.
    //   3) PreferencesService (Scoped)              — captures IJSRuntime + the gate.
    //   4) ToastService / EventDetectionService     — Singletons consuming INotificationsGate.
    builder.Services.AddSingleton<ApStatsForOpenWrt.Services.NotificationsGate>();
    builder.Services.AddSingleton<ApStatsForOpenWrt.Services.INotificationsGate>(sp =>
        sp.GetRequiredService<ApStatsForOpenWrt.Services.NotificationsGate>());
    // Phase 8 D-09 (post-fix): PreferencesService is Scoped — wraps wwwroot/js/preferences.js
    // (per-browser localStorage) with a thin async interop layer + cached
    // IsNotificationsEnabled hot-path. The cached value is mirrored into the Singleton
    // NotificationsGate on first read and on every Set so cross-circuit consumers
    // (ToastService, EventDetectionService) see the operator's preference.
    builder.Services.AddScoped<ApStatsForOpenWrt.Services.PreferencesService>();
    // Phase 8 Plan 08-06 D-08 — ToastService is the in-process event bus consumed by
    // ToastHost (Wave 3 mount in MainLayout) and emitted to by EventDetectionService.
    // Registered as Singleton AFTER PreferencesService + INotificationsGate so the DI
    // graph resolves cleanly at startup.
    builder.Services.AddSingleton<ApStatsForOpenWrt.Services.ToastService>();
    // Phase 8 Plan 08-07 D-08/D-11 — EventDetectionService subscribes to PollNotifier.Cycled
    // in its constructor. Singletons are constructed lazily on first resolve, so we register
    // an eager-start IHostedService shim that resolves the Singleton at app startup; without
    // it, a session with no UI activity would never instantiate the detector. Ordered AFTER
    // ToastService (its dependency) and BEFORE the polling pipeline registrations so the DI
    // graph remains acyclic.
    builder.Services.AddSingleton<ApStatsForOpenWrt.Services.EventDetectionService>();
    // Phase 10 ORCH-07 — TimeProvider DI lets PollOrchestrator's enrichment loop accept an injected
    // clock (TimeProvider.System in production; a fake in tests). Required for the trigger evaluator
    // and the LastEnrichmentRefreshAt write timestamp.
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<PollOrchestrator>();
    builder.Services.AddTransient<ISshSession, RealSshSession>();
    // Phase 9 — Subnet sweep + reverse DNS probes (purely additive surface; orchestrator does NOT
    // invoke them in Phase 9 per SC4 isolation guarantee. Phase 10 wires the orchestrator call site).
    // SC4 validation: PollOrchestratorPhase9IsolationTests asserts RunCycleAsync issues only the
    // v0.9 command set on the existing poll path — no Phase-9-new commands leak in.
    builder.Services.AddTransient<SubnetSweepProbe>();
    builder.Services.AddTransient<ReverseDnsProbe>();
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
    // Phase 10 ORCH-01..08 — enrichment factory. Mirrors the v0.9 probeFactory pattern (one
    // DI-registered Func captured by PollOrchestrator's primary ctor). This factory opens its
    // OWN ISshSession per node (separate from NodePoller's), connects, invokes SubnetSweepProbe
    // and ReverseDnsProbe, returns POCO EnrichmentDelta. Cost: one extra SSH connect per node
    // per refresh tick (default <= once per hour at RefreshIntervalSeconds=3600). The orchestrator
    // gates this factory call on EnrichmentRefreshTrigger.ShouldRefresh — it is NOT invoked
    // when the trigger does not fire (SC4 isolation invariant inverted in Phase 10).
    builder.Services.AddSingleton<Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<ApStatsForOpenWrt.Services.Polling.EnrichmentDelta?>>>(sp =>
        async (node, ipsForReverseDns, peerIp, ct) =>
        {
            // W-03 fix: only resolve services we actually use. The probes (sweepLocal, dnsLocal)
            // are constructed per-call from the just-connected session — sp-resolved Transient
            // SubnetSweepProbe / ReverseDnsProbe instances would be unused here and trigger
            // IDE0059/CS0219 warnings under warnings-as-errors.
            var session = sp.GetRequiredService<ApStatsForOpenWrt.Services.Polling.Ssh.ISshSession>();
            var dpp     = sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
            var log     = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ApStatsForOpenWrt.Services.Polling.PollOrchestrator>>();

            try
            {
                string plaintext;
                try
                {
                    plaintext = dpp.CreateProtector("node-creds").Unprotect(node.Password);
                }
                catch (System.Security.Cryptography.CryptographicException ex)
                {
                    log.LogWarning(ex, "Node {Hostname} (Id={NodeId}): enrichment skipped — password decrypt failed.", node.Hostname, node.Id);
                    return null;
                }

                await session.ConnectAsync(node, plaintext,
                    expectedFingerprint: string.IsNullOrEmpty(node.HostKeyFingerprint) ? null : node.HostKeyFingerprint,
                    onHostKeyAccepted: null,
                    ct: ct);

                // The probe constructors take ISshSession via DI; the registered Transient session
                // injected into them above is the SAME instance because GetRequiredService<ISshSession>
                // returns a fresh transient and the probes ALSO request a transient — they may resolve
                // DIFFERENT instances. Defensive shape: construct the probes inline binding the just-
                // connected session, mirroring the SubnetSweepProbeTests / ReverseDnsProbeTests
                // composition. This avoids a second connect inside the probes.
                var sweepLocal = new ApStatsForOpenWrt.Services.Polling.SubnetSweepProbe(session,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ApStatsForOpenWrt.Services.Polling.SubnetSweepProbe>>());
                var dnsLocal = new ApStatsForOpenWrt.Services.Polling.ReverseDnsProbe(session,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ApStatsForOpenWrt.Services.Polling.ReverseDnsProbe>>());

                // QUICK 260510-kjt: SubnetSweepProbe's T-09-03-05 defense-in-depth gate
                // (IPAddress.TryParse) refuses to sweep when peerIp is a DNS name. The orchestrator
                // hands us node.Hostname per its line-215 contract ("factory is responsible for
                // resolving"); honor that contract HERE, after ConnectAsync proves reachability.
                //
                //   - peerIp is already a literal IPv4 -> pass through (no extra DNS round-trip).
                //   - peerIp is a DNS name resolving to >=1 A record -> use the first IPv4.
                //   - DNS lookup throws (NXDOMAIN, refused, etc.) -> pass the raw hostname through;
                //     the probe's existing warning will fire and the failure stays explicit.
                string resolvedPeerIp = peerIp;
                if (!IPAddress.TryParse(peerIp, out _))
                {
                    try
                    {
                        var addrs = await Dns.GetHostAddressesAsync(peerIp, AddressFamily.InterNetwork, ct);
                        if (addrs.Length > 0)
                        {
                            resolvedPeerIp = addrs[0].ToString();    // canonical dotted-quad — always passes IPAddress.TryParse
                        }
                    }
                    catch (SocketException ex)
                    {
                        log.LogWarning(ex,
                            "Node {Hostname} (Id={NodeId}): DNS resolution of peerIp '{PeerIp}' failed; sweep will refuse and emit invalid-peer-ip.",
                            node.Hostname, node.Id, peerIp);
                        // resolvedPeerIp stays = peerIp (the raw hostname) — preserves the explicit
                        // SubnetSweepProbe Failed("invalid-peer-ip") signal rather than masking it.
                    }
                }

                var sweepRes = await sweepLocal.PollAsync(node, resolvedPeerIp, ct);
                var sweepMap = sweepRes is ApStatsForOpenWrt.Services.Polling.SubnetSweepProbeResult.Success ss
                    ? ss.MacToIp
                    : (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);

                // QUICK 260511-ma0: delete-then-rebid one-cycle hostname convergence. The
                // sweep-then-DNS sequencing + dnsMap1 ∪ dnsMap2 merge is extracted to
                // EnrichmentFactoryClosure.BuildMergedDnsMapAsync so the merge logic stays
                // directly unit-testable without rebuilding the production DI graph. The
                // helper runs ONE batched-nslookup SSH dispatch on steady-state cycles (no
                // sweep-only IPs) and TWO on rebid cycles where sweep discovered an IP the
                // orchestrator did not pre-supply in ipsForReverseDns. dnsMap2 wins on
                // collisions; partial-failure preserves dnsMap1 verbatim (DB-as-cache).
                var mergedDns = await ApStatsForOpenWrt.Services.Polling.EnrichmentFactoryClosure
                    .BuildMergedDnsMapAsync(dnsLocal, node, ipsForReverseDns, sweepMap, ct);

                return new ApStatsForOpenWrt.Services.Polling.EnrichmentDelta(node.Id, sweepMap, mergedDns);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Node {Hostname} (Id={NodeId}): enrichment factory failed; persisted IPs/Hostnames preserved (DB-as-cache).", node.Hostname, node.Id);
                return null;     // null = orchestrator skips delta-merge for this node this cycle (DB-as-cache preserves last-known).
            }
            finally
            {
                try { await session.DisposeAsync(); } catch { /* Pitfall 5 dispose-hang */ }
            }
        });
    // PollingHostedService consumes a runCycleAsync delegate (not PollOrchestrator directly) so the
    // host stays unit-testable. The runtime delegate resolves PollOrchestrator from the host scope.
    builder.Services.AddSingleton<Func<CancellationToken, Task>>(sp => ct =>
        sp.GetRequiredService<PollOrchestrator>().RunCycleAsync(ct));

    // 5c. Phase 3 — Cookie auth + conditional fallback policy + rate limiter +
    //     ForwardedHeaders + UI password hasher + OUI lookup + UI services.
    //     RESEARCH §Code Examples §1; Pitfalls 3 + 4 + 8 + 9 binding.

    // M-SEC-05 / plan 16-01 — ForwardedHeaders conditional registration. The env vars were
    // read + VALIDATED EAGERLY above the host try/catch (fail-fast); here we only register the
    // Configure<> lambda, which consumes the PRE-VALIDATED lists. The lambda still runs lazily
    // (first IOptions resolution inside UseForwardedHeaders()), but it can no longer throw — all
    // validation already happened eagerly, so the lazy path is now a pure copy of an
    // already-proven-good set.
    //
    // Pitfall 4 ordering preserved: app.UseForwardedHeaders() runs BEFORE app.UseRateLimiter()
    // so the per-IP partition key consumes the rewritten RemoteIpAddress when the middleware IS
    // active.
    if (forwardedHeadersEnabled)
    {
        builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(opts =>
        {
            opts.ForwardedHeaders =
                Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
            opts.KnownProxies.Clear();
            opts.KnownIPNetworks.Clear();

            foreach (var ip in validatedProxies) opts.KnownProxies.Add(ip);
            foreach (var net in validatedNetworks) opts.KnownIPNetworks.Add(net);
        });
    }

    // M-SEC-07 / plan 16-05 — baseline security response headers middleware.
    // Registered as transient per IMiddleware pattern. Pipeline registration below
    // (immediately before app.UseStaticFiles()).
    builder.Services.AddTransient<ApStatsForOpenWrt.Services.Security.SecurityHeadersMiddleware>();

    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.Cookie.Name = "ApStatsForOpenWrt.Auth";   // D-05 verbatim
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
            o.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
            o.LoginPath = "/Login";
            o.LogoutPath = "/Logout";
            o.ExpireTimeSpan = TimeSpan.FromDays(7);          // M-SEC-04 — UNCHANGED from v0.11
            o.SlidingExpiration = true;                        // M-SEC-04 — UNCHANGED from v0.11

            // M-SEC-04 / plan 15-04 — session-version claim check on every request.
            // Per CONTEXT.md Decision 1, missing claim is treated as a mismatch (every v0.11
            // cookie at v0.12 deploy) — one-time UX cost; operators re-auth once on deploy.
            //
            // Documented deviation from Decision 1's pinned shape (15-04 truths block):
            // adds `SignOutAsync` AFTER `RejectPrincipal` so the now-invalid cookie is also
            // wiped from the browser via a Set-Cookie deletion header. RejectPrincipal alone
            // leaves the stale cookie in place — every subsequent request would pay the full
            // validation cost before rejection. Additive mitigation; does not weaken the gate.
            //
            // IAppSettingsService is resolved PER-REQUEST via ctx.HttpContext.RequestServices
            // — NOT captured at registration time (CONTEXT.md code_context "OnValidatePrincipal").
            o.Events.OnValidatePrincipal = static async ctx =>
            {
                var settings = ctx.HttpContext.RequestServices.GetRequiredService<IAppSettingsService>();
                var sv = ctx.Principal?.FindFirst("session_version")?.Value;
                if (sv is null || !int.TryParse(sv, out var claimVersion) || claimVersion != settings.SessionVersion)
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(
                        Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
        });

    // M-SEC-04 / plan 15-04 — bind the DI-resolved TimeProvider onto CookieAuthenticationOptions
    // via IPostConfigureOptions. The AddCookie lambda above runs at registration time with no
    // scope (cannot resolve TimeProvider directly); IPostConfigureOptions runs AFTER the options
    // instance is constructed, inside a scope where TimeProvider is resolvable.
    // TimeProvider.System is already registered as a Singleton above (Phase 10 ORCH-07);
    // SlidingExpirationTests swaps it for a FakeTimeProvider via ConfigureTestServices.
    builder.Services.AddSingleton<IPostConfigureOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>(
        sp => new ApStatsForOpenWrt.Services.Auth.CookieAuthTimeProviderConfigurator(sp.GetRequiredService<TimeProvider>()));

    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddAuthorization(o =>
    {
        o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .AddRequirements(new ApStatsForOpenWrt.Services.Auth.ConditionalAuthRequirement())
            .Build();
    });
    builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
        ApStatsForOpenWrt.Services.Auth.ConditionalAuthHandler>();

    builder.Services.AddRateLimiter(opts =>
    {
        // M-SEC-03 brute-force limiter — POST-ONLY contract
        // (debug login-429-counts-page-views, operator-approved 2026-05-30).
        //
        // The limiter counts submitted sign-in ATTEMPTS (POST /Login), never page VIEWS
        // (GET /Login) or any other method. PRIOR shape attached the per-IP budget via
        // [EnableRateLimiting("LoginPolicy")] on Login.razor (which the attribute applies to
        // the Razor page endpoint's GET render too) AND gated the global budget on
        // Path.StartsWithSegments("/Login") with NO method check — so GET page views consumed
        // both budgets and a legitimate operator who merely viewed/reloaded /Login (e.g. the
        // sign-out → /Login redirect chain) got 429'd on the 6th view. Live repro from deb509a:
        // 7 rapid GET /Login → 200,200,200,200,200,429,429.
        //
        // FIX: fold BOTH budgets into opts.GlobalLimiter via PartitionedRateLimiter.CreateChained,
        // with each partition predicate gated on Method == "POST" && Path == /Login. Non-matching
        // requests (any GET, any non-/Login path) → GetNoLimiter (pay no cost). The
        // [EnableRateLimiting("LoginPolicy")] attribute is REMOVED from Login.razor because an
        // EnableRateLimiting attribute on a Razor page endpoint cannot be method-scoped — the
        // attribute is exactly why GET was counted. The two standalone opts.AddPolicy(...)
        // registrations (LoginPolicy / LoginGlobalPolicy) are removed: both budgets now live in
        // this chained GlobalLimiter, leaving no dangling [EnableRateLimiting] reference.
        //
        // M-SEC-03 / CONTEXT.md Decision 2 budget NUMBERS are UNCHANGED — per-IP PermitLimit=5,
        // global PermitLimit=30, FixedWindow=1min, QueueLimit=0, AutoReplenishment=true. ONLY the
        // trigger condition tightens to POST /Login.
        //
        // Pitfall 4 ordering preserved: app.UseForwardedHeaders() (when registered) runs BEFORE
        // app.UseRateLimiter(), so the per-IP partition key sees the rewritten RemoteIpAddress.
        static bool IsLoginPost(HttpContext ctx) =>
            HttpMethods.IsPost(ctx.Request.Method) &&
            ctx.Request.Path.StartsWithSegments("/Login");

        opts.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.CreateChained(
            // Per-IP ceiling: 5 POST /Login attempts per minute per source IP (SEC-06 / D-03).
            System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                IsLoginPost(ctx)
                    ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                        factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 5,                                 // SEC-06 / D-03
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        })
                    : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter<string>("non-login-post")),
            // Global ceiling: 30 POST /Login attempts per minute server-wide (M-SEC-03 plan 15-05).
            System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                IsLoginPost(ctx)
                    ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: "_global_",                            // M-SEC-03 plan 15-05
                        factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 30,                                // CONTEXT.md Decision 2
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        })
                    : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter<string>("non-login-post")));

        opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

    // SEC-04 password hasher; DASH-13 OUI singleton; D-07 + D-12 UI services.
    builder.Services.AddTransient<ApStatsForOpenWrt.Services.Auth.IUIPasswordHasher,
                                  ApStatsForOpenWrt.Services.Auth.UIPasswordHasher>();
    builder.Services.AddSingleton<ApStatsForOpenWrt.Services.Vendor.IOuiLookup,
                                  ApStatsForOpenWrt.Services.Vendor.OuiLookup>();
    builder.Services.AddTransient<ApStatsForOpenWrt.Services.Nodes.ITestConnectionService,
                                  ApStatsForOpenWrt.Services.Nodes.TestConnectionService>();
    builder.Services.AddTransient<ApStatsForOpenWrt.Services.Nodes.IWlanDiscoveryService,
                                  ApStatsForOpenWrt.Services.Nodes.WlanDiscoveryService>();

    // Phase 6 INF-06 / POLL-05 / POLL-06 — real visitor gate.
    // CircuitTracker holds the Interlocked counter + re-armable TCS; CircuitTrackerHandler is
    // scoped (one per circuit) and forwards open/close to the singleton tracker; CircuitTrackerGate
    // is the singleton ICircuitGate forwarding adapter consumed by LivePausedBadge / PollCountdown
    // (Pattern 2 subscribers — Plans 05-03 / 05-05 unchanged).
    // CircuitOptions.DisconnectedCircuitRetentionPeriod = 3 min (D-03 — hard-coded, NOT App Settings
    // in v1; total grace = 3 min + 30s in CircuitTracker semantics).
    builder.Services.AddSingleton<ApStatsForOpenWrt.Services.Gating.CircuitTracker>();
    // Phase 7+ smoke-test escape hatch (debug session smoke-poll-gate-not-armed, 2026-05-09):
    // The visitor gate is normally CircuitTrackerGate, which only releases its TaskCompletionSource
    // when the Blazor framework opens a real SignalR circuit (browser executes blazor.web.js).
    // Smoke tests that drive the app via raw HTTP only (no browser) never open a circuit, so the
    // orchestrator parks indefinitely on tracker.WaitForVisitorAsync() and /health.lastPollAge
    // stays null forever. To opt out, set the env var Polling__GateMode=AlwaysLive (which binds
    // to the IConfiguration key Polling:GateMode per .NET conventions) and we register the
    // permanently-Live AlwaysLiveGate instead. Default (unset, or any other value) is unchanged
    // production behavior - CircuitTrackerGate. AlwaysLiveGate.WaitForVisitorAsync returns
    // Task.CompletedTask, so the cold-start kick in PollingHostedService fires immediately.
    var gateMode = builder.Configuration["Polling:GateMode"];
    if (string.Equals(gateMode, "AlwaysLive", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<ApStatsForOpenWrt.Services.Gating.ICircuitGate,
                                      ApStatsForOpenWrt.Services.Gating.AlwaysLiveGate>();
        Log.Information("Polling:GateMode=AlwaysLive - registering AlwaysLiveGate (visitor gate disabled). Smoke/test path only.");
    }
    else
    {
        builder.Services.AddSingleton<ApStatsForOpenWrt.Services.Gating.ICircuitGate,
                                      ApStatsForOpenWrt.Services.Gating.CircuitTrackerGate>();
    }
    builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler,
                               ApStatsForOpenWrt.Services.Gating.CircuitTrackerHandler>();
    builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o =>
    {
        o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);  // D-03
    });

    // Phase 6 INF-06 / D-08 — request-time health checks. Two named checks; status code
    // (200 / 503) is auto-derived from HealthReport.Status by MapHealthChecks below.
    builder.Services.AddHealthChecks()
        .AddCheck<ApStatsForOpenWrt.Services.Health.DbHealthCheck>("db")
        .AddCheck<ApStatsForOpenWrt.Services.Health.DataProtectionHealthCheck>("dataProtection");

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
    builder.Services.AddHostedService<ApStatsForOpenWrt.Services.EventDetectionEagerStartHostedService>(); // 6th — Phase 8
    // Phase 17 M-UPD-01 — 7th. After AppSettingsBootstrap (4th, so the UpdateCheckEnabled toggle is
    // warm) and after Polling (5th — a bystander to the polling mission). Registration order =
    // StartAsync order (Pitfall 10). The 24h visitor-gated loop issues the first check on first visitor.
    builder.Services.AddHostedService<UpdateCheckHostedService>();          // 7th — Phase 17 (M-UPD-01)

    // 7. Razor components (Phase 3 starts here). Auth + ratelimit + forwarded headers are
    //    added in Plan 03-03; this Plan 03-01 only lights up the Razor seam so the smoke
    //    spike (AdminLteAssetSmokeTests) can boot the host without 404s on the placeholder
    //    Index.razor route. D-06: AddInteractiveServerComponents + AddInteractiveServerRenderMode.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    var app = builder.Build();

    // M-SEC-05 / plan 16-01 — startup posture log line. Operator-grep-friendly prefix
    // matches the M-SEC-NN: convention established in Phase 15 (M-SEC-04 banner / M-SEC-10
    // ASPNETCORE warning) and continues in 16-02 (algo negotiation) + 16-03 (TOFU capture).
    {
        var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        startupLog.LogInformation(
            "M-SEC-05: ForwardedHeaders middleware: mode={Mode} trustedProxies={ProxyCount} trustedNetworks={NetworkCount}",
            forwardedHeadersEnabled ? "enabled" : "disabled",
            forwardedProxyCount,
            forwardedNetworkCount);
    }

    // M-SEC-10 / plan 15-04 — Warning when ASPNETCORE_ENVIRONMENT=Development is set,
    // so an operator who accidentally ships the dev env flag into a production container
    // sees it in docker logs at boot. The verbatim message is pinned by
    // DevelopmentEnvironmentWarningTests (em-dash is U+2014, NOT a hyphen).
    if (app.Environment.IsDevelopment())
    {
        var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        startupLog.LogWarning(
            "ASPNETCORE_ENVIRONMENT=Development is set. This should never be true in production deployments — verify your container/orchestrator config.");
    }

    // Middleware pipeline — order is LOAD-BEARING (RESEARCH §Pitfall 4):
    // UseForwardedHeaders MUST run before UseAuthentication (so cookie sees the rewritten
    // RemoteIpAddress) AND before UseRateLimiter (so the partition key is correct).
    //
    // M-SEC-05 / plan 16-01 — only register the middleware when the operator has pinned at
    // least one trusted proxy or network. Default LAN-HTTP case: NOT registered;
    // RemoteIpAddress reflects the actual TCP peer; forged X-Forwarded-For headers are
    // ignored because the middleware never runs. When registered, UseForwardedHeaders()
    // still runs here (before UseRateLimiter / UseAuthentication) so the partition key +
    // auth cookie see the rewritten RemoteIpAddress (Pitfall 4 ordering preserved).
    if (forwardedHeadersEnabled)
    {
        app.UseForwardedHeaders();
    }
    app.UseSerilogRequestLogging();
    // M-SEC-07 / plan 16-05 — baseline security headers. Placed BEFORE UseStaticFiles
    // so static-asset responses (/lib/*, /app.css, /favicon.ico) also carry the
    // 5 headers. Headers omitted by charter: Strict-Transport-Security AND CSP
    // upgrade-insecure-requests (both fight HTTP-on-LAN deployment).
    app.UseMiddleware<ApStatsForOpenWrt.Services.Security.SecurityHeadersMiddleware>();
    app.UseStaticFiles();           // serves /lib/adminlte/* /lib/bootstrap-icons/* /lib/source-sans-3/* /app.css /favicon.ico
    app.UseAntiforgery();
    // D-07: Auto-redirect first-run operator to the onboarding wizard (GET / only).
    // Placed after UseStaticFiles (assets bypass it) and after UseAntiforgery,
    // but BEFORE UseAuthentication so the anonymous first-run GET / is handled before
    // the auth pipeline. The /setup/* anti-takeover 404 gate is a SEPARATE middleware
    // (WizardSetupGateMiddleware) registered AFTER UseAuthentication so it can be
    // auth-aware — see debug session wizard-lockout-regression.
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
            .GetRequiredService<ApStatsForOpenWrt.Services.Polling.PollOrchestrator>();
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

    app.UseAuthentication();        // reads "ApStatsForOpenWrt.Auth" cookie

    // M-SEC-02 — AUTH-AWARE wizard /setup/* anti-takeover 404 gate. Registered AFTER
    // UseAuthentication() (so context.User reflects the validated auth cookie) but
    // BEFORE UseAuthorization() (so the friendly 404 for an anonymous genuinely-complete
    // /setup/* request is served VERBATIM, not pre-empted by the ConditionalAuth
    // fallback policy's OnRedirectToLogin 302). The just-signed-in operator (post
    // step 1 submit-password SignInAsync) traverses /setup/nodes + /setup/wlans;
    // anonymous callers get the friendly 404 only once the system is genuinely complete
    // (hasPassword AND anyNodes). In the password-only window the gate is OFF and the
    // anonymous request falls through to the ConditionalAuth 302 /Login. Fixes
    // wizard-lockout-regression + wizard-gate-and-semantics; the independent M-SEC-01
    // POST gate on /setup/submit-password is untouched (it deliberately stays on the
    // hasPassword boundary, distinct from these visible AND gates).
    app.UseMiddleware<WizardSetupGateMiddleware>();

    app.UseAuthorization();         // applies fallback ConditionalAuthRequirement
    app.UseRateLimiter();           // sees rewritten IP (Pitfall 4)

    // /Logout — minimal endpoint; SignOut + redirect (RESEARCH §Open Question #1 recommendation).
    // M-SEC-01 — explicit antiforgery validation because `ctx.Request.Form` binding bypasses
    // `app.UseAntiforgery()` auto-validation. The user-menu form posts the antiforgery token;
    // we validate it explicitly here. No commitment-404 gate on /Logout — sign-out is always
    // legitimate for an authenticated principal.
    app.MapPost("/Logout", async (
        IAntiforgery antiforgery,
        HttpContext ctx) =>
    {
        try { await antiforgery.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException)
        {
            return Microsoft.AspNetCore.Http.Results.BadRequest();
        }
        await ctx.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
        return Microsoft.AspNetCore.Http.Results.Redirect("/");
    });

    // D-08: "Skip for now" POST handler. Sets WizardDismissed = true and redirects to dashboard.
    // Works from all three wizard steps (even Step 1 static SSR via the SetupLayout form POST).
    // M-SEC-01 — explicit antiforgery validation + commitment-404 gate. WizardDismissed is NOT
    // in the gate (Option B resumability — dismissed-only state must still allow skip to be
    // re-issued until a real commitment lands).
    app.MapPost("/setup/skip", async (
        IAntiforgery antiforgery,
        IAppSettingsService settings,
        INodeRepo nodeRepo,
        HttpContext ctx) =>
    {
        try { await antiforgery.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException)
        {
            return Microsoft.AspNetCore.Http.Results.BadRequest();
        }

        // M-SEC-01 auth-aware gate (debug skip-setup-404-auth-blind, sibling of
        // wizard-lockout-regression; AND semantics per wizard-gate-and-semantics): refuse
        // 404 only once the system is GENUINELY COMPLETE (hasPassword AND anyNodes) and ONLY
        // for ANONYMOUS callers (anti-takeover). In the password-only window the gate is OFF,
        // so an anonymous skip succeeds (sets WizardDismissed, 302 /). The authenticated
        // operator is mid-onboarding: step 1 (submit-password) flips hasPassword == true AND
        // signs them in, so an auth-blind gate 404'd their legitimate "Skip for now" on
        // steps 2/3. This MapPost runs after UseAuthentication()/UseAuthorization(), so
        // ctx.User is populated. WizardDismissed is NOT in the gate (Option B resumability).
        var isAuthenticated = ctx.User?.Identity?.IsAuthenticated == true;
        var anyNodes = await nodeRepo.AnyAsync(ctx.RequestAborted);
        var hasPassword = !string.IsNullOrEmpty(settings.UIPasswordHash);
        if (!isAuthenticated && (anyNodes && hasPassword))
            return Microsoft.AspNetCore.Http.Results.NotFound();

        await settings.SetWizardDismissedAsync(true, ctx.RequestAborted);
        return Microsoft.AspNetCore.Http.Results.Redirect("/");
    }).AllowAnonymous();

    // Step 1 password submission — auth needs the HTTP response pipeline, not the Blazor circuit.
    // Password.razor is a normal InteractiveServer page; this endpoint handles the POST.
    // M-SEC-01 — explicit antiforgery validation + commitment-404 gate. WizardDismissed is NOT
    // in the gate (Option B resumability).
    app.MapPost("/setup/submit-password", async (
        IAntiforgery antiforgery,
        ApStatsForOpenWrt.Services.Auth.IUIPasswordHasher hasher,
        IAppSettingsService settings,
        INodeRepo nodeRepo,
        HttpContext ctx) =>
    {
        try { await antiforgery.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException)
        {
            return Microsoft.AspNetCore.Http.Results.BadRequest();
        }

        // ACTION-AWARE gate split (debug wizard-resume-404-after-skip). The form carries
        // two actions: action=set (WRITE a password) and action=skip (a no-op ADVANCE to
        // step 2 that writes nothing — the "Skip password" button on step 1). Read the
        // action FIRST, so the commitment-404 gate below applies ONLY to the password WRITE.
        var form   = ctx.Request.Form;
        var action = form["action"].ToString();

        // action=skip is a pure ADVANCE — it never writes a password, so the M-SEC-01
        // anti-takeover gate must NOT 404 it. A returning operator whose step 1 already
        // set a password (then skipped the wizard) clicks "Skip password" to advance to
        // /setup/nodes; gating this on hasPassword dead-ended wizard completion. Letting
        // the advance through does NOT reopen M-SEC-01 (no password write occurs here).
        if (action != "set")
            return Microsoft.AspNetCore.Http.Results.Redirect("/setup/nodes");

        // M-SEC-01 — refuse the password WRITE once a real commitment exists, but ONLY for
        // ANONYMOUS callers (auth-aware gate, debug wizard-resume-pw-reset-404). WizardDismissed
        // is NOT in the gate (Option B resumability). This WRITE gate DELIBERATELY stays on the
        // hasPassword boundary (anyNodes || hasPassword), distinct from the visible AND gates
        // (GET /setup/* + POST /setup/skip): tightening it to AND would reopen the M-SEC-01
        // anonymous password-takeover window (an attacker could re-POST a NEW password before
        // the first AP exists). Operator accepted this asymmetry (wizard-gate-and-semantics).
        //
        // The !isAuthenticated guard mirrors the existing auth-aware pattern in
        // WizardSetupGateMiddleware + POST /setup/skip: the AUTHENTICATED operator (signed in
        // by step 1's SignInAsync) may set/re-set the password and complete the wizard on a
        // resume — they were 404'd before the moment a password existed, dead-ending step 1.
        // An ANONYMOUS caller is STILL 404'd once a password exists, so the anti-takeover
        // boundary is PRESERVED (M-SEC-01: no anonymous overwrite in the password-only window).
        // This MapPost runs after UseAuthentication()/UseAuthorization(), so ctx.User is
        // populated. The handler re-issues the auth cookie with the bumped SessionVersion below
        // (M-SEC-04), so an authenticated re-set does NOT self-logout. The gate is scoped to
        // action=set only so the harmless action=skip advance above is never 404'd (debug
        // wizard-resume-404-after-skip).
        var isAuthenticated = ctx.User?.Identity?.IsAuthenticated == true;
        var anyNodes = await nodeRepo.AnyAsync(ctx.RequestAborted);
        var hasPassword = !string.IsNullOrEmpty(settings.UIPasswordHash);
        if (!isAuthenticated && (anyNodes || hasPassword))
            return Microsoft.AspNetCore.Http.Results.NotFound();

        var password = form["Password"].ToString();
        var confirm  = form["ConfirmPassword"].ToString();

        if (string.IsNullOrEmpty(password))
            return Microsoft.AspNetCore.Http.Results.Redirect(
                "/setup/password?Error=" + Uri.EscapeDataString("Password is required."));

        if (password != confirm)
            return Microsoft.AspNetCore.Http.Results.Redirect(
                "/setup/password?Error=" + Uri.EscapeDataString("Passwords do not match."));

        // M-SEC-03 / plan 15-05 — policy rejection. UIPasswordHasher.Hash (plan 15-02) throws
        // ArgumentException with the verbatim operator-facing message when the plaintext fails
        // the four-part policy (>=8 chars + upper + lower + digit-or-symbol). Setup/Password.razor
        // renders the Error query param via its alert-danger block; redirect there with the
        // pinned message preserved through URL-encoding. No DB write, no session bump, no SignIn.
        string hash;
        try
        {
            hash = hasher.Hash(password);
        }
        catch (ArgumentException ex)
        {
            return Microsoft.AspNetCore.Http.Results.Redirect(
                "/setup/password?Error=" + Uri.EscapeDataString(ex.Message));
        }
        await settings.SetUIPasswordHashAsync(hash, ctx.RequestAborted);

        // M-SEC-04 / plan 15-04 — stamp the post-bump SessionVersion as a claim. SetUIPasswordHashAsync
        // bumps the version atomically (plan 15-01), so settings.SessionVersion here is the new value;
        // the freshly-issued cookie validates cleanly against OnValidatePrincipal on the next request.
        var claims   = new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "operator"),
            new System.Security.Claims.Claim("session_version", settings.SessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        var identity = new System.Security.Claims.ClaimsIdentity(
            claims,
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
            new System.Security.Claims.ClaimsPrincipal(identity));

        return Microsoft.AspNetCore.Http.Results.Redirect("/setup/nodes");
    }).AllowAnonymous();   // wizard skip must be reachable regardless of auth state

    // Blazor Web App framework asset endpoints — see .planning/debug/resolved/blazor-js-404.md.
    // .NET 9 introduced MapStaticAssets() as the canonical replacement for UseStaticFiles() for
    // Blazor Web Apps; it registers endpoint routes for the static-web-assets manifest including
    // the virtual `_framework/blazor.web.js` runtime script and any fingerprinted scoped CSS. Without
    // this call the runtime JS is unreachable (flat 404 from Kestrel — no endpoint matches), the
    // browser never establishes the SignalR circuit, and every InteractiveServer page silently
    // falls back to static SSR (broken Save buttons, dead Test Connection, etc.).
    // UseStaticFiles() is intentionally retained above — it serves the physical files vendored under
    // wwwroot/ (AdminLTE, Bootstrap Icons, Source Sans 3, app.css, favicons). MapStaticAssets only
    // owns the publish-time static-web-assets manifest endpoints.
    // Mapped here (after the auth/wizard endpoints, immediately before MapRazorComponents) so the
    // framework asset endpoints inherit the same routing context as Blazor itself.
    //
    // .AllowAnonymous() is LOAD-BEARING (quick task 260530-9mg, live debug 2026-05-30). On .NET 10,
    // MapStaticAssets() does NOT auto-tag its endpoints with AllowAnonymous metadata — the prior
    // comment here asserted it did, which is false. Without the explicit .AllowAnonymous() below,
    // every static-asset endpoint (blazor.web.js, the scoped-CSS bundle ApStatsForOpenWrt.styles.css,
    // /lib/*, /css/site.css, favicons) inherits the AddAuthorization FallbackPolicy
    // (ConditionalAuthRequirement). Once a UI password is set that policy engages and 302-redirects
    // each asset to /Login — confirmed live against the running container: only /health (which
    // carries its own .AllowAnonymous(), mapped above) returned 200 while every asset 302'd to
    // /Login?ReturnUrl=.... The 302 (not 404) proves the endpoints matched and were auth-gated, not
    // unresolved. Tagging the static-asset endpoints anonymous exempts them from the fallback policy
    // so they serve before any auth check — matching the /health pattern.
    app.MapStaticAssets().AllowAnonymous();

    app.MapRazorComponents<ApStatsForOpenWrt.Components.App>()
        .AddInteractiveServerRenderMode();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "ApStatsForOpenWrt terminated unexpectedly");
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
