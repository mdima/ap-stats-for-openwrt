using ApStatsForOpenWrt.Services.Enrichment;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Services.Enrichment;

/// <summary>
/// Unit tests for v0.11 ENRICH-01 / CONTEXT 14 §D-05 force-flag singleton. Covers:
/// Request() sets the flag; ConsumeIfSet() returns true exactly once after Request();
/// idempotent re-consume returns false; IsQueued reflects state without clearing;
/// concurrent Request() + ConsumeIfSet() race is safe (Interlocked CAS — 50-task race
/// asserts exactly-once consumption semantics).
/// </summary>
public sealed class ForceEnrichmentServiceTests
{
    [Fact]
    public void NewInstance_IsNotQueued()
    {
        var svc = new ForceEnrichmentService();
        Assert.False(svc.IsQueued);
        Assert.False(svc.ConsumeIfSet());
    }

    [Fact]
    public void Request_SetsTheFlag()
    {
        var svc = new ForceEnrichmentService();
        svc.Request();
        Assert.True(svc.IsQueued);
    }

    [Fact]
    public void Request_IsIdempotent()
    {
        var svc = new ForceEnrichmentService();
        svc.Request();
        svc.Request();
        svc.Request();
        Assert.True(svc.IsQueued);
        Assert.True(svc.ConsumeIfSet());
        Assert.False(svc.ConsumeIfSet()); // consumed; not re-set by the extra Requests
    }

    [Fact]
    public void ConsumeIfSet_ReturnsTrueExactlyOnce_ThenFalse()
    {
        var svc = new ForceEnrichmentService();
        svc.Request();
        Assert.True(svc.ConsumeIfSet());
        Assert.False(svc.ConsumeIfSet());
        Assert.False(svc.IsQueued);
    }

    [Fact]
    public void IsQueued_DoesNotClear()
    {
        var svc = new ForceEnrichmentService();
        svc.Request();
        Assert.True(svc.IsQueued);
        Assert.True(svc.IsQueued);
        Assert.True(svc.IsQueued);
        // Still queued: IsQueued is a read-only peek.
        Assert.True(svc.ConsumeIfSet());
    }

    [Fact]
    public async Task ConcurrentConsumeIfSet_OnlyOneCallerWinsTheCas()
    {
        // 50 contenders all racing on the same set-cycle. The flag is set ONCE; exactly
        // ONE ConsumeIfSet must return true; the other 49 must return false.
        // Interlocked.CompareExchange guarantees this.
        var ct = TestContext.Current.CancellationToken;
        var svc = new ForceEnrichmentService();
        svc.Request();

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => svc.ConsumeIfSet(), ct))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(49, results.Count(r => !r));
        Assert.False(svc.IsQueued);
    }

    [Fact]
    public async Task ConcurrentRequestThenConsume_AlwaysEventuallyConsumed()
    {
        // Set + consume from different tasks. After both complete, the flag must be in a
        // consistent state. No deadlock, no torn state.
        var ct = TestContext.Current.CancellationToken;
        var svc = new ForceEnrichmentService();

        await Task.WhenAll(
            Task.Run(() => svc.Request(), ct),
            Task.Run(() => svc.ConsumeIfSet(), ct),
            Task.Run(() => svc.Request(), ct),
            Task.Run(() => svc.ConsumeIfSet(), ct),
            Task.Run(() => svc.Request(), ct)
        );

        // Final state may be 0 (consumed) or 1 (last Request stuck). The contract is that
        // no operation throws and the flag is in a consistent state. Drain the residual:
        svc.ConsumeIfSet();
        Assert.False(svc.IsQueued);
    }
}
