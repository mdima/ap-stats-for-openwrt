namespace ApStatsForOpenWrt.Services.UpdateCheck;

/// <summary>
/// Payload describing a newly-discovered upstream release. Mirrors <c>PollSnapshot</c>'s
/// immutable-record shape (L-04 — UpdateNotifier is a structural twin of <c>PollNotifier</c>,
/// NOT an extension of it). <see cref="NewVersion"/> is the parsed GitHub release tag;
/// <see cref="ReleaseUrl"/> is its release-page URL.
/// </summary>
public sealed record UpdateInfo(Version NewVersion, string ReleaseUrl);

/// <summary>
/// M-UPD-04 (pub/sub half) — singleton event hub for the discovery-time "update available"
/// signal. A separate singleton from <c>PollNotifier</c> by deliberate decision (NOT an
/// extension of it); injected by concrete type with no interface, exactly like
/// <c>PollNotifier</c>. The checker (Plan 02) calls <see cref="RaiseAsync"/> on a real
/// N→N+1 advance; the navbar badge + discovery toast (Plan 03) subscribe with
/// <c>async Task</c> handlers (NOT <c>async void</c>; Pitfall 7) and unsubscribe in
/// <c>Dispose</c> (Pattern 2 — captured <c>this</c> leaks otherwise).
/// <para>
/// Transition-fires-once discipline (D-05) lives in the CHECKER (Plan 02), not here — this
/// notifier raises whatever it is told to raise; there is no dedup logic.
/// </para>
/// </summary>
public sealed class UpdateNotifier
{
    // 260518-pch ordering invariant — cached so a per-circuit subscriber mounted MID-CYCLE
    // (badge remount / new tab) can hydrate its local state from the notifier instead of
    // sitting empty until the next UpdateAvailable event. The write happens BEFORE event
    // invocation (see RaiseAsync) so a handler reading LastUpdate inside its own callback
    // sees the new value — pinned by UpdateNotifierTests.Ordering_LastUpdate_VisibleInsideHandler.
    private UpdateInfo? _lastUpdate;

    /// <summary>
    /// Last raised <see cref="UpdateInfo"/>, or null if no update has been announced in this
    /// process lifetime. Hydration source for a badge component mounted mid-circuit.
    /// </summary>
    public UpdateInfo? LastUpdate => Volatile.Read(ref _lastUpdate);

    public event Func<UpdateInfo, Task>? UpdateAvailable;

    public Task RaiseAsync(UpdateInfo info)
    {
        // STATE WRITE BEFORE INVOCATION (ordering invariant). A subscriber reading
        // Notifier.LastUpdate inside its own handler MUST observe the new value.
        Interlocked.Exchange(ref _lastUpdate, info);
        return UpdateAvailable?.Invoke(info) ?? Task.CompletedTask;
    }
}
