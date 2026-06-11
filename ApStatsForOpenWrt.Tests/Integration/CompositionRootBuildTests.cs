using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// Phase 8 hardening (added 2026-05-08 alongside the phase8-prefs-captive-dep fix) —
/// asserts that the real composition root in <c>Program.cs</c> survives
/// <see cref="ServiceProviderOptions.ValidateScopes"/> + <see cref="ServiceProviderOptions.ValidateOnBuild"/>
/// without throwing the captive-dependency <see cref="System.AggregateException"/> that
/// the operator hit on VS F5.
///
/// <para>
/// Why this test is necessary: <see cref="WebApplicationFactory{TEntryPoint}"/> defaults
/// to a non-Development hosting environment ("Testing" in the existing
/// <see cref="SmokeFactoryFixture"/>), and ASP.NET Core only enables
/// <c>ValidateScopes</c>/<c>ValidateOnBuild</c> in Development. Without this assertion,
/// 478 green tests (including all Integration suite tests) can still mask a captive-dep
/// bug — exactly what happened at HEAD <c>0cb0f51</c> with
/// <c>PreferencesService</c> registered Singleton over a Scoped <c>IJSRuntime</c>.
/// </para>
///
/// <para>
/// Failure mode: if any future refactor reintroduces a captive-dep (Singleton consuming
/// Scoped, or Scoped consuming Transient with disposal concerns), this test fails fast
/// with the exact <c>InvalidOperationException</c> message naming the offending services.
/// </para>
/// </summary>
[Collection("Integration")]
public sealed class CompositionRootBuildTests
{
    /// <summary>
    /// Builds the same composition root <see cref="Program"/> sets up, but with the DI
    /// validators forced on. The DbContextFactory is swapped to in-memory SQLite (mirrors
    /// <see cref="SmokeFactoryFixture"/>) so the test does not write to <c>/data</c>.
    /// </summary>
    [Fact]
    public void CompositionRoot_BuildsCleanly_WithValidateScopesAndValidateOnBuild()
    {
        var dataSource = $"CompRoot-{System.Guid.NewGuid():N}";
        using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={dataSource};Mode=Memory;Cache=Shared");
        keepAlive.Open();

        // Subclass the WAF so we can override CreateHost and force the
        // ServiceProviderOptions before WebApplication.Build() runs.
        using var factory = new ValidatingFactory(dataSource);

        // CreateClient() forces host startup; this is the line that throws if any
        // captive-dep is present. We don't issue a request — just assert no throw.
        var ex = Record.Exception(() => factory.CreateClient());

        Assert.Null(ex);
    }

    /// <summary>
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> subclass that swaps the production
    /// SQLite connection for in-memory and explicitly enables <c>ValidateScopes</c> +
    /// <c>ValidateOnBuild</c>. The latter is the gate that fired on the operator's VS F5
    /// run; turning it back on in tests makes future captive-dep regressions fail in CI
    /// instead of at first dev launch.
    /// </summary>
    private sealed class ValidatingFactory : WebApplicationFactory<Program>
    {
        private readonly string _dataSource;

        // Redirect DATA_ROOT/LOGS_ROOT to a writable temp dir for the host's lifetime so the
        // real Program.cs filesystem bootstrap (writability probe + Migrations CreateDirectory)
        // does not abort startup on a non-root CI runner with an unwritable /data. Constructed
        // BEFORE CreateClient() (in the ctor) so the env var is live when the bootstrap chain runs.
        private readonly TempBootstrapRoots _roots = new();

        public ValidatingFactory(string dataSource)
        {
            _dataSource = dataSource;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            // Force the validators ON. UseDefaultServiceProvider runs after the host
            // builder's own configuration, so this overrides whatever Development /
            // Testing default would otherwise apply.
            builder.UseDefaultServiceProvider(opts =>
            {
                opts.ValidateScopes = true;
                opts.ValidateOnBuild = true;
            });

            builder.ConfigureServices(services =>
            {
                // Mirror SmokeFactoryFixture's DB swap so tests don't depend on /data
                // being writable. The /data/keys directory is produced by ASP.NET Data
                // Protection under the redirected DATA_ROOT temp dir, which is writable.
                var dbDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IDbContextFactory<DatabaseContext>));
                if (dbDescriptor is not null) services.Remove(dbDescriptor);

                services.AddDbContextFactory<DatabaseContext>(opts =>
                    opts.UseSqlite($"Data Source={_dataSource};Mode=Memory;Cache=Shared"));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _roots.Dispose();
        }
    }
}
