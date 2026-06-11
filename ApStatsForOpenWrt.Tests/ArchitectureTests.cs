using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using ApStatsForOpenWrt.Data;
using Xunit;

namespace ApStatsForOpenWrt.Tests;

/// <summary>
/// Architectural reflection probe enforcing PER-04 / ROADMAP §Phase 1 Success Criterion 5
/// ("Repositories are the only code that touches DbContext"). Adding a class outside
/// <c>ApStatsForOpenWrt.Data.Repositories</c> that takes <c>IDbContextFactory&lt;DatabaseContext&gt;</c>
/// or holds a <see cref="DatabaseContext"/> field/property MUST fail this test.
/// <para>
/// <see cref="DatabaseContext"/> itself is special-cased by full type name (it legitimately
/// holds <c>DbContextOptions&lt;DatabaseContext&gt;</c> on its primary constructor).
/// <c>MigrationsHostedService</c> is a tightly-scoped carve-out (one full type name, NOT a
/// namespace) reserved for the migration pipeline owner — Phase 1 has not authored that
/// service yet, so the carve-out is documented but inert.
/// <c>PollOrchestrator</c> is the Phase 2 carve-out (single-DB-writer; coordinator transaction;
/// PER-04 binding).
/// </para>
/// </summary>
public sealed class ArchitectureTests
{
    [Fact]
    public void DbContext_IsOnlyUsedByRepositories()
    {
        var asm = typeof(DatabaseContext).Assembly;
        var dbContextFullName = typeof(DatabaseContext).FullName!;

        // Carve-outs (one full type name each — NOT namespaces):
        //   1) DatabaseContext itself — primary-ctor takes DbContextOptions<DatabaseContext>.
        //   2) MigrationsHostedService — owner of the migration pipeline (Plan 01-04 will land
        //      this type; the carve-out is registered now so the test stays green when it
        //      arrives). There is no repo seam below the migration system itself.
        const string migrationsHostedServiceFullName = "ApStatsForOpenWrt.Services.Bootstrap.MigrationsHostedService";

        // Carve-out 3 (Phase 2): PollOrchestrator owns the per-cycle coordinator transaction and
        // MUST inject IDbContextFactory<DatabaseContext> directly to call BeginTransactionAsync over
        // the caller-owned repo overloads. This is the Phase 2 PER-04 carve-out (D-04 binding).
        const string pollOrchestratorFullName = "ApStatsForOpenWrt.Services.Polling.PollOrchestrator";

        // Whitelist: ONLY the repositories namespace.
        // ROADMAP §Phase 1 Success Criterion 5: "Repositories are the only code that touches DbContext."
        // PER-04 (verbatim REQUIREMENTS): "A repository layer encapsulates all DB access; no DbContext is
        // referenced directly from Blazor components or background services."
        const string repositoriesNamespace = "ApStatsForOpenWrt.Data.Repositories";

        var violators = asm.GetTypes()
            // Special-case DatabaseContext itself by FULL TYPE NAME.
            .Where(t => t.FullName != dbContextFullName)
            // Special-case MigrationsHostedService by FULL TYPE NAME (owner of the migration pipeline).
            .Where(t => t.FullName != migrationsHostedServiceFullName)
            // Special-case PollOrchestrator by FULL TYPE NAME (owner of the per-cycle coordinator transaction).
            .Where(t => t.FullName != pollOrchestratorFullName)
            // Exclude compiler-generated / nested non-public helper types that aren't part of
            // the user-facing API (anonymous types, async state machines, closure classes).
            .Where(t => !t.IsNested || t.IsPublic)
            .Where(UsesDbContext)
            .Where(t => !((t.Namespace ?? "") == repositoriesNamespace ||
                         (t.Namespace ?? "").StartsWith(repositoriesNamespace + ".", StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(violators);

        static bool UsesDbContext(Type t)
        {
            // ctor parameters
            foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                foreach (var p in ctor.GetParameters())
                    if (Mentions(p.ParameterType)) return true;

            // fields
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                if (Mentions(f.FieldType)) return true;

            // properties
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                if (Mentions(p.PropertyType)) return true;

            return false;

            static bool Mentions(Type t)
            {
                if (typeof(DatabaseContext).IsAssignableFrom(t)) return true;
                if (t.IsGenericType &&
                    t.GetGenericArguments().Any(a => typeof(DatabaseContext).IsAssignableFrom(a)))
                    return true;
                return false;
            }
        }
    }

    /// <summary>
    /// D-09 CR-02 architectural proof. Any method named "UpdateAsync" with a
    /// <see cref="ApStatsForOpenWrt.Data.Models.WiFiNode"/> parameter would re-track the whole row
    /// and clobber the encrypted Password from a stub's default value. <c>UpdateMetadataAsync</c>
    /// + <c>UpdatePasswordAsync</c> replace it with single-property
    /// <c>ExecuteUpdateAsync</c> shapes. This test makes any future re-introduction
    /// of the dangerous shape provably loud.
    /// </summary>
    [Fact]
    public void INodeRepo_HasNoUpdateAsyncWiFiNodeMethod()
    {
        var methods = typeof(ApStatsForOpenWrt.Data.Repositories.INodeRepo).GetMethods();
        var violators = methods
            .Where(m => m.Name == "UpdateAsync"
                     && m.GetParameters().Any(p => p.ParameterType == typeof(ApStatsForOpenWrt.Data.Models.WiFiNode)))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
            .ToList();

        Assert.Empty(violators);
    }

    /// <summary>
    /// D-03 / Phase 5 D-03 reconciliation. <c>PollNotifier.Cycled</c> MUST be
    /// <c>Func&lt;PollSnapshot, Task&gt;?</c> so subscribers can declare
    /// <c>async Task</c> handlers (NOT forbidden <c>async void</c> per Pitfall 7).
    /// Reverting to <c>Action&lt;PollSnapshot&gt;</c> would force <c>async void</c>
    /// handlers — this test makes that regression provably loud.
    /// </summary>
    [Fact]
    public void PollNotifier_EventSignature_IsFuncTask()
    {
        var ev = typeof(ApStatsForOpenWrt.Services.Polling.PollNotifier).GetEvent("Cycled");
        Assert.NotNull(ev);
        var expected = typeof(Func<ApStatsForOpenWrt.Services.Polling.PollSnapshot, Task>);
        Assert.Equal(expected, ev!.EventHandlerType);
    }

    /// <summary>
    /// D-09 / Phase 5 CR-02 mirror on <see cref="ApStatsForOpenWrt.Data.Repositories.IStationRepo"/>.
    /// Any method named <c>UpdateAsync</c> taking <see cref="ApStatsForOpenWrt.Data.Models.WLANStation"/>
    /// would re-track the whole row and clobber operator-assigned <c>Name</c> from a stub's
    /// default value. The repo exposes <c>SetNameAsync</c> (single-property
    /// <c>ExecuteUpdateAsync</c>) and <c>DeleteByMacAsync</c> (single-row <c>ExecuteDeleteAsync</c>)
    /// — never an entity-shaped Update. This test makes any future re-introduction provably loud.
    /// </summary>
    [Fact]
    public void IStationRepo_DoesNotExposeUpdateWLANStation()
    {
        var methods = typeof(ApStatsForOpenWrt.Data.Repositories.IStationRepo).GetMethods();
        var violators = methods
            .Where(m => m.Name == "UpdateAsync"
                     && m.GetParameters().Any(p => p.ParameterType == typeof(ApStatsForOpenWrt.Data.Models.WLANStation)))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
            .ToList();

        Assert.Empty(violators);
    }

    /// <summary>
    /// Phase 6 mirror of <see cref="PollNotifier_EventSignature_IsFuncTask"/>.
    /// <c>ICircuitGate.Changed</c> MUST be <c>Func&lt;GateState, Task&gt;?</c> so subscribers
    /// can declare <c>async Task</c> handlers (NOT forbidden <c>async void</c> per Pitfall 7).
    /// Reverting to <c>Action&lt;GateState&gt;</c> would re-introduce the async-void exception
    /// in <c>LivePausedBadge.OnGateChanged</c> — this test makes that regression loud.
    /// </summary>
    [Fact]
    public void ICircuitGate_ChangedEventSignature_IsFuncTask()
    {
        var ev = typeof(ApStatsForOpenWrt.Services.Gating.ICircuitGate).GetEvent("Changed");
        Assert.NotNull(ev);
        var expected = typeof(Func<ApStatsForOpenWrt.Services.Gating.GateState, Task>);
        Assert.Equal(expected, ev!.EventHandlerType);
    }

    /// <summary>
    /// v0.11 Phase 14 Plan 14-04 / CONTEXT 14 §D-09 — <c>NodeAdminService</c> is a write-side
    /// SSH responsibility; it does NOT touch the DB (disconnect is out-of-band; effects appear
    /// via the next normal poll cycle observing the station's absence). Receiving the WiFiNode +
    /// iface from the Detail page (which already loaded them via INodeRepo + INetworkRepo) is
    /// the architectural contract; injecting <c>IDbContextFactory&lt;DatabaseContext&gt;</c>
    /// directly would conflate the write-side SSH responsibility with the polling/persistence
    /// plane and re-open the captive-dep risk that <c>DbContext_IsOnlyUsedByRepositories</c>
    /// guards against. This test makes any future drift provably loud.
    /// </summary>
    [Fact]
    public void NodeAdminService_DoesNotInject_IDbContextFactory()
    {
        var ctorParams = typeof(ApStatsForOpenWrt.Services.Admin.NodeAdminService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType.FullName ?? "")
            .ToList();
        Assert.DoesNotContain(ctorParams, t => t.Contains("IDbContextFactory", StringComparison.Ordinal));
    }

    /// <summary>
    /// 260518-vbs — operator-discovered build-config friction. <c>&lt;Version&gt;</c> is owned by
    /// <c>Directory.Build.props</c> (the file's lines 15-18 comment block calls it the "Single source
    /// of truth"). MSBuild imports <c>Directory.Build.props</c> BEFORE each csproj's
    /// <c>&lt;PropertyGroup&gt;</c>, so a duplicate <c>&lt;Version&gt;</c> inside any csproj silently
    /// OVERRIDES the props value — creating two-place version drift (operator must remember to bump
    /// both files; e9a3e67 only bumped one). This fact globs every <c>*.csproj</c> under the repo
    /// root and fails if any of them declares <c>&lt;Version&gt;</c>. RED while
    /// <c>ApStatsForOpenWrt/ApStatsForOpenWrt.csproj:34</c> still carries the duplicate; GREEN
    /// after the deletion. Any future re-introduction is provably loud.
    /// </summary>
    [Fact]
    public void NoCsprojDeclaresVersion_DirectoryBuildPropsIsTheOnlySource()
    {
        // Walk up from the test binary's base directory looking for Directory.Build.props as the
        // repo-root marker. Using Directory.Build.props (not .git) keeps this robust across
        // container builds and worktrees without a .git directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        Assert.NotNull(dir); // sanity — repo root must be discoverable from the test binary

        var versionElement = new Regex(@"<Version\s*>", RegexOptions.IgnoreCase);
        var csprojs = Directory.GetFiles(dir!.FullName, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var violators = csprojs
            .Where(path => versionElement.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(dir.FullName, path))
            .ToList();

        Assert.True(
            violators.Count == 0,
            $"260518-vbs: <Version> is owned by Directory.Build.props and MUST NOT be declared in any csproj " +
            $"(csproj declarations silently override the props value, per MSBuild import order). " +
            $"Offending csproj(s): {string.Join(", ", violators)}. " +
            $"Fix: delete the <Version> element from each listed file; Directory.Build.props is the single source of truth.");
    }

    /// <summary>
    /// ci-test-data-writability (residual-fix regression guard). EVERY
    /// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
    /// subclass in the TEST assembly boots the real <c>Program.cs</c> bootstrap chain, whose
    /// writability probe + <c>MigrationsHostedService.CreateDirectory</c> target the resolved
    /// <c>DATA_ROOT</c>/<c>LOGS_ROOT</c>. On a non-root CI runner the default <c>/data</c> is
    /// unwritable, so any host-booting factory that does NOT redirect those roots to a writable
    /// temp dir aborts host startup and fails ONLY on a clean runner (the dev box masks it via a
    /// writable <c>C:\data</c>). The original fix wired 5 factories; a real-runner re-run still
    /// failed because 8 more host-boot sites were missed. This test asserts every such subclass
    /// declares a <see cref="ApStatsForOpenWrt.Tests.TestSupport.TempBootstrapRoots"/> field so a
    /// future host-booting test that forgets the redirect fails FAST locally, not only on CI.
    /// </summary>
    [Fact]
    public void EveryWebApplicationFactory_RedirectsBootstrapRootsToTempDir()
    {
        var testAssembly = typeof(ArchitectureTests).Assembly;
        var tempRootsType = typeof(ApStatsForOpenWrt.Tests.TestSupport.TempBootstrapRoots);

        // Every concrete subclass of WebApplicationFactory<Program> in the test assembly.
        var hostBootFactories = testAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(IsWebApplicationFactoryOfProgram)
            .ToList();

        // Sanity: the reflection probe must actually find the known host-booting factories
        // (e.g. SmokeFactoryFixture). A zero count would mean the matcher silently broke and
        // the guard would vacuously pass.
        Assert.NotEmpty(hostBootFactories);

        var violators = hostBootFactories
            .Where(t => !DeclaresTempBootstrapRootsField(t, tempRootsType))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violators.Count == 0,
            "ci-test-data-writability: every WebApplicationFactory<Program> subclass MUST hold a " +
            "TempBootstrapRoots field so DATA_ROOT/LOGS_ROOT are redirected to a writable temp dir " +
            "BEFORE the real Program.cs bootstrap runs (otherwise the writability probe aborts host " +
            "startup on a non-root CI runner and the test fails only on a clean runner). " +
            "Offending factory(ies): " + string.Join(", ", violators) + ". " +
            "Fix: add `private readonly ApStatsForOpenWrt.Tests.TestSupport.TempBootstrapRoots _roots = new();` " +
            "as a field initializer and dispose it in the factory's Dispose/DisposeAsync.");

        static bool IsWebApplicationFactoryOfProgram(Type t)
        {
            for (var b = t.BaseType; b is not null; b = b.BaseType)
            {
                if (b.IsGenericType &&
                    b.GetGenericTypeDefinition() == typeof(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<>) &&
                    b.GetGenericArguments()[0] == typeof(Program))
                    return true;
            }
            return false;
        }

        static bool DeclaresTempBootstrapRootsField(Type t, Type tempRootsType)
        {
            // Walk the type and any (non-WAF) base types it introduces, so a shared base helper
            // that holds the field also satisfies the guard.
            for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                var fields = cur.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (fields.Any(f => tempRootsType.IsAssignableFrom(f.FieldType)))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 260609-9fe regression guard. The two Data Protection key-ring guards
    /// (<c>DataProtectionHealthHostedService</c>, <c>DataProtectionHealthCheck</c>) MUST resolve the
    /// key-ring path via <c>BootstrapPaths.KeysDir</c> (the <c>DATA_ROOT</c> env-var resolver), NOT a
    /// bare <c>"/data/keys"</c> string literal in executable code. The original bug: the guards
    /// hard-coded <c>"/data/keys"</c> while <c>Program.cs</c> persisted keys to <c>{DATA_ROOT}/keys</c>,
    /// so under local dev (<c>DATA_ROOT=C:\temp\data</c>) the guard probed an empty <c>C:\data\keys</c>,
    /// saw encrypted creds, logged FTL "key ring is empty" and called <c>StopApplication()</c>. This
    /// test scans <c>*.cs</c> under <c>ApStatsForOpenWrt/Services</c> and fails if any NON-COMMENT line
    /// contains the <c>"/data/keys"</c> literal — doc comments mentioning it for explanation do not trip.
    /// </summary>
    [Fact]
    public void NoServiceSourceHardCodesDataKeysLiteral()
    {
        // Walk up from the test binary to the repo root (Directory.Build.props marker), then into
        // the production Services dir — same path-discovery idiom as
        // NoCsprojDeclaresVersion_DirectoryBuildPropsIsTheOnlySource.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        Assert.NotNull(dir); // sanity — repo root must be discoverable from the test binary

        var servicesDir = Path.Combine(dir!.FullName, "ApStatsForOpenWrt", "Services");
        Assert.True(Directory.Exists(servicesDir),
            $"260609-9fe: production Services dir not found at '{servicesDir}' — path probe broke; guard would be vacuous.");

        var serviceFiles = Directory.GetFiles(servicesDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // Sanity: the probe must actually find source files (a zero count would mean the path
        // silently broke and the guard would vacuously pass) — mirrors Assert.NotEmpty(hostBootFactories).
        Assert.NotEmpty(serviceFiles);

        var violators = serviceFiles
            .Where(path => FileHasDataKeysLiteralInCode(path))
            .Select(path => Path.GetRelativePath(dir.FullName, path))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violators.Count == 0,
            "260609-9fe: no executable code under ApStatsForOpenWrt/Services may hard-code the " +
            "key-ring path. Resolve via BootstrapPaths.KeysDir — the DATA_ROOT env-var resolver — " +
            "not a bare '/data/keys' literal; this prevents the 260609-9fe local-dev fail-fast " +
            "regression. Offending file(s): " + string.Join(", ", violators) + ".");

        // Returns true if any NON-COMMENT line in the file contains the "/data/keys" literal.
        // Strips // and /// line comments and the interior of /* */ block comments (incl. XML-doc
        // prose) so an explanatory mention of /data/keys does not trip the guard.
        static bool FileHasDataKeysLiteralInCode(string path)
        {
            var inBlockComment = false;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw;

                // Handle (single-line and multi-line) /* */ block comments.
                if (inBlockComment)
                {
                    var end = line.IndexOf("*/", StringComparison.Ordinal);
                    if (end < 0) continue;            // entire line is still inside the block comment
                    line = line[(end + 2)..];          // resume scanning after the comment closes
                    inBlockComment = false;
                }

                var open = line.IndexOf("/*", StringComparison.Ordinal);
                if (open >= 0)
                {
                    var close = line.IndexOf("*/", open + 2, StringComparison.Ordinal);
                    if (close >= 0)
                        line = line.Remove(open, close + 2 - open); // strip inline /* ... */
                    else
                    {
                        line = line[..open];          // strip from /* to EOL; remember we're inside
                        inBlockComment = true;
                    }
                }

                // Strip // and /// line comments (XML-doc included).
                var slash = line.IndexOf("//", StringComparison.Ordinal);
                if (slash >= 0) line = line[..slash];

                if (line.Contains("/data/keys", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
