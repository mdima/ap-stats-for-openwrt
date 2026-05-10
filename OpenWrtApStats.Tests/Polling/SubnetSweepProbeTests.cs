using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Services.Polling;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

/// <summary>
/// Phase 9 ENRICH-01..02 + ENRICH-04 + ENRICH-06 — fixture-replay integration arms for
/// <see cref="SubnetSweepProbe"/>. Uses the Plan 09-03 FakeSshSession extension
/// (CommandsIssued + CommandOverrides) to map the AP-side batched-loop SSH command
/// (which slugifies to an unpredictable filename) onto a static <c>arping.txt</c> /
/// <c>ping.txt</c> fixture per OpenWRT version slot.
/// </summary>
public sealed class SubnetSweepProbeTests
{
    public static TheoryData<string> AllVersionFolders() => new()
    {
        Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT"),
        Path.Combine("Fixtures", "ssh-output", "openwrt-22.03.7"),
        Path.Combine("Fixtures", "ssh-output", "openwrt-21.02.7"),
    };

    private static WiFiNode MakeNode(string? ranges = null, bool enabled = true)
        => new WiFiNode
        {
            Id = 1,
            Hostname = "test-ap",
            Username = "operator",
            Password = string.Empty,
            SubnetScanRanges = ranges,
            SubnetScanEnabled = enabled,
        };

    [Fact]
    public async Task PollAsync_SubnetScanEnabledFalse_ReturnsEmptySuccess_NoSshIssued()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakeDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var session = new FakeSshSession(fakeDir);
        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);

        var node = MakeNode(enabled: false);
        var result = await probe.PollAsync(node, peerIp: "192.168.1.42", ct);

        var success = Assert.IsType<SubnetSweepProbeResult.Success>(result);
        Assert.Empty(success.MacToIp);
        Assert.Empty(session.CommandsIssued);     // No SSH commands issued.
    }

    [Fact]
    public async Task PollAsync_InvalidPeerIp_ReturnsFailedAndIssuesNoSshCommands()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakeDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var session = new FakeSshSession(fakeDir);
        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);

        var node = MakeNode(ranges: null);
        var result = await probe.PollAsync(node, peerIp: "not-an-ip; rm -rf /", ct);

        var failed = Assert.IsType<SubnetSweepProbeResult.Failed>(result);
        Assert.Equal("invalid-peer-ip", failed.Reason);
        Assert.Empty(session.CommandsIssued);
    }

    [Theory]
    [MemberData(nameof(AllVersionFolders))]
    public async Task PollAsync_SubnetScanRangesSet_UsesCidrListWithoutAutoDerivation(string fixtureDir)
    {
        var arpingPath = Path.Combine(fixtureDir, "arping.txt");
        Assert.SkipUnless(File.Exists(arpingPath), $"Fixture not yet captured: {arpingPath}");

        var ct = TestContext.Current.CancellationToken;
        var session = new FakeSshSession(fixtureDir);
        // Map the AP-side loop command to arping.txt regardless of slug shape.
        session.CommandOverrides["arping -c1 -w1"] = "arping";
        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);

        var node = MakeNode(ranges: "192.168.1.0/30");
        var result = await probe.PollAsync(node, peerIp: "192.168.1.42", ct);

        Assert.IsType<SubnetSweepProbeResult.Success>(result);
        // Auto-derivation must NOT have run — no `ip route get`/`ip -4 addr show` issued.
        Assert.DoesNotContain(session.CommandsIssued, c => c.StartsWith("ip route get"));
        Assert.DoesNotContain(session.CommandsIssued, c => c.StartsWith("ip -4 addr show"));
        // Capability probe + sweep loop both issued.
        Assert.Contains(session.CommandsIssued, c => c == "command -v arping");
        Assert.Contains(session.CommandsIssued, c => c.Contains("arping -c1 -w1"));
    }

    [Theory]
    [MemberData(nameof(AllVersionFolders))]
    public async Task PollAsync_AutoDerivedSubnet_ParsesArpingOutputToUppercaseMacMap(string fixtureDir)
    {
        var arpingPath = Path.Combine(fixtureDir, "arping.txt");
        Assert.SkipUnless(File.Exists(arpingPath), $"Fixture not yet captured: {arpingPath}");

        var ct = TestContext.Current.CancellationToken;
        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["ip route get"] = "ip-route-get-derive";
        session.CommandOverrides["ip -4 addr show"] = "ip-4-addr-show-derive";
        session.CommandOverrides["arping -c1 -w1"] = "arping";

        // Provision route + addr fixtures inline (operator capture comes later) — written
        // alongside the synthetic/captured arping.txt so the auto-derivation branch resolves.
        File.WriteAllText(Path.Combine(fixtureDir, "ip-route-get-derive.txt"),
            "192.168.1.1 dev br-lan src 192.168.1.42 uid 0 \n    cache");
        File.WriteAllText(Path.Combine(fixtureDir, "ip-4-addr-show-derive.txt"),
            "3: br-lan: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 \n" +
            "    inet 192.168.1.42/24 brd 192.168.1.255 scope global br-lan");

        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);
        var node = MakeNode(ranges: null);
        var result = await probe.PollAsync(node, peerIp: "192.168.1.1", ct);

        var success = Assert.IsType<SubnetSweepProbeResult.Success>(result);
        Assert.All(success.MacToIp.Keys, mac => Assert.Equal(mac.ToUpperInvariant(), mac));
    }

    [Fact]
    public async Task PollAsync_AutoDerivationFailsNoIface_ReturnsFailedWithReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["ip route get"] = "garbage-route";
        // Note: the test content must NOT contain the substring "dev " followed by a word, since
        // SubnetDeriver.IpRouteDevRegex would otherwise match (capturing the next token as the
        // iface name). Use a payload that has no "dev <word>" pattern at all.
        File.WriteAllText(Path.Combine(fixtureDir, "garbage-route.txt"), "garbage output without route metadata");

        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);
        var node = MakeNode(ranges: null);
        var result = await probe.PollAsync(node, peerIp: "192.168.1.1", ct);

        var failed = Assert.IsType<SubnetSweepProbeResult.Failed>(result);
        Assert.Equal("subnet-derivation-failed", failed.Reason);
    }

    [Fact]
    public async Task PollAsync_ArpingAbsent_FallsBackToPing_DoesNotIssueArpingLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        // Override command-v-arping to return a non-zero exit code via a per-test stub.
        File.WriteAllText(Path.Combine(fixtureDir, "missing-arping.txt"), "");
        File.WriteAllText(Path.Combine(fixtureDir, "missing-arping.txt.exit"), "1");
        // Provide a ping fixture so the fallback loop has something to read.
        var pingFixture = Path.Combine(fixtureDir, "ping.txt");
        if (!File.Exists(pingFixture))
        {
            File.WriteAllText(pingFixture,
                "PING 192.168.1.42 (192.168.1.42): 56 data bytes\n" +
                "64 bytes from 192.168.1.42: seq=0 ttl=64 time=1.234 ms\n");
        }

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["command -v arping"] = "missing-arping";
        session.CommandOverrides["ping -c1 -W1"] = "ping";

        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);
        var node = MakeNode(ranges: "192.168.1.0/30");
        var result = await probe.PollAsync(node, peerIp: "192.168.1.1", ct);

        Assert.IsType<SubnetSweepProbeResult.Success>(result);
        // Arping loop must NOT have been issued.
        Assert.DoesNotContain(session.CommandsIssued, c => c.Contains("arping -c1 -w1"));
        // Ping loop must have been issued.
        Assert.Contains(session.CommandsIssued, c => c.Contains("ping -c1 -W1"));
    }
}
