namespace ApStatsForOpenWrt.Tests.TestSupport;

/// <summary>
/// Redirects the production <c>DATA_ROOT</c> / <c>LOGS_ROOT</c> bootstrap env vars to a private,
/// writable per-instance temp directory for the lifetime of a host-starting test fixture, then
/// restores the previous values (and deletes the temp dir) on <see cref="Dispose"/>.
/// <para>
/// Why this exists: every <c>WebApplicationFactory&lt;Program&gt;</c>-based fixture boots the REAL
/// <c>Program.cs</c> bootstrap chain. Those fixtures swap the EF DbContext to in-memory SQLite so
/// the DB write to <c>/data</c> is avoided, but the FILESYSTEM choke points
/// (<c>WritabilityCheckHostedService</c>'s probe and <c>MigrationsHostedService</c>'s
/// <c>CreateDirectory</c>) still target the resolved data/logs roots. On a non-root CI runner
/// (and inside any container without a mounted, writable <c>/data</c>) the default <c>/data</c>
/// root is unwritable, so the probe aborts host startup with <c>UnauthorizedAccessException</c> /
/// <c>IOException</c> and the host never builds — the failure surface seen on a clean GitHub
/// Actions runner. Pointing <c>DATA_ROOT</c>/<c>LOGS_ROOT</c> at a temp dir makes the real
/// bootstrap target a writable location, mirroring the env-var redirect that
/// <c>MigrationsBackupTests</c> already uses.
/// <para>
/// Concurrency: the env vars are process-global. The integration fixtures that use this helper
/// all live in the <c>[Collection("Integration")]</c> collection (DisableParallelization = true),
/// and the only other writer — <c>MigrationsBackupTests</c> / <c>MigrationsHostedServiceTests</c>
/// — lives in <c>[Collection("DataRootSerial")]</c> (also DisableParallelization = true). xUnit
/// does not run a DisableParallelization collection alongside any other collection, so there is
/// no cross-collection race; the save/restore pair keeps nested use correct.
/// </para>
/// </para>
/// </summary>
public sealed class TempBootstrapRoots : IDisposable
{
    private readonly string _previousDataRoot;
    private readonly string _previousLogsRoot;
    private bool _disposed;

    /// <summary>The writable per-instance temp directory now wired to both env vars.</summary>
    public string Root { get; }

    public TempBootstrapRoots()
    {
        Root = Path.Combine(Path.GetTempPath(), $"apstats-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);

        _previousDataRoot = Environment.GetEnvironmentVariable("DATA_ROOT") ?? "";
        _previousLogsRoot = Environment.GetEnvironmentVariable("LOGS_ROOT") ?? "";

        // Both roots point at the same writable temp dir; the production resolver derives
        // {DATA_ROOT}/keys for the key ring, which Directory.CreateDirectory creates on demand.
        Environment.SetEnvironmentVariable("DATA_ROOT", Root);
        Environment.SetEnvironmentVariable("LOGS_ROOT", Root);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Restore the prior values (empty -> unset, so the default /data + /logs come back).
        Environment.SetEnvironmentVariable(
            "DATA_ROOT", _previousDataRoot.Length == 0 ? null : _previousDataRoot);
        Environment.SetEnvironmentVariable(
            "LOGS_ROOT", _previousLogsRoot.Length == 0 ? null : _previousLogsRoot);

        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — Windows file-handle quirks (Serilog async file sink still
            // flushing) must not fail the test run.
        }
    }
}
