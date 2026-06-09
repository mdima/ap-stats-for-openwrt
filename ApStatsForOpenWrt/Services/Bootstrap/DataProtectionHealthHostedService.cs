using ApStatsForOpenWrt.Data.Repositories;

namespace ApStatsForOpenWrt.Services.Bootstrap;

/// <summary>
/// D-12 key-ring health probe. Runs AFTER <see cref="MigrationsHostedService"/> (so the
/// <c>WiFiNodes</c> table exists) and BEFORE <see cref="AppSettingsBootstrapHostedService"/>
/// (so a key-ring/DB mismatch fails fast before settings warm-up).
/// <para>
/// Per PER-04 this service does NOT inject <c>IDbContextFactory&lt;DatabaseContext&gt;</c> —
/// it goes through the <see cref="INodeRepo.AnyEncryptedPasswordsAsync"/> seam so DbContext
/// access stays exclusive to the <c>ApStatsForOpenWrt.Data.Repositories</c> namespace
/// (architecturally enforced by <c>ArchitectureTests.DbContext_IsOnlyUsedByRepositories</c>).
/// </para>
/// <para>
/// Failure mode: if encrypted node passwords exist but the key-ring directory
/// (<see cref="BootstrapPaths.KeysDir"/> — <c>{DataRoot}/keys</c>) is empty, log Critical
/// (so the operator sees an actionable message), set <see cref="Environment.ExitCode"/> to 1,
/// and call <see cref="IHostApplicationLifetime.StopApplication"/>. Do NOT throw — the host
/// would otherwise wrap the exception and lose the actionable LogCritical context.
/// </para>
/// <para>
/// D-13: the key-ring path is resolved via <see cref="BootstrapPaths.KeysDir"/> (the
/// <c>DATA_ROOT</c> env var, default <c>/data</c> → <c>/data/keys</c> in production), NOT a
/// hard-coded literal. This is the SAME resolver <c>Program.cs</c> uses to PERSIST the key ring
/// (<c>PersistKeysToFileSystem({DataRoot}/keys)</c>), so the guard probes exactly where the keys
/// live. D-13's principle is "env var, not appsettings" — <c>BootstrapPaths</c> reads the
/// <c>DATA_ROOT</c> env var (NOT appsettings), so this is fully consistent with D-13. When
/// <c>DATA_ROOT</c> is unset (the shipped container) the path is byte-identical to <c>/data/keys</c>.
/// </para>
/// </summary>
public sealed class DataProtectionHealthHostedService(
    INodeRepo nodeRepo,
    IHostApplicationLifetime lifetime,
    ILogger<DataProtectionHealthHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Guard 1: any encrypted credentials in DB? Goes through the repo seam (PER-04).
        var hasEncryptedCreds = await nodeRepo.AnyEncryptedPasswordsAsync(cancellationToken);
        if (!hasEncryptedCreds) return;

        // Guard 2: is the resolved key-ring dir ({DataRoot}/keys) populated?
        var keysExist = Directory.Exists(BootstrapPaths.KeysDir)
                     && Directory.EnumerateFiles(BootstrapPaths.KeysDir, "*.xml").Any();
        if (keysExist) return;

        // Both guards failed: encrypted creds exist but no keys. Loud failure (D-12).
        log.LogCritical(
            "Data Protection key ring at {Path} is empty but encrypted node passwords exist " +
            "in the database. Mount /data, restore the key ring backup, or clear node " +
            "passwords from the WiFiNodes table before restarting.",
            BootstrapPaths.KeysDir);

        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
