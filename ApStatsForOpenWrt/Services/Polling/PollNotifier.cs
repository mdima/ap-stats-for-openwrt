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
/// Singleton event hub. <see cref="Cycled"/> is raised by <c>PollOrchestrator</c> after <c>tx.CommitAsync</c>.
/// Phase 5 D-03 reconciles the delegate type to <c>Func&lt;PollSnapshot, Task&gt;?</c> matching
/// <c>ARCHITECTURE.md:356</c> — closes the Phase-2 doc/code drift. Subscribers MUST register
/// <c>async Task</c> handlers (NOT <c>async void</c>; Pitfall 7) and unsubscribe in <c>Dispose</c>
/// (Pattern 2 — captured <c>this</c> leaks otherwise).
/// <para>
/// <see cref="CycleStarted"/> (v0.11 / Plan 14-01 / POLL-01 — CONTEXT 14 §D-02) is raised by
/// <c>PollOrchestrator.RunCycleAsync()</c> at the TOP of the method, BEFORE <c>BeginTransactionAsync</c>
/// and BEFORE any await that might throw. So a thrown probe/SSH/DB exception inside the try{} body
/// still leaves the badge held on <c>Polling…</c> (CONTEXT 14 §D-03 do-nothing-on-failure: the
/// orchestrator's existing try/catch/finally is NOT extended; a failing cycle simply omits the
/// trailing <c>Cycled</c>, and PollCountdown stays on <c>Polling…</c> until the next successful
/// <c>Cycled</c>). The event is parameterless (planner choice — PollCountdown only needs the
/// cycle-start signal, not a timestamp, and Plan 14-02's "Run now" button disable-state only
/// needs the boolean signal). Same subscribe/dispose discipline as <see cref="Cycled"/>.
/// </para>
/// </summary>
public sealed class PollNotifier
{
    public event Func<PollSnapshot, Task>? Cycled;
    public event Func<Task>? CycleStarted;

    public Task RaiseAsync(PollSnapshot snapshot)
        => Cycled?.Invoke(snapshot) ?? Task.CompletedTask;

    public Task RaiseCycleStartedAsync()
        => CycleStarted?.Invoke() ?? Task.CompletedTask;
}
