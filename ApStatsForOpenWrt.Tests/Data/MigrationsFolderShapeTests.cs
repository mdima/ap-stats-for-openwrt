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
        // exactly one migration named InitialCreate. The regenerated triplet is:
        //   {timestamp}_InitialCreate.cs
        //   {timestamp}_InitialCreate.Designer.cs
        //   DatabaseContextModelSnapshot.cs
        // = 3 .cs files total, of which exactly 1 is a non-Designer *_InitialCreate.cs.

        var dir = ResolveMigrationsDir();
        Assert.True(Directory.Exists(dir), $"Migrations folder not found at {dir}");

        var allCs = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).ToList();
        Assert.Equal(3, allCs.Count);

        var initialCreate = allCs
            .Where(p => Path.GetFileName(p).EndsWith("_InitialCreate.cs", StringComparison.Ordinal))
            .Where(p => !Path.GetFileName(p).EndsWith(".Designer.cs", StringComparison.Ordinal))
            .ToList();
        Assert.Single(initialCreate);

        var designer = allCs
            .Where(p => Path.GetFileName(p).EndsWith("_InitialCreate.Designer.cs", StringComparison.Ordinal))
            .ToList();
        Assert.Single(designer);

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
