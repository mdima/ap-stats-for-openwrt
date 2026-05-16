// ApStatsForOpenWrt.Tests/Data/MigrationsFolderShapeTests.cs
//
// D-09.2 — Success-criterion #4 file-count assertion in the FAST suite (not Smoke).
// Bisect-friendly: catches accidental re-add of a second migration during any later wave.
// No Docker required.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Data;

public class MigrationsFolderShapeTests
{
    // The Migrations folder is at <repo-root>/ApStatsForOpenWrt/Data/Migrations.
    // The test runs from <repo-root>/ApStatsForOpenWrt.Tests/bin/<config>/net10.0/.
    // Climb up 4 levels then resolve down.
    private static string ResolveMigrationsDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(MigrationsFolderShapeTests).Assembly.Location)
                     ?? AppContext.BaseDirectory;
        // bin/<config>/net10.0 -> bin/<config> -> bin -> <test-project> -> <repo-root>
        var repoRoot = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "ApStatsForOpenWrt", "Data", "Migrations");
    }

    [Fact]
    public void MigrationsFolder_ContainsExactlyOneInitialCreate_RegeneratedAgainstNewAssemblyName()
    {
        // Per CONTEXT.md D-09.2 + ROADMAP success criterion #4 — Migrations/ MUST contain
        // exactly one migration named InitialCreate. v0.11 Phase 14 Plan 14-04 D-10
        // conditional yield adds a SECOND migration AddWiFiNetworkIfaceName (single-column
        // add for the DISC-01 disconnect path).
        //
        // Expected shape from v0.11 onwards:
        //   {ts1}_InitialCreate.cs                            (Phase 1)
        //   {ts1}_InitialCreate.Designer.cs                   (Phase 1)
        //   {ts2}_AddWiFiNetworkIfaceName.cs                  (Plan 14-04 D-10)
        //   {ts2}_AddWiFiNetworkIfaceName.Designer.cs         (Plan 14-04 D-10)
        //   DatabaseContextModelSnapshot.cs
        // = 5 .cs files. The InitialCreate uniqueness assertion is preserved.

        var dir = ResolveMigrationsDir();
        Assert.True(Directory.Exists(dir), $"Migrations folder not found at {dir}");

        var allCs = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).ToList();
        Assert.Equal(5, allCs.Count);

        var initialCreate = allCs
            .Where(p => Path.GetFileName(p).EndsWith("_InitialCreate.cs", StringComparison.Ordinal))
            .Where(p => !Path.GetFileName(p).EndsWith(".Designer.cs", StringComparison.Ordinal))
            .ToList();
        Assert.Single(initialCreate);

        var designer = allCs
            .Where(p => Path.GetFileName(p).EndsWith("_InitialCreate.Designer.cs", StringComparison.Ordinal))
            .ToList();
        Assert.Single(designer);

        // v0.11 D-10 — the AddWiFiNetworkIfaceName migration sister pair (.cs + .Designer.cs).
        var addIface = allCs
            .Where(p => Path.GetFileName(p).EndsWith("_AddWiFiNetworkIfaceName.cs", StringComparison.Ordinal))
            .Where(p => !Path.GetFileName(p).EndsWith(".Designer.cs", StringComparison.Ordinal))
            .ToList();
        Assert.Single(addIface);

        var addIfaceDesigner = allCs
            .Where(p => Path.GetFileName(p).EndsWith("_AddWiFiNetworkIfaceName.Designer.cs", StringComparison.Ordinal))
            .ToList();
        Assert.Single(addIfaceDesigner);

        var snapshot = allCs
            .Where(p => Path.GetFileName(p).Equals("DatabaseContextModelSnapshot.cs", StringComparison.Ordinal))
            .ToList();
        Assert.Single(snapshot);
    }

    [Fact]
    public void MigrationsFolder_AllFiles_DeclareNewAssemblyNamespace()
    {
        // RUNTIME-03 invariant — every regenerated file declares namespace
        // ApStatsForOpenWrt.Data.Migrations (derived from new AssemblyName/RootNamespace
        // per Phase 11 Plan 11-02 commit 4d1fc0f).
        var dir = ResolveMigrationsDir();
        foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(path);
            Assert.Contains("namespace ApStatsForOpenWrt.Data.Migrations", text);
        }
    }
}
