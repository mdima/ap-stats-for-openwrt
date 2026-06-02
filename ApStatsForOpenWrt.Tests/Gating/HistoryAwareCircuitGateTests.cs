using ApStatsForOpenWrt.Services.Gating;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Gating;

/// <summary>
/// Phase 18 D-02 (M-HIST-03). <see cref="HistoryAwareCircuitGate"/> is a decorator over the
/// production <see cref="CircuitTrackerGate"/> + <see cref="IAppSettingsService"/>:
/// <list type="bullet">
///   <item>HistoryEnabled=true ⇒ <c>Current == Live</c> and <c>WaitForVisitorAsync</c> completes
///         immediately with zero visitors on the inner gate (continuous polling — M-HIST-03).</item>
///   <item>HistoryEnabled=false ⇒ both delegate to the inner gate (today's pause-when-no-visitor).</item>
///   <item>A false→true HistoryEnabled toggle fires a synthetic <c>Changed(GateState.Live)</c> so a
///         PAUSED PollingHostedService blocked at <c>WaitForVisitorAsync</c> wakes via its existing
///         cold-start kick. A true→false toggle fires NO synthetic event.</item>
///   <item>Inner gate <c>Changed</c> transitions are forwarded through the decorator unchanged.</item>
///   <item><c>Dispose</c> unsubscribes from BOTH the inner gate's and the settings service's events.</item>
/// </list>
/// ⚠ The settings event is <c>Action&lt;IAppSettingsService&gt;</c>, NOT <c>Func&lt;GateState,Task&gt;</c>;
/// the decorator's settings handler matches the Action signature.
/// </summary>
public sealed class HistoryAwareCircuitGateTests
{
    /// <summary>Controllable inner gate: settable <see cref="Current"/>, manually-raisable <see cref="Changed"/>.</summary>
    private sealed class FakeInnerGate : ICircuitGate
    {
        public GateState Current { get; set; } = GateState.Paused;
        public int WaitForVisitorCallCount { get; private set; }
        public event Func<GateState, Task>? Changed;

        public Task WaitForVisitorAsync(CancellationToken ct)
        {
            WaitForVisitorCallCount++;
            // A real CircuitTrackerGate with no visitor returns a never-completing task; model that
            // so the "completes immediately under HistoryEnabled" fact cannot pass by accident.
            return new TaskCompletionSource().Task;
        }

        public Task RaiseChanged(GateState s) => Changed?.Invoke(s) ?? Task.CompletedTask;
        public bool HasChangedSubscribers => Changed is not null;
    }

    /// <summary>Minimal fake: settable <see cref="HistoryEnabled"/>, manually-raisable <see cref="Changed"/>.</summary>
    private sealed class FakeAppSettings : IAppSettingsService
    {
        public bool HistoryEnabled { get; set; }
        public event Action<IAppSettingsService>? Changed;
        public void RaiseChanged() => Changed?.Invoke(this);
        public bool HasChangedSubscribers => Changed is not null;

        // Unused members for these tests.
        public int PollingIntervalSeconds => 0;
        public string? UIPasswordHash => null;
        public bool WizardDismissed => false;
        public int SessionVersion => 0;
        public int RefreshIntervalSeconds => 0;
        public IReadOnlyList<string> LocalDomainSuffixes => Array.Empty<string>();
        public bool UpdateCheckEnabled => false;
        public string? LatestKnownVersion => null;
        public string? LatestReleaseUrl => null;
        public DateTime? LastCheckedAtUtc => null;
        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHistoryEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // (a) HistoryEnabled=true ⇒ Current == Live and WaitForVisitorAsync completes immediately,
    //     WITHOUT touching the inner gate (zero-visitor short-circuit — M-HIST-03).
    [Fact]
    public void HistoryEnabled_True_ShortCircuitsToLive_WithoutTouchingInner()
    {
        var inner = new FakeInnerGate { Current = GateState.Paused };
        var settings = new FakeAppSettings { HistoryEnabled = true };
        using var gate = new HistoryAwareCircuitGate(inner, settings);

        Assert.Equal(GateState.Live, gate.Current);

        var wait = gate.WaitForVisitorAsync(CancellationToken.None);
        Assert.True(wait.IsCompletedSuccessfully);
        Assert.Equal(0, inner.WaitForVisitorCallCount);   // inner never consulted
    }

    // (b) HistoryEnabled=false ⇒ Current and WaitForVisitorAsync delegate to the inner gate.
    [Fact]
    public void HistoryEnabled_False_DelegatesToInner()
    {
        var inner = new FakeInnerGate { Current = GateState.Paused };
        var settings = new FakeAppSettings { HistoryEnabled = false };
        using var gate = new HistoryAwareCircuitGate(inner, settings);

        Assert.Equal(GateState.Paused, gate.Current);
        inner.Current = GateState.Live;
        Assert.Equal(GateState.Live, gate.Current);

        var wait = gate.WaitForVisitorAsync(CancellationToken.None);
        Assert.Equal(1, inner.WaitForVisitorCallCount);   // forwarded to inner
        Assert.False(wait.IsCompleted);                   // inner's never-completing task
    }

    // (c) A false→true settings transition fires the decorator's Changed exactly once with Live.
    [Fact]
    public async Task SettingsChanged_FalseToTrue_FiresSyntheticLive_ExactlyOnce()
    {
        var inner = new FakeInnerGate();
        var settings = new FakeAppSettings { HistoryEnabled = false };
        using var gate = new HistoryAwareCircuitGate(inner, settings);

        var fired = new List<GateState>();
        gate.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        settings.HistoryEnabled = true;   // flip the edge
        settings.RaiseChanged();

        Assert.Single(fired);
        Assert.Equal(GateState.Live, fired[0]);

        // A second Changed with no further edge fires nothing more (idempotent edge detection).
        settings.RaiseChanged();
        Assert.Single(fired);
        await Task.CompletedTask;
    }

    // (d) A true→false transition fires NO synthetic event.
    [Fact]
    public void SettingsChanged_TrueToFalse_FiresNoSyntheticEvent()
    {
        var inner = new FakeInnerGate();
        var settings = new FakeAppSettings { HistoryEnabled = true };
        using var gate = new HistoryAwareCircuitGate(inner, settings);

        var fired = new List<GateState>();
        gate.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        settings.HistoryEnabled = false;
        settings.RaiseChanged();

        Assert.Empty(fired);
    }

    // (d2) An unrelated Changed (HistoryEnabled steady-true) fires no synthetic event.
    [Fact]
    public void SettingsChanged_NoEdge_FiresNoSyntheticEvent()
    {
        var inner = new FakeInnerGate();
        var settings = new FakeAppSettings { HistoryEnabled = true };
        using var gate = new HistoryAwareCircuitGate(inner, settings);

        var fired = new List<GateState>();
        gate.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        settings.RaiseChanged();   // still true → still true: no edge

        Assert.Empty(fired);
    }

    // (e) An inner Changed(Paused) is forwarded through the decorator's Changed.
    [Fact]
    public async Task InnerChanged_IsForwardedThroughDecorator()
    {
        var inner = new FakeInnerGate();
        var settings = new FakeAppSettings { HistoryEnabled = false };
        using var gate = new HistoryAwareCircuitGate(inner, settings);

        var fired = new List<GateState>();
        gate.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        await inner.RaiseChanged(GateState.Paused);
        await inner.RaiseChanged(GateState.Live);

        Assert.Equal(new[] { GateState.Paused, GateState.Live }, fired);
    }

    // (f) Dispose unsubscribes from BOTH events — post-dispose raises invoke no decorator handler.
    [Fact]
    public async Task Dispose_UnsubscribesFromBothEvents()
    {
        var inner = new FakeInnerGate();
        var settings = new FakeAppSettings { HistoryEnabled = false };
        var gate = new HistoryAwareCircuitGate(inner, settings);

        // Sanity: while alive, the decorator is subscribed to both events.
        Assert.True(inner.HasChangedSubscribers);
        Assert.True(settings.HasChangedSubscribers);

        var fired = new List<GateState>();
        gate.Changed += s => { fired.Add(s); return Task.CompletedTask; };

        gate.Dispose();

        // Decorator's own handlers must be detached from both sources.
        Assert.False(inner.HasChangedSubscribers);
        Assert.False(settings.HasChangedSubscribers);

        // Raising either source post-dispose must not reach the decorator's Changed.
        await inner.RaiseChanged(GateState.Live);
        settings.HistoryEnabled = true;
        settings.RaiseChanged();

        Assert.Empty(fired);
    }
}
