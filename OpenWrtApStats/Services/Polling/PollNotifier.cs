namespace OpenWrtApStats.Services.Polling;

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
/// Singleton event hub. Raised by <c>PollOrchestrator</c> after <c>tx.CommitAsync</c>. No subscribers in Phase 2.
/// </summary>
public sealed class PollNotifier
{
    public event Action<PollSnapshot>? Cycled;

    public Task RaiseAsync(PollSnapshot snapshot)
    {
        Cycled?.Invoke(snapshot);
        return Task.CompletedTask;
    }
}
