using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApStatsForOpenWrt.Data;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Security;

/// <summary>
/// M-HRD-04 / D-07 (plan 21-03) — the <c>Csp__ConnectSrc</c> fail-loud knob.
///
/// <para>
/// The CSP <c>connect-src</c> directive tightens from <c>'self' ws: wss:</c> to <c>'self'</c>.
/// Proxied deployments append their WebSocket origin via the optional, comma-separated
/// <c>Csp__ConnectSrc</c> env var. Each origin is validated at startup against
/// <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> PLUS a scheme allow-list of
/// <c>ws</c>/<c>wss</c> ONLY (WR-02 / 21-REVIEW: <c>http</c>/<c>https</c> are rejected — the knob
/// exists solely to enable the SignalR <c>ws</c> upgrade, and an http(s) entry would only broaden
/// the fetch/XHR allow-list), AND a bare-origin check (no path/query/fragment). A malformed,
/// disallowed-scheme, or non-origin value throws <see cref="InvalidOperationException"/> at
/// startup — mirroring the <c>ForwardedHeaders__*</c> throw-on-invalid pattern (D-07: a
/// misconfigured security knob never silently degrades).
/// </para>
///
/// <list type="number">
///   <item>Malformed origin (not a URI) → host build throws naming the bad value.</item>
///   <item>Disallowed scheme (<c>ftp://</c>) → host build throws naming the bad value.</item>
///   <item>Disallowed scheme (<c>https://</c>) → host build throws (WR-02: ws/wss only).</item>
///   <item>Origin carrying a path (<c>wss://x/y</c>) → host build throws (WR-02: bare origins only).</item>
///   <item>Valid <c>wss://</c> origin → appended to <c>connect-src 'self'</c> in the emitted CSP.</item>
///   <item>No env var → CSP <c>connect-src 'self'</c> with NO appended origins, NO <c>ws:</c>/<c>wss:</c> literals.</item>
/// </list>
///
/// <para>
/// Like <see cref="ForwardedHeadersConditionalTests"/>, the production Program.cs reads
/// <c>Csp__ConnectSrc</c> at host-build time, so each test sets/clears the env var BEFORE
/// constructing the factory and restores it in a finally. Joins the <c>RateLimitSerial</c>
/// collection so a sibling's env var can never leak into the no-env case.
/// </para>
/// </summary>
[Collection("RateLimitSerial")]
[Trait("Category", "SecurityFix")]
public sealed class CspConnectSrcConfigTests
{
    private const string ConnectSrcVar = "Csp__ConnectSrc";

    private static string? GetCsp(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("Content-Security-Policy", out var v1)) return string.Join(", ", v1);
        if (resp.Content.Headers.TryGetValues("Content-Security-Policy", out var v2)) return string.Join(", ", v2);
        return null;
    }

    // ── Case 1: malformed origin → fail-fast at startup ─────────────────────────

    [Fact]
    public async Task MalformedOrigin_FailsFastAtStartup()
    {
        var prior = Environment.GetEnvironmentVariable(ConnectSrcVar);
        try
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, "not-a-uri");

            await using var factory = new CspFactory();
            var thrown = Record.Exception(() => _ = factory.Services);

            AssertFailFastMessage(new[] { factory.StartupException, thrown },
                "Csp__ConnectSrc contains invalid origin: 'not-a-uri'");
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, prior);
        }
    }

    // ── Case 2: disallowed scheme → fail-fast at startup ────────────────────────

    [Fact]
    public async Task DisallowedScheme_FailsFastAtStartup()
    {
        var prior = Environment.GetEnvironmentVariable(ConnectSrcVar);
        try
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, "ftp://x.example.com");

            await using var factory = new CspFactory();
            var thrown = Record.Exception(() => _ = factory.Services);

            AssertFailFastMessage(new[] { factory.StartupException, thrown },
                "Csp__ConnectSrc contains invalid origin: 'ftp://x.example.com'");
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, prior);
        }
    }

    // ── Case 2b (WR-02): http/https scheme → fail-fast (ws/wss only) ────────────

    [Fact]
    public async Task HttpsScheme_FailsFastAtStartup()
    {
        var prior = Environment.GetEnvironmentVariable(ConnectSrcVar);
        try
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, "https://proxy.example.com");

            await using var factory = new CspFactory();
            var thrown = Record.Exception(() => _ = factory.Services);

            AssertFailFastMessage(new[] { factory.StartupException, thrown },
                "Csp__ConnectSrc contains invalid origin: 'https://proxy.example.com'");
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, prior);
        }
    }

    // ── Case 2c (WR-02): origin carrying a path → fail-fast (bare origins only) ──

    [Fact]
    public async Task OriginWithPath_FailsFastAtStartup()
    {
        var prior = Environment.GetEnvironmentVariable(ConnectSrcVar);
        try
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, "wss://proxy.example.com/hub");

            await using var factory = new CspFactory();
            var thrown = Record.Exception(() => _ = factory.Services);

            AssertFailFastMessage(new[] { factory.StartupException, thrown },
                "Csp__ConnectSrc contains invalid origin: 'wss://proxy.example.com/hub'");
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, prior);
        }
    }

    // ── Case 3: valid wss:// origin → appended to connect-src 'self' ────────────

    [Fact]
    public async Task ValidWssOrigin_AppendedToConnectSrc()
    {
        var ct = TestContext.Current.CancellationToken;
        var prior = Environment.GetEnvironmentVariable(ConnectSrcVar);
        try
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, "wss://proxy.example.com");

            await using var factory = new CspFactory();
            using var resp = await factory.CreateClient().GetAsync("/", ct);

            var csp = GetCsp(resp);
            Assert.NotNull(csp);
            // 'self' baseline preserved AND the validated origin appended.
            Assert.Contains("connect-src 'self' wss://proxy.example.com", csp!);
            // The tightened directive must NOT carry the old ws:/wss: wildcards.
            Assert.DoesNotContain("connect-src 'self' ws: wss:", csp!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, prior);
        }
    }

    // ── Case 4: no env var → tightened connect-src 'self', no literals ──────────

    [Fact]
    public async Task NoEnvVar_ConnectSrcIsSelfOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var prior = Environment.GetEnvironmentVariable(ConnectSrcVar);
        try
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, null);

            await using var factory = new CspFactory();
            using var resp = await factory.CreateClient().GetAsync("/", ct);

            var csp = GetCsp(resp);
            Assert.NotNull(csp);
            Assert.Contains("connect-src 'self';", csp!);
            Assert.DoesNotContain("ws:", csp!);
            Assert.DoesNotContain("wss:", csp!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectSrcVar, prior);
        }
    }

    // ── Helpers (mirrors ForwardedHeadersConditionalTests) ──────────────────────

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

    /// <summary>
    /// In-memory SQLite WebApplicationFactory (SmokeFactoryFixture shape) capturing any
    /// host-start exception at its source — the fail-loud Csp__ConnectSrc validation throws
    /// during host build (Program.cs top-level, like ForwardedHeaders), which
    /// WebApplicationFactory would otherwise mask behind a disposed-provider error.
    /// </summary>
    private sealed class CspFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _dataSource = $"CspConnSrc-{Guid.NewGuid():N}";
        private SqliteConnection? _keepAlive;

        // ci-test-data-writability (residual fix) -- redirect DATA_ROOT/LOGS_ROOT to a writable
        // per-instance temp dir BEFORE the real Program.cs bootstrap runs. On a clean CI runner the
        // writability probe would otherwise abort host startup BEFORE the Csp__ConnectSrc validation
        // under test could throw -- masking the intended fail-fast. Restored on dispose.
        private readonly ApStatsForOpenWrt.Tests.TestSupport.TempBootstrapRoots _roots = new();

        public CspFactory()
        {
            _keepAlive = new SqliteConnection($"Data Source={_dataSource};Mode=Memory;Cache=Shared");
            _keepAlive.Open();
        }

        public Exception? StartupException { get; private set; }

        protected override IHost CreateHost(IHostBuilder builder)
        {
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
}
