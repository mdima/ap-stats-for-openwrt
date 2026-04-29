using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data;

namespace OpenWrtApStats.Services.Bootstrap;

/// <summary>
/// Boot-time runner for EF Core migrations (INF-03). Owns the migration pipeline directly —
/// this is the single carve-out from PER-04 (one full type name, registered in
/// <c>ArchitectureTests.DbContext_IsOnlyUsedByRepositories</c>) because there is no repo seam
/// below the migration system itself.
/// <para>
/// Order: registered FIRST in <c>Program.cs</c> so the schema exists before
/// <c>DataProtectionHealthHostedService</c> queries <c>WiFiNodes</c> (Pitfall 10).
/// </para>
/// <para>
/// Failure mode: <see cref="DatabaseFacade.MigrateAsync(CancellationToken)"/> is intentionally
/// NOT wrapped in try/catch (Pitfall: migration silent failure). A failed migration MUST crash
/// the process so the container orchestrator (Phase 7) sees exit 1 and marks the container failed.
/// </para>
/// </summary>
public sealed class MigrationsHostedService(
    IDbContextFactory<DatabaseContext> factory,
    ILogger<MigrationsHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Idempotent — WSL2/container first-run convenience. /data and /logs are hardcoded per D-13.
        Directory.CreateDirectory("/data");
        Directory.CreateDirectory("/logs");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            log.LogInformation(
                "Applying {Count} pending migrations: {Migrations}",
                pending.Count, string.Join(", ", pending));
        }

        // Fail fast on error (PITFALL: migration silent failure).
        // Do NOT wrap this in try/catch — a failed migration MUST crash the process.
        await db.Database.MigrateAsync(cancellationToken);

        log.LogInformation("Database migration complete");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
