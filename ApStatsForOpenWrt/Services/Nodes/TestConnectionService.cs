using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Polling.Exceptions;

namespace ApStatsForOpenWrt.Services.Nodes;

/// <summary>
/// D-07 implementation. Encrypts the form-supplied plaintext password through the same
/// <c>"node-creds"</c> Data Protection purpose chain that Phase 2 production uses, builds
/// a transient <see cref="WiFiNode"/> (Id=0 for Add path; existing Id for Edit path),
/// invokes the probe-factory, maps the result to a <see cref="TestConnectionOutcome"/>.
/// Read-only: zero DB writes. The first real poll cycle captures any TOFU fingerprint
/// inside the orchestrator's coordinator transaction (Phase 2 D-02 binding).
/// </summary>
public sealed class TestConnectionService(
    Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probe,
    IDataProtectionProvider dpp,
    ILogger<TestConnectionService> log)
    : ITestConnectionService
{
    public async Task<TestConnectionOutcome> TestAsync(
        string hostname,
        string username,
        string plaintextPassword,
        int? existingNodeId,
        CancellationToken ct)
    {
        // RESEARCH §Pattern 3 / Assumption A2: NodePoller's probe-factory calls
        // dpp.CreateProtector("node-creds").Unprotect(node.Password). Plaintext would
        // throw CryptographicException → "decrypt-failed". Encrypt before calling.
        var protector = dpp.CreateProtector("node-creds");
        var encrypted = protector.Protect(plaintextPassword);

        var node = new WiFiNode
        {
            Id = existingNodeId ?? 0,
            Hostname = hostname,
            Username = username,
            Password = encrypted,
            HostKeyFingerprint = null    // intentional: no TOFU persistence (D-07)
        };

        try
        {
            var result = await probe(node, ct);
            return MapToOutcome(result);
        }
        catch (HostKeyMismatchException ex)
        {
            log.LogWarning("Test Connection: host-key-mismatch on {Host}", hostname);
            return new TestConnectionOutcome.HostKeyMismatch(
                ex.ExpectedFingerprint, ex.ActualFingerprint);
        }
    }

    // The reason-token strings on the right MUST match the EXACT vocabulary emitted by
    // NodePoller / PollOrchestrator. Verified at execute time:
    //   "within-cycle-reentry", "decrypt-failed", "algo-negotiate-failed", "auth-failed",
    //   "network-error", "timeout", "probe-failed", "host-key-mismatch"
    private static TestConnectionOutcome MapToOutcome(NodeProbeResult r) => r switch
    {
        NodeProbeResult.Success s
            => new TestConnectionOutcome.Success(s.Networks.Count, s.Stations.Count),

        NodeProbeResult.Failed { Reason: var reason }
            when ContainsAny(reason, "negotiate", "algo")
            => new TestConnectionOutcome.NegotiateFailed(),

        NodeProbeResult.Failed { Reason: var reason }
            when ContainsAny(reason, "auth")
            => new TestConnectionOutcome.AuthFailed(),

        NodeProbeResult.Failed { Reason: var reason }
            when ContainsAny(reason, "timeout", "timed out")
            => new TestConnectionOutcome.Timeout(),

        NodeProbeResult.Failed { Reason: var reason }
            when reason.Contains("decrypt", StringComparison.OrdinalIgnoreCase)
            => new TestConnectionOutcome.DecryptFailed(),

        NodeProbeResult.Failed { Reason: var reason }
            when reason.Contains("host-key", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("host key", StringComparison.OrdinalIgnoreCase)
            => new TestConnectionOutcome.HostKeyMismatch("(unknown)", "(unknown)"),

        NodeProbeResult.Failed { Reason: var reason }
            when reason.Contains("within-cycle-reentry", StringComparison.OrdinalIgnoreCase)
            => new TestConnectionOutcome.Busy(),

        NodeProbeResult.Failed { Reason: var reason }
            => new TestConnectionOutcome.Generic(reason),

        _ => new TestConnectionOutcome.Generic("unknown probe result")
    };

    private static bool ContainsAny(string s, params string[] tokens)
        => tokens.Any(t => s.Contains(t, StringComparison.OrdinalIgnoreCase));
}
