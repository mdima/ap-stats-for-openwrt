namespace ApStatsForOpenWrt.Services.Bootstrap;

/// <summary>
/// Single source of truth for the bootstrap-time filesystem roots (<c>DATA_ROOT</c> /
/// <c>LOGS_ROOT</c>). Mirrors the resolution that <c>Program.cs</c> already applies for the
/// SQLite connection string (<c>Data Source={DATA_ROOT}/AP-Stats-for-OpenWrt.db</c>), the Data
/// Protection key ring (<c>{DATA_ROOT}/keys</c>) and the Serilog file sink
/// (<c>{LOGS_ROOT}/...</c>), and that <c>MigrationsHostedService</c> already uses for the
/// M-HRD-02 pre-migration backup path.
/// <para>
/// D-13 invariant preserved: when the env vars are UNSET the resolved roots are byte-identical
/// to the historical hard-coded literals — <c>/data</c>, <c>/data/keys</c> and <c>/logs</c>. The
/// env-var indirection exists only so local Windows dev (and the CI test host, which is a
/// non-root runner with no writable <c>/data</c>) can redirect storage to a writable temp dir
/// WITHOUT changing production container behavior.
/// </para>
/// <para>
/// WR-04 (21-REVIEW) precedent: build paths with <see cref="System.IO.Path.Combine(string,string)"/>
/// after trimming a single trailing separator off the resolved root — a trailing separator in
/// <c>DATA_ROOT</c> (<c>/data/</c>) would otherwise yield a double-separator child path. This is
/// the EXACT pattern <c>MigrationsHostedService</c> uses for the backup path; do not invent a
/// second one.
/// </para>
/// </summary>
internal static class BootstrapPaths
{
    /// <summary>Resolved data root — <c>DATA_ROOT</c> env var, or <c>/data</c> when unset (D-13).</summary>
    public static string DataRoot =>
        (Environment.GetEnvironmentVariable("DATA_ROOT") ?? "/data").TrimEnd('/', '\\');

    /// <summary>Resolved logs root — <c>LOGS_ROOT</c> env var, or <c>/logs</c> when unset (D-13).</summary>
    public static string LogsRoot =>
        (Environment.GetEnvironmentVariable("LOGS_ROOT") ?? "/logs").TrimEnd('/', '\\');

    /// <summary>
    /// The Data Protection key-ring directory under the resolved data root
    /// (<c>{DataRoot}/keys</c>), matching <c>Program.cs</c>'s
    /// <c>PersistKeysToFileSystem(new DirectoryInfo($"{dataRoot}/keys"))</c>.
    /// </summary>
    public static string KeysDir => Path.Combine(DataRoot, "keys");
}
