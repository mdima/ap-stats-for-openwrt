using OpenWrtApStats.Data.Repositories;

namespace OpenWrtApStats.Services.Bootstrap;

/// <summary>
/// D-12 key-ring health probe. Runs AFTER <see cref="MigrationsHostedService"/> (so the
/// <c>WiFiNodes</c> table exists) and BEFORE <see cref="AppSettingsBootstrapHostedService"/>
/// (so a key-ring/DB mismatch fails fast before settings warm-up).
/// <para>
/// Per PER-04 this service does NOT inject <c>IDbContextFactory&lt;DatabaseContext&gt;</c> —
/// it goes through the <see cref="INodeRepo.AnyEncryptedPasswordsAsync"/> seam so DbContext
/// access stays exclusive to the <c>OpenWrtApStats.Data.Repositories</c> namespace
/// (architecturally enforced by <c>ArchitectureTests.DbContext_IsOnlyUsedByRepositories</c>).
/// </para>
/// <para>
/// Failure mode: if encrypted node passwords exist but <c>/data/keys</c> is empty, log Critical
/// (so the operator sees an actionable message), set <see cref="Environment.ExitCode"/> to 1,
/// and call <see cref="IHostApplicationLifetime.StopApplication"/>. Do NOT throw — the host
/// would otherwise wrap the exception and lose the actionable LogCritical context.
/// </para>
/// </summary>
public sealed class DataProtectionHealthHostedService(
    INodeRepo nodeRepo,
    IHostApplicationLifetime lifetime,
    ILogger<DataProtectionHealthHostedService> log) : IHostedService
{
    private const string KeyRingPath = "/data/keys";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Guard 1: any encrypted credentials in DB? Goes through the repo seam (PER-04).
        var hasEncryptedCreds = await nodeRepo.AnyEncryptedPasswordsAsync(cancellationToken);
        if (!hasEncryptedCreds) return;

        // Guard 2: is /data/keys populated?
        var keysExist = Directory.Exists(KeyRingPath)
                     && Directory.EnumerateFiles(KeyRingPath, "*.xml").Any();
        if (keysExist) return;

        // Both guards failed: encrypted creds exist but no keys. Loud failure (D-12).
        log.LogCritical(
            "Data Protection key ring at {Path} is empty but encrypted node passwords exist " +
            "in the database. Mount /data, restore the key ring backup, or clear node " +
            "passwords from the WiFiNodes table before restarting.",
            KeyRingPath);

        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
