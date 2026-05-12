using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Polling;

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
    public async Task PollAsync_AutoDerivation_IssuesSshClientShapedRouteCommand_AndDerivesLanSubnetNotLoopback()
    {
        // Regression for debug session p10-trigger-and-nslookup fourth-order fix.
        //
        // Pre-fix code issued `ip route get {peerIp}` where peerIp is the SSH destination
        // (the AP's own LAN IP after the factory's Dns.GetHostAddressesAsync resolution).
        // On Linux/BusyBox, `ip route get <self-IP>` returns the loopback route
        // (`local <ip> dev lo`), causing iface='lo' → `ip -4 addr show dev lo` →
        // `127.0.0.1/8` → MaskToSlash24 → `127.0.0.0/24`, and arping swept loopback finding
        // zero mappings every cycle.
        //
        // Post-fix: the issued command is `ip route get "${SSH_CLIENT%% *}"`. BusyBox
        // parameter expansion resolves to the orchestrator's source IP (the SSH client),
        // whose route necessarily traverses the LAN-facing interface — the correct subnet
        // for the arping sweep.
        //
        // Genuine RED→GREEN flip: pre-fix, no issued command contains the literal substring
        // `${SSH_CLIENT%% *}`. Post-fix, the route-get command does. Additionally, the
        // derived /24 must be the LAN subnet (192.168.22.0/24) not loopback (127.0.0.0/24).
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["ip route get"] = "ip-route-get-ssh-client";
        session.CommandOverrides["ip -4 addr show"] = "ip-4-addr-show-ssh-client";
        session.CommandOverrides["arping -c1 -w1"] = "arping-ssh-client";

        // Route lookup for the SSH client IP (the orchestrator host on 192.168.22.50)
        // traverses br-lan. This matches what a real BusyBox kernel would return when
        // queried with the LAN-side client IP — distinct from the loopback shape that
        // would be returned for the AP's own self-IP.
        File.WriteAllText(Path.Combine(fixtureDir, "ip-route-get-ssh-client.txt"),
            "192.168.22.50 via 192.168.22.50 dev br-lan src 192.168.22.2 uid 0 \n    cache");
        File.WriteAllText(Path.Combine(fixtureDir, "ip-4-addr-show-ssh-client.txt"),
            "3: br-lan: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 \n" +
            "    inet 192.168.22.2/24 brd 192.168.22.255 scope global br-lan");
        File.WriteAllText(Path.Combine(fixtureDir, "arping-ssh-client.txt"),
            "ARPING 192.168.22.39 from 192.168.22.2 br-lan\n" +
            "Unicast reply from 192.168.22.39 [AA:BB:CC:DD:EE:39] 1.234ms\n");

        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);
        // peerIp here mirrors the production wiring: the orchestrator passes the AP's
        // resolved IP (the SSH destination). The T-09-03-05 defense-in-depth check at
        // PollAsync entry still validates that peerIp is a parseable IPv4, but the value
        // is no longer interpolated into the shell command construction.
        var node = MakeNode(ranges: null);
        var result = await probe.PollAsync(node, peerIp: "192.168.22.2", ct);

        Assert.IsType<SubnetSweepProbeResult.Success>(result);

        // Decisive RED→GREEN assertion 1: the issued route-get command must reference
        // SSH_CLIENT via BusyBox parameter expansion, NOT the literal peerIp.
        Assert.Contains(session.CommandsIssued,
            c => c.Contains("${SSH_CLIENT%% *}", StringComparison.Ordinal));
        Assert.DoesNotContain(session.CommandsIssued,
            c => c == "ip route get 192.168.22.2");

        // Decisive RED→GREEN assertion 2: the derived /24 (visible via the arping loop's
        // IP list, since the auto-derivation feeds SubnetDeriver.ExpandCidr) must be the
        // LAN subnet, NOT loopback.
        Assert.Contains(session.CommandsIssued,
            c => c.Contains("arping -c1 -w1") && c.Contains("192.168.22."));
        Assert.DoesNotContain(session.CommandsIssued,
            c => c.Contains("arping -c1 -w1") && c.Contains("127.0.0."));
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

    /// <summary>
    /// Quick task 260511-lct (2026-05-11): with the ChunkSize bump from 16 → 64, a sweep over
    /// ~200 hosts must produce exactly 4 chunks (ceil(252/64) = 4 for two /25s = 252 hosts),
    /// where each chunk contains at most 64 IPs and the chunk boundaries are `; ` between
    /// `for ip in ... done; wait` segments. Pre-bump this fact would FAIL (16 chunks would
    /// be produced instead of 4 — RED→GREEN signal). Indirect via PollAsync to avoid
    /// touching private-member visibility (internal-via-InternalsVisibleTo would also work
    /// but is unnecessary; the SSH-issued-command capture already gives us the assertable
    /// shape).
    /// </summary>
    [Fact]
    public async Task BuildSweepCommand_For200Ips_Produces4ChunksOfAtMost64Ips()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var session = new FakeSshSession(fixtureDir);
        // Route the batched arping loop to the existing arping.txt fixture regardless of
        // the long, IP-list-dependent command body.
        session.CommandOverrides["arping -c1 -w1"] = "arping";

        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);
        // Two /25 CIDRs expand to 126 + 126 = 252 host IPs → ceil(252/64) = 4 chunks.
        var node = MakeNode(ranges: "192.168.0.0/25,192.168.1.0/25");
        var result = await probe.PollAsync(node, peerIp: "192.168.0.1", ct);

        Assert.IsType<SubnetSweepProbeResult.Success>(result);

        // Find the single arping batched-loop command.
        var arpingLoop = session.CommandsIssued.SingleOrDefault(c => c.Contains("arping -c1 -w1"));
        Assert.NotNull(arpingLoop);

        // 4 chunks → 4 occurrences each of `for ip in ` and ` wait` (counting via Split-1).
        var forIpCount = arpingLoop!.Split("for ip in ").Length - 1;
        Assert.Equal(4, forIpCount);
        var waitCount = arpingLoop.Split("wait").Length - 1;
        Assert.Equal(4, waitCount);

        // The command starts with `for ip in ` and ends with `wait` (no trailing whitespace).
        Assert.StartsWith("for ip in ", arpingLoop);
        Assert.EndsWith("wait", arpingLoop);

        // Per-chunk IP-count invariant: every chunk holds at most 64 IPs. The chunk body shape
        // is `for ip in <ip1> ... <ipN>; do arping ... & done; wait`, so we slice between each
        // `for ip in ` marker and the next `; do ` marker that follows it.
        var chunkStarts = new List<int>();
        var idx = 0;
        while ((idx = arpingLoop.IndexOf("for ip in ", idx, StringComparison.Ordinal)) >= 0)
        {
            chunkStarts.Add(idx);
            idx += "for ip in ".Length;
        }
        Assert.Equal(4, chunkStarts.Count);

        foreach (var start in chunkStarts)
        {
            var ipsStart = start + "for ip in ".Length;
            var doIdx = arpingLoop.IndexOf("; do ", ipsStart, StringComparison.Ordinal);
            Assert.True(doIdx > ipsStart, $"Could not find chunk terminator '; do ' after start={start}.");
            var ipsSlice = arpingLoop.Substring(ipsStart, doIdx - ipsStart);
            var ipCount = ipsSlice.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(ipCount <= 64, $"Chunk exceeded 64 IPs: count={ipCount}.");
        }
    }
}
