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
///
/// 260517-hcd — D-08 re-amended (fourth amendment in the v0.11 chain) to a belt-and-suspenders
/// triple cascade per disconnect call: step 1 ubus best-effort, step 2 iw best-effort, step 3
/// hostapd_cli AUTHORITATIVE. The test fake's <c>RunCommandWithExitCodeAsync</c> dequeues a canned
/// response per call (Queue&lt;…&gt;-backed) so tests can pin per-step behaviour. The single-
/// canned-tuple constructor preserves backward compatibility by replaying the same response three
/// times — existing tests that don't care about per-step differentiation continue to compile.
/// </summary>
public sealed class NodeAdminServiceTests
{
    // Test-only fake session. 260517-hcd Queue redesign: a list of in-order canned responses
    // is dequeued one per RunCommandWithExitCodeAsync call. The Commands list captures every
    // issued command in order so tests can assert the 3-step (ubus → iw → hostapd_cli) shape.
    private sealed class CannedSshSession : ISshSession
    {
        private readonly Queue<(int ExitCode, string StdOut, string StdErr)> _responses;
        private readonly bool _connectThrows;
        private readonly Exception? _connectException;
        public List<string> Commands { get; } = new();
        public int DisposeCount { get; private set; }

        // Derived convenience: the most-recently-issued command (preserves the legacy LastCommand
        // surface from pre-260517-hcd test sites).
        public string? LastCommand => Commands.Count > 0 ? Commands[^1] : null;

        public CannedSshSession(
            (int ExitCode, string StdOut, string StdErr) canned,
            bool connectThrows = false,
            Exception? connectException = null)
        {
            // 260517-hcd — the disconnect path now issues 3 commands per call. Single-canned mode
            // replays the SAME response across all 3 steps so existing test sites that don't care
            // about per-step differentiation compile without changes.
            _responses = new Queue<(int, string, string)>();
            for (var i = 0; i < 3; i++) _responses.Enqueue(canned);
            _connectThrows = connectThrows;
            _connectException = connectException;
        }

        public CannedSshSession(
            IEnumerable<(int ExitCode, string StdOut, string StdErr)> queued,
            bool connectThrows = false,
            Exception? connectException = null)
        {
            _responses = new Queue<(int, string, string)>(queued);
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
            Commands.Add(command);
            (int ExitCode, string StdOut, string StdErr) resp = _responses.Count > 0
                ? _responses.Dequeue()
                : (0, "", "");
            return Task.FromResult(resp.StdOut);
        }

        public Task<(int ExitCode, string StdOut, string StdErr)> RunCommandWithExitCodeAsync(string command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            // Defensive: if a test queues fewer than 3 responses, return a benign exit-0 default
            // for the overflow calls so the test author sees the failure on the actual assertion
            // (Commands.Count or DisconnectResult shape) rather than a NullReferenceException.
            (int ExitCode, string StdOut, string StdErr) resp = _responses.Count > 0
                ? _responses.Dequeue()
                : (0, "OK", "");
            return Task.FromResult(resp);
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

    // 260517-hcd — per-step queued setup for tests that need to differentiate step 1 / step 2 /
    // step 3 canned responses. Enumerable order matches issued command order: ubus, iw, hostapd_cli.
    private static (WiFiNode node, NodeAdminService svc, CannedSshSession ssh) SetupQueued(
        IEnumerable<(int ExitCode, string StdOut, string StdErr)> queued)
    {
        var dpp = new EphemeralDataProtectionProvider();
        var protectedPwd = dpp.CreateProtector("node-creds").Protect("test-password-plain");
        var ssh = new CannedSshSession(queued);
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
        var (node, svc, ssh) = Setup((0, "OK\n", ""));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);

        Assert.IsType<DisconnectResult.Success>(r);
        // 260517-hcd — D-08 fourth amendment: belt-and-suspenders triple cascade. Three SSH
        // commands are issued per disconnect call in fixed order (ubus → iw → hostapd_cli).
        Assert.Equal(3, ssh.Commands.Count);
        Assert.StartsWith("ubus call hostapd.wlan0 del_client", ssh.Commands[0]);
        Assert.Equal("iw dev wlan0 station del AA:BB:CC:DD:EE:FF", ssh.Commands[1]);
        Assert.Contains("hostapd_cli -p", ssh.Commands[2]);
        Assert.Contains("for d in /var/run/hostapd-", ssh.Commands[2]);
        Assert.Contains("deauthenticate AA:BB:CC:DD:EE:FF", ssh.Commands[2]);
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
    public async Task DisconnectStationAsync_HostapdCliReturnsFAIL_ReturnsNoSuchStation()
    {
        // 260517-hcd — hostapd_cli's authoritative classifier: exit 0 + stdout starting with
        // "FAIL" means the station MAC is not in hostapd's STA list. Benign race per D-12 case 3
        // ("Station is no longer associated with this AP" — informational tone). Replaces the
        // 260516-iwo `NoSuchStation_ReturnsNoSuchStation` test which keyed on iw's ENOENT stderr
        // shape (that semantic no longer applies — hostapd_cli is the authoritative source).
        var (node, svc, _) = SetupQueued(new (int, string, string)[]
        {
            (0, "", ""),      // step 1 ubus: best-effort exit 0 (no-op logged)
            (0, "", ""),      // step 2 iw: best-effort exit 0 (no-op logged)
            (0, "FAIL\n", "") // step 3 hostapd_cli AUTHORITATIVE: FAIL → NoSuchStation
        });
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.NoSuchStation>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_GenericNonZeroExit_NoKnownPattern_ReturnsUnknownError()
    {
        // Single-canned replays (5, "", "Internal error") 3x — including for the step-3 hostapd_cli
        // call which is the authoritative classifier. Non-zero + unrecognised stderr → UnknownError.
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
    public async Task DisconnectStationAsync_HostapdCliExit127NoSocketForIface_ReturnsNotSupported()
    {
        // 260517-hcd — the shell-glob socket-discovery loop emits `exit 127` with stderr
        // `no hostapd socket for <iface>` when neither `/var/run/hostapd-*/<iface>` nor
        // `/var/run/hostapd/<iface>` is a socket file. ClassifyHostapdCli maps this sentinel
        // to NotSupported (D-12 case 2). Replaces the 260516-iwo `IwNoSuchDevice_*` test which
        // keyed on iw's ENODEV stderr — that semantic doesn't map to hostapd_cli since hostapd_cli
        // is never invoked when no socket exists.
        var (node, svc, _) = SetupQueued(new (int, string, string)[]
        {
            (0, "", ""),                                // step 1 ubus
            (0, "", ""),                                // step 2 iw
            (127, "", "no hostapd socket for wlan0\n")  // step 3 hostapd_cli AUTHORITATIVE
        });
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.NotSupported>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_HostapdCliReturnsOK_ReturnsSuccess()
    {
        // 260517-hcd — explicit positive test against the verified-working `OK\n` shape that
        // `hostapd_cli deauthenticate` emits when hostapd accepts the deauth request. Steps 1
        // and 2 fail (ubus has no hostapd object on this AP class; iw silently exits 0); the
        // authoritative step 3 returns Success.
        var (node, svc, _) = SetupQueued(new (int, string, string)[]
        {
            (127, "", "ubus: command not found"),  // step 1 ubus best-effort (failure logged)
            (0,   "", ""),                          // step 2 iw best-effort (silent no-op)
            (0,   "OK\n", "")                       // step 3 hostapd_cli AUTHORITATIVE: OK → Success
        });
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.Success>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_HostapdCliFailedToConnect_ReturnsNotSupported()
    {
        // 260517-hcd — covers the case where the shell-glob DOES find a socket file under
        // /var/run/hostapd-* but hostapd isn't actually listening on the other end. hostapd_cli
        // emits its own diagnostic on stderr: `Failed to connect to hostapd - wpa_ctrl_open: …`
        // and exits non-zero. Map to NotSupported (D-12 case 2): the AP nominally has the
        // ctrl-socket infrastructure but the daemon is unreachable, which from the operator's
        // perspective is the same outcome as "no hostapd here to deauth via."
        var (node, svc, _) = SetupQueued(new (int, string, string)[]
        {
            (127, "", "ubus: command not found"),
            (0,   "", ""),
            (1,   "", "Failed to connect to hostapd - wpa_ctrl_open: No such file or directory")
        });
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.NotSupported>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_AllThreeCommandsAlwaysIssued_InOrder_EvenWhenUbusExitsZero()
    {
        // 260517-hcd — forward guard: even when step 1 ubus exits 0 (i.e., would have been
        // a stop-at-first-success path under the rejected Reading A semantics), steps 2 and 3
        // STILL run. This pins the operator's belt-and-suspenders intent ("fails or not fails"
        // — all three commands must fire). The single-canned setup replays (0,"","") across
        // all three steps; we then assert the issued command count + per-step prefixes.
        var (node, svc, ssh) = Setup((0, "", ""));
        await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);

        Assert.Equal(3, ssh.Commands.Count);
        Assert.StartsWith("ubus call hostapd.wlan0 del_client", ssh.Commands[0]);
        Assert.Contains("\"addr\":\"AA:BB:CC:DD:EE:FF\"", ssh.Commands[0]);
        Assert.Contains("\"reason\":5", ssh.Commands[0]);
        Assert.Contains("\"deauth\":true", ssh.Commands[0]);
        Assert.Equal("iw dev wlan0 station del AA:BB:CC:DD:EE:FF", ssh.Commands[1]);
        Assert.Contains("hostapd_cli -p", ssh.Commands[2]);
        Assert.Contains("for d in /var/run/hostapd-", ssh.Commands[2]);
        Assert.Contains("/var/run/hostapd", ssh.Commands[2]);
        Assert.Contains("deauthenticate AA:BB:CC:DD:EE:FF", ssh.Commands[2]);
        Assert.Contains("no hostapd socket for wlan0", ssh.Commands[2]);
    }

    [Fact]
    public async Task DisconnectStationAsync_HostapdCliAuthoritative_UbusAndIwFailuresDoNotAffectVerdict()
    {
        // 260517-hcd — pin the "step 3 is the source of truth" contract: when ubus AND iw
        // both exit non-zero (typical operator's fleet case before this quick task) but
        // hostapd_cli succeeds with `OK\n`, the user-facing DisconnectResult is Success.
        // Without this guard, a future refactor could re-introduce a "stop-on-first-success"
        // or "best-of-N" cascade decision that would mis-classify these cases.
        var (node, svc, ssh) = SetupQueued(new (int, string, string)[]
        {
            (127, "", "Command failed: Not found"),   // step 1 ubus — operator's NodeId=1 shape
            (254, "", "command failed: No such device (-19)"),  // step 2 iw — ENODEV
            (0,   "OK\n", "")                          // step 3 hostapd_cli — the verified-working path
        });
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.Success>(r);
        Assert.Equal(3, ssh.Commands.Count);
    }
}
