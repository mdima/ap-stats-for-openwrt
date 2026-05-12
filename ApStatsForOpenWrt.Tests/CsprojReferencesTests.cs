using System.Xml.Linq;
using Xunit;

namespace ApStatsForOpenWrt.Tests;

/// <summary>
/// CI-02 architectural gate at test time. Inspects the test project's csproj as XML and asserts
/// the locked NuGet pins are present (xunit.v3 3.2.2, bunit 2.7.2,
/// Microsoft.Testing.Extensions.CodeCoverage 18.6.2) AND that ZERO PackageReferences whose
/// Include attribute starts with "coverlet." are declared. The CONTEXT.md CI-02 decision pins
/// Microsoft.Testing.Extensions.CodeCoverage exclusively for v1 — even the new coverlet.MTP
/// MTP-native variant is excluded from the locked test stack until a future CONTEXT.md update
/// authorises it.
/// <para>
/// This test runs in CI before the coverage gate, so a contributor adding any coverlet.* package
/// to "make MTP work" sees the violation in the standard test pipeline rather than in code review.
/// </para>
/// </summary>
public sealed class CsprojReferencesTests
{
    private static string LocateTestCsproj()
    {
        // Walk up from the test bin directory until we find the source path.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(
                dir, "ApStatsForOpenWrt.Tests", "ApStatsForOpenWrt.Tests.csproj");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new FileNotFoundException("Could not locate ApStatsForOpenWrt.Tests.csproj");
    }

    [Fact]
    public void UsesXUnitV3_NoCoverlet()
    {
        // CI-02: locked test stack. xUnit v3 3.2.2, bUnit 2.7.2, Microsoft.Testing.Extensions.CodeCoverage 18.6.2.
        // ZERO coverlet.* references — neither the legacy collector/msbuild integrations nor the
        // new coverlet.MTP MTP-native variant. CONTEXT.md CI-02 pins Microsoft.Testing.Extensions.CodeCoverage
        // exclusively for v1. A future planner who wants coverlet.MTP must update CONTEXT.md FIRST.
        var csproj = LocateTestCsproj();
        var doc = XDocument.Load(csproj);

        var pkgRefs = doc.Descendants("PackageReference")
            .Select(e => new
            {
                Include = (string?)e.Attribute("Include"),
                Version = (string?)e.Attribute("Version"),
            })
            .Where(r => r.Include is not null)
            .ToList();

        // Locked pins — exact strings.
        Assert.Contains(pkgRefs, r => r.Include == "xunit.v3" && r.Version == "3.2.2");
        Assert.Contains(pkgRefs, r => r.Include == "bunit"    && r.Version == "2.7.2");
        Assert.Contains(pkgRefs, r => r.Include == "Microsoft.Testing.Extensions.CodeCoverage" && r.Version == "18.6.2");

        // Forbidden — ANY coverlet.* variant (CI-02). Even the new coverlet.MTP integration is
        // excluded for v1 because CONTEXT.md CI-02 pins Microsoft.Testing.Extensions.CodeCoverage
        // exclusively. Case-insensitive comparison guards against "Coverlet.Collector" creative casing.
        var coverletRefs = pkgRefs
            .Where(r => r.Include!.StartsWith("coverlet.", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Include)
            .ToList();
        Assert.Empty(coverletRefs);
    }
}
