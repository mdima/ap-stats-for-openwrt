using Xunit;

namespace ApStatsForOpenWrt.Tests.Smoke;

/// <summary>
/// INF-07 / INF-08 / D-01..D-04 plain-text lint of the repo-root Dockerfile + .dockerignore +
/// docker-compose.yml. Runs in the DEFAULT test suite (NOT Smoke-trait-gated) per RESEARCH
/// §"Wave 0 Gaps" — the assertions are file-text greps that need NO Docker daemon, so the
/// structural regression catches at every commit before the (slow) container-bound smoke
/// job picks it up.
/// <para>
/// Plan 07-02 flipped the two Plan-07-01 handoff facts (the former negative-asserts on
/// the HEALTHCHECK directive and on the curl install) into positive assertions over the
/// now-amended Dockerfile, and added the docker-compose.yml structural facts (named
/// volumes, port mapping, restart policy, NO healthcheck override per RESEARCH Finding #8).
/// </para>
/// </summary>
public sealed class DockerfileStructuralTests
{
    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ApStatsForOpenWrt.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("ApStatsForOpenWrt.sln not found");
    }

    private static string DockerfileText() =>
        File.ReadAllText(Path.Combine(SolutionRoot(), "Dockerfile"));

    private static string DockerignoreText() =>
        File.ReadAllText(Path.Combine(SolutionRoot(), ".dockerignore"));

    private static string DockerComposeText() =>
        File.ReadAllText(Path.Combine(SolutionRoot(), "docker-compose.yml"));

    // ── Dockerfile structural facts ──────────────────────────────────────────

    [Fact]
    public void Dockerfile_DeclaresMultiStageBuild_FromSdkImage()
    {
        // INF-01 base lock + multi-stage shape. ARG-substituted form
        // (FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build) matches the regex.
        // Phase 22 / M-REL-03: the build stage is now pinned to --platform=$BUILDPLATFORM so
        // the SDK runs natively while cross-compiling per TARGETARCH — the regex allows the
        // optional --platform=... prefix between FROM and the SDK image.
        Assert.Matches(
            @"FROM\s+(?:--platform=\S+\s+)?mcr\.microsoft\.com/dotnet/sdk:\$\{?DOTNET_VERSION\}?\s+AS\s+build",
            DockerfileText());
    }

    [Fact]
    public void Dockerfile_BuildStage_CrossCompilesViaBuildPlatformAndTargetArch()
    {
        // Phase 22 / M-REL-03: positively pin the cross-compile contract so a future edit
        // that drops --platform=$BUILDPLATFORM or the -a $TARGETARCH threading regresses
        // multi-arch buildx loudly. $BUILDPLATFORM keeps the SDK native; -a $TARGETARCH
        // cross-compiles the publish output per target RID (empty under plain docker build,
        // host-RID fallback — the DockerSmokeFixture path).
        var t = DockerfileText();
        Assert.Matches(@"FROM\s+--platform=\$BUILDPLATFORM\s+mcr\.microsoft\.com/dotnet/sdk:", t);
        Assert.Matches(@"(?m)^ARG\s+TARGETARCH\s*$", t);
        Assert.Matches(@"dotnet\s+restore\s+-a\s+\$TARGETARCH", t);
        Assert.Matches(@"dotnet\s+publish[\s\S]*?-a\s+\$TARGETARCH", t);
    }

    [Fact]
    public void Dockerfile_RuntimeStage_UsesAspnetImage()
    {
        // CLAUDE.md Container base lock: aspnet:10.0 (Debian) for v1; chiseled deferred to v2.
        Assert.Matches(@"FROM\s+mcr\.microsoft\.com/dotnet/aspnet:", DockerfileText());
    }

    [Fact]
    public void Dockerfile_ExecutesAsAppUser()
    {
        // INF-07 non-root posture. The base image provides the `app` user at UID 1654; we
        // only assert the directive — UID assertion belongs to the live-container smoke job.
        Assert.Matches(@"(?m)^USER\s+app\s*$", DockerfileText());
    }

    [Fact]
    public void Dockerfile_ChownsDataAndLogsBeforeAppUser()
    {
        // RESEARCH Finding #2: chown MUST happen as root BEFORE the final `USER app`. We
        // assert source-order: the chown line index is strictly less than the LAST USER app
        // index (the runtime stage's final user switch).
        var t = DockerfileText();
        var chownIdx = t.IndexOf("chown -R app:app /data /logs", StringComparison.Ordinal);
        var lastUserAppIdx = t.LastIndexOf("USER app", StringComparison.Ordinal);
        Assert.True(chownIdx >= 0, "chown -R app:app /data /logs must be present");
        Assert.True(lastUserAppIdx >= 0, "USER app directive must be present");
        Assert.True(chownIdx < lastUserAppIdx,
            "chown -R app:app /data /logs MUST appear lexically BEFORE the final `USER app`.");
    }

    [Fact]
    public void Dockerfile_ChmodsKeysDirToOwnerOnly()
    {
        // T-07-04 mitigation: /data/keys is 700 (owner-only) so the DataProtection key ring
        // is not group/world readable.
        Assert.Contains("chmod 700 /data/keys", DockerfileText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerfile_Exposes8080()
    {
        // D-03 / D-05: port matches the HEALTHCHECK CMD added in Plan 07-02 and the
        // /health endpoint shipped in Phase 6.
        Assert.Matches(@"(?m)^EXPOSE\s+8080\s*$", DockerfileText());
    }

    [Fact]
    public void Dockerfile_DeclaresHealthcheckOverHealthEndpoint()
    {
        // FLIPPED in Plan 07-02 from the Plan 07-01 negative-assert handoff fact.
        // INF-08 / D-03 verbatim: HEALTHCHECK directive shape + the curl probe targeting
        // the Phase 6 /health endpoint (D-07/D-08).
        var t = DockerfileText();
        Assert.Contains("HEALTHCHECK", t, StringComparison.Ordinal);
        Assert.Contains("--interval=30s", t, StringComparison.Ordinal);
        Assert.Contains("--timeout=5s", t, StringComparison.Ordinal);
        Assert.Contains("--start-period=15s", t, StringComparison.Ordinal);
        Assert.Contains("--retries=3", t, StringComparison.Ordinal);
        Assert.Contains("curl -fsS http://localhost:8080/health", t, StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerfile_InstallsCurl_NoCachedAptLists()
    {
        // FLIPPED in Plan 07-02 from the Plan 07-01 negative-assert handoff fact.
        // D-04 verbatim: curl is installed via apt-get with --no-install-recommends and the
        // apt cache is removed in the SAME RUN line so the cache files do not appear in any
        // image layer (T-07-10 mitigation).
        var t = DockerfileText();
        Assert.Contains("apt-get install -y --no-install-recommends curl", t, StringComparison.Ordinal);
        Assert.Contains("rm -rf /var/lib/apt/lists/*", t, StringComparison.Ordinal);
    }

    // ── .dockerignore structural facts ───────────────────────────────────────

    [Fact]
    public void Dockerignore_ExcludesPlanningArtifacts()
    {
        // T-07-02 mitigation: keep .planning/ out of image layers.
        Assert.Contains(".planning/", DockerignoreText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerignore_ExcludesClaudeArtifacts()
    {
        // T-07-02 mitigation: keep agentic / IDE artifacts out of image layers.
        Assert.Contains(".claude/", DockerignoreText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerignore_ExcludesTestProject()
    {
        // T-07-02 mitigation: the test project is restored for csproj-only layer cache, but
        // its source must NOT enter the published image. The COPY . . in the build stage is
        // filtered by this entry so test code never lands at /app.
        Assert.Contains("ApStatsForOpenWrt.Tests/", DockerignoreText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerignore_ExcludesBinAndObj()
    {
        var t = DockerignoreText();
        Assert.Contains("**/bin/", t, StringComparison.Ordinal);
        Assert.Contains("**/obj/", t, StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerignore_ExcludesGit()
    {
        // T-07-02 mitigation: .git/ contains commit history + author emails + ssh remotes —
        // never appropriate for an image layer.
        Assert.Contains("**/.git/", DockerignoreText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerignore_ExcludesUserFiles()
    {
        // T-07-02 mitigation: *.user files (Visual Studio per-developer secrets, EF connection
        // strings) must never enter image layers.
        Assert.Contains("**/*.user", DockerignoreText(), StringComparison.Ordinal);
    }

    // ── docker-compose.yml structural facts (Plan 07-02 — RESEARCH Finding #8) ───

    [Fact]
    public void DockerCompose_DeclaresApStatsForOpenWrtDataVolume()
    {
        // D-10 / RESEARCH Finding #2: named volume apstatsforopenwrt-data mounted at /data so
        // SQLite + DataProtection key ring survive `docker compose down && up`.
        Assert.Contains("apstatsforopenwrt-data:/data", DockerComposeText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DockerCompose_DeclaresApStatsForOpenWrtLogsVolume()
    {
        // D-10: named volume apstatsforopenwrt-logs mounted at /logs for Serilog daily-rolling
        // files; survives container lifecycle.
        Assert.Contains("apstatsforopenwrt-logs:/logs", DockerComposeText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DockerCompose_DeclaresPortMapping_8080()
    {
        // D-03: port matches Dockerfile's EXPOSE 8080 + the HEALTHCHECK CMD's curl target.
        Assert.Contains("\"8080:8080\"", DockerComposeText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DockerCompose_HasUnlessStoppedRestartPolicy()
    {
        // RESEARCH Finding #8: standard restart policy for self-hosted operator workloads.
        Assert.Contains("restart: unless-stopped", DockerComposeText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DockerCompose_OwnsHealthcheckProbe_PerOperator260520Hcc()
    {
        // Operator-authorised inversion of Phase 7 RESEARCH Finding #8 (which
        // forbade a Compose `healthcheck:` block on the grounds it would OVERRIDE
        // the Dockerfile HEALTHCHECK directive). Commit 4e9cbe3 (quick task
        // 260520-hcc) explicitly chose Compose-as-source-of-truth on 2026-05-20:
        // "just add the healthcheck in the existing docker compose yaml". The
        // Dockerfile directive at Dockerfile:67-68 stays in place as the fallback
        // for `docker run`-only deployments, and shape parity between the two is
        // the binding contract — probe URL, interval, timeout, start-period, and
        // retries must match byte-for-byte semantically so behavior is identical
        // across both paths. This assertion was stale on master from 2026-05-20
        // until 2026-05-28 and is the deferred item tracked in
        // .planning/phases/15-auth-surface-hardening/deferred-items.md; closing
        // it unblocks the v0.12 Phase 16 QUAL-01 fresh-clone gate.
        var t = DockerComposeText();

        // (a) The healthcheck block must exist under the service mapping.
        //     Matches whitespace-then-`healthcheck:` (no `#`), so the leading docs
        //     comment block in the file (which is `#`-prefixed) cannot satisfy this.
        Assert.Matches(@"(?m)^\s+healthcheck:\s*$", t);

        // (b) The `test:` line must carry the exact shape-parity probe string from
        //     Dockerfile:67-68. Operator-authorised contract per 4e9cbe3: probe URL
        //     `http://localhost:8080/health`, exec form (`CMD`, not `CMD-SHELL`),
        //     curl flags `-fsS`. Any drift breaks parity with the `docker run`
        //     fallback path (Dockerfile HEALTHCHECK directive).
        Assert.Contains(
            "test: [\"CMD\", \"curl\", \"-fsS\", \"http://localhost:8080/health\"]",
            t,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DockerCompose_DeclaresTopLevelVolumesSection()
    {
        // D-10: named volumes must be declared at the top level (not just referenced inside
        // services:) so `docker compose down` preserves them and `docker compose up` reuses
        // them on restart.
        var t = DockerComposeText().Replace("\r\n", "\n");
        Assert.Contains("\nvolumes:\n", t);
    }
}
