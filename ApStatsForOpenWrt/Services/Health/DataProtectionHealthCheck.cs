using Microsoft.Extensions.Diagnostics.HealthChecks;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Bootstrap;

namespace ApStatsForOpenWrt.Services.Health;

/// <summary>
/// Phase 6 INF-06 — request-time Data Protection key-ring readability probe. Reuses the
/// <c>DataProtectionHealthHostedService.cs:32-38</c> probe body verbatim:
/// <list type="bullet">
///   <item><description>Guard 1: <see cref="INodeRepo.AnyEncryptedPasswordsAsync"/> == false → Healthy
///   (no encrypted creds means key-ring readability is irrelevant).</description></item>
///   <item><description>Guard 2: <see cref="BootstrapPaths.KeysDir"/> (<c>{DataRoot}/keys</c>)
///   exists with ≥ 1 <c>*.xml</c> file → Healthy.</description></item>
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
/// The key-ring path is resolved via <see cref="BootstrapPaths.KeysDir"/> (the <c>DATA_ROOT</c>
/// env var, default <c>/data</c> → <c>/data/keys</c> in production) per D-13 — NOT appsettings
/// indirection and NOT a hard-coded literal. This is the SAME resolver <c>Program.cs</c> uses to
/// PERSIST the key ring, so the probe targets exactly where the keys live; local Windows dev (and
/// the non-root CI runner) redirects via <c>DATA_ROOT</c> exactly as persistence does. D-13's
/// principle is "env var, not appsettings", which <c>BootstrapPaths</c> honors. When
/// <c>DATA_ROOT</c> is unset (the shipped container) the path is byte-identical to <c>/data/keys</c>.
/// </para>
/// </summary>
public sealed class DataProtectionHealthCheck(INodeRepo nodeRepo) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Guard 1 — no encrypted credentials yet → key ring is irrelevant.
            if (!await nodeRepo.AnyEncryptedPasswordsAsync(cancellationToken))
                return HealthCheckResult.Healthy("ok");

            // Guard 2 — encrypted creds present; verify the resolved key ring is readable.
            var keysExist = Directory.Exists(BootstrapPaths.KeysDir)
                         && Directory.EnumerateFiles(BootstrapPaths.KeysDir, "*.xml").Any();
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
