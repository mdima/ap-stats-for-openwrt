using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ApStatsForOpenWrt.Data;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Auth;

/// <summary>
/// M-HRD-05 / T9 (plan 21-03) — the auth cookie <c>SecurePolicy</c> is proxy-conditional,
/// with ZERO new knobs.
///
/// <para>
/// The trigger is the EXISTING <c>forwardedHeadersEnabled</c> boolean (Program.cs ~:49),
/// computed from <c>ForwardedHeaders__KnownProxies</c>/<c>__KnownNetworks</c>:
/// </para>
/// <list type="bullet">
///   <item>A proxy env var is set → <see cref="CookieSecurePolicy.Always"/> (the internal
///         leg behind a TLS-terminating proxy must require Secure — Information Disclosure
///         mitigation T-21-05).</item>
///   <item>No proxy env → <see cref="CookieSecurePolicy.SameAsRequest"/> (the plain-HTTP LAN
///         default is preserved so unencrypted LAN login still works).</item>
/// </list>
///
/// <para>
/// Drives the conditional via env-var presence (the same approach as
/// <c>ForwardedHeadersConditionalTests</c>), reading the resolved
/// <see cref="CookieAuthenticationOptions"/> from the host's service provider. Joins the
/// <c>RateLimitSerial</c> collection so the process-wide ForwardedHeaders env vars never leak
/// between cases.
/// </para>
/// </summary>
[Collection("RateLimitSerial")]
[Trait("Category", "SecurityFix")]
public sealed class CookieSecurePolicyTests
{
    private const string ProxiesVar = "ForwardedHeaders__KnownProxies";
    private const string NetworksVar = "ForwardedHeaders__KnownNetworks";

    private static CookieSecurePolicy ResolveSecurePolicy(WebApplicationFactory<Program> factory)
    {
        // Force host start, then read the resolved cookie options for the "Cookies" scheme.
        _ = factory.Services;
        var monitor = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var opts = monitor.Get(CookieAuthenticationDefaults.AuthenticationScheme);
        return opts.Cookie.SecurePolicy;
    }

    // ── Case 1: proxy env present → Always ──────────────────────────────────────

    [Fact]
    public async Task ProxyEnvSet_SecurePolicyIsAlways()
    {
        var priorProxies = Environment.GetEnvironmentVariable(ProxiesVar);
        var priorNetworks = Environment.GetEnvironmentVariable(NetworksVar);
        try
        {
            Environment.SetEnvironmentVariable(ProxiesVar, "127.0.0.1");
            Environment.SetEnvironmentVariable(NetworksVar, null);

            await using var factory = new CookieFactory();
            Assert.Equal(CookieSecurePolicy.Always, ResolveSecurePolicy(factory));
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxiesVar, priorProxies);
            Environment.SetEnvironmentVariable(NetworksVar, priorNetworks);
        }
    }

    // ── Case 2: network env present → Always ────────────────────────────────────

    [Fact]
    public async Task NetworkEnvSet_SecurePolicyIsAlways()
    {
        var priorProxies = Environment.GetEnvironmentVariable(ProxiesVar);
        var priorNetworks = Environment.GetEnvironmentVariable(NetworksVar);
        try
        {
            Environment.SetEnvironmentVariable(ProxiesVar, null);
            Environment.SetEnvironmentVariable(NetworksVar, "127.0.0.0/8");

            await using var factory = new CookieFactory();
            Assert.Equal(CookieSecurePolicy.Always, ResolveSecurePolicy(factory));
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxiesVar, priorProxies);
            Environment.SetEnvironmentVariable(NetworksVar, priorNetworks);
        }
    }

    // ── Case 3: no proxy env → SameAsRequest ────────────────────────────────────

    [Fact]
    public async Task NoProxyEnv_SecurePolicyIsSameAsRequest()
    {
        var priorProxies = Environment.GetEnvironmentVariable(ProxiesVar);
        var priorNetworks = Environment.GetEnvironmentVariable(NetworksVar);
        try
        {
            Environment.SetEnvironmentVariable(ProxiesVar, null);
            Environment.SetEnvironmentVariable(NetworksVar, null);

            await using var factory = new CookieFactory();
            Assert.Equal(CookieSecurePolicy.SameAsRequest, ResolveSecurePolicy(factory));
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProxiesVar, priorProxies);
            Environment.SetEnvironmentVariable(NetworksVar, priorNetworks);
        }
    }

    /// <summary>
    /// In-memory SQLite WebApplicationFactory (SmokeFactoryFixture shape). Each instance is
    /// built AFTER the env vars are set so the host-build-time <c>forwardedHeadersEnabled</c>
    /// computation observes them.
    /// </summary>
    private sealed class CookieFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _dataSource = $"CookieSecPolicy-{Guid.NewGuid():N}";
        private SqliteConnection? _keepAlive;

        // ci-test-data-writability (residual fix) -- redirect DATA_ROOT/LOGS_ROOT to a writable
        // per-instance temp dir BEFORE the real Program.cs bootstrap runs, so the writability
        // probe + MigrationsHostedService.CreateDirectory do not abort host startup on a non-root
        // CI runner with an unwritable /data. Field initializer => constructed in the ctor, before
        // the first CreateClient()/Services access triggers the host build. Restored on dispose.
        private readonly ApStatsForOpenWrt.Tests.TestSupport.TempBootstrapRoots _roots = new();

        public CookieFactory()
        {
            _keepAlive = new SqliteConnection($"Data Source={_dataSource};Mode=Memory;Cache=Shared");
            _keepAlive.Open();
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
