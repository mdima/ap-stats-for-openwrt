namespace ApStatsForOpenWrt.Services.Polling;

/// <summary>
/// Snapshot of one completed poll cycle. Phase 5's Dashboard subscribes to <see cref="PollNotifier.Cycled"/>
/// and calls <c>InvokeAsync(StateHasChanged)</c>. Phase 2 raises but does NOT subscribe — the singleton
/// is registered for forward-compat (Claude's Discretion: register-and-raise from day 1, no subscribers).
/// </summary>
public sealed record PollSnapshot(
    int TotalNodes,
    int OnlineNodes,
    int FailedNodes,
    int StationCount,
    DateTime CycleEndUtc);

/// <summary>
/// Singleton event hub. Raised by <c>PollOrchestrator</c> after <c>tx.CommitAsync</c>. Phase 5 D-03
/// reconciles the delegate type to <c>Func&lt;PollSnapshot, Task&gt;?</c> matching
/// <c>ARCHITECTURE.md:356</c> — closes the Phase-2 doc/code drift. Subscribers MUST register
/// <c>async Task</c> handlers (NOT <c>async void</c>; Pitfall 7) and unsubscribe in <c>Dispose</c>
/// (Pattern 2 — captured <c>this</c> leaks otherwise).
/// </summary>
public sealed class PollNotifier
{
    public event Func<PollSnapshot, Task>? Cycled;

    public Task RaiseAsync(PollSnapshot snapshot)
        => Cycled?.Invoke(snapshot) ?? Task.CompletedTask;
}
