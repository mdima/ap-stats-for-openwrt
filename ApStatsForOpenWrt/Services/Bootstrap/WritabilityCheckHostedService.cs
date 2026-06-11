namespace ApStatsForOpenWrt.Services.Bootstrap;

/// <summary>
/// INF-07 / D-01 / D-02 fail-fast writability probe. Registered FIRST in the IHostedService
/// chain (before <see cref="MigrationsHostedService"/>) so a read-only or unwritable
/// <c>/data</c> / <c>/logs</c> mount surfaces BEFORE EF Core migrations or DataProtection
/// key-ring initialization — both of which would otherwise fail with cryptic IOException
/// messages and corrupt the bootstrap chain.
/// <para>
/// Per CLAUDE.md "Bootstrap order = registration order" the production registration order
/// MUST be: Writability → Migrations → DataProtectionHealth → AppSettingsBootstrap → Polling.
/// The probe targets the data root, the Data Protection key-ring dir, and the logs root —
/// resolved through <see cref="BootstrapPaths"/> from the <c>DATA_ROOT</c> / <c>LOGS_ROOT</c>
/// env vars. D-13 invariant is preserved: when those env vars are UNSET the probed paths are
/// byte-identical to the historical literals <c>/data</c>, <c>/data/keys</c>, <c>/logs</c>, so
/// the production container ALWAYS probes those exact paths. The indirection exists only so the
/// host can boot against a writable temp dir on a non-root CI runner / Windows dev box (the same
/// indirection <c>Program.cs</c> already applies to the SQLite connection string, the key ring,
/// and the Serilog file sink).
/// </para>
/// <para>
/// Failure mode (D-02): on <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>,
/// log <see cref="Microsoft.Extensions.Logging.LogLevel.Error"/> with the offending path, set
/// <see cref="Environment.ExitCode"/> to <c>78</c> (EX_CONFIG, sysexits.h convention), and
/// call <see cref="IHostApplicationLifetime.StopApplication"/>. We use <c>LogError</c> here
/// (NOT <c>LogCritical</c>, which <see cref="DataProtectionHealthHostedService"/> uses for
/// unrecoverable key-ring loss) because a writability failure is operationally recoverable
/// — the operator <c>chown</c>s the volume and restarts.
/// </para>
/// <para>
/// Per PER-04 this service does NOT touch the DB at all (filesystem-only probe). Per
/// CLAUDE.md "no <c>EnsureCreated</c>" — no DB seam to worry about.
/// </para>
/// </summary>
public sealed class WritabilityCheckHostedService : IHostedService
{
    /// <summary>
    /// Production target paths — resolved from <c>DATA_ROOT</c> / <c>LOGS_ROOT</c> via
    /// <see cref="BootstrapPaths"/>. Evaluated per access (a property, NOT a
    /// <c>static readonly</c> field) so the env-var resolution happens at host-build time —
    /// when the production parameterless ctor runs — rather than being frozen at type load.
    /// When the env vars are UNSET this is exactly <c>{ "/data", "/data/keys", "/logs" }</c>
    /// (D-13 byte-identical).
    /// </summary>
    internal static string[] DefaultPaths => new[]
    {
        BootstrapPaths.DataRoot,
        BootstrapPaths.KeysDir,
        BootstrapPaths.LogsRoot,
    };

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WritabilityCheckHostedService> _logger;
    private readonly IReadOnlyList<string> _paths;

    /// <summary>Production constructor — uses the resolved <see cref="DefaultPaths"/>.</summary>
    public WritabilityCheckHostedService(
        IHostApplicationLifetime lifetime,
        ILogger<WritabilityCheckHostedService> logger)
        : this(lifetime, logger, DefaultPaths)
    {
    }

    /// <summary>Test-only constructor — caller-supplied path list. Honors D-01 contract by
    /// running the same per-path try/catch loop, just against tester-controlled paths so the
    /// success and failure arms can be exercised without root access to <c>/data</c>.</summary>
    internal WritabilityCheckHostedService(
        IHostApplicationLifetime lifetime,
        ILogger<WritabilityCheckHostedService> logger,
        IReadOnlyList<string> paths)
    {
        _lifetime = lifetime;
        _logger = logger;
        _paths = paths;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var path in _paths)
        {
            try
            {
                ProbeWritable(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex,
                    "Writability probe FAILED for {Path}. Container will exit non-zero. " +
                    "Check that the volume is mounted and writable by UID 1654 (app user).",
                    path);
                Environment.ExitCode = 78; // EX_CONFIG (sysexits.h convention) — D-02
                _lifetime.StopApplication();
                return Task.CompletedTask;
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ProbeWritable(string path)
    {
        Directory.CreateDirectory(path);     // Defensive — RESEARCH Finding #3 Risk #3
        var sentinel = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
        File.WriteAllText(sentinel, "probe");
        File.Delete(sentinel);
    }
}
