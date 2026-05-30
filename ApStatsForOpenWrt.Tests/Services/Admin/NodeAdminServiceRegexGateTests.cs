using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Admin;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Services.Admin;

/// <summary>
/// M-SEC-09 / plan 16-06 — pins the defense-in-depth regex-gate contract on
/// <see cref="NodeAdminService.DisconnectStationAsync"/>. A crafted MAC or iface bearing shell
/// metacharacters is rejected as <c>invalid-mac</c> / <c>invalid-iface</c> at the TOP of the
/// method, BEFORE any SSH session is created (the <c>sessionFactory</c> is never invoked) and
/// before the password is decrypted. The happy path (canonical-uppercase MAC + valid iface,
/// including dotted VLAN subinterfaces) passes the gate unchanged.
/// </summary>
[Trait("Category", "SecurityFix")]
public sealed class NodeAdminServiceRegexGateTests
{
    // Minimal fake: records every command issued (for the happy path) and replays a single canned
    // response across the 3-step cascade. Mirrors the NodeAdminServiceTests CannedSshSession shape.
    private sealed class FakeSshSession : ISshSession
    {
        private readonly Queue<(int ExitCode, string StdOut, string StdErr)> _responses;
        public List<string> Commands { get; } = new();

        public FakeSshSession((int ExitCode, string StdOut, string StdErr) canned)
        {
            _responses = new Queue<(int, string, string)>();
            for (var i = 0; i < 3; i++) _responses.Enqueue(canned);
        }

        public Task ConnectAsync(WiFiNode node, string plaintextPassword, string? expectedFingerprint,
            Action<string>? onHostKeyAccepted, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> RunCommandAsync(string command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult("");
        }

        public Task<(int ExitCode, string StdOut, string StdErr)> RunCommandWithExitCodeAsync(string command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            var resp = _responses.Count > 0 ? _responses.Dequeue() : (0, "OK", "");
            return Task.FromResult(resp);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Builds the service-under-test with a factory-invocation flag. On invalid input the gate
    // short-circuits before the factory is called, so `factoryInvoked` stays false — the
    // load-bearing "no-SSH-issued" assertion.
    private static (WiFiNode node, NodeAdminService svc, Func<bool> factoryInvoked) Setup(
        (int ExitCode, string StdOut, string StdErr) canned = default)
    {
        var invoked = new bool[1];
        var dpp = new EphemeralDataProtectionProvider();
        var protectedPwd = dpp.CreateProtector("node-creds").Protect("test-password-plain");
        Func<ISshSession> factory = () =>
        {
            invoked[0] = true;
            return new FakeSshSession(canned == default ? (0, "OK\n", "") : canned);
        };
        var svc = new NodeAdminService(factory, dpp, NullLogger<NodeAdminService>.Instance);
        var node = new WiFiNode
        {
            Id = 1,
            Hostname = "ap1.local",
            Username = "root",
            Password = protectedPwd,
            HostKeyFingerprint = null,
        };
        return (node, svc, () => invoked[0]);
    }

    [Fact]
    public async Task CraftedMacWithShellMetachar_ReturnsInvalidMac_NoSshIssued()
    {
        var (node, svc, factoryInvoked) = Setup();
        var r = await svc.DisconnectStationAsync(node, "AA:BB:CC; rm -rf /", "wlan0", CancellationToken.None);

        var ue = Assert.IsType<DisconnectResult.UnknownError>(r);
        Assert.Equal("invalid-mac", ue.Raw);
        Assert.False(factoryInvoked(), "no SSH session must be created on invalid MAC");
    }

    [Fact]
    public async Task CraftedIfaceWithShellMetachar_ReturnsInvalidIface_NoSshIssued()
    {
        var (node, svc, factoryInvoked) = Setup();
        var r = await svc.DisconnectStationAsync(node, "C8:2B:96:E5:24:0B", "wlan0; reboot", CancellationToken.None);

        var ue = Assert.IsType<DisconnectResult.UnknownError>(r);
        Assert.Equal("invalid-iface", ue.Raw);
        Assert.False(factoryInvoked(), "no SSH session must be created on invalid iface");
    }

    [Fact]
    public async Task BothInvalid_MacCheckedFirst_ReturnsInvalidMac()
    {
        var (node, svc, factoryInvoked) = Setup();
        var r = await svc.DisconnectStationAsync(node, "bad", "wlan0; reboot", CancellationToken.None);

        var ue = Assert.IsType<DisconnectResult.UnknownError>(r);
        Assert.Equal("invalid-mac", ue.Raw); // deterministic precedence: MAC checked first
        Assert.False(factoryInvoked());
    }

    [Fact]
    public async Task LowercaseMac_RejectedAsInvalidMac()
    {
        // Uppercase-only regex: a lowercase MAC reaching this path signals an upstream
        // canonicalization regression and is surfaced as invalid-mac (fail-closed).
        var (node, svc, factoryInvoked) = Setup();
        var r = await svc.DisconnectStationAsync(node, "c8:2b:96:e5:24:0b", "wlan0", CancellationToken.None);

        var ue = Assert.IsType<DisconnectResult.UnknownError>(r);
        Assert.Equal("invalid-mac", ue.Raw);
        Assert.False(factoryInvoked());
    }

    [Theory]
    [InlineData("")]
    [InlineData("AA:BB")]
    [InlineData("GG:HH:II:JJ:KK:LL")]            // non-hex
    [InlineData("AA:BB:CC:DD:EE:FF:00")]         // 7 octets
    [InlineData("AA-BB-CC-DD-EE-FF")]            // dash separator
    [InlineData("AA:BB:CC; rm -rf /")]           // shell metachar
    public async Task InvalidMacShapes_RejectedAsInvalidMac_NoSshIssued(string mac)
    {
        var (node, svc, factoryInvoked) = Setup();
        var r = await svc.DisconnectStationAsync(node, mac, "wlan0", CancellationToken.None);

        var ue = Assert.IsType<DisconnectResult.UnknownError>(r);
        Assert.Equal("invalid-mac", ue.Raw);
        Assert.False(factoryInvoked());
    }

    [Theory]
    [InlineData("wlan0; reboot")]
    [InlineData("wlan0 && cat /etc/passwd")]
    [InlineData("$(whoami)")]
    [InlineData("wlan0|nc")]
    [InlineData("")]
    [InlineData("wlan0`id`")]
    public async Task InvalidIfaceShapes_RejectedAsInvalidIface_NoSshIssued(string iface)
    {
        var (node, svc, factoryInvoked) = Setup();
        var r = await svc.DisconnectStationAsync(node, "C8:2B:96:E5:24:0B", iface, CancellationToken.None);

        var ue = Assert.IsType<DisconnectResult.UnknownError>(r);
        Assert.Equal("invalid-iface", ue.Raw);
        Assert.False(factoryInvoked());
    }

    [Fact]
    public async Task ValidIfaceWithDot_AcceptedPastTheGate()
    {
        // wlan0.1 VLAN subinterface — the dot is allowed by the iface regex, so the gate passes
        // and the SSH factory IS invoked. The result must not be either rejection classifier.
        var (node, svc, factoryInvoked) = Setup((0, "OK\n", ""));
        var r = await svc.DisconnectStationAsync(node, "C8:2B:96:E5:24:0B", "wlan0.1", CancellationToken.None);

        Assert.True(factoryInvoked(), "valid dotted iface must pass the gate and create a session");
        if (r is DisconnectResult.UnknownError ue)
        {
            Assert.NotEqual("invalid-mac", ue.Raw);
            Assert.NotEqual("invalid-iface", ue.Raw);
        }
    }

    [Fact]
    public async Task ValidCanonicalInputs_HappyPathProceeds()
    {
        var (node, svc, factoryInvoked) = Setup((0, "OK\n", ""));
        var r = await svc.DisconnectStationAsync(node, "C8:2B:96:E5:24:0B", "wlan0", CancellationToken.None);

        Assert.True(factoryInvoked());
        Assert.IsType<DisconnectResult.Success>(r); // gate is transparent on valid input
    }
}
