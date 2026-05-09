using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace OpenWrtApStats.Tests.Smoke;

/// <summary>
/// INF-08 live arm — verifies that the production Docker image (built by
/// <see cref="DockerSmokeFixture"/>) reaches the docker-reported <c>healthy</c>
/// state within the HEALTHCHECK start-period window, that the /health endpoint
/// returns the locked Phase 6 D-07 JSON shape from inside the container, and that
/// the container runs as the non-root <c>app</c> user (UID 1654 — INF-07 live arm /
/// RESEARCH Open Question #3).
///
/// All facts carry [Trait("Category","Smoke")] AND [Trait("RequiresDocker","true")]
/// so the default <c>dotnet test -- --filter-not-trait Category=Smoke</c> invocation
/// excludes them. CI's smoke-tests job (ubuntu-latest) runs them with the inverse
/// filter. Phase 7 / D-06 / D-07.
/// </summary>
[Collection("DockerSmoke")]
[Trait("Category", "Smoke")]
[Trait("RequiresDocker", "true")]
public sealed class HealthcheckSmokeTests(DockerSmokeFixture fixture) : IClassFixture<DockerSmokeFixture>
{
    private readonly DockerSmokeFixture _fixture = fixture;

    /// <summary>
    /// Builds a container instance against the smoke image with the default
    /// /health-200 wait strategy used by the JSON-shape and UID smoke tests.
    /// The healthcheck-status test below uses <see cref="BuildHealthyContainer"/>
    /// instead — it must wait on the docker daemon's reported Health.Status, not
    /// just on /health responding.
    /// </summary>
    private IContainer BuildContainer() =>
        new ContainerBuilder("openwrtapstats:smoke")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithVolumeMount(_fixture.DataVolume, "/data")
            .WithVolumeMount(_fixture.LogsVolume, "/logs")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            // Debug session smoke-poll-gate-not-armed (2026-05-09): opt out of the visitor gate.
            // Smoke tests drive the app via raw HTTP only — no Blazor circuit, so CircuitTrackerGate
            // would never arm and the orchestrator would park forever on WaitForVisitorAsync().
            // Polling__GateMode=AlwaysLive binds to IConfiguration key Polling:GateMode and
            // makes Program.cs register AlwaysLiveGate instead. Production deployments leave
            // this unset and keep CircuitTrackerGate.
            .WithEnvironment("Polling__GateMode", "AlwaysLive")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
                r.ForPath("/health").ForPort(8080).ForStatusCode(HttpStatusCode.OK)))
            .Build();

    /// <summary>
    /// Variant of <see cref="BuildContainer"/> that waits on the docker daemon's
    /// reported HEALTHCHECK status (Health.Status == healthy) rather than on a
    /// raw HTTP probe. Required for INF-08 because that is exactly the surface
    /// the test asserts. <c>UntilContainerIsHealthy(failingStreak)</c> polls the
    /// container's State.Health each cycle and returns success the moment the
    /// daemon flips to <c>healthy</c>; on the production Dockerfile this happens
    /// after the first successful CMD (~5–10s after start, well inside
    /// <c>start-period=30s</c>).
    /// </summary>
    private IContainer BuildHealthyContainer() =>
        new ContainerBuilder("openwrtapstats:smoke")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithVolumeMount(_fixture.DataVolume, "/data")
            .WithVolumeMount(_fixture.LogsVolume, "/logs")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            // Same opt-out as BuildContainer — see debug session smoke-poll-gate-not-armed.
            // Even the healthcheck-status test benefits: the daemon healthcheck reaches healthy
            // off /health responding 200 (DbHealthCheck + DataProtectionHealthCheck), independent
            // of whether the orchestrator ever fires a cycle. AlwaysLive does not change that
            // behavior, so this env var is safe to set on the healthy-wait variant too.
            .WithEnvironment("Polling__GateMode", "AlwaysLive")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
            .Build();

    [Fact]
    public async Task Container_ReachesHealthy_WithinStartPeriod()
    {
        // INF-08 live arm: assert the docker-daemon-reported Health.Status reaches
        // `healthy`. We rely on Testcontainers 4.11's `UntilContainerIsHealthy()`
        // wait strategy, which inspects the container each interval and returns
        // success once `State.Health.Status == "healthy"`.
        //
        // Why not poll IContainer.Health from a manual loop?  In Testcontainers
        // 4.11, DockerContainer keeps a *cached* ContainerInspectResponse
        // (`_container`) that is only refreshed in a few lifecycle hooks
        // (StartAsync, StopAsync, the wait-strategy CheckReadinessAsync loop, and
        // pause/unpause). After StartAsync returns, no public API forces a re-
        // inspect, so the property reads stale data forever — typically still
        // "starting" because the snapshot was taken just seconds after run.
        // That's the trap the previous implementation fell into; using a real
        // wait strategy is the only correct path.
        var ct = TestContext.Current.CancellationToken;
        var container = BuildHealthyContainer();
        try
        {
            await container.StartAsync(ct);

            // StartAsync returning successfully under UntilContainerIsHealthy()
            // is itself the assertion — the daemon reported Health.Status =
            // healthy. We re-read the (now-fresh) cached value as a paranoia
            // check so a regression in Testcontainers' wait strategy surfaces
            // here rather than silently passing.
            Assert.Equal(TestcontainersHealthStatus.Healthy, container.Health);
        }
        finally
        {
            await container.StopAsync(CancellationToken.None);
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Health_Endpoint_ReturnsExpectedJsonShape()
    {
        var ct = TestContext.Current.CancellationToken;
        var container = BuildContainer();
        try
        {
            await container.StartAsync(ct);
            var port = container.GetMappedPublicPort(8080);
            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };

            var response = await http.GetAsync("/health", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // D-07 — { status, components: { db, dataProtection }, lastPollAge }
            Assert.True(doc.RootElement.TryGetProperty("status", out var status));
            Assert.Equal("Healthy", status.GetString());

            Assert.True(doc.RootElement.TryGetProperty("components", out var components));
            Assert.Equal("ok", components.GetProperty("db").GetString());
            Assert.Equal("ok", components.GetProperty("dataProtection").GetString());

            Assert.True(doc.RootElement.TryGetProperty("lastPollAge", out var lastPollAge));
            // First boot before any visitor: lastPollAge MAY be null (no poll cycle has fired yet).
            Assert.True(
                lastPollAge.ValueKind == JsonValueKind.Null ||
                (lastPollAge.ValueKind == JsonValueKind.String &&
                 System.Text.RegularExpressions.Regex.IsMatch(
                     lastPollAge.GetString()!, @"^(\d+s|\d+m\d{2}s)$")));
        }
        finally
        {
            await container.StopAsync(CancellationToken.None);
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Container_RunsAsNonRootAppUser_Uid1654()
    {
        // INF-07 live arm — exec `id -u` inside the container and assert the runtime
        // UID is 1654 (the non-root `app` user provided by the aspnet:10.0 base image
        // and preserved by the final `USER app` directive in the Dockerfile).
        // Closes RESEARCH Open Question #3.
        var ct = TestContext.Current.CancellationToken;
        var container = BuildContainer();
        try
        {
            await container.StartAsync(ct);
            var execResult = await container.ExecAsync(new[] { "id", "-u" }, ct);
            Assert.Equal(0, execResult.ExitCode);
            Assert.Equal("1654", execResult.Stdout.Trim());
        }
        finally
        {
            await container.StopAsync(CancellationToken.None);
            await container.DisposeAsync();
        }
    }
}
