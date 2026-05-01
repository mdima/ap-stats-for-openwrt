using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Services.Nodes;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Polling.Exceptions;
using Xunit;

namespace OpenWrtApStats.Tests.Services.Nodes;

/// <summary>
/// NODE-02 / D-11 catalog mapping. Each [Theory] row asserts: a Failed reason-token
/// (substring of the actual NodePoller emission) maps to the verbatim UI-SPEC §Test
/// Connection banner copy. The outcome's UserMessage is what the Razor form renders
/// inside the alert div.
///
/// <para>
/// Reason-token vocabulary verified at Plan 03-04 execute time against
/// <c>OpenWrtApStats/Services/Polling/NodePoller.cs</c> + <c>PollOrchestrator.cs</c>:
/// <c>within-cycle-reentry</c>, <c>decrypt-failed</c>, <c>algo-negotiate-failed</c>,
/// <c>auth-failed</c>, <c>network-error</c>, <c>timeout</c>, <c>probe-failed</c>,
/// <c>host-key-mismatch</c>.
/// </para>
/// </summary>
public sealed class TestConnectionServiceTests
{
    private static IDataProtectionProvider EphemeralDpp()
    {
        var sc = new ServiceCollection();
        sc.AddDataProtection().SetApplicationName("OpenWrtApStats-Tests-Plan03-04");
        return sc.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private static TestConnectionService Create(
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe)
        => new TestConnectionService(probe, EphemeralDpp(), NullLogger<TestConnectionService>.Instance);

    [Fact]
    public async Task Success_MapsToSuccessBanner_WithCounts()
    {
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
            Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                n.Id,
                Networks: new List<WiFiNetwork>
                {
                    new() { MacAddress = "AA:BB:CC:00:00:01", SSID = "x" },
                    new() { MacAddress = "AA:BB:CC:00:00:02", SSID = "y" },
                },
                Stations: new List<WLANStation>
                {
                    new() { MacAddress = "DD:EE:FF:00:00:01" },
                },
                NewlyTrustedFingerprint: null));
        var svc = Create(probe);
        var ct = TestContext.Current.CancellationToken;

        var outcome = await svc.TestAsync("h", "u", "p", existingNodeId: null, ct);

        Assert.IsType<TestConnectionOutcome.Success>(outcome);
        Assert.Equal(OutcomeSeverity.Success, outcome.Severity);
        Assert.Equal("Connected. Discovered 2 WLANs, observed 1 associated stations.", outcome.UserMessage);
    }

    [Theory]
    [InlineData("algo-negotiate-failed", "advertise an algorithm we accept")]
    [InlineData("auth-failed", "Authentication failed")]
    [InlineData("timeout", "Connection timed out after 5 seconds")]
    [InlineData("decrypt-failed", "Stored password could not be decrypted")]
    [InlineData("host-key-mismatch", "Host key changed since first trust")]
    [InlineData("within-cycle-reentry", "Node is busy with the regular polling cycle")]
    [InlineData("network-error", "Connection failed: network-error")]
    [InlineData("probe-failed", "Connection failed: probe-failed")]
    public async Task Failed_ReasonToken_MapsToOperatorFacingCopy(string reasonToken, string expectedSubstring)
    {
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
            Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, reasonToken));
        var svc = Create(probe);
        var ct = TestContext.Current.CancellationToken;

        var outcome = await svc.TestAsync("h", "u", "p", existingNodeId: null, ct);

        Assert.Contains(expectedSubstring, outcome.UserMessage);
    }

    [Fact]
    public async Task HostKeyMismatchException_MapsToHostKeyMismatchOutcome()
    {
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
            throw new HostKeyMismatchException("expected-fp", "actual-fp");
        var svc = Create(probe);
        var ct = TestContext.Current.CancellationToken;

        var outcome = await svc.TestAsync("h", "u", "p", null, ct);

        Assert.IsType<TestConnectionOutcome.HostKeyMismatch>(outcome);
        Assert.Contains("Host key changed since first trust", outcome.UserMessage);
    }

    [Fact]
    public async Task Add_Path_PassesId0_ToProbe()
    {
        int observedId = -1;
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            observedId = n.Id;
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                n.Id, new List<WiFiNetwork>(), new List<WLANStation>(), null));
        };
        var svc = Create(probe);
        var ct = TestContext.Current.CancellationToken;

        await svc.TestAsync("h", "u", "p", existingNodeId: null, ct);

        Assert.Equal(0, observedId);
    }

    [Fact]
    public async Task Edit_Path_PassesExistingNodeId_ToProbe()
    {
        int observedId = -1;
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            observedId = n.Id;
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                n.Id, new List<WiFiNetwork>(), new List<WLANStation>(), null));
        };
        var svc = Create(probe);
        var ct = TestContext.Current.CancellationToken;

        await svc.TestAsync("h", "u", "p", existingNodeId: 42, ct);

        Assert.Equal(42, observedId);
    }

    [Fact]
    public async Task EncryptsPlaintext_BeforeCallingProbe()
    {
        // RESEARCH §Pattern 3 / Assumption A2: NodePoller's probe-factory unprotects
        // node.Password through the "node-creds" purpose chain. Plaintext would throw
        // CryptographicException → "decrypt-failed". The service therefore MUST
        // pre-encrypt the form-supplied plaintext.
        string? passwordSeenByProbe = null;
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe = (n, _) =>
        {
            passwordSeenByProbe = n.Password;
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                n.Id, new List<WiFiNetwork>(), new List<WLANStation>(), null));
        };
        var svc = Create(probe);
        var ct = TestContext.Current.CancellationToken;

        await svc.TestAsync("h", "u", "PLAINTEXT-123", null, ct);

        Assert.NotNull(passwordSeenByProbe);
        Assert.NotEqual("PLAINTEXT-123", passwordSeenByProbe);
        // Data Protection ciphertext starts with the well-known "CfDJ" prefix.
        Assert.StartsWith("CfDJ", passwordSeenByProbe!);
    }
}
