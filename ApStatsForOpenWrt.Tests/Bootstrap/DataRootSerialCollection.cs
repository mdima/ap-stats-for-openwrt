using Xunit;

namespace ApStatsForOpenWrt.Tests.Bootstrap;

/// <summary>
/// Serializes every test class that either mutates the process-global <c>DATA_ROOT</c>
/// environment variable or executes <c>MigrationsHostedService.StartAsync</c> (which reads
/// <c>DATA_ROOT</c> to resolve the pre-migration backup path, M-HRD-02).
/// <para>
/// Without this, xUnit's default class-level parallelism lets <c>MigrationsBackupTests</c>
/// repoint <c>DATA_ROOT</c> at its per-test temp dir while <c>MigrationsHostedServiceTests</c>
/// boots the same hosted service concurrently — each class then observes the other's
/// <c>DATA_ROOT</c>, writing <c>.bak</c> files into the wrong test's directory (observed as
/// intermittent failures of <c>FreshVolume_NoDb_SkipsBackup</c> and
/// <c>AppliesPendingMigrations_ThenIsIdempotent</c> in full-suite <c>-c Release</c> runs).
/// <c>DisableParallelization = true</c> also stops this collection from running in parallel
/// with other collections, closing the cross-collection global-state race. Mirrors the
/// established pattern on the <c>RateLimitSerial</c>, <c>Integration</c>,
/// <c>Phase10AcceptanceSignalTests</c>, and <c>DockerSmoke</c> collection definitions.
/// </para>
/// </summary>
[CollectionDefinition("DataRootSerial", DisableParallelization = true)]
public sealed class DataRootSerialCollection { }
