using Xunit;

namespace OpenWrtApStats.Tests.Smoke;

/// <summary>
/// INF-07 / D-01..D-04 plain-text lint of the repo-root Dockerfile + .dockerignore. Runs in
/// the DEFAULT test suite (NOT Smoke-trait-gated) per RESEARCH §"Wave 0 Gaps" — the assertions
/// are file-text greps that need NO Docker daemon, so the structural regression catches at
/// every commit before the (slow) container-bound smoke job picks it up.
/// <para>
/// Two Plan-07-02 handoff facts deliberately INVERT in the next wave:
/// <see cref="Dockerfile_DoesNotIncludeHealthcheckYet_BelongsTo_07_02"/> flips to
/// <c>Assert.Contains("HEALTHCHECK", ...)</c> as part of the same commit that adds the
/// <c>HEALTHCHECK</c> directive; <see cref="Dockerfile_DoesNotInstallCurlYet_BelongsTo_07_02"/>
/// flips identically for the curl install.
/// </para>
/// </summary>
public sealed class DockerfileStructuralTests
{
    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OpenWrtApStats.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("OpenWrtApStats.sln not found");
    }

    private static string DockerfileText() =>
        File.ReadAllText(Path.Combine(SolutionRoot(), "Dockerfile"));

    private static string DockerignoreText() =>
        File.ReadAllText(Path.Combine(SolutionRoot(), ".dockerignore"));

    // ── Dockerfile structural facts ──────────────────────────────────────────

    [Fact]
    public void Dockerfile_DeclaresMultiStageBuild_FromSdkImage()
    {
        // INF-01 base lock + multi-stage shape. ARG-substituted form
        // (FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build) matches the regex.
        Assert.Matches(@"FROM\s+mcr\.microsoft\.com/dotnet/sdk:\$\{?DOTNET_VERSION\}?\s+AS\s+build",
            DockerfileText());
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
    public void Dockerfile_DoesNotIncludeHealthcheckYet_BelongsTo_07_02()
    {
        // HANDOFF: this assertion FLIPS in Plan 07-02 to Assert.Contains("HEALTHCHECK", ...).
        // The plan acceptance criterion `grep -c "HEALTHCHECK" Dockerfile` requires zero
        // occurrences of the literal token in this baseline; the explanatory comment at the
        // top of the runtime stage is phrased to avoid the literal word for that reason.
        Assert.DoesNotContain("HEALTHCHECK", DockerfileText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerfile_DoesNotInstallCurlYet_BelongsTo_07_02()
    {
        // HANDOFF: this assertion FLIPS in Plan 07-02 to assert the curl install. D-04 ships
        // the apt-get install line with the same commit that adds HEALTHCHECK.
        Assert.DoesNotContain("apt-get install", DockerfileText(), StringComparison.Ordinal);
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
        Assert.Contains("OpenWrtApStats.Tests/", DockerignoreText(), StringComparison.Ordinal);
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
}
