using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Tests.Integration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Security;

/// <summary>
/// M-SEC-05 (plan 16-01) acceptance suite for the conditional ForwardedHeaders
/// registration. Pins the four matrix cells (no-env / proxy-only / network-only) plus
/// the whitespace-skip and fail-fast parser contracts at the WebHost boundary.
///
/// <list type="number">
///   <item>No env vars → middleware NOT registered → forged X-Forwarded-For ignored →
///         RemoteIpAddress is the loopback test peer; startup log mode=disabled.</item>
///   <item>KnownProxies=127.0.0.1 → middleware registered → X-Forwarded-For from the
///         (trusted) loopback peer is honored; startup log trustedProxies=1.</item>
///   <item>KnownNetworks=127.0.0.0/8 → middleware registered → header from inside the
///         CIDR honored; startup log trustedNetworks=1.</item>
///   <item>Whitespace/empty entries trimmed + skipped → behaves like case 2; log
///         trustedProxies=1.</item>
///   <item>Invalid IP → host build throws InvalidOperationException naming the bad value.</item>
///   <item>Invalid CIDR → host build throws InvalidOperationException naming the bad value.</item>
/// </list>
///
/// <para>
/// The production Program.cs reads ForwardedHeaders__KnownProxies / __KnownNetworks at
/// host-build time (before builder.Build()). Each test therefore sets/clears the env vars
/// BEFORE constructing the factory and restores them in a try/finally so no value leaks into
/// sibling tests. The class joins the <c>RateLimitSerial</c> collection so it never runs
/// concurrently with the migrated Phase 15 forwarded-headers factories (which also drive the
/// same process-wide env var) — the disabled-mode case (#1) would otherwise flake if a
/// sibling's <c>ForwardedHeaders__KnownNetworks=0.0.0.0/0</c> were still set during this
/// host's build.
/// </para>
/// </summary>
[Collection("RateLimitSerial")]
[Trait("Category", "SecurityFix")]
public sealed class ForwardedHeadersConditionalTests
{
    private const string ProxiesVar = "ForwardedHeaders__KnownProxies";
    private const string NetworksVar = "ForwardedHeaders__KnownNetworks";

    // Captures RemoteIpAddress seen by a terminal test middleware, keyed by a per-test
    // correlation header value the client sets, so cases run without cross-talk.
    private static readonly ConcurrentDictionary<string, string?> RemoteIpByCorrelation = new();

    // ── Case 1 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoEnvVarsSet_MiddlewareNotRegistered_ForgedXFFIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var priorProxies = Environment.GetEnvironmentVariable(ProxiesVar);
        var priorNetworks = Environment.GetEnvironmentVariable(NetworksVar);
        try
        {
            Environment.SetEnvironmentVariable(ProxiesVar, null);
            Environment.SetEnvironmentVariable(NetworksVar, null);

            await using var factory = new CapturingFactory();
            var correlation = Guid.NewGuid().ToString("N");
            var client = CreateClient(factory, correlation, forwardedFor: "1.2.3.4");

            using var resp = await client.GetAsync("/__forwarded-probe", ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var remoteIp = RemoteIpByCorrelation[correlation];
            // Middleware NOT registered → the forged X-Forwarded-For is ignored.
            Assert.NotEqual("1.2.3.4", remoteIp);

            AssertStartupLog(factory, "disabled", proxyCount: 0, networkCount: 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxiesVar, priorProxies);
            Environment.SetEnvironmentVariable(NetworksVar, priorNetworks);
        }
    }

    // ── Case 2 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KnownProxiesSetToSingleIP_HeaderFromThatIpHonored()
    {
        var ct = TestContext.Current.CancellationToken;
        var priorProxies = Environment.GetEnvironmentVariable(ProxiesVar);
        var priorNetworks = Environment.GetEnvironmentVariable(NetworksVar);
        try
        {
            // The TestServer's synthetic peer is the loopback address; trust it so the
            // spoofed X-Forwarded-For rewrites RemoteIpAddress.
            Environment.SetEnvironmentVariable(ProxiesVar, "127.0.0.1");
            Environment.SetEnvironmentVariable(NetworksVar, null);

            await using var factory = new CapturingFactory();
            var correlation = Guid.NewGuid().ToString("N");
            var client = CreateClient(factory, correlation, forwardedFor: "1.2.3.4");

            using var resp = await client.GetAsync("/__forwarded-probe", ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var remoteIp = RemoteIpByCorrelation[correlation];
            Assert.Equal("1.2.3.4", remoteIp);

            AssertStartupLog(factory, "enabled", proxyCount: 1, networkCount: 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxiesVar, priorProxies);
            Environment.SetEnvironmentVariable(NetworksVar, priorNetworks);
        }
    }

    // ── Case 3 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KnownNetworksSetToCidr_HeaderFromInsideHonored()
    {
        var ct = TestContext.Current.CancellationToken;
        var priorProxies = Environment.GetEnvironmentVariable(ProxiesVar);
        var priorNetworks = Environment.GetEnvironmentVariable(NetworksVar);
        try
        {
            Environment.SetEnvironmentVariable(ProxiesVar, null);
            Environment.SetEnvironmentVariable(NetworksVar, "127.0.0.0/8");

            await using var factory = new CapturingFactory();
            var correlation = Guid.NewGuid().ToString("N");
            var client = CreateClient(factory, correlation, forwardedFor: "5.6.7.8");

            using var resp = await client.GetAsync("/__forwarded-probe", ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var remoteIp = RemoteIpByCorrelation[correlation];
            Assert.Equal("5.6.7.8", remoteIp);

            AssertStartupLog(factory, "enabled", proxyCount: 0, networkCount: 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxiesVar, priorProxies);
            Environment.SetEnvironmentVariable(NetworksVar, priorNetworks);
        }
    }

    // ── Case 4 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhitespaceAndEmptyEntries_TrimmedAndSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var priorProxies = Environment.GetEnvironmentVariable(ProxiesVar);
        var priorNetworks = Environment.GetEnvironmentVariable(NetworksVar);
        try
        {
            Environment.SetEnvironmentVariable(ProxiesVar, " , 127.0.0.1 , , ");
            Environment.SetEnvironmentVariable(NetworksVar, null);

            await using var factory = new CapturingFactory();
            var correlation = Guid.NewGuid().ToString("N");
            var client = CreateClient(factory, correlation, forwardedFor: "1.2.3.4");

            using var resp = await client.GetAsync("/__forwarded-probe", ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var remoteIp = RemoteIpByCorrelation[correlation];
            Assert.Equal("1.2.3.4", remoteIp);

            AssertStartupLog(factory, "enabled", proxyCount: 1, networkCount: 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxiesVar, priorProxies);
            Environment.SetEnvironmentVariable(NetworksVar, priorNetworks);
        }
    }

    // ── Case 5 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidIp_FailsFastAtStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        var priorProxies = Environment.GetEnvironmentVariable(ProxiesVar);
        var priorNetworks = Environment.GetEnvironmentVariable(NetworksVar);
        try
        {
            Environment.SetEnvironmentVariable(ProxiesVar, "172.18.0.500");
            Environment.SetEnvironmentVariable(NetworksVar, null);

            await using var factory = new CapturingFactory();

            // Force host start; the fail-fast Configure<ForwardedHeadersOptions> callback runs
            // during start (UseForwardedHeaders resolves the options), throwing on first access.
            // The factory also captures the original exception at its source as a belt-and-braces
            // fallback (see CapturingFactory.CreateHost / StartupException).
            var thrown = Record.Exception(() => _ = factory.Services);
            AssertFailFastMessage(new[] { factory.StartupException, thrown },
                "ForwardedHeaders__KnownProxies contains invalid IP: '172.18.0.500'");
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxiesVar, priorProxies);
            Environment.SetEnvironmentVariable(NetworksVar, priorNetworks);
        }
    }

    // ── Case 6 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidCidr_FailsFastAtStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        var priorProxies = Environment.GetEnvironmentVariable(ProxiesVar);
        var priorNetworks = Environment.GetEnvironmentVariable(NetworksVar);
        try
        {
            Environment.SetEnvironmentVariable(ProxiesVar, null);
            Environment.SetEnvironmentVariable(NetworksVar, "not-a-cidr");

            await using var factory = new CapturingFactory();

            var thrown = Record.Exception(() => _ = factory.Services);
            AssertFailFastMessage(new[] { factory.StartupException, thrown },
                "ForwardedHeaders__KnownNetworks contains invalid CIDR: 'not-a-cidr'");
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxiesVar, priorProxies);
            Environment.SetEnvironmentVariable(NetworksVar, priorNetworks);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that some exception was thrown during host startup, and that an
    /// InvalidOperationException with EXACTLY the given message appears somewhere in the
    /// exception tree (the host bootstrap may wrap the fail-fast exception in an
    /// AggregateException or rethrow a disposed-provider error alongside it).
    /// </summary>
    private static void AssertFailFastMessage(Exception?[] candidates, string expectedMessage)
    {
        var nonNull = candidates.Where(e => e is not null).Cast<Exception>().ToList();
        Assert.NotEmpty(nonNull);
        var found = nonNull
            .SelectMany(FlattenExceptions)
            .OfType<InvalidOperationException>()
            .Any(e => e.Message == expectedMessage);
        Assert.True(found,
            $"expected an InvalidOperationException with message '{expectedMessage}' in the " +
            $"exception tree(s); got: {string.Join(" | ", nonNull.Select(e => e.GetType().Name + ": " + e.Message))}");
    }

    private static IEnumerable<Exception> FlattenExceptions(Exception ex)
    {
        yield return ex;
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
                foreach (var e in FlattenExceptions(inner))
                    yield return e;
        }
        else if (ex.InnerException is not null)
        {
            foreach (var e in FlattenExceptions(ex.InnerException))
                yield return e;
        }
    }

    private static HttpClient CreateClient(CapturingFactory factory, string correlation, string forwardedFor)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        client.DefaultRequestHeaders.Add("X-Test-Correlation", correlation);
        return client;
    }

    /// <summary>
    /// Asserts the M-SEC-05 startup log line was emitted with the given mode + counts. Matches
    /// the structured event by its message template (stable, not affected by Serilog's
    /// quote-wrapping of string property values when rendered) and then checks the bound
    /// property values.
    /// </summary>
    private static void AssertStartupLog(
        CapturingFactory factory, string mode, int proxyCount, int networkCount)
    {
        const string template =
            "M-SEC-05: ForwardedHeaders middleware: mode={Mode} trustedProxies={ProxyCount} trustedNetworks={NetworkCount}";

        var match = factory.CapturedEvents.FirstOrDefault(e =>
            e.MessageTemplate.Text == template);
        Assert.True(match is not null,
            "expected an M-SEC-05 ForwardedHeaders startup log event; none captured.");

        Assert.Equal(mode, PropertyString(match!, "Mode"));
        Assert.Equal(proxyCount.ToString(), PropertyString(match!, "ProxyCount"));
        Assert.Equal(networkCount.ToString(), PropertyString(match!, "NetworkCount"));
    }

    private static string? PropertyString(LogEvent e, string name)
    {
        if (!e.Properties.TryGetValue(name, out var value)) return null;
        // ScalarValue.ToString() wraps strings in quotes; render via the value's
        // underlying scalar to get the raw form.
        return value is ScalarValue sv ? sv.Value?.ToString() : value.ToString();
    }

    /// <summary>
    /// In-memory SQLite factory (SmokeFactoryFixture pattern) that ALSO swaps the Serilog
    /// pipeline for an InMemorySink so the M-SEC-05 startup log line is observable, and adds
    /// a terminal probe middleware at <c>/__forwarded-probe</c> capturing the rewritten (or
    /// not) RemoteIpAddress keyed by the X-Test-Correlation header. The middleware is added
    /// LAST in the pipeline (after UseForwardedHeaders if registered) so it observes the
    /// final RemoteIpAddress value.
    /// </summary>
    private sealed class CapturingFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _dataSource = $"FwdHdrCond-{Guid.NewGuid():N}";
        private readonly InMemorySink _sink = new();
        private SqliteConnection? _keepAlive;

        // ci-test-data-writability (residual fix) -- redirect DATA_ROOT/LOGS_ROOT to a writable
        // per-instance temp dir BEFORE the real Program.cs bootstrap runs. On a clean CI runner the
        // writability probe would otherwise abort host startup BEFORE the ForwardedHeaders validation
        // under test could throw -- masking the intended fail-fast. Restored on dispose.
        private readonly ApStatsForOpenWrt.Tests.TestSupport.TempBootstrapRoots _roots = new();

        public CapturingFactory()
        {
            _keepAlive = new SqliteConnection($"Data Source={_dataSource};Mode=Memory;Cache=Shared");
            _keepAlive.Open();
        }

        public IReadOnlyList<LogEvent> CapturedEvents => _sink.Snapshot();

        /// <summary>
        /// The exception thrown while starting the host, if any. The fail-fast
        /// ForwardedHeaders parser throws an InvalidOperationException during host start
        /// (UseForwardedHeaders resolves IOptions&lt;ForwardedHeadersOptions&gt;, running the
        /// Configure callback). WebApplicationFactory swallows that original exception and
        /// surfaces an ObjectDisposedException on subsequent access, so we capture the real
        /// one here at its source.
        /// </summary>
        public Exception? StartupException { get; private set; }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // LAST UseSerilog wins — override the production pipeline so the startup log
            // line (emitted via app.Services ILoggerFactory) lands in the in-memory sink.
            builder.UseSerilog((ctx, services, lc) => lc
                .Enrich.FromLogContext()
                .MinimumLevel.Information()
                .WriteTo.Sink(_sink));
            try
            {
                return base.CreateHost(builder);
            }
            catch (Exception ex)
            {
                StartupException = ex;
                throw;
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IDbContextFactory<DatabaseContext>));
                if (dbDescriptor is not null) services.Remove(dbDescriptor);
                services.AddDbContextFactory<DatabaseContext>(opts =>
                    opts.UseSqlite($"Data Source={_dataSource};Mode=Memory;Cache=Shared"));

                // Append a terminal probe middleware at the START of the pipeline's tail via
                // an IStartupFilter. The filter wraps the production Configure so our middleware
                // runs AFTER all of Program.cs's app.Use* registrations — including
                // UseForwardedHeaders() when it is conditionally registered — so it observes the
                // final (possibly rewritten) RemoteIpAddress. It short-circuits /__forwarded-probe
                // so the request never reaches Blazor/auth.
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(
                    new ProbeStartupFilter(ex => StartupException = ex));
            });
        }

        public new async ValueTask DisposeAsync()
        {
            _keepAlive?.Dispose();
            _keepAlive = null;
            _roots.Dispose();
            await base.DisposeAsync();
        }
    }

    /// <summary>
    /// Inserts a probe middleware at the FRONT of the production pipeline. Because it sits
    /// before Program.cs's <c>UseForwardedHeaders()</c> (when registered), it captures
    /// <c>RemoteIpAddress</c> AFTER awaiting <c>next()</c> — by which point the forwarded-headers
    /// middleware (which runs during the inbound leg) has already rewritten the value. For the
    /// dedicated <c>/__forwarded-probe</c> path it records the IP (keyed by the X-Test-Correlation
    /// header) and writes a 200 so the test client gets a clean response.
    /// </summary>
    private sealed class ProbeStartupFilter(Action<Exception> onStartupException)
        : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                // Eagerly resolve the ForwardedHeadersOptions BEFORE the production pipeline
                // builds, so the fail-fast Configure<ForwardedHeadersOptions> callback runs at
                // a point we control. Its InvalidOperationException is recorded for the
                // fail-fast tests (the framework would otherwise swallow it and surface only a
                // disposed-provider error). The throw is re-raised so host start still fails.
                try
                {
                    _ = app.ApplicationServices
                        .GetRequiredService<Microsoft.Extensions.Options.IOptions<
                            Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>()
                        .Value;
                }
                catch (Exception ex)
                {
                    onStartupException(ex);
                    throw;
                }

                app.Use(async (ctx, nextMw) =>
                {
                    var isProbe = ctx.Request.Path == "/__forwarded-probe";
                    await nextMw();
                    if (isProbe)
                    {
                        var correlation = ctx.Request.Headers["X-Test-Correlation"].ToString();
                        RemoteIpByCorrelation[correlation] = ctx.Connection.RemoteIpAddress?.ToString();
                        if (!ctx.Response.HasStarted)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status200OK;
                        }
                    }
                });
                next(app);
            };
    }
}
