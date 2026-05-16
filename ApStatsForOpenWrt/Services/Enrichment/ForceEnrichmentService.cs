namespace ApStatsForOpenWrt.Services.Enrichment;

/// <summary>
/// <see cref="IForceEnrichmentService"/> implementation. Single <c>int</c> + Interlocked CAS
/// = lock-free atomic. <c>0</c> = not queued, <c>1</c> = queued. CONTEXT 14 §D-05 flag-location
/// decision (dedicated tiny service over the AppSettingsService-property alt and the
/// per-orchestrator-volatile alt, because: cleanest separation of settings-vs-flag semantics;
/// lifetime = process; trivially testable in isolation).
/// <para>
/// Concurrency: <see cref="Request"/> uses <see cref="Interlocked.Exchange(ref int, int)"/> so
/// concurrent set-set races collapse to a single set. <see cref="ConsumeIfSet"/> uses
/// <see cref="Interlocked.CompareExchange(ref int, int, int)"/> so concurrent consume-consume
/// races collapse to exactly one winner. <see cref="IsQueued"/> uses <see cref="Volatile.Read(ref int)"/>
/// for a torn-free read on every supported runtime.
/// </para>
/// </summary>
public sealed class ForceEnrichmentService : IForceEnrichmentService
{
    // 0 = not queued, 1 = queued. Single int + Interlocked.CompareExchange = lock-free atomic CAS.
    private int _flag;

    /// <inheritdoc />
    public bool IsQueued => Volatile.Read(ref _flag) == 1;

    /// <inheritdoc />
    public void Request() => Interlocked.Exchange(ref _flag, 1);

    /// <inheritdoc />
    public bool ConsumeIfSet() => Interlocked.CompareExchange(ref _flag, 0, 1) == 1;
}
