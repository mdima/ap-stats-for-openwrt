using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenWrtApStats.Components.Shared;
using OpenWrtApStats.Services.Gating;
using Xunit;

namespace OpenWrtApStats.Tests.Components.Shared;

/// <summary>
/// Phase 5 D-12 / DASH-08 LivePausedBadge — renders Live (Phase 5 default via AlwaysLiveGate)
/// and Paused (Phase 6 forward-compat via a configurable test gate).
/// </summary>
public sealed class LivePausedBadgeTests
{
    private sealed class TestGate : ICircuitGate
    {
        public GateState Current { get; set; } = GateState.Live;
        public event Func<GateState, Task>? Changed;     // W3: matches the W3-amended interface
        public Task Raise(GateState newState)
        {
            Current = newState;
            return Changed?.Invoke(newState) ?? Task.CompletedTask;
        }
    }

    [Fact]
    public void RendersLive_WhenAlwaysLiveGate_IsRegistered()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICircuitGate>(new AlwaysLiveGate());
        var cut = ctx.Render<LivePausedBadge>();

        Assert.Contains("Live", cut.Markup);
        Assert.Contains("text-bg-success", cut.Markup);
        Assert.Contains("bi-circle-fill", cut.Markup);
        Assert.DoesNotContain("Paused", cut.Markup);
    }

    [Fact]
    public void RendersPaused_WhenGateCurrentIsPaused()
    {
        using var ctx = new BunitContext();
        var gate = new TestGate { Current = GateState.Paused };
        ctx.Services.AddSingleton<ICircuitGate>(gate);
        var cut = ctx.Render<LivePausedBadge>();

        Assert.Contains("Paused", cut.Markup);
        Assert.Contains("text-bg-secondary", cut.Markup);
        Assert.Contains("bi-pause-fill", cut.Markup);
        // The literal text "Live" must not appear (the ternary picks "Paused" instead).
        // Defensive: strip "Paused" before checking, in case any markup sub-token coincidentally
        // contains the substring "Live".
        Assert.DoesNotContain("Live", cut.Markup.Replace("Paused", ""));
    }

    [Fact]
    public async Task GateChanged_FlipsBadgeFromLiveToPaused()
    {
        using var ctx = new BunitContext();
        var gate = new TestGate { Current = GateState.Live };
        ctx.Services.AddSingleton<ICircuitGate>(gate);
        var cut = ctx.Render<LivePausedBadge>();

        Assert.Contains("Live", cut.Markup);

        await gate.Raise(GateState.Paused);     // W3: Raise returns Task
        cut.WaitForState(() => cut.Markup.Contains("Paused"), TimeSpan.FromSeconds(2));

        Assert.Contains("Paused", cut.Markup);
        Assert.DoesNotContain("Live", cut.Markup.Replace("Paused", ""));   // Defensive: ensure Paused replaced Live
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromGateChanged()
    {
        var gate = new TestGate();
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICircuitGate>(gate);
        var cut = ctx.Render<LivePausedBadge>();
        ctx.Dispose();

        // After dispose, raising should NOT throw or update the (disposed) cut.
        // W3: Raise returns Task; if no subscribers remain it returns Task.CompletedTask.
        await gate.Raise(GateState.Paused);   // no exception expected — handler removed
    }
}
