using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data;

namespace OpenWrtApStats.Tests.TestSupport;

/// <summary>
/// Test-only adapter that satisfies <see cref="IDbContextFactory{TContext}"/> against the
/// per-fixture <see cref="DbContextOptions{TContext}"/>. Production code uses the runtime
/// factory registered via <c>AddDbContextFactory</c> in Plan 01-04; this adapter keeps the
/// repo's primary-constructor signature honest in tests without spinning up a host.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
{
    public DatabaseContext CreateDbContext() => new DatabaseContext(options);
}
