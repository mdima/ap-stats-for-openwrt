using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ApStatsForOpenWrt.Services.Gating;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Services.UpdateCheck;
using Xunit;

namespace ApStatsForOpenWrt.Tests.UpdateCheck;

/// <summary>
/// M-UPD-01 — UpdateCheckHostedService is the simpler charter loop: WaitForVisitor (top) → if enabled
/// CheckOnce → WaitForNextTick(24h). The first iteration after the first visitor IS the startup check.
/// Toggling the feature off skips CheckOnce within one iteration. The 24h cadence is driven by an
/// injected TimeProvider so the test advances time without a real wait. A throwing CheckOnceAsync must
/// NOT terminate the loop (CLAUDE.md). Cancellation exits cleanly with the stopped Information log.
/// NONE of the polling-specific machinery (cold-start kick / OnGateChanged / SemaphoreSlim / timer
/// reconfiguration) is present.
/// </summary>
public sealed class UpdateCheckHostedServiceTests
{
    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();
        private readonly object _gate = new();
        public List<string> Messages { get { lock (_gate) return _entries.Select(e => e.Message).ToList(); } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add((level, formatter(state, exception), exception));
        }
    }

    /// <summary>Records CheckOnceAsync invocations; optionally throws to prove the loop survives.</summary>
    private sealed class FakeUpdateChecker : IUpdateChecker
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Throw { get; set; }
        public Func<Task>? OnCheck { get; set; }

        public async Task CheckOnceAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            if (OnCheck is not null) await OnCheck();
            if (Throw) throw new InvalidOperationException("boom (should be swallowed by the loop)");
        }
    }

    private sealed class StubGate : ICircuitGate
    {
        private int _currentInt = (int)GateState.Paused;
        public GateState Current => (GateState)Volatile.Read(ref _currentInt);
        public event Func<GateState, Task>? Changed;
        private TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForVisitorAsync(CancellationToken ct)
        {
            if (Current == GateState.Live) return Task.CompletedTask;
            return Volatile.Read(ref _tcs).Task.WaitAsync(ct);
        }

        public void Open()
        {
            Volatile.Write(ref _currentInt, (int)GateState.Live);
            Volatile.Read(ref _tcs).TrySetResult(true);
            _ = (Changed?.Invoke(GateState.Live) ?? Task.CompletedTask);
        }

        public void Close()
        {
            Volatile.Write(ref _currentInt, (int)GateState.Paused);
            Interlocked.Exchange(ref _tcs, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            _ = (Changed?.Invoke(GateState.Paused) ?? Task.CompletedTask);
        }
    }

    private sealed class StubAppSettingsService : IAppSettingsService
    {
        public int PollingIntervalSeconds => 30;
        public string? UIPasswordHash => null;
        public bool WizardDismissed => false;
        public int RefreshIntervalSeconds => 3600;
        public IReadOnlyList<string> LocalDomainSuffixes => Array.Empty<string>();
        public int SessionVersion => 0;
        public event Action<IAppSettingsService>? Changed;

        public bool UpdateCheckEnabled { get; set; } = true;
        public bool HistoryEnabled => true;
        public Task SetHistoryEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
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
        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) { UpdateCheckEnabled = enabled; Changed?.Invoke(this); return Task.CompletedTask; }
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // Spin until a condition holds or a short deadline elapses (no fixed-delay flakiness).
    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GateClosed_BlocksCheck_ThenFiresOnceAfterVisitor()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = new FakeUpdateChecker();
        var settings = new StubAppSettingsService();
        var gate = new StubGate();
        var log = new CapturingLogger<UpdateCheckHostedService>();
        var time = new FakeTimeProvider();

        var svc = new UpdateCheckHostedService(checker, settings, gate, time, log);
        await svc.StartAsync(ct);
        try
        {
            // Gate closed → no check.
            await Task.Delay(100, ct);
            Assert.Equal(0, checker.Calls);

            // First visitor → exactly one check (startup check).
            gate.Open();
            await WaitUntilAsync(() => checker.Calls >= 1);
            Assert.Equal(1, checker.Calls);
        }
        finally
        {
            await svc.StopAsync(ct);
        }
    }

    [Fact]
    public async Task ToggleOff_SkipsCheck_WithVisitorPresent()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = new FakeUpdateChecker();
        var settings = new StubAppSettingsService { UpdateCheckEnabled = false };
        var gate = new StubGate();
        var log = new CapturingLogger<UpdateCheckHostedService>();
        var time = new FakeTimeProvider();

        var svc = new UpdateCheckHostedService(checker, settings, gate, time, log);
        await svc.StartAsync(ct);
        try
        {
            gate.Open();
            await Task.Delay(150, ct);
            // Toggle is OFF → the visitor-gated loop skips CheckOnceAsync.
            Assert.Equal(0, checker.Calls);
        }
        finally
        {
            await svc.StopAsync(ct);
        }
    }

    [Fact]
    public async Task TwentyFourHourCadence_FiresSecondCheck_OnFakeClockAdvance()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = new FakeUpdateChecker();
        var settings = new StubAppSettingsService();
        var gate = new StubGate();
        var log = new CapturingLogger<UpdateCheckHostedService>();
        var time = new FakeTimeProvider();

        var svc = new UpdateCheckHostedService(checker, settings, gate, time, log);
        await svc.StartAsync(ct);
        try
        {
            gate.Open();
            await WaitUntilAsync(() => checker.Calls >= 1);
            Assert.Equal(1, checker.Calls);

            // No tick yet → no second call.
            await Task.Delay(100, ct);
            Assert.Equal(1, checker.Calls);

            // Advance the fake clock by 24h → exactly one more check.
            time.Advance(TimeSpan.FromHours(24));
            await WaitUntilAsync(() => checker.Calls >= 2);
            Assert.Equal(2, checker.Calls);
        }
        finally
        {
            await svc.StopAsync(ct);
        }
    }

    [Fact]
    public async Task ThrowingCheck_DoesNotTerminateLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = new FakeUpdateChecker { Throw = true };
        var settings = new StubAppSettingsService();
        var gate = new StubGate();
        var log = new CapturingLogger<UpdateCheckHostedService>();
        var time = new FakeTimeProvider();

        var svc = new UpdateCheckHostedService(checker, settings, gate, time, log);
        await svc.StartAsync(ct);
        try
        {
            gate.Open();
            await WaitUntilAsync(() => checker.Calls >= 1);
            Assert.Equal(1, checker.Calls);

            // The first check threw; the loop must still drive the next 24h tick.
            time.Advance(TimeSpan.FromHours(24));
            await WaitUntilAsync(() => checker.Calls >= 2);
            Assert.Equal(2, checker.Calls);
        }
        finally
        {
            await svc.StopAsync(ct);
        }
    }

    [Fact]
    public async Task Cancellation_ExitsCleanly_WithStoppedLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = new FakeUpdateChecker();
        var settings = new StubAppSettingsService();
        var gate = new StubGate();
        var log = new CapturingLogger<UpdateCheckHostedService>();
        var time = new FakeTimeProvider();

        var svc = new UpdateCheckHostedService(checker, settings, gate, time, log);
        await svc.StartAsync(ct);
        gate.Open();
        await WaitUntilAsync(() => checker.Calls >= 1);

        await svc.StopAsync(ct);

        Assert.Contains(log.Messages, m => m.Contains("UpdateCheckHostedService stopped", StringComparison.Ordinal));
    }
}
