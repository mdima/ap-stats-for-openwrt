using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenWrtApStats.Data.Repositories;

namespace OpenWrtApStats.Services.Health;

/// <summary>
/// Phase 6 INF-06 — request-time DB reachability probe. Returns Healthy when an
/// <c>AsNoTracking().AnyAsync()</c> round-trip against <c>WiFiNodes</c> succeeds, Unhealthy
/// otherwise. The boolean result is discarded — reachability (not row count) is the contract.
/// <para>
/// Per PER-04 (<c>ArchitectureTests.DbContext_IsOnlyUsedByRepositories</c>): consumes
/// <see cref="INodeRepo"/> — MUST NOT inject <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>
/// directly. The cheapest existing repo seam is <see cref="INodeRepo.AnyAsync"/> — a single
/// <c>AsNoTracking().AnyAsync()</c> round-trip (same shape Plan 04-01 used for the wizard
/// auto-redirect middleware).
/// </para>
/// <para>
/// Catches <see cref="System.Exception"/> deliberately — health checks must surface DB
/// unreachability as 503 without crashing the request pipeline. Mirrors
/// <c>DataProtectionHealthHostedService.cs:41-48</c> deliberate non-throw policy
/// (logs Critical + StopApplication instead of throwing).
/// </para>
/// <para>
/// Coexists with <c>DataProtectionHealthHostedService</c> — that one is the bootstrap-time
/// fail-fast guard; this one is the request-time guard for external monitors. Different DI
/// surfaces (<see cref="IHealthCheck"/> vs <see cref="Microsoft.Extensions.Hosting.IHostedService"/>);
/// both consume the same INodeRepo seam.
/// </para>
/// </summary>
public sealed class DbHealthCheck(INodeRepo nodeRepo) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await nodeRepo.AnyAsync(cancellationToken);     // discard the bool — reachability is the contract.
            return HealthCheckResult.Healthy("ok");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("fail", ex);
        }
    }
}
