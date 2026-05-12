using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Volumes;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Smoke;

/// <summary>
/// IAsyncLifetime fixture for the OpenWRT-AP-Stats Docker smoke tests.
/// Builds the image ONCE via <see cref="ImageFromDockerfileBuilder"/> and creates
/// GUID-scoped named volumes per test run so concurrent CI invocations do not collide.
///
/// Phase 7 / D-06 / D-09 / RESEARCH Finding #5. Smoke fixtures run inside the
/// <c>[Collection("DockerSmoke")]</c> which disables parallelization (RESEARCH Risk #2 —
/// avoids two test classes racing on the build).
/// </summary>
public sealed class DockerSmokeFixture : IAsyncLifetime
{
    public IFutureDockerImage Image { get; private set; } = null!;
    public IVolume DataVolume { get; private set; } = null!;
    public IVolume LogsVolume { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // ImageFromDockerfileBuilder.WithDockerfileDirectory(GetSolutionDirectory(), "")
        // walks up from the test binary to the ApStatsForOpenWrt.sln directory and uses
        // it as the Docker build context. WithDockerfile("Dockerfile") is the verbatim
        // shape from PATTERNS.md / RESEARCH Finding #5.
        Image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), string.Empty)
            .WithDockerfile("Dockerfile")
            .WithName("openwrtapstats:smoke")
            .WithCleanUp(true)
            .Build();
        await Image.CreateAsync();

        // Two GUID-scoped named volumes so concurrent runs (e.g. CI on a busy runner)
        // do not collide on volume names. WithCleanUp(true) drops the volume after the
        // fixture's DisposeAsync — the SECOND container instance in
        // DockerComposeRoundtripSmokeTests still re-uses the SAME volume reference
        // because the disposal happens at fixture teardown, not container teardown.
        DataVolume = new VolumeBuilder()
            .WithName($"openwrtapstats-data-smoke-{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();
        await DataVolume.CreateAsync();

        LogsVolume = new VolumeBuilder()
            .WithName($"openwrtapstats-logs-smoke-{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();
        await LogsVolume.CreateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataVolume.DisposeAsync();
        await LogsVolume.DisposeAsync();
        await Image.DisposeAsync();
    }
}
