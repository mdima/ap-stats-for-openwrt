using ApStatsForOpenWrt.Services.UpdateCheck;
using Xunit;

namespace ApStatsForOpenWrt.Tests.UpdateCheck;

/// <summary>
/// Phase 17 Plan 17-01 — pins the M-UPD-04 pub/sub half. <see cref="UpdateNotifier"/> is a
/// structural twin of <c>PollNotifier</c> (L-04): a sealed singleton with a
/// <c>Func&lt;UpdateInfo, Task&gt;?</c> event whose <c>RaiseAsync</c> returns
/// <c>UpdateAvailable?.Invoke(info) ?? Task.CompletedTask</c>, and which writes its cached
/// <c>LastUpdate</c> state BEFORE invoking the event (260518-pch ordering invariant) so a
/// subscriber reading the cache inside its own handler observes the new value.
/// Transition-fires-once dedup (D-05) lives in the checker (Plan 02), NOT here.
/// </summary>
public sealed class UpdateNotifierTests
{
    private static UpdateInfo Sample() =>
        new(new Version(1, 2, 4), "https://github.com/owner/repo/releases/tag/v1.2.4");

    [Fact]
    public async Task RaiseAsync_NoSubscribers_ReturnsCompletedTask_DoesNotThrow()
    {
        var notifier = new UpdateNotifier();

        // Must not throw, and must complete synchronously (Task.CompletedTask).
        var task = notifier.RaiseAsync(Sample());
        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task RaiseAsync_DeliversPayload_ToSubscriber()
    {
        var notifier = new UpdateNotifier();
        UpdateInfo? received = null;
        notifier.UpdateAvailable += info => { received = info; return Task.CompletedTask; };

        var sent = Sample();
        await notifier.RaiseAsync(sent);

        Assert.NotNull(received);
        Assert.Equal(sent.NewVersion, received!.NewVersion);
        Assert.Equal(sent.ReleaseUrl, received.ReleaseUrl);
    }

    /// <summary>
    /// Ordering invariant — LastUpdate is written BEFORE UpdateAvailable?.Invoke() so a handler
    /// reading the cache inside its own callback observes the new value (mirrors
    /// PollNotifierTests.Ordering_LastSnapshot_VisibleInsideCycledHandler). Hydration source for
    /// a badge that remounts mid-circuit.
    /// </summary>
    [Fact]
    public async Task Ordering_LastUpdate_VisibleInsideHandler()
    {
        var notifier = new UpdateNotifier();
        Assert.Null(notifier.LastUpdate);

        UpdateInfo? capturedInHandler = null;
        notifier.UpdateAvailable += _ =>
        {
            capturedInHandler = notifier.LastUpdate;
            return Task.CompletedTask;
        };

        var sent = Sample();
        await notifier.RaiseAsync(sent);

        Assert.NotNull(capturedInHandler);
        Assert.Equal(sent, capturedInHandler);
        Assert.Equal(sent, notifier.LastUpdate);
    }

    [Fact]
    public async Task UnsubscribedHandler_IsNotInvoked()
    {
        var notifier = new UpdateNotifier();
        var invoked = 0;
        Func<UpdateInfo, Task> handler = _ => { Interlocked.Increment(ref invoked); return Task.CompletedTask; };

        notifier.UpdateAvailable += handler;
        notifier.UpdateAvailable -= handler;

        await notifier.RaiseAsync(Sample());

        Assert.Equal(0, invoked);
    }
}
