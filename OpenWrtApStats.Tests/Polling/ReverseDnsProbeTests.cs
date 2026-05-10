using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Services.Polling;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

/// <summary>
/// Phase 9 ENRICH-03 + ENRICH-05 — fixture-replay integration arms + synthetic facts for
/// <see cref="ReverseDnsProbe"/>. Mirrors the SubnetSweepProbeTests shape: per-test inline
/// fixture provisioning via FakeSshSession.CommandOverrides, three-version theory rows
/// gated by <see cref="Assert.SkipUnless"/>.
/// </summary>
public sealed class ReverseDnsProbeTests
{
    public static TheoryData<string> AllVersionFolders() => new()
    {
        Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT"),
        Path.Combine("Fixtures", "ssh-output", "openwrt-22.03.7"),
        Path.Combine("Fixtures", "ssh-output", "openwrt-21.02.7"),
    };

    private static WiFiNode MakeNode() => new()
    {
        Id = 1,
        Hostname = "test-ap",
        Username = "operator",
        Password = string.Empty,
    };

    [Fact]
    public async Task PollAsync_EmptyIpList_ReturnsEmptySuccess_NoSshIssued()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = new FakeSshSession(Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT"));
        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);

        var result = await probe.PollAsync(MakeNode(), Array.Empty<string>(), ct);
        var success = Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Empty(success.IpToHostname);
        Assert.Empty(session.CommandsIssued);
    }

    [Fact]
    public async Task PollAsync_SingleIpHappyPath_ReturnsNormalizedFqdn()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        // Per-test override fixture: maps the nslookup invocation to a synthetic file.
        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-happy.txt"),
            "Server:    192.168.1.1\nAddress 1: 192.168.1.1\n\nName:      MY-HOST.LAN.\nAddress 1: 192.168.1.42 my-host.lan\n");

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["nslookup 192.168.1.42"] = "nslookup-happy";

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var result = await probe.PollAsync(MakeNode(), new[] { "192.168.1.42" }, ct);

        var success = Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Single(success.IpToHostname);
        Assert.Equal("my-host.lan", success.IpToHostname["192.168.1.42"]);     // Lowered, dot-stripped.
        Assert.Contains(session.CommandsIssued, c => c == "nslookup 192.168.1.42");
    }

    [Fact]
    public async Task PollAsync_NegativeResult_StoresNullHostname()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-nxdomain.txt"),
            "Server:    192.168.1.1\n\n** server can't find 192.168.99.99: NXDOMAIN\n");

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["nslookup 192.168.99.99"] = "nslookup-nxdomain";

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var result = await probe.PollAsync(MakeNode(), new[] { "192.168.99.99" }, ct);

        var success = Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.True(success.IpToHostname.ContainsKey("192.168.99.99"));
        Assert.Null(success.IpToHostname["192.168.99.99"]);
    }

    [Fact]
    public async Task PollAsync_MultipleIps_ReturnsAllResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-host42.txt"),
            "Name:      host42.lan\nAddress 1: 192.168.1.42 host42.lan\n");
        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-mixed-nxdomain.txt"),
            "** server can't find 192.168.1.43: NXDOMAIN\n");
        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-host44.txt"),
            "Name:      Host44.LAN.\nAddress 1: 192.168.1.44 host44.lan\n");

        var session = new FakeSshSession(fixtureDir);
        // CommandOverrides lookups are first-match-wins; register specific IPs explicitly so
        // each invocation routes to its dedicated fixture.
        session.CommandOverrides["nslookup 192.168.1.42"] = "nslookup-host42";
        session.CommandOverrides["nslookup 192.168.1.43"] = "nslookup-mixed-nxdomain";
        session.CommandOverrides["nslookup 192.168.1.44"] = "nslookup-host44";

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var result = await probe.PollAsync(MakeNode(),
            new[] { "192.168.1.42", "192.168.1.43", "192.168.1.44" }, ct);

        var success = Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Equal(3, success.IpToHostname.Count);
        Assert.Equal("host42.lan", success.IpToHostname["192.168.1.42"]);
        Assert.Null(success.IpToHostname["192.168.1.43"]);
        Assert.Equal("host44.lan", success.IpToHostname["192.168.1.44"]);
    }

    [Theory]
    [MemberData(nameof(AllVersionFolders))]
    public async Task PollAsync_AcrossAllSupportedVersions_ProducesNormalizedFqdnOrNull(string fixtureDir)
    {
        var path = Path.Combine(fixtureDir, "nslookup.txt");
        Assert.SkipUnless(File.Exists(path), $"Fixture not yet captured: {path}");

        var ct = TestContext.Current.CancellationToken;
        var session = new FakeSshSession(fixtureDir);
        // Map any "nslookup " command to the committed nslookup.txt fixture for this version.
        session.CommandOverrides["nslookup "] = "nslookup";

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var result = await probe.PollAsync(MakeNode(), new[] { "192.168.1.42" }, ct);

        var success = Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Single(success.IpToHostname);
        var fqdn = success.IpToHostname["192.168.1.42"];
        if (fqdn is null) return;     // null is acceptable (negative result).
        Assert.Equal(fqdn.ToLowerInvariant(), fqdn);
        Assert.False(fqdn.EndsWith('.'), $"Trailing dot was not stripped: '{fqdn}'");
    }
}
