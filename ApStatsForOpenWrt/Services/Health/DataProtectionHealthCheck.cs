using Microsoft.Extensions.Diagnostics.HealthChecks;
using ApStatsForOpenWrt.Data.Repositories;

namespace ApStatsForOpenWrt.Services.Health;

/// <summary>
/// Phase 6 INF-06 — request-time Data Protection key-ring readability probe. Reuses the
/// <c>DataProtectionHealthHostedService.cs:32-38</c> probe body verbatim:
/// <list type="bullet">
///   <item><description>Guard 1: <see cref="INodeRepo.AnyEncryptedPasswordsAsync"/> == false → Healthy
///   (no encrypted creds means key-ring readability is irrelevant).</description></item>
///   <item><description>Guard 2: <c>/data/keys</c> exists with ≥ 1 <c>*.xml</c> file → Healthy.</description></item>
///   <item><description>Otherwise (creds present but no keys) → Unhealthy.</description></item>
/// </list>
/// <para>
/// Per PER-04 (<c>ArchitectureTests.DbContext_IsOnlyUsedByRepositories</c>): consumes
/// <see cref="INodeRepo"/> — MUST NOT inject <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>
/// directly.
/// </para>
/// <para>
/// Coexistence with <c>DataProtectionHealthHostedService</c> (NOT redundant — CONTEXT.md
/// §code_context line 103): the bootstrap service is the fail-fast guard at host start (logs
/// Critical + <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.StopApplication"/>).
/// This <see cref="IHealthCheck"/> is the request-time guard (returns Unhealthy → 503). Both
/// consume the same <see cref="INodeRepo"/> seam.
/// </para>
/// <para>
/// <c>KeyRingPath</c> is hard-coded to <c>/data/keys</c> per D-13 — no appsettings indirection.
/// Local Windows dev redirects via the <c>DATA_ROOT</c> env var read in <c>Program.cs</c>; this
/// health check is concerned only with the production-volume contract.
/// </para>
/// </summary>
public sealed class DataProtectionHealthCheck(INodeRepo nodeRepo) : IHealthCheck
{
    private const string KeyRingPath = "/data/keys";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Guard 1 — no encrypted credentials yet → key ring is irrelevant.
            if (!await nodeRepo.AnyEncryptedPasswordsAsync(cancellationToken))
                return HealthCheckResult.Healthy("ok");

            // Guard 2 — encrypted creds present; verify the key ring is readable.
            var keysExist = Directory.Exists(KeyRingPath)
                         && Directory.EnumerateFiles(KeyRingPath, "*.xml").Any();
            return keysExist
                ? HealthCheckResult.Healthy("ok")
                : HealthCheckResult.Unhealthy("fail");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("fail", ex);
        }
    }
}
