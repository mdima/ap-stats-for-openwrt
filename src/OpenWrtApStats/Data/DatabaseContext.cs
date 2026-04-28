using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data.Models;

namespace OpenWrtApStats.Data;

/// <summary>
/// EF Core context for the OpenWrtApStats persistence plane. Sealed and primary-constructed
/// per RESEARCH Pattern 2 — DI configures the connection string via
/// <see cref="DbContextOptions{DatabaseContext}"/>; the design-time configuration override
/// is intentionally absent (Pitfall 1: would silently override DI factory configuration).
/// </summary>
public sealed class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options)
{
    public DbSet<WiFiNode> WiFiNodes => Set<WiFiNode>();
    public DbSet<WiFiNetwork> WiFiNetworks => Set<WiFiNetwork>();
    public DbSet<WLANStation> WLANStations => Set<WLANStation>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // D-09 / D-10: seed exactly one row, captured in __EFMigrationsHistory.
        modelBuilder.Entity<AppSetting>().HasData(new AppSetting
        {
            Key      = "PollingIntervalSeconds",
            IntValue = 30
        });
    }
}
