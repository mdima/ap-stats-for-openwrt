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
    public async Task PollAsync_CidrListBranch_BypassesSshClientDerivation_OmitsIfaceFlag_AndMapsMacs()
    {
        // M-HRD-08 (v1.0 hardening) — E2E pin of the CIDR-list branch as the Dropbear
        // `$SSH_CLIENT`-unset workaround. When SubnetScanRanges is set, PollAsync must:
        //   (a) NEVER issue `ip route get "${SSH_CLIENT%% *}"` (the auto-derivation that fails
        //       on Dropbear, which does not export $SSH_CLIENT) — the operator-configured CIDR
        //       list short-circuits derivation entirely;
        //   (b) build the sweep command WITHOUT the `-I <iface>` flag (iface stays null on the
        //       CIDR branch — the AP's own routing picks the interface);
        //   (c) still return a Success carrying the parsed MAC→IP mappings from the sweep output.
        // The T-09-03-05 peerIp gate is still enforced at entry (valid IPv4 required) but the
        // value is NOT interpolated into command construction on this branch (SC4 isolation).
        //
        // Unlike PollAsync_SubnetScanRangesSet_UsesCidrListWithoutAutoDerivation (theory,
        // skip-guarded on a captured arping.txt), this test provisions its own arping fixture
        // inline so it ALWAYS runs and can assert deterministic MAC→IP output + the -I omission.
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["arping -c1 -w1"] = "arping-cidr-e2e";

        // Two responding hosts inside 192.168.1.0/30 (.1 and .2 are the usable hosts); BusyBox
        // arping output, lowercase MACs — the parser must uppercase them.
        File.WriteAllText(Path.Combine(fixtureDir, "arping-cidr-e2e.txt"),
            "ARPING 192.168.1.1 from 192.168.1.1 br-lan\n" +
            "Unicast reply from 192.168.1.1 [de:ad:be:ef:00:01] 0.500ms\n" +
            "ARPING 192.168.1.2 from 192.168.1.1 br-lan\n" +
            "Unicast reply from 192.168.1.2 [de:ad:be:ef:00:02] 0.700ms\n");

        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);
        var node = MakeNode(ranges: "192.168.1.0/30");
        var result = await probe.PollAsync(node, peerIp: "192.168.1.42", ct);

        // (c) Success with the parsed, uppercased MAC→IP mappings.
        var success = Assert.IsType<SubnetSweepProbeResult.Success>(result);
        Assert.Equal("192.168.1.1", success.MacToIp["DE:AD:BE:EF:00:01"]);
        Assert.Equal("192.168.1.2", success.MacToIp["DE:AD:BE:EF:00:02"]);

        // (a) $SSH_CLIENT auto-derivation must NOT have run.
        Assert.DoesNotContain(session.CommandsIssued, c => c.Contains("${SSH_CLIENT%% *}", StringComparison.Ordinal));
        Assert.DoesNotContain(session.CommandsIssued, c => c.StartsWith("ip route get", StringComparison.Ordinal));
        Assert.DoesNotContain(session.CommandsIssued, c => c.StartsWith("ip -4 addr show", StringComparison.Ordinal));

        // (b) the sweep command omits the `-I ` iface flag (iface is null on the CIDR branch).
        var sweep = session.CommandsIssued.Single(c => c.Contains("arping -c1 -w1"));
        Assert.DoesNotContain("-I ", sweep);

        // Sanity: the capability probe + the sweep loop are the issued commands.
        Assert.Contains(session.CommandsIssued, c => c == "command -v arping");
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

        // The command starts with `for ip in `; the sweep body's final `wait` is now followed by
        // the appended sentinel + `ip neigh show` dump (quick task 260720-q8i), so the whole
        // command ends with `; ip neigh show`. The 4× `wait` chunk-count invariant above is
        // unaffected (neither the sentinel nor `ip neigh show` contains the substring "wait").
        Assert.StartsWith("for ip in ", arpingLoop);
        Assert.EndsWith("; ip neigh show", arpingLoop);
        Assert.Contains("wait; echo '===AP-STATS-NEIGH==='; ip neigh show", arpingLoop);

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

    // Sentinel echoed between the sweep output and the `ip neigh show` dump (must match
    // SubnetSweepProbe.NeighSentinel). Fixtures below concatenate sweep output + sentinel +
    // neighbour-table output to mirror the single-round-trip combined stdout the probe parses.
    private const string NeighSentinel = "===AP-STATS-NEIGH===";

    [Fact]
    public async Task PollAsync_PingFallback_ResolvesMacIpFromNeighbourTable()
    {
        // Quick task 260720-q8i (fix): a node WITHOUT arping falls back to ping, whose stdout
        // carries no MAC (ParseArpingPingOutput yields nothing). The universal fix appends
        // `; ip neigh show` and reads the ARP cache the ping sweep just populated. This is the
        // exact node-3 / OpenWrt-25.12 scenario from C:\Temp\logs ("found 0 MAC-IP mappings via
        // ping") — post-fix it resolves via the neighbour-table read.
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        // arping absent → capability probe exits non-zero → ping branch.
        File.WriteAllText(Path.Combine(fixtureDir, "missing-arping.txt"), "");
        File.WriteAllText(Path.Combine(fixtureDir, "missing-arping.txt.exit"), "1");

        // Combined stdout of `<ping loop>; echo <sentinel>; ip neigh show`: ping lines (no MAC —
        // ignored) then a BusyBox `ip neigh show` block (lowercase MACs — parser uppercases).
        File.WriteAllText(Path.Combine(fixtureDir, "ping-neigh-resolved.txt"),
            "PING 192.168.1.1 (192.168.1.1): 56 data bytes\n" +
            "64 bytes from 192.168.1.1: seq=0 ttl=64 time=1.100 ms\n" +
            "PING 192.168.1.2 (192.168.1.2): 56 data bytes\n" +
            "64 bytes from 192.168.1.2: seq=0 ttl=64 time=1.200 ms\n" +
            NeighSentinel + "\n" +
            "192.168.1.1 dev br-lan lladdr de:ad:be:ef:00:01 REACHABLE\n" +
            "192.168.1.2 dev br-lan lladdr de:ad:be:ef:00:02 STALE\n");   // STALE → dropped

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["command -v arping"] = "missing-arping";
        session.CommandOverrides["ping -c1 -W1"] = "ping-neigh-resolved";

        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);
        var node = MakeNode(ranges: "192.168.1.0/30");
        var result = await probe.PollAsync(node, peerIp: "192.168.1.1", ct);

        var success = Assert.IsType<SubnetSweepProbeResult.Success>(result);
        // The REACHABLE neigh entry resolves (uppercased); the STALE one is dropped.
        Assert.Equal("192.168.1.1", success.MacToIp["DE:AD:BE:EF:00:01"]);
        Assert.False(success.MacToIp.ContainsKey("DE:AD:BE:EF:00:02"));
        // Single combined SSH round-trip: the ping loop command carries the appended neigh dump,
        // and the arping loop was NOT issued (capability probe said arping is absent).
        Assert.Contains(session.CommandsIssued,
            c => c.Contains("ping -c1 -W1") && c.EndsWith("; ip neigh show", StringComparison.Ordinal));
        Assert.DoesNotContain(session.CommandsIssued, c => c.Contains("arping -c1 -w1"));
    }

    [Fact]
    public async Task PollAsync_PingFallback_NoNeighbourEntries_YieldsEmptyMap_PinsOldFailure()
    {
        // Quick task 260720-q8i (regression pin): with ping stdout only and an EMPTY neighbour
        // table, the map is empty — proving the neighbour-table read (not the ping stdout) is what
        // actually resolves IPs on arping-less nodes. Pre-fix this path always returned empty; this
        // test locks that the ping stdout alone still yields nothing, so a future refactor cannot
        // silently reintroduce a "ping stdout is enough" assumption.
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        File.WriteAllText(Path.Combine(fixtureDir, "missing-arping.txt"), "");
        File.WriteAllText(Path.Combine(fixtureDir, "missing-arping.txt.exit"), "1");
        // Ping stdout, sentinel, then NO neighbour entries (unresponsive hosts / empty ARP cache).
        File.WriteAllText(Path.Combine(fixtureDir, "ping-no-neigh.txt"),
            "PING 192.168.1.1 (192.168.1.1): 56 data bytes\n" +
            "PING 192.168.1.2 (192.168.1.2): 56 data bytes\n" +
            NeighSentinel + "\n");

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["command -v arping"] = "missing-arping";
        session.CommandOverrides["ping -c1 -W1"] = "ping-no-neigh";

        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);
        var node = MakeNode(ranges: "192.168.1.0/30");
        var result = await probe.PollAsync(node, peerIp: "192.168.1.1", ct);

        var success = Assert.IsType<SubnetSweepProbeResult.Success>(result);
        Assert.Empty(success.MacToIp);
    }

    [Fact]
    public async Task PollAsync_ArpingPresent_NeighbourReadIsAdditive_AndSweepWinsOnConflict()
    {
        // Quick task 260720-q8i: the neighbour-table read is UNIVERSAL (runs on the arping path
        // too). It must (a) fill a MAC that arping missed but the kernel cache knows, and (b) NOT
        // clobber a fresh arping IP when the (older) neighbour entry disagrees — the live sweep
        // wins on conflict. The sentinel split is what makes (b) hold: without it, the neigh line's
        // MAC+IPv4 would be swept up by ParseArpingPingOutput's last-wins rule and overwrite arping.
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        // Combined stdout of `<arping loop>; echo <sentinel>; ip neigh show`.
        //   MAC ..01: arping reports .1; neigh (REACHABLE) disagrees at .251 → arping must win.
        //   MAC ..02: arping reports .2; neigh agrees at .2.
        //   MAC ..03: arping missed it; only the neigh cache has it at .3 → additive fill.
        File.WriteAllText(Path.Combine(fixtureDir, "arping-plus-neigh.txt"),
            "ARPING 192.168.1.1 from 192.168.1.1 br-lan\n" +
            "Unicast reply from 192.168.1.1 [de:ad:be:ef:00:01] 0.5ms\n" +
            "ARPING 192.168.1.2 from 192.168.1.1 br-lan\n" +
            "Unicast reply from 192.168.1.2 [de:ad:be:ef:00:02] 0.6ms\n" +
            NeighSentinel + "\n" +
            "192.168.1.251 dev br-lan lladdr de:ad:be:ef:00:01 REACHABLE\n" +
            "192.168.1.2 dev br-lan lladdr de:ad:be:ef:00:02 REACHABLE\n" +
            "192.168.1.3 dev br-lan lladdr de:ad:be:ef:00:03 REACHABLE\n");

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["arping -c1 -w1"] = "arping-plus-neigh";

        var probe = new SubnetSweepProbe(session, NullLogger<SubnetSweepProbe>.Instance);
        var node = MakeNode(ranges: "192.168.1.0/29");   // usable hosts .1..-.6
        var result = await probe.PollAsync(node, peerIp: "192.168.1.42", ct);

        var success = Assert.IsType<SubnetSweepProbeResult.Success>(result);
        Assert.Equal("192.168.1.1", success.MacToIp["DE:AD:BE:EF:00:01"]);   // (b) arping wins over neigh .251
        Assert.Equal("192.168.1.2", success.MacToIp["DE:AD:BE:EF:00:02"]);   // agreement
        Assert.Equal("192.168.1.3", success.MacToIp["DE:AD:BE:EF:00:03"]);   // (a) neigh-only additive fill
    }
}
