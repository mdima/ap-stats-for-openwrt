using ApStatsForOpenWrt.Services.Polling.Ssh;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Renci.SshNet;
using Xunit;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace ApStatsForOpenWrt.Tests.Dropbear;

/// <summary>
/// IAsyncLifetime fixture for the M-FLD-01 DropbearCompat SSH-negotiation arm (Phase 22 / D-07/D-08).
/// Builds BOTH in-repo Dropbear server images (legacy ~2019.78 + current) via
/// <see cref="ImageFromDockerfileBuilder"/> — mirrors <c>Tests/Smoke/DockerSmokeFixture.cs</c> — and
/// starts a container per image with a random host port bound to the Dropbear port 22.
///
/// The crux of M-FLD-01 is <see cref="CreateProductionClient"/>: it constructs the SSH.NET client
/// EXACTLY as <c>RealSshSession.ConnectAsync</c> does (<c>ConnectionInfo</c> + <c>Timeout = 5s</c> +
/// <c>SshAlgorithmFallbackPolicy.ExtendForOldDropbear</c> + <c>new SshClient</c>), so the unit under
/// test is the real production negotiation path, not a hand-rolled client (PATTERNS "SSH
/// production-path parity"). Runs inside <c>[Collection("DropbearCompat")]</c> which disables
/// parallelization (RESEARCH Risk — avoids two test classes racing on the dual image build).
/// </summary>
public sealed class DropbearCompatFixture : IAsyncLifetime
{
    // Throwaway test-only credentials baked into both images (T-22-07).
    private const string SshUser = "apstats";
    private const string SshPassword = "apstats";
    private const int DropbearPort = 22;

    private IFutureDockerImage _legacyImage = null!;
    private IFutureDockerImage _currentImage = null!;
    private IContainer _legacyContainer = null!;
    private IContainer _currentContainer = null!;

    /// <summary>Host + mapped port of the running legacy (~2019.78) Dropbear container.</summary>
    public SshEndpoint LegacyEndpoint { get; private set; } = null!;

    /// <summary>Host + mapped port of the running current Dropbear container.</summary>
    public SshEndpoint CurrentEndpoint { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        // Build both images from the in-repo Dockerfiles. WithDockerfileDirectory points the build
        // context at the solution root; WithDockerfile names the per-image Dockerfile under
        // test/dropbear/{legacy,current}/. Mirrors the DockerSmokeFixture ImageFromDockerfileBuilder
        // shape (the sentinel is authored inline in each Dockerfile, so no extra build context needed).
        _legacyImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), string.Empty)
            .WithDockerfile("test/dropbear/legacy/Dockerfile")
            .WithName("apstats-dropbear-legacy:test")
            .WithCleanUp(true)
            .Build();
        await _legacyImage.CreateAsync(ct);

        _currentImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), string.Empty)
            .WithDockerfile("test/dropbear/current/Dockerfile")
            .WithName("apstats-dropbear-current:test")
            .WithCleanUp(true)
            .Build();
        await _currentImage.CreateAsync(ct);

        // Start a container per image with a random host port bound to 22. Wait until Dropbear's
        // internal TCP port 22 is listening (the foreground server binds it immediately after the
        // RSA host key loads). Construct via the image-name ContainerBuilder ctor (matching the
        // Smoke fixture's string-image pattern) — the parameterless ctor is obsolete in
        // Testcontainers 4.12; the .WithName() above pins each image's tag so the name is stable.
        _legacyContainer = new ContainerBuilder("apstats-dropbear-legacy:test")
            .WithPortBinding(DropbearPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(DropbearPort))
            .WithCleanUp(true)
            .Build();
        await _legacyContainer.StartAsync(ct);

        _currentContainer = new ContainerBuilder("apstats-dropbear-current:test")
            .WithPortBinding(DropbearPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(DropbearPort))
            .WithCleanUp(true)
            .Build();
        await _currentContainer.StartAsync(ct);

        LegacyEndpoint = new SshEndpoint(
            _legacyContainer.Hostname,
            _legacyContainer.GetMappedPublicPort(DropbearPort),
            SshUser,
            SshPassword);
        CurrentEndpoint = new SshEndpoint(
            _currentContainer.Hostname,
            _currentContainer.GetMappedPublicPort(DropbearPort),
            SshUser,
            SshPassword);
    }

    /// <summary>
    /// PRODUCTION-PARITY SSH client factory (M-FLD-01 crux). Reproduces the exact
    /// <c>RealSshSession.ConnectAsync</c> construction (RealSshSession.cs:42-54): a
    /// <see cref="ConnectionInfo"/> with <c>PasswordAuthenticationMethod</c> + the 5-second
    /// POLL-04 belt timeout, extended via <see cref="SshAlgorithmFallbackPolicy.ExtendForOldDropbear"/>
    /// (NOT a hand-rolled algorithm list — accessed via the existing InternalsVisibleTo seam), then a
    /// fresh <see cref="SshClient"/>. The caller owns Connect()/Dispose() lifecycle (mirrors how the
    /// production poller drives the session).
    /// </summary>
    public SshClient CreateProductionClient(SshEndpoint endpoint)
    {
        var connInfo = new ConnectionInfo(
            endpoint.Host,
            endpoint.Port,
            endpoint.User,
            new PasswordAuthenticationMethod(endpoint.User, endpoint.Password))
        {
            Timeout = TimeSpan.FromSeconds(5)        // POLL-04 belt — verbatim from RealSshSession
        };

        // Pitfall 1: extend the algorithm lists (does not replace) — same call the production path makes.
        SshAlgorithmFallbackPolicy.ExtendForOldDropbear(connInfo);

        return new SshClient(connInfo);
    }

    public async ValueTask DisposeAsync()
    {
        // Mirror DockerSmokeFixture teardown — stop+dispose both containers, dispose both images.
        // SSH-fixture teardown must run regardless of a cancelled per-test token.
#pragma warning disable xUnit1051 // deliberate None — fixture teardown must run even if the test token is cancelled
        await _legacyContainer.StopAsync(CancellationToken.None);
        await _currentContainer.StopAsync(CancellationToken.None);
#pragma warning restore xUnit1051
        await _legacyContainer.DisposeAsync();
        await _currentContainer.DisposeAsync();
        await _legacyImage.DisposeAsync();
        await _currentImage.DisposeAsync();
    }
}

/// <summary>Host + mapped port + test credentials for a running Dropbear container.</summary>
/// <param name="Host">Container hostname (typically <c>localhost</c>/127.0.0.1).</param>
/// <param name="Port">Random host port mapped to the container's Dropbear port 22.</param>
/// <param name="User">Throwaway test SSH user baked into the image.</param>
/// <param name="Password">Throwaway test SSH password baked into the image.</param>
public sealed record SshEndpoint(string Host, ushort Port, string User, string Password);
