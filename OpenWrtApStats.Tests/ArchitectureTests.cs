using System.Reflection;
using OpenWrtApStats.Data;
using Xunit;

namespace OpenWrtApStats.Tests;

/// <summary>
/// Architectural reflection probe enforcing PER-04 / ROADMAP §Phase 1 Success Criterion 5
/// ("Repositories are the only code that touches DbContext"). Adding a class outside
/// <c>OpenWrtApStats.Data.Repositories</c> that takes <c>IDbContextFactory&lt;DatabaseContext&gt;</c>
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
        const string migrationsHostedServiceFullName = "OpenWrtApStats.Services.Bootstrap.MigrationsHostedService";

        // Carve-out 3 (Phase 2): PollOrchestrator owns the per-cycle coordinator transaction and
        // MUST inject IDbContextFactory<DatabaseContext> directly to call BeginTransactionAsync over
        // the caller-owned repo overloads. This is the Phase 2 PER-04 carve-out (D-04 binding).
        const string pollOrchestratorFullName = "OpenWrtApStats.Services.Polling.PollOrchestrator";

        // Whitelist: ONLY the repositories namespace.
        // ROADMAP §Phase 1 Success Criterion 5: "Repositories are the only code that touches DbContext."
        // PER-04 (verbatim REQUIREMENTS): "A repository layer encapsulates all DB access; no DbContext is
        // referenced directly from Blazor components or background services."
        const string repositoriesNamespace = "OpenWrtApStats.Data.Repositories";

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
}
