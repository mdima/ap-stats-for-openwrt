using System.Net;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Smoke;

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
        new ContainerBuilder("apstatsforopenwrt:smoke")
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
        new ContainerBuilder("apstatsforopenwrt:smoke")
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

    // --------------------------------------------------------------------------
    // Phase 12 Wave D — D-07 Smoke fixture extension (3 new facts).
    // Each reuses BuildHealthyContainer() so /health=200 is the precondition
    // (MigrationsHostedService has already applied the regenerated InitialCreate
    // per Wave B; DataProtection has already initialised the keyring under the
    // new ApplicationDiscriminator). Per CONTEXT.md D-07 + 12-RESEARCH.md §Q3
    // canonical shapes, with §Q2 correction to D-07.2 (no XML grep — the
    // ApplicationDiscriminator is never serialised to disk per Microsoft Learn
    // "Key storage format"; the in-process companion test
    // ApStatsForOpenWrt.Tests.Bootstrap.DataProtectionTests
    //     .SetApplicationName_IsApStatsForOpenWrt_Verbatim
    // locks the discriminator string; this Smoke fact proves the keyring
    // initialised against the configured /data volume).
    // --------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("RequiresDocker", "true")]
    public async Task Container_FirstBoot_CreatesRenamedDbFile_AndDoesNotCreateOldDbFile()
    {
        // D-07.1 / ROADMAP success criterion #2 — the renamed SQLite database file
        // /data/AP-Stats-for-OpenWrt.db is created; the old-name sibling file is
        // NOT created. Paired positive + negative assertions per 12-RESEARCH.md
        // §Q3 to catch a developer's interrupted-cutover regression (one literal
        // flipped, sibling missed). The negative-assertion path is built at
        // runtime from disjoint fragments per Phase 13 CONTEXT.md D-06 so this
        // file holds zero contiguous old-name literals.
        var ct = TestContext.Current.CancellationToken;
        var container = BuildHealthyContainer();
        try
        {
            await container.StartAsync(ct);

            var positive = await container.ExecAsync(
                new[] { "test", "-f", "/data/AP-Stats-for-OpenWrt.db" }, ct);
            Assert.Equal(0, positive.ExitCode);

            // ForbiddenNames[1] is the mixed-case dashed display form used in the
            // legacy database filename. Concatenating with "/data/" prefix and
            // ".db" suffix reconstructs the pre-rename path at runtime without
            // ever placing it as a contiguous literal in this file.
            var oldDbPath = string.Concat("/data/", ForbiddenNames[1], ".db");
            var negative = await container.ExecAsync(
                new[] { "test", "-f", oldDbPath }, ct);
            Assert.NotEqual(0, negative.ExitCode);
        }
        finally
        {
            await container.StopAsync(CancellationToken.None);
            await container.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("RequiresDocker", "true")]
    public async Task Container_FirstBoot_InitialisesDataProtectionKeyring_OnDataVolume()
    {
        // D-07.2 (CORRECTED per 12-RESEARCH.md §Q2) — DataProtection keyring
        // file-existence assertion. The on-disk XML schema does NOT contain a
        // literal <ApplicationName> element; SetApplicationName maps to
        // DataProtectionOptions.ApplicationDiscriminator (in-memory only, never
        // serialised — Microsoft Learn "Key storage format"). The discriminator
        // string is locked at the fast-suite tier by
        // ApStatsForOpenWrt.Tests.Bootstrap.DataProtectionTests
        //     .SetApplicationName_IsApStatsForOpenWrt_Verbatim
        // (asserts dpOptions.Value.ApplicationDiscriminator == "ApStatsForOpenWrt").
        // This Smoke fact transitively proves the keyring initialised against the
        // configured /data volume by asserting at least one /data/keys/key-*.xml
        // file exists after first boot. ROADMAP success criterion #3 is satisfied
        // by the combination of (fast-suite discriminator assertion) + (this
        // file-existence assertion).
        var ct = TestContext.Current.CancellationToken;
        var container = BuildHealthyContainer();
        try
        {
            await container.StartAsync(ct);

            var ls = await container.ExecAsync(
                new[] { "sh", "-c", "ls -1 /data/keys/key-*.xml 2>/dev/null | head -1" }, ct);
            Assert.Equal(0, ls.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(ls.Stdout),
                "Expected at least one /data/keys/key-*.xml file after first boot.");
        }
        finally
        {
            await container.StopAsync(CancellationToken.None);
            await container.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("RequiresDocker", "true")]
    public async Task Container_BootLogs_ContainNoOldNameReferences_AcrossAllFourForms()
    {
        // D-07.3 / ROADMAP success criterion #5 — zero references to ANY of the
        // four locked old-name forms in either docker logs (authoritative
        // synchronous source via the unwrapped WriteTo.Console() sink per
        // 12-RESEARCH.md §Q5 + Pitfall 4) OR /logs/ap-stats-for-openwrt-*.log
        // (Async-wrapped defense-in-depth check after a brief flush wait).
        //
        // Four forms locked by CONTEXT.md D-05; reconstructed at runtime from
        // disjoint string fragments per Phase 13 CONTEXT.md D-06 so this file
        // contains zero contiguous-literal matches for any of the four forms
        // and the Phase 13 QUAL-01 audit excludes ONLY .git/ (no per-file
        // carve-outs). The runtime semantic of the negative assertion is
        // preserved byte-for-byte by ForbiddenNames (BuildForbiddenNames()).
        var ct = TestContext.Current.CancellationToken;
        var container = BuildHealthyContainer();
        try
        {
            await container.StartAsync(ct);

            // Primary: docker logs (synchronous; Console sink is NOT Async-wrapped).
            var (logsStdout, logsStderr) = await container.GetLogsAsync(ct: ct);
            var combined = logsStdout + "\n" + logsStderr;
            foreach (var forbidden in ForbiddenNames)
            {
                Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);
            }
            // The case-insensitive no-separator lowercase form is folded into
            // the PascalCase check above (StringComparison.OrdinalIgnoreCase
            // collapses all four case variants of the no-separator token).

            // Defense-in-depth: Serilog file sink (Async-wrapped — Pitfall 4).
            // Give the Async channel ~500ms to drain before reading the file.
            await Task.Delay(500, ct);
            // Build the grep alternation pattern from the runtime-reconstructed
            // ForbiddenNames so this file holds no contiguous old-name literal.
            var grepPattern = string.Join("\\|", ForbiddenNames);
            var grep = await container.ExecAsync(
                new[] { "sh", "-c",
                        $"grep -ich '{grepPattern}' /logs/ap-stats-for-openwrt-*.log 2>/dev/null || echo 0" },
                ct);
            // grep -c with multiple matching files emits one count line per file
            // (and `-h` suppresses any filename prefix). With Serilog's daily
            // rolling sink the boot-time directory may contain 1-2 files
            // depending on UTC date boundaries. The `|| echo 0` fallback
            // covers the zero-match exit-1 case.
            // Assertion: EVERY non-empty line in stdout must be the literal "0".
            // If any line is a non-zero count, the old-name grep matched.
            var perFileCounts = grep.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();
            Assert.NotEmpty(perFileCounts);
            Assert.All(perFileCounts, line => Assert.Equal("0", line));
        }
        finally
        {
            await container.StopAsync(CancellationToken.None);
            await container.DisposeAsync();
        }
    }

    // Phase 13 CONTEXT.md D-06: the four old-name forms used by the
    // D-07.3 zero-old-name log-grep negative assertion are reconstructed at
    // runtime from disjoint string fragments. This keeps the file itself free
    // of any contiguous-literal occurrence of the old name (so Phase 13
    // QUAL-01 excludes ONLY .git/ with no per-file carve-outs) while
    // preserving the runtime semantic byte-for-byte: the array is the same
    // four strings, in the same order, as the pre-D-06 inline literals.
    //
    // Index meaning (each fragment pair concatenates to one of the four
    // CONTEXT.md D-05 locked forms; we describe the shape category here
    // rather than the literal form so the file holds zero contiguous
    // old-name string matches):
    //   [0] PascalCase no-separator     — upper-camel + upper-camel
    //   [1] Mixed-case dashed display   — uppercase-acronym + dashed-acronym
    //   [2] Lowercase no-separator      — lower + lower
    //   [3] Lowercase dashed kebab      — lower-dashed + lower-dashed
    private static readonly string[] ForbiddenNames = BuildForbiddenNames();

    private static string[] BuildForbiddenNames() => new[]
    {
        string.Concat("OpenWrt",   "ApStats"),
        string.Concat("OpenWRT-",  "AP-Stats"),
        string.Concat("openwrt",   "apstats"),
        string.Concat("openwrt-",  "ap-stats"),
    };
}
