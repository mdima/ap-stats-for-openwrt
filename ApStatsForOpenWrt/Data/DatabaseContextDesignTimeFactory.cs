using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ApStatsForOpenWrt.Data;

/// <summary>
/// Design-time-only factory used by <c>dotnet ef migrations add</c> /
/// <c>dotnet ef database update</c>. It does NOT participate in runtime DI — Plan 01-04 wires
/// the real <see cref="DbContextOptions{DatabaseContext}"/> through
/// <c>AddDbContextFactory&lt;DatabaseContext&gt;</c> in <c>Program.cs</c>. The connection
/// string here is a placeholder used solely to let EF tooling build a model snapshot; no
/// migration ever connects to this path at design time.
/// </summary>
internal sealed class DatabaseContextDesignTimeFactory : IDesignTimeDbContextFactory<DatabaseContext>
{
    public DatabaseContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new DatabaseContext(options);
    }
}
