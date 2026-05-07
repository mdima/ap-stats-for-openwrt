using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Services;
using Xunit;

namespace OpenWrtApStats.Tests.Services;

/// <summary>
/// Phase 8 Plan 08-06 Task 1 — exercises <see cref="ToastService"/> against the
/// <see cref="INotificationsGate"/> abstraction (D-10 defense-in-depth, gate-only test
/// pattern locked by plan-checker WARNING 6 closure). Tests construct ToastService with
/// a hand-rolled <c>FakeGate</c>; PreferencesService is never instantiated here.
///
/// Behaviors locked in 08-06-PLAN.md Task 1:
///   1. Show with notifications enabled → OnShow event fires exactly once with the entry.
///   2. Show with notifications disabled → OnShow does NOT fire (D-10 hot-path early-exit).
///   3. Show with severity not in {info, success, warning, danger} → throws ArgumentException.
///   4. Show with null/empty/whitespace title → throws ArgumentException (defensive).
///   5. Show with subscriber that throws → other subscribers continue (event-bus resilience).
///   6. Show raises with a fresh Id Guid per call (regression: not Guid.Empty, unique).
///
/// Test stack: xUnit v3 + bUnit 2.7.2 + Microsoft.Testing.Extensions.CodeCoverage (NEVER coverlet).
/// Coverage gate per D-18: ≥ 80% line + ≥ 80% branch on ToastService.cs.
/// </summary>
public sealed class ToastServiceTests
{
    private sealed class FakeGate : INotificationsGate
    {
        public bool IsEnabled { get; init; }
    }

    private static ToastService CreateService(bool gateOpen)
        => new(new FakeGate { IsEnabled = gateOpen }, NullLogger<ToastService>.Instance);

    // 1. Show with notifications enabled → OnShow fires exactly once with the entry. -----------
    [Fact]
    public async Task Show_WhenNotificationsEnabled_RaisesOnShow_Once_WithEntry()
    {
        var svc = CreateService(gateOpen: true);
        var received = new List<ToastEntry>();
        svc.OnShow += entry =>
        {
            received.Add(entry);
            return Task.CompletedTask;
        };

        await svc.Show("New station", "kitchen-ap", "info");

        var entry = Assert.Single(received);
        Assert.Equal("New station", entry.Title);
        Assert.Equal("kitchen-ap", entry.Message);
        Assert.Equal("info", entry.Severity);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    // 2. Show with notifications disabled → no OnShow emission (D-10 hot-path gate). ------------
    [Fact]
    public async Task Show_WhenNotificationsDisabled_DoesNotRaiseOnShow()
    {
        var svc = CreateService(gateOpen: false);
        var fired = false;
        svc.OnShow += _ =>
        {
            fired = true;
            return Task.CompletedTask;
        };

        await svc.Show("Should not fire", "—", "info");

        Assert.False(fired);
    }

    // 3. Show with invalid severity → ArgumentException. ----------------------------------------
    [Theory]
    [InlineData("error")]      // close-but-wrong (Bootstrap 5 has danger, not error)
    [InlineData("INFO")]       // case-sensitive — uppercase rejected
    [InlineData("")]
    [InlineData("<script>")]   // T-08-20 — severity injection guard
    public async Task Show_WithInvalidSeverity_ThrowsArgumentException(string severity)
    {
        var svc = CreateService(gateOpen: true);
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.Show("Title", "Message", severity));
    }

    // 4. Show with null/empty/whitespace title → ArgumentException (defensive). -----------------
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Show_WithNullOrWhitespaceTitle_ThrowsArgumentException(string? title)
    {
        var svc = CreateService(gateOpen: true);
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.Show(title!, "Message", "info"));
    }

    // 5. One subscriber throwing does NOT abort the other subscribers (T-08-18). ----------------
    [Fact]
    public async Task Show_SubscriberThrows_OtherSubscribersStillRun()
    {
        var svc = CreateService(gateOpen: true);
        var goodFired = 0;

        svc.OnShow += _ => throw new InvalidOperationException("subscriber A blew up");
        svc.OnShow += _ =>
        {
            goodFired++;
            return Task.CompletedTask;
        };

        // Must NOT throw — ToastService catches per-subscriber exceptions.
        await svc.Show("Title", "Message", "info");

        Assert.Equal(1, goodFired);
    }

    // 6. Each Show emits a fresh Guid (regression: not Guid.Empty, unique across calls). --------
    [Fact]
    public async Task Show_EmitsFreshGuid_PerCall()
    {
        var svc = CreateService(gateOpen: true);
        var ids = new List<Guid>();
        svc.OnShow += entry =>
        {
            ids.Add(entry.Id);
            return Task.CompletedTask;
        };

        await svc.Show("T1", "M1", "info");
        await svc.Show("T2", "M2", "success");
        await svc.Show("T3", "M3", "warning");

        Assert.Equal(3, ids.Count);
        Assert.All(ids, id => Assert.NotEqual(Guid.Empty, id));
        Assert.Equal(3, ids.Distinct().Count());   // all unique
    }
}
