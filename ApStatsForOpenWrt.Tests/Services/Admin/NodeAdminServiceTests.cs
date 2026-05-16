using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Admin;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Services.Admin;

/// <summary>
/// Service-level tests for <see cref="NodeAdminService"/>. Covers the happy path (Success) plus
/// the four DISC-05 / D-12 failure cases (Timeout / NotSupported / NoSuchStation / UnknownError)
/// plus decrypt-failure and outer-cancellation. Uses an in-test <c>CannedSshSession</c> fake to
/// inject canned exit-code/stderr triples; no live SSH or DB. v0.11 Plan 14-04.
/// </summary>
public sealed class NodeAdminServiceTests
{
    // Test-only fake session that records the issued command + returns a canned response.
    // 260516-iwo — simplified to single-canned mode. The per-prefix mode shipped in 260516-iwf
    // for the cascade tests is removed along with those tests (D-08 re-amended to iw-only;
    // there's only ever one SSH command per disconnect call now).
    private sealed class CannedSshSession : ISshSession
    {
        private readonly (int ExitCode, string StdOut, string StdErr) _canned;
        private readonly bool _connectThrows;
        private readonly Exception? _connectException;
        public string? LastCommand { get; private set; }
        public int DisposeCount { get; private set; }

        public CannedSshSession(
            (int ExitCode, string StdOut, string StdErr) canned,
            bool connectThrows = false,
            Exception? connectException = null)
        {
            _canned = canned;
            _connectThrows = connectThrows;
            _connectException = connectException;
        }

        public Task ConnectAsync(WiFiNode node, string plaintextPassword, string? expectedFingerprint,
            Action<string>? onHostKeyAccepted, CancellationToken ct)
        {
            // Observing ct is canonical for ISshSession implementations (RealSshSession would throw
            // on cancelled token); we mirror that here so outer-cancellation tests are deterministic.
            ct.ThrowIfCancellationRequested();
            if (_connectThrows) throw _connectException ?? new TimeoutException("connect timeout");
            return Task.CompletedTask;
        }

        public Task<string> RunCommandAsync(string command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastCommand = command;
            return Task.FromResult(_canned.StdOut);
        }

        public Task<(int ExitCode, string StdOut, string StdErr)> RunCommandWithExitCodeAsync(string command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastCommand = command;
            return Task.FromResult(_canned);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    // The service decrypts node.Password via DataProtection's "node-creds" purpose. Bake a known
    // plaintext into the protected blob using the same provider so the service's Unprotect succeeds.
    private static (WiFiNode node, NodeAdminService svc, CannedSshSession ssh) Setup(
        (int ExitCode, string StdOut, string StdErr) canned,
        bool connectThrows = false,
        Exception? connectEx = null)
    {
        var dpp = new EphemeralDataProtectionProvider();
        var protectedPwd = dpp.CreateProtector("node-creds").Protect("test-password-plain");
        var ssh = new CannedSshSession(canned, connectThrows, connectEx);
        var svc = new NodeAdminService(() => ssh, dpp, NullLogger<NodeAdminService>.Instance);
        var node = new WiFiNode
        {
            Id = 1,
            Hostname = "ap1.local",
            Username = "root",
            Password = protectedPwd,
            HostKeyFingerprint = null,
        };
        return (node, svc, ssh);
    }

    [Fact]
    public async Task DisconnectStationAsync_ExitCodeZero_ReturnsSuccess()
    {
        var (node, svc, ssh) = Setup((0, "", ""));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);

        Assert.IsType<DisconnectResult.Success>(r);
        Assert.NotNull(ssh.LastCommand);
        // 260516-iwo — D-08 amended to iw-only. The single SSH command issued is exactly
        // `iw dev <iface> station del <mac>` with no JSON payload, no reason code, no
        // deauth flag, no ban_time. Cfg80211 kernel-drop achieves the operator's roam-
        // friendly intent (§Specifics #3) structurally rather than via an explicit payload.
        Assert.Equal("iw dev wlan0 station del AA:BB:CC:DD:EE:FF", ssh.LastCommand);
        // Pitfall 5 — DisposeAsync is invoked exactly once in the finally block.
        Assert.Equal(1, ssh.DisposeCount);
    }

    [Fact]
    public async Task DisconnectStationAsync_ConnectTimeoutsAfter5s_ReturnsTimeout()
    {
        // Connect throws OperationCanceledException without the OUTER ct firing -> internal 5s budget.
        var (node, svc, _) = Setup((0, "", ""), connectThrows: true, connectEx: new OperationCanceledException());
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.Timeout>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_NoSuchStation_ReturnsNoSuchStation()
    {
        // 260516-iwo — `iw dev <iface> station del <mac>` emits `command failed: No such
        // file or directory (-2)` (errno ENOENT) when the MAC isn't in the kernel's
        // per-iface station list. Benign race per D-12 case 3 ("Station is no longer
        // associated with this AP").
        var (node, svc, _) = Setup((234, "", "command failed: No such file or directory (-2)"));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.NoSuchStation>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_GenericNonZeroExit_NoKnownPattern_ReturnsUnknownError()
    {
        // Exit=5; stderr does NOT match any known marker -> falls through to UnknownError.
        var (node, svc, _) = Setup((5, "", "Internal error: malformed payload"));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        var ue = Assert.IsType<DisconnectResult.UnknownError>(r);
        Assert.Contains("Internal error", ue.Raw);
    }

    [Fact]
    public async Task DisconnectStationAsync_ConnectThrows_GenericException_ReturnsUnknownError()
    {
        var (node, svc, _) = Setup((0, "", ""), connectThrows: true, connectEx: new InvalidOperationException("unexpected"));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.UnknownError>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_DecryptFails_ReturnsUnknownError()
    {
        // Corrupted protected password -> service's Unprotect catches CryptographicException.
        var ssh = new CannedSshSession((0, "", ""));
        var dpp = new EphemeralDataProtectionProvider();
        var svc = new NodeAdminService(() => ssh, dpp, NullLogger<NodeAdminService>.Instance);
        var node = new WiFiNode
        {
            Id = 1,
            Hostname = "ap1",
            Username = "root",
            Password = "NOT-A-VALID-PROTECTED-BLOB",
            HostKeyFingerprint = null,
        };
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        var ue = Assert.IsType<DisconnectResult.UnknownError>(r);
        Assert.Equal("decrypt-failed", ue.Raw);
        // No SSH session is created when decrypt fails (early return).
        Assert.Equal(0, ssh.DisposeCount);
    }

    [Fact]
    public async Task DisconnectStationAsync_OuterCancellation_PropagatesOperationCanceled()
    {
        // External ct cancellation (NOT the internal 5s budget) propagates as OperationCanceledException.
        var (node, svc, _) = Setup((0, "", ""));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", cts.Token));
    }

    [Fact]
    public async Task DisconnectStationAsync_IwNoSuchDevice_ReturnsNotSupported()
    {
        // 260516-iwo — `iw dev <iface> station del <mac>` emits `command failed: No such device (-19)`
        // (errno ENODEV) when the netdev doesn't exist or isn't in AP mode. The operator-relevant
        // signal: the iface name stored in WiFiNetwork.IfaceName (Phase 9 / D-10) doesn't match the
        // kernel netdev on this AP. D-12 case 2 disposition: "Disconnect not supported on this AP
        // -- wireless interface unavailable" (banner text updated by this quick task to drop the
        // now-obsolete 'ubus/hostapd missing' suffix).
        var (node, svc, _) = Setup((252, "", "command failed: No such device (-19)"));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.NotSupported>(r);
    }

    // 260516-iwo — the three ubus→iw cascade tests (`UbusNotFound_FallsBackToIw_*`,
    // `UbusNotFound_FallbackIwAlsoFails_*`, `UbusSuccess_DoesNotInvokeFallback`) and the
    // four ubus-pattern classifier tests (`UbusObjectNotFound_*`, `UbusNotFound_*`,
    // `UbusMethodNotFound_*`, `CommandNotFound_*`) along with their `SetupCascade` helper
    // are removed in this commit. The cascade no longer exists; ubus is no longer invoked.
    // The single new positive test for the iw-only path is `DisconnectStationAsync_IwNoSuchDevice_ReturnsNotSupported`
    // (added in commit 1a9c5e0 / quick task 260516-iwo Task 1) plus the updated happy-path
    // and NoSuchStation tests above.
}
