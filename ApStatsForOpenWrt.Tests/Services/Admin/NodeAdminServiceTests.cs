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
    // Test-only fake session that records every issued command + returns a canned response.
    // Two modes:
    //   (1) Single-canned (legacy): every command returns the same (ExitCode, StdOut, StdErr) tuple.
    //       Preserved verbatim for the 9 pre-existing tests (Plan 14-04 Task 2).
    //   (2) Per-prefix (260516-iwf): map command-prefix -> canned response, with an optional default.
    //       Used by the cascade tests (ubus call hostapd. -> response A; iw dev -> response B).
    private sealed class CannedSshSession : ISshSession
    {
        private readonly (int ExitCode, string StdOut, string StdErr) _canned;
        private readonly IReadOnlyList<(string Prefix, (int ExitCode, string StdOut, string StdErr) Response)>? _prefixMap;
        private readonly bool _connectThrows;
        private readonly Exception? _connectException;
        public string? LastCommand { get; private set; }
        public List<string> Commands { get; } = new();    // 260516-iwf: every command, in order of invocation.
        public int DisposeCount { get; private set; }

        public CannedSshSession(
            (int ExitCode, string StdOut, string StdErr) canned,
            bool connectThrows = false,
            Exception? connectException = null)
        {
            _canned = canned;
            _prefixMap = null;
            _connectThrows = connectThrows;
            _connectException = connectException;
        }

        // 260516-iwf: per-prefix mode. The first matching prefix wins (longest-prefix match isn't
        // needed here — the cascade only ever issues two distinct command shapes per call).
        // `defaultResponse` fires for unmatched commands; if omitted, an unmatched command throws
        // InvalidOperationException so the test surfaces unexpected command shapes immediately.
        public CannedSshSession(
            IReadOnlyList<(string Prefix, (int ExitCode, string StdOut, string StdErr) Response)> prefixMap,
            (int ExitCode, string StdOut, string StdErr)? defaultResponse = null)
        {
            _prefixMap = prefixMap;
            _canned = defaultResponse ?? (-1, "", "TEST: unmatched command (no default response configured)");
            _connectThrows = false;
            _connectException = null;
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
            Commands.Add(command);
            return Task.FromResult(ResolveResponse(command).StdOut);
        }

        public Task<(int ExitCode, string StdOut, string StdErr)> RunCommandWithExitCodeAsync(string command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastCommand = command;
            Commands.Add(command);
            return Task.FromResult(ResolveResponse(command));
        }

        private (int ExitCode, string StdOut, string StdErr) ResolveResponse(string command)
        {
            if (_prefixMap is null) return _canned;
            foreach (var (prefix, response) in _prefixMap)
            {
                if (command.StartsWith(prefix, StringComparison.Ordinal)) return response;
            }
            return _canned;     // default / "unmatched" sentinel
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
        // D-08 payload shape -- JsonSerializer eliminates injection on the MAC arg.
        Assert.Contains("ubus call hostapd.wlan0 del_client", ssh.LastCommand);
        Assert.Contains("\"addr\":\"AA:BB:CC:DD:EE:FF\"", ssh.LastCommand);
        Assert.Contains("\"reason\":5", ssh.LastCommand);
        Assert.Contains("\"deauth\":true", ssh.LastCommand);
        Assert.Contains("\"ban_time\":0", ssh.LastCommand);
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
    public async Task DisconnectStationAsync_UbusObjectNotFound_ReturnsNotSupported()
    {
        // ubus call against an unknown object emits a non-zero exit with stderr "Object not found".
        var (node, svc, _) = Setup((4, "", "Command failed: Object not found"));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.NotSupported>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_NoSuchStation_ReturnsNoSuchStation()
    {
        var (node, svc, _) = Setup((1, "", "ubus: No such station"));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.NoSuchStation>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_UbusNotFound_ReturnsNotSupported()
    {
        // 260516-ncf — ubus emits the bare UBUS_STATUS_NOT_FOUND textual form
        // ("Command failed: Not found", capital N, no "Object" prefix) when the
        // hostapd.<iface> object doesn't exist on the AP. This is what surfaced
        // verbatim in the v0.11 QUAL-02 LIVE-UAT walk on 2026-05-16:
        //   [11:57:57 WRN] Disconnect failed (unknown): NodeId=1 Iface=wlan0
        //   StationMac=C8:2B:96:E5:24:0B Raw=Command failed: Not found
        // The classifier must map this to NotSupported (D-12 case 2 -- "Disconnect
        // not supported on this AP -- ubus/hostapd missing"), not the catch-all
        // UnknownError ("Disconnect failed -- check logs"). Pre-fix code lacked
        // the "Command failed: Not found" substring so it fell through.
        var (node, svc, _) = Setup((4, "", "Command failed: Not found"));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.NotSupported>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_UbusMethodNotFound_ReturnsNotSupported()
    {
        // 260516-ncf — ubus emits UBUS_STATUS_METHOD_NOT_FOUND ("Command failed:
        // Method not found") when the hostapd.<iface> object IS registered but
        // the del_client method is missing (older hostapd builds; non-AP modes).
        // Same operator intent as UBUS_STATUS_NOT_FOUND -- the disconnect path
        // isn't reachable -- so D-12 case 2 is the correct disposition.
        var (node, svc, _) = Setup((4, "", "Command failed: Method not found"));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.NotSupported>(r);
    }

    [Fact]
    public async Task DisconnectStationAsync_CommandNotFound_ReturnsNotSupported()
    {
        // /bin/sh emits "ubus: command not found" on systems without ubus installed. D-12 case 2.
        var (node, svc, _) = Setup((127, "", "/bin/sh: ubus: command not found"));
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);
        Assert.IsType<DisconnectResult.NotSupported>(r);
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

    // ── 260516-iwf — ubus -> iw fallback cascade (CONTEXT 14 §D-08 amended 2026-05-16) ──
    //
    // Operator's v0.11 / Phase 14 QUAL-02 LIVE-UAT surface (d) walk demonstrated that
    // `ubus call hostapd.<iface> del_client {...}` returns "Command failed: Not found" on AP
    // classes where the hostapd ubus object isn't registered under exactly `hostapd.<iface>`.
    // Bug A (260516-ncf) fixed the classification; Bug B (this task) makes the disconnect
    // actually succeed by falling back to `iw dev <iface> station del <mac>` (universal
    // cfg80211 path, present on every supported OpenWRT release; Phase 9 already uses
    // `iw dev <iface> station dump` on the same iface for station enumeration).

    // Helper: build a per-prefix CannedSshSession-backed service. ubus_prefix matches the
    // primary D-08 path; iw_prefix matches the new 260516-iwf fallback.
    private static (WiFiNode node, NodeAdminService svc, CannedSshSession ssh) SetupCascade(
        (int ExitCode, string StdOut, string StdErr) ubusResponse,
        (int ExitCode, string StdOut, string StdErr) iwResponse)
    {
        var dpp = new EphemeralDataProtectionProvider();
        var protectedPwd = dpp.CreateProtector("node-creds").Protect("test-password-plain");
        var prefixMap = new (string, (int, string, string))[]
        {
            ("ubus call hostapd.", ubusResponse),
            ("iw dev",             iwResponse),
        };
        var ssh = new CannedSshSession(prefixMap);
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
    public async Task DisconnectStationAsync_UbusNotFound_FallsBackToIw_ReturnsSuccess()
    {
        // 260516-iwf — verbatim operator-side scenario (2026-05-16T11:57:57Z LIVE-UAT log):
        // ubus reports `Command failed: Not found` (UBUS_STATUS_NOT_FOUND) because the
        // hostapd.<iface> object isn't registered on this AP class. The cascade falls back
        // to `iw dev <iface> station del <mac>` which kicks the station at the cfg80211 layer.
        // iw exits 0 on success -> overall Success.
        var (node, svc, ssh) = SetupCascade(
            ubusResponse: (4, "", "Command failed: Not found"),
            iwResponse:   (0, "", ""));

        var r = await svc.DisconnectStationAsync(node, "C8:2B:96:E5:24:0B", "wlan0", CancellationToken.None);

        Assert.IsType<DisconnectResult.Success>(r);

        // Both commands must have been issued, in the order ubus -> iw.
        Assert.Equal(2, ssh.Commands.Count);
        Assert.StartsWith("ubus call hostapd.wlan0 del_client", ssh.Commands[0]);
        Assert.Equal("iw dev wlan0 station del C8:2B:96:E5:24:0B", ssh.Commands[1]);
        // Pitfall 5 — dispose still exactly once.
        Assert.Equal(1, ssh.DisposeCount);
    }

    [Fact]
    public async Task DisconnectStationAsync_UbusNotFound_FallbackIwAlsoFails_ReturnsNotSupported()
    {
        // 260516-iwf — both paths fail. iw emits its standard failure shape when the netdev
        // is missing (which would also be the case if the iface name itself is wrong in the DB
        // -- the deeper Phase-9 / D-10 path). The cascade carries BOTH stderrs in the raw so
        // the operator's Serilog audit captures both failure modes in one log line.
        var (node, svc, ssh) = SetupCascade(
            ubusResponse: (4, "", "Command failed: Not found"),
            iwResponse:   (252, "", "command failed: No such device (-19)"));

        var r = await svc.DisconnectStationAsync(node, "C8:2B:96:E5:24:0B", "wlan0", CancellationToken.None);

        var ns = Assert.IsType<DisconnectResult.NotSupported>(r);
        // Combined raw includes both branches with a clear separator.
        Assert.Contains("ubus:", ns.Raw);
        Assert.Contains("Command failed: Not found", ns.Raw);
        Assert.Contains("iw:", ns.Raw);
        Assert.Contains("No such device", ns.Raw);
        // Both commands were issued.
        Assert.Equal(2, ssh.Commands.Count);
    }

    [Fact]
    public async Task DisconnectStationAsync_UbusSuccess_DoesNotInvokeFallback()
    {
        // 260516-iwf forward guard: the fallback fires ONLY when the ubus path is classified
        // as NotSupported. A successful primary path must NOT invoke the iw command -- this
        // protects against a future regression where the cascade fires unconditionally.
        // (The iwResponse is canned to a failure on purpose: if the fallback DID fire, the
        // overall result would flip to NotSupported, and this test would fail loudly.)
        var (node, svc, ssh) = SetupCascade(
            ubusResponse: (0, "", ""),
            iwResponse:   (252, "", "command failed: No such device (-19)"));

        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC:DD:EE:FF", "wlan0", CancellationToken.None);

        Assert.IsType<DisconnectResult.Success>(r);
        // Exactly ONE command was issued, and it was the ubus call.
        Assert.Single(ssh.Commands);
        Assert.StartsWith("ubus call hostapd.wlan0 del_client", ssh.Commands[0]);
    }
}
