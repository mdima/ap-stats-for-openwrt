using Microsoft.Extensions.Logging;
using OpenWrtApStats.Services.Gating;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Settings;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

/// <summary>
/// PollingHostedService tests. Closes:
/// D-06 (cycle-overlap skip with Information log),
/// POLL-02 (interval change observed on the NEXT tick with `Polling interval changed:` log),
/// CLAUDE.md Conventions (per-cycle exception MUST NOT terminate the loop),
/// Phase 6 D-01 (cold-start kick — wait for visitor, run immediately, re-arm on Paused).
/// </summary>
public sealed class PollingHostedServiceTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();
        private readonly object _gate = new();
        public List<string> Messages
        {
            get
            {
                lock (_gate) return _entries.Select(e => e.Message).ToList();
            }
        }

        public List<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_gate) return _entries.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add((level, formatter(state, exception), exception));
        }
    }

    /// <summary>
    /// Mutable in-memory IAppSettingsService stub. Tests change PollingIntervalSeconds at runtime
    /// to drive the POLL-02 interval-change observation path.
    /// </summary>
    private sealed class StubAppSettingsService(int pollingIntervalSeconds) : IAppSettingsService
    {
        public int PollingIntervalSeconds { get; set; } = pollingIntervalSeconds;
        public string? UIPasswordHash => null;
        public bool WizardDismissed => false;
        public event Action<IAppSettingsService>? Changed;

        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default)
        {
            PollingIntervalSeconds = seconds;
            Changed?.Invoke(this);
            return Task.CompletedTask;
        }

        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Phase 6 D-01 — controllable ICircuitGate stub. Open() flips Current to Live, completes
    /// the pending TCS, fires Changed(Live). Close() flips back to Paused, re-arms a fresh
    /// TCS, fires Changed(Paused). WaitForVisitorAsync mirrors CircuitTracker semantics:
    /// returns Task.CompletedTask immediately when Current==Live (so a race where the test
    /// calls Open() before the SUT subscribes still returns synchronously on the next call).
    /// </summary>
    private sealed class StubGate : ICircuitGate
    {
        // int backing for the enum so Volatile.Read/Write work cleanly (enum is not a generic
        // type argument to Volatile.Read<T>). 0 == Paused, 1 == Live (matches GateState).
        private int _currentInt = (int)GateState.Paused;
        public GateState Current => (GateState)Volatile.Read(ref _currentInt);
        public event Func<GateState, Task>? Changed;

        private TaskCompletionSource<bool> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForVisitorAsync(CancellationToken ct)
        {
            // Mirror CircuitTracker.WaitForVisitorAsync: short-circuit when already Live so a
            // race where the test calls Open() before the SUT calls WaitForVisitorAsync still
            // returns synchronously on the next call.
            if (Current == GateState.Live) return Task.CompletedTask;
            return Volatile.Read(ref _tcs).Task.WaitAsync(ct);
        }

        /// <summary>Test API — flips Live, completes the current TCS, fires Changed(Live).</summary>
        public void Open()
        {
            Volatile.Write(ref _currentInt, (int)GateState.Live);
            Volatile.Read(ref _tcs).TrySetResult(true);
            _ = (Changed?.Invoke(GateState.Live) ?? Task.CompletedTask);
        }

        /// <summary>Test API — flips Paused, re-arms a fresh TCS, fires Changed(Paused).</summary>
        public void Close()
        {
            Volatile.Write(ref _currentInt, (int)GateState.Paused);
            // Re-arm so the next WaitForVisitorAsync caller awaits a fresh, non-pre-completed TCS.
            Interlocked.Exchange(ref _tcs,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            _ = (Changed?.Invoke(GateState.Paused) ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task CycleOverlap_SkipsTickWithInformationLog()
    {
        // D-06 — interval=1s, cycle=1.2s; observation window 2.5s -> at least one overlap-skip occurs.
        // B6 LOCK budget: total wall-clock < 5s.
        var ct = TestContext.Current.CancellationToken;
        var capLogger = new CapturingLogger<PollingHostedService>();

        Func<CancellationToken, Task> runCycleAsync = async cct => await Task.Delay(1200, cct);
        var settings = new StubAppSettingsService(pollingIntervalSeconds: 1);

        var svc = new PollingHostedService(runCycleAsync, settings, new AlwaysLiveGate(), capLogger);

        await svc.StartAsync(ct);
        try
        {
            await Task.Delay(2500, ct);   // observation window
        }
        finally
        {
            await svc.StopAsync(ct);
        }

        Assert.Contains(capLogger.Messages, m =>
            m.Contains("Polling tick skipped: previous cycle still in flight", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IntervalChange_ObservedOnNextTick()
    {
        // POLL-02 — start with 1s interval, mutate to 2s; observe `Polling interval changed: 1s → 2s`.
        var ct = TestContext.Current.CancellationToken;
        var capLogger = new CapturingLogger<PollingHostedService>();

        Func<CancellationToken, Task> runCycleAsync = _ => Task.CompletedTask;
        var settings = new StubAppSettingsService(pollingIntervalSeconds: 1);

        var svc = new PollingHostedService(runCycleAsync, settings, new AlwaysLiveGate(), capLogger);

        await svc.StartAsync(ct);
        try
        {
            await Task.Delay(1500, ct);          // ≥1 tick at 1s interval has passed
            settings.PollingIntervalSeconds = 2; // mutate -> observed on the NEXT tick
            await Task.Delay(1500, ct);          // wait long enough that another tick fires
        }
        finally
        {
            await svc.StopAsync(ct);
        }

        Assert.Contains(capLogger.Messages, m =>
            m.Contains("Polling interval changed:", StringComparison.Ordinal)
            && m.Contains("1s", StringComparison.Ordinal)
            && m.Contains("2s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PerCycleException_DoesNotTerminateLoop()
    {
        // CLAUDE.md Conventions: per-cycle exception logs at Error, the BackgroundService loop continues.
        var ct = TestContext.Current.CancellationToken;
        var capLogger = new CapturingLogger<PollingHostedService>();

        var callCount = 0;
        Func<CancellationToken, Task> runCycleAsync = _ =>
        {
            var n = Interlocked.Increment(ref callCount);
            if (n == 1) throw new InvalidOperationException("synthetic first-cycle failure");
            return Task.CompletedTask;
        };
        var settings = new StubAppSettingsService(pollingIntervalSeconds: 1);

        var svc = new PollingHostedService(runCycleAsync, settings, new AlwaysLiveGate(), capLogger);

        await svc.StartAsync(ct);
        try
        {
            await Task.Delay(3500, ct);   // ~3 ticks: 1 throw + ≥2 successful cycles
        }
        finally
        {
            await svc.StopAsync(ct);
        }

        // Error log emitted for the failing cycle.
        Assert.Contains(capLogger.Entries, e =>
            e.Level == LogLevel.Error
            && e.Message.Contains("Poll cycle failed", StringComparison.Ordinal));

        // Loop survived the throw — at least 2 subsequent successful cycles ran.
        Assert.True(callCount >= 3, $"Expected ≥3 cycles total (1 failed + ≥2 succeeded); actual={callCount}");
    }

    [Fact]
    public async Task ExecuteAsync_WithPausedGate_DoesNotRunFirstCycle_UntilOpen()
    {
        // Phase 6 D-01 — cold-start kick. With a Paused stub gate, no cycle runs until Open()
        // is called; once it does, exactly one cycle fires immediately (no full PeriodicTimer wait).
        var ct = TestContext.Current.CancellationToken;
        var capLogger = new CapturingLogger<PollingHostedService>();
        var settings = new StubAppSettingsService(pollingIntervalSeconds: 60);  // long timer so only the kick fires
        var gate = new StubGate();

        var cycleCount = 0;
        Func<CancellationToken, Task> runCycle = _ =>
        {
            Interlocked.Increment(ref cycleCount);
            return Task.CompletedTask;
        };

        var sut = new PollingHostedService(runCycle, settings, gate, capLogger);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await sut.StartAsync(cts.Token);

        // Wait a short time — the gate is Paused, so no cycle should run.
        await Task.Delay(200, ct);
        Assert.Equal(0, cycleCount);

        // Open the gate — cold-start kick fires.
        gate.Open();

        // Bound the wait; if the kick doesn't fire, fail fast.
        await WaitForCount(() => cycleCount, 1, ct);
        Assert.Equal(1, cycleCount);

        cts.Cancel();
        await sut.StopAsync(ct);
    }

    [Fact]
    public async Task ExecuteAsync_AfterPauseAndReopen_TriggersColdStartKick()
    {
        // Phase 6 D-01 — re-armable cold-start. After a Live -> Paused -> Live transition the
        // next WaitForVisitorAsync completion runs an IMMEDIATE cycle (timer reset), even though
        // the long PeriodicTimer interval would not have fired yet on its own.
        var ct = TestContext.Current.CancellationToken;
        var capLogger = new CapturingLogger<PollingHostedService>();
        var settings = new StubAppSettingsService(pollingIntervalSeconds: 60);  // long timer means the kick is the ONLY thing that fires
        var gate = new StubGate();

        var cycleCount = 0;
        Func<CancellationToken, Task> runCycle = _ =>
        {
            Interlocked.Increment(ref cycleCount);
            return Task.CompletedTask;
        };

        var sut = new PollingHostedService(runCycle, settings, gate, capLogger);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await sut.StartAsync(cts.Token);
        gate.Open();                // first kick
        await WaitForCount(() => cycleCount, 1, ct);

        // Long timer means a second cycle would not occur naturally within the test window.
        gate.Close();
        await Task.Delay(50, ct);   // give the OnGateChanged callback time to flip _coldStart back true
        gate.Open();                // re-armed kick
        await WaitForCount(() => cycleCount, 2, ct);

        cts.Cancel();
        await sut.StopAsync(ct);
    }

    private static async Task WaitForCount(Func<int> count, int target, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (count() < target && DateTime.UtcNow < deadline) await Task.Delay(20, ct);
        Assert.Equal(target, count());
    }
}
