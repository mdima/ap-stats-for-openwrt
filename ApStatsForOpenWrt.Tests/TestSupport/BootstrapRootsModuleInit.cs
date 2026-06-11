using System.Runtime.CompilerServices;

namespace ApStatsForOpenWrt.Tests.TestSupport;

/// <summary>
/// ci-test-data-writability (round-3 root fix). A test-assembly
/// <see cref="ModuleInitializerAttribute">[ModuleInitializer]</see> that, BEFORE any test runs,
/// establishes a single per-run WRITABLE temp directory as the process-wide DEFAULT for the
/// production <c>DATA_ROOT</c> / <c>LOGS_ROOT</c> bootstrap env vars.
/// <para>
/// WHY THIS LAYER (and not per-site wiring): every code path that resolves a bootstrap filesystem
/// root goes through <c>BootstrapPaths.DataRoot</c>/<c>LogsRoot</c>, which default to the
/// container literals <c>/data</c>/<c>/logs</c> when the env vars are UNSET (D-13). On a non-root
/// Linux CI runner those defaults are unwritable, so ANY test that boots the real bootstrap chain
/// — whether a <c>WebApplicationFactory&lt;Program&gt;</c> host (the writability probe +
/// <c>MigrationsHostedService.CreateDirectory</c>) OR a DIRECT
/// <c>MigrationsHostedService.StartAsync</c> call (MigrationsHostedServiceTests /
/// MigrationsBackupTests) — fails with <c>UnauthorizedAccessException</c> / <c>IOException</c>.
/// The Windows dev box MASKED this for two prior fix rounds because Windows resolves <c>/data</c>
/// to the writable drive-root <c>C:\data</c>; the failure was only ever visible on a clean runner.
/// Pointing the process-wide DEFAULT at a writable temp dir makes the bootstrap roots writable
/// EVERYWHERE (CI + local) in one move, fixing host-boot AND direct tests without per-site patches.
/// </para>
/// <para>
/// BENIGN, not hostile: this is the OPPOSITE of the rejected round-2 process-wide UNWRITABLE
/// DATA_ROOT (which broke MigrationsBackupTests). A WRITABLE temp default GIVES those tests the
/// writable root they need. The Data Protection <c>{DATA_ROOT}/keys</c> subdir is pre-created so
/// the production writability probe's keys-dir leg succeeds.
/// </para>
/// <para>
/// RECONCILES the explicit env managers:
/// <list type="bullet">
///   <item><see cref="TempBootstrapRoots"/> (the <c>[Collection("Integration")]</c> host
///   fixtures) CAPTURES the prior value (now this temp default) and RESTORES it on Dispose — so a
///   restore never falls back to a null/<c>/data</c>. The per-factory redirect is now redundant
///   with this global default but kept (harmless) + still asserted by the
///   <c>EveryWebApplicationFactory_RedirectsBootstrapRootsToTempDir</c> regression guard so the
///   global mechanism cannot silently regress.</item>
///   <item><c>MigrationsBackupTests.WithDataRoot</c> (the <c>[Collection("DataRootSerial")]</c>
///   class) overrides <c>DATA_ROOT</c> per-test and restores the CAPTURED prior value (this temp
///   default) in its finally — never a hard unset-to-null. It does not manage <c>LOGS_ROOT</c> at
///   all; that now defaults to this writable temp, which is exactly what its
///   <c>MigrationsHostedService.StartAsync</c> call needs for its <c>CreateDirectory(logsRoot)</c>
///   leg.</item>
///   <item><c>MigrationsHostedServiceTests</c> sets neither env var; both now default to this
///   writable temp, so the service's <c>CreateDirectory(dataRoot)</c>/<c>(logsRoot)</c> legs
///   succeed on a clean runner.</item>
/// </list>
/// </para>
/// <para>
/// HONORS an externally-supplied environment: if a CI step or operator has ALREADY exported
/// <c>DATA_ROOT</c>/<c>LOGS_ROOT</c> before the test host starts, those values are left untouched
/// — the module init only fills the DEFAULT when the var is unset/empty. Production source is
/// byte-identical: env-unset still resolves to <c>/data</c>/<c>/logs</c> in the shipped container;
/// this initializer lives only in the test assembly.
/// </para>
/// <para>
/// The negative WritabilityCheckHostedServiceTests arms pass explicit unwritable paths to the
/// service constructor (NOT via env), so the fail-loud probe behavior is unaffected by this
/// writable default — the production abort-on-unwritable-/data contract is still proven.
/// </para>
/// </summary>
internal static class BootstrapRootsModuleInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // One writable temp dir for the whole test run. Reused for both roots; the production
        // resolver derives {DATA_ROOT}/keys for the Data Protection key ring, pre-created below.
        var root = Path.Combine(
            Path.GetTempPath(),
            $"apstats-bootstrap-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "keys"));

        // Fill the DEFAULT only when unset/empty — never clobber an externally-supplied value
        // (a CI step or operator may have exported these before the test host started).
        SetDefaultIfUnset("DATA_ROOT", root);
        SetDefaultIfUnset("LOGS_ROOT", root);

        static void SetDefaultIfUnset(string name, string value)
        {
            var existing = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(existing))
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
