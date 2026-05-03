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

    private IContainer BuildContainer() =>
        new ContainerBuilder("openwrtapstats:smoke")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithVolumeMount(_fixture.DataVolume, "/data")
            .WithVolumeMount(_fixture.LogsVolume, "/logs")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
                r.ForPath("/health").ForStatusCode(HttpStatusCode.OK)))
            .Build();

    [Fact]
    public async Task Container_ReachesHealthy_WithinStartPeriod()
    {
        // Testcontainers 4.11.0 IWaitForContainerOS does NOT expose UntilHealthy()
        // (the API surface in earlier docs is not present in this release; verified
        // via in-tree compile probe). Workaround: wait for the HTTP /health endpoint
        // to return 200 (which is the same surface the Dockerfile HEALTHCHECK CMD
        // probes via curl), then poll IContainer.Health until it transitions to
        // Healthy. start-period=30s + first interval=30s is the worst-case window
        // before docker has a chance to report `healthy`; we give 120s of headroom.
        var ct = TestContext.Current.CancellationToken;
        var container = BuildContainer();
        try
        {
            await container.StartAsync(ct);

            // The /health probe already returned 200 (gated by WithWaitStrategy).
            // Now poll the docker daemon's Health.Status until it reports Healthy.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                if (container.Health == TestcontainersHealthStatus.Healthy)
                {
                    return; // Success — assertion satisfied.
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            Assert.Fail(
                $"Container did not reach `Healthy` within 120s; current status: {container.Health}. " +
                "INF-08 live arm regression — the Dockerfile HEALTHCHECK directive is not " +
                "succeeding inside the container (curl missing? /health returning 503? " +
                "start-period too short?).");
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
