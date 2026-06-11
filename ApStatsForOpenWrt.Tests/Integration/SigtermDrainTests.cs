using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// ROADMAP success #4 + D-05/D-06 — SIGTERM mid-cycle drain regression test. Brings up the
/// host with a probe-factory stub that blocks on a TaskCompletionSource, calls
/// IHostApplicationLifetime.StopApplication while a cycle is in flight, asserts the orchestrator's
/// existing tx.RollbackAsync(CancellationToken.None) path runs cleanly and no half-commit lands
/// in SQLite. Also asserts HostOptions.ShutdownTimeout default == 30s (D-06) and exercises the
/// W6 fresh-connection-after-stop "no database is locked on next boot" check.
/// </summary>
[Collection("Integration")]
public sealed class SigtermDrainTests
{
    [Fact]
    public void HostOptions_ShutdownTimeout_DefaultIs30Seconds()
    {
        // D-06 — HostOptions.ShutdownTimeout keeps its .NET 10 default of 30s. Not exposed as
        // a setting in v1. This test makes any future regression (e.g. if a developer sets a
        // shorter timeout chasing a CI win) provably loud.
        var builder = Host.CreateApplicationBuilder();
        using var host = builder.Build();

        var hostOptions = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(30), hostOptions.ShutdownTimeout);
    }

    [Fact]
    public async Task Stop_DuringInFlightPoll_RollsBackTransaction_NoHalfCommit()
    {
        // D-05 — best-effort cancel + rollback. Mid-cycle SIGTERM observes cancellation,
        // tx.RollbackAsync(CancellationToken.None) executes, no half-commit lands in SQLite.
        // ROADMAP success #4: "Sending SIGTERM during an in-flight poll cycle causes every
        // SshClient to be disconnected/disposed within the configured shutdown timeout; the
        // SQLite WAL is checkpointed and no `database is locked` error appears on next boot."
        var ct = TestContext.Current.CancellationToken;

        // The fixture replaces the production probe-factory seam (Func<WiFiNode, CT, Task<NodeProbeResult>>
        // — Program.cs line 89) with a stub that blocks on ProbeBlocker until the test releases it.
        await using var f = new SigtermFixture();
        await f.InitializeAsync();
        _ = f.CreateClient();      // force host start

        await f.SeedNodeAsync(ct);

        // Open the gate so the polling loop kicks the cold-start cycle. The probe stub awaits
        // f.ProbeBlocker.Task, so once the cycle reaches the probe step it parks until we
        // either complete the TCS or cancel via shutdown.
        var tracker = f.Services.GetRequiredService<ApStatsForOpenWrt.Services.Gating.CircuitTracker>();
        tracker.OnOpened();

        // Give the polling loop a brief window to reach the probe-await point. 200ms is far
        // more than the cold-start kick needs (sub-millisecond in a release build).
        await Task.Delay(200, ct);

        // Trigger graceful shutdown while the cycle is in flight. IHostApplicationLifetime.
        // StopApplication is the in-process equivalent of SIGTERM: it triggers the host's
        // ApplicationStopping/Stopped tokens and runs all IHostedService.StopAsync calls within
        // HostOptions.ShutdownTimeout (30s default).
        var lifetime = f.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();

        // Now release the probe so the cycle observes cancellation cleanly (rollback path).
        f.ProbeBlocker.TrySetResult(true);

        // Wait (bounded) for the host to fully stop. ApplicationStopped fires after every
        // IHostedService.StopAsync has returned.
        await WaitForApplicationStoppedAsync(lifetime, TimeSpan.FromSeconds(10), ct);

        // Verify no half-commit: open a brand-new DbContext on the same shared in-memory DB
        // (the host's DI scope is disposed after StopAsync, so we cannot reuse f.ContextFactory).
        // The seeded node remains exactly as it was (Status=Offline, no LastPollDate update).
        var freshOptions = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite(SigtermFixture.ConnectionString)
            .Options;
        await using var ctx = new DatabaseContext(freshOptions);
        var node = await ctx.WiFiNodes.AsNoTracking().FirstAsync(n => n.Hostname == "ap-test", ct);
        Assert.Equal("ap-test", node.Hostname);                      // pre-cycle seeded row preserved.
        Assert.Equal(Status.Offline, node.Status);                    // mark-Offline-then-upsert rolled back, so still Offline.
        Assert.Equal(default(DateTime), node.LastPollDate);           // never set — rollback discarded the would-be update.
    }

    [Fact]
    public async Task AfterStop_FreshConnectionOpens_WithoutLockError()
    {
        // W6 — exercises ROADMAP §6 SC4: "no `database is locked` error appears on next boot."
        // After shutdown completes, open a FRESH SqliteConnection on the same shared in-memory
        // DB and run `PRAGMA wal_checkpoint(FULL);`. If the orchestrator left a stale lock or a
        // half-flushed WAL, this call surfaces SqliteException("database is locked"); the
        // assertion is that no exception is thrown.
        var ct = TestContext.Current.CancellationToken;

        await using var f = new SigtermFixture();
        await f.InitializeAsync();
        _ = f.CreateClient();
        await f.SeedNodeAsync(ct);

        var tracker = f.Services.GetRequiredService<ApStatsForOpenWrt.Services.Gating.CircuitTracker>();
        tracker.OnOpened();
        await Task.Delay(200, ct);

        var lifetime = f.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();
        f.ProbeBlocker.TrySetResult(true);
        await WaitForApplicationStoppedAsync(lifetime, TimeSpan.FromSeconds(10), ct);

        // Open a brand-new connection AFTER the host has fully stopped.
        await using var freshConn = new SqliteConnection(SigtermFixture.ConnectionString);
        await freshConn.OpenAsync(ct);
        using var cmd = freshConn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(FULL);";
        var ex = await Record.ExceptionAsync(() => cmd.ExecuteScalarAsync(ct));
        Assert.Null(ex);   // SC4: no `database is locked` on next-boot equivalent.
    }

    private static async Task WaitForApplicationStoppedAsync(
        IHostApplicationLifetime lifetime,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = lifetime.ApplicationStopped.Register(() => tcs.TrySetResult());
        if (lifetime.ApplicationStopped.IsCancellationRequested) return;
        await tcs.Task.WaitAsync(timeout, ct);
    }

    /// <summary>
    /// B1 — REVISED FIXTURE. The production probe seam is
    /// <c>Func&lt;WiFiNode, CancellationToken, Task&lt;NodeProbeResult&gt;&gt;</c>
    /// (Program.cs line 89, HostedServiceRegistrationOrderTests.cs line 53,
    /// PollOrchestratorTests.cs `probeAsync` parameter pattern). There is NO
    /// <c>INodeProber</c> interface; the round-1 plan revision invented one. This fixture uses
    /// the canonical delegate-replacement form.
    /// <para>
    /// Uses a unique shared-cache file path so each test run gets a clean DB (avoids cross-run
    /// state leak from the global SQLite file handle).
    /// </para>
    /// <para>
    /// Redirects <c>DATA_ROOT</c>/<c>LOGS_ROOT</c> to a writable temp dir
    /// (<see cref="TempBootstrapRoots"/>) so the real <c>Program.cs</c> filesystem bootstrap
    /// (writability probe + <c>MigrationsHostedService.CreateDirectory</c>) does not abort host
    /// startup on a non-root CI runner with an unwritable <c>/data</c>.
    /// </para>
    /// </summary>
    public sealed class SigtermFixture : WebApplicationFactory<Program>, IAsyncDisposable
    {
        // Stable path for the in-memory shared-cache DB so the W6 fresh-connection test can
        // re-open the same DataSource after the host stops. Per-fixture-instance UUID guarantees
        // test isolation across the [Collection("Integration")] sequencing.
        public static readonly string DataSourceName = $"sigterm-{Guid.NewGuid():N}";
        public static string ConnectionString
            => $"Data Source={DataSourceName};Mode=Memory;Cache=Shared";

        // Constructed at fixture instantiation (before CreateClient) so the env redirect is live
        // when Program.cs's bootstrap chain runs.
        private readonly TempBootstrapRoots _roots = new();
        private SqliteConnection? _keepAlive;

        public TaskCompletionSource<bool> ProbeBlocker { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // In-memory SQLite swap (mirrors SmokeFactoryFixture) — keep-alive opened in
                // InitializeAsync below.
                var dbDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IDbContextFactory<DatabaseContext>));
                if (dbDescriptor is not null) services.Remove(dbDescriptor);

                services.AddDbContextFactory<DatabaseContext>(opts =>
                    opts.UseSqlite(ConnectionString));

                // Replace the production probe-factory delegate (Program.cs line 89) with a
                // stub that blocks on ProbeBlocker until the test releases it. canonical seam:
                // Func<WiFiNode, CancellationToken, Task<NodeProbeResult>>.
                services.RemoveAll<Func<WiFiNode, CancellationToken, Task<NodeProbeResult>>>();
                services.AddSingleton<Func<WiFiNode, CancellationToken, Task<NodeProbeResult>>>(
                    _ => async (node, probeCt) =>
                    {
                        // Wait for the test to either release us or trip the host's stoppingToken.
                        await ProbeBlocker.Task.WaitAsync(probeCt);
                        // Synthetic Success result so the orchestrator's persist path runs. The
                        // result content does not matter — the test exercises the SIGTERM-during-
                        // probe-await path; if we reach here at all, the cycle did not abort.
                        return new NodeProbeResult.Success(
                            NodeId: node.Id,
                            Networks: Array.Empty<WiFiNetwork>(),
                            Stations: Array.Empty<WLANStation>(),
                            NewlyTrustedFingerprint: null);
                    });
            });
        }

        public Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();
            return Task.CompletedTask;
        }

        public async Task SeedNodeAsync(CancellationToken ct)
        {
            using var scope = Services.CreateScope();
            var factory = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<DatabaseContext>>();
            await using var ctx = factory.CreateDbContext();
            ctx.WiFiNodes.Add(new WiFiNode
            {
                Hostname = "ap-test",
                Username = "root",
                Password = "U2FsdGVkX1placeholderCiphertext==",   // ciphertext form — not decrypted by this test
                Status = Status.Offline,
            });
            await ctx.SaveChangesAsync(ct);
        }

        public IDbContextFactory<DatabaseContext> ContextFactory =>
            Services.GetRequiredService<IDbContextFactory<DatabaseContext>>();

        public new async ValueTask DisposeAsync()
        {
            // Release the blocker if the test never did, so any background task awaiting it
            // can complete and the host can fully stop in DisposeAsync.
            ProbeBlocker.TrySetResult(true);
            _keepAlive?.Dispose();
            await base.DisposeAsync();
            _roots.Dispose();
        }
    }
}
