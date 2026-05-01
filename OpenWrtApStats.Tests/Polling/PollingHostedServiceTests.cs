using Microsoft.Extensions.Logging;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Settings;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

/// <summary>
/// PollingHostedService tests. Closes:
/// D-06 (cycle-overlap skip with Information log),
/// POLL-02 (interval change observed on the NEXT tick with `Polling interval changed:` log),
/// CLAUDE.md Conventions (per-cycle exception MUST NOT terminate the loop).
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

    [Fact]
    public async Task CycleOverlap_SkipsTickWithInformationLog()
    {
        // D-06 — interval=1s, cycle=1.2s; observation window 2.5s -> at least one overlap-skip occurs.
        // B6 LOCK budget: total wall-clock < 5s.
        var ct = TestContext.Current.CancellationToken;
        var capLogger = new CapturingLogger<PollingHostedService>();

        Func<CancellationToken, Task> runCycleAsync = async cct => await Task.Delay(1200, cct);
        var settings = new StubAppSettingsService(pollingIntervalSeconds: 1);

        var svc = new PollingHostedService(runCycleAsync, settings, capLogger);

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

        var svc = new PollingHostedService(runCycleAsync, settings, capLogger);

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

        var svc = new PollingHostedService(runCycleAsync, settings, capLogger);

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
}
