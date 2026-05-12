using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Services.Polling;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

/// <summary>
/// Phase 9 ENRICH-06 / D-04 / D-06 — coverage for the static SubnetDeriver helpers:
/// CIDR list parsing, CIDR expansion, and `ip route get` / `ip -4 addr show` parsing
/// that backs the auto-derivation branch of <see cref="SubnetSweepProbe"/>.
/// </summary>
public sealed class SubnetDeriverTests
{
    // ParseCidrList tests
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseCidrList_NullOrEmpty_ReturnsEmpty(string? input)
    {
        var result = SubnetDeriver.ParseCidrList(input, NullLogger.Instance);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseCidrList_SingleCidr_ReturnsOneEntry()
    {
        var result = SubnetDeriver.ParseCidrList("192.168.1.0/24", NullLogger.Instance);
        Assert.Single(result);
        Assert.Equal("192.168.1.0/24", result[0]);
    }

    [Fact]
    public void ParseCidrList_CommaSeparated_ReturnsAllEntries()
    {
        var result = SubnetDeriver.ParseCidrList("192.168.1.0/24,10.0.0.0/24", NullLogger.Instance);
        Assert.Equal(2, result.Count);
        Assert.Equal("192.168.1.0/24", result[0]);
        Assert.Equal("10.0.0.0/24", result[1]);
    }

    [Fact]
    public void ParseCidrList_TrimsWhitespace()
    {
        var result = SubnetDeriver.ParseCidrList("  192.168.1.0/24 ,  10.0.0.0/24  ", NullLogger.Instance);
        Assert.Equal(2, result.Count);
        Assert.Equal("192.168.1.0/24", result[0]);
        Assert.Equal("10.0.0.0/24", result[1]);
    }

    [Fact]
    public void ParseCidrList_MalformedEntry_LogsWarningAndSkips()
    {
        var result = SubnetDeriver.ParseCidrList("not-a-cidr,192.168.1.0/24", NullLogger.Instance);
        Assert.Single(result);
        Assert.Equal("192.168.1.0/24", result[0]);
    }

    // ExpandCidr tests
    [Fact]
    public void ExpandCidr_Slash24_Yields254Hosts()
    {
        var hosts = SubnetDeriver.ExpandCidr("192.168.1.0/24", NullLogger.Instance);
        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0]);
        Assert.Equal("192.168.1.254", hosts[253]);
        Assert.DoesNotContain("192.168.1.0", hosts);
        Assert.DoesNotContain("192.168.1.255", hosts);
    }

    [Fact]
    public void ExpandCidr_Slash30_Yields2Hosts()
    {
        var hosts = SubnetDeriver.ExpandCidr("192.168.1.0/30", NullLogger.Instance);
        Assert.Equal(2, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0]);
        Assert.Equal("192.168.1.2", hosts[1]);
    }

    [Fact]
    public void ExpandCidr_Slash32_Yields1Host()
    {
        var hosts = SubnetDeriver.ExpandCidr("192.168.1.42/32", NullLogger.Instance);
        Assert.Single(hosts);
        Assert.Equal("192.168.1.42", hosts[0]);
    }

    // ExtractInterfaceFromIpRouteGet tests
    [Fact]
    public void ExtractInterfaceFromIpRouteGet_HappyPath_ReturnsInterface()
    {
        const string raw = "192.168.1.1 dev br-lan src 192.168.1.42 uid 0\n    cache";
        var iface = SubnetDeriver.ExtractInterfaceFromIpRouteGet(raw);
        Assert.Equal("br-lan", iface);
    }

    [Fact]
    public void ExtractInterfaceFromIpRouteGet_NoDev_ReturnsNull()
    {
        Assert.Null(SubnetDeriver.ExtractInterfaceFromIpRouteGet("garbage"));
        Assert.Null(SubnetDeriver.ExtractInterfaceFromIpRouteGet(""));
    }

    // ExtractIPv4FromIpAddrShow tests
    [Fact]
    public void ExtractIPv4FromIpAddrShow_HappyPath_ReturnsBareIPv4()
    {
        const string raw = "3: br-lan: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500\n" +
                           "    inet 192.168.1.42/24 brd 192.168.1.255 scope global br-lan";
        var ipv4 = SubnetDeriver.ExtractIPv4FromIpAddrShow(raw);
        Assert.Equal("192.168.1.42", ipv4);
    }

    [Fact]
    public void ExtractIPv4FromIpAddrShow_NoInet_ReturnsNull()
    {
        Assert.Null(SubnetDeriver.ExtractIPv4FromIpAddrShow("garbage"));
    }

    // MaskToSlash24 tests
    [Theory]
    [InlineData("192.168.1.42", "192.168.1.0/24")]
    [InlineData("10.5.7.99", "10.5.7.0/24")]
    [InlineData("172.16.0.1", "172.16.0.0/24")]
    public void MaskToSlash24_StripsLastOctet(string input, string expected)
    {
        Assert.Equal(expected, SubnetDeriver.MaskToSlash24(input));
    }

    // DeriveSubnetFromInterfaceOutput tests
    [Fact]
    public void DeriveSubnetFromInterfaceOutput_HappyPath_Returns24CIDR()
    {
        const string ipRouteGet = "192.168.1.1 dev br-lan src 192.168.1.42 uid 0\n    cache";
        const string ipAddrShow = "3: br-lan: <BROADCAST,...> mtu 1500\n" +
                                  "    inet 192.168.1.42/24 brd 192.168.1.255 scope global br-lan";
        var cidr = SubnetDeriver.DeriveSubnetFromInterfaceOutput(ipRouteGet, ipAddrShow, NullLogger.Instance);
        Assert.Equal("192.168.1.0/24", cidr);
    }

    [Fact]
    public void DeriveSubnetFromInterfaceOutput_NoInterface_ReturnsNull()
    {
        var cidr = SubnetDeriver.DeriveSubnetFromInterfaceOutput("garbage", "garbage", NullLogger.Instance);
        Assert.Null(cidr);
    }

    [Fact]
    public void DeriveSubnetFromInterfaceOutput_NoIPv4_ReturnsNull()
    {
        const string ipRouteGet = "192.168.1.1 dev br-lan src 192.168.1.42 uid 0";
        const string ipAddrShow = "3: br-lan: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500";
        var cidr = SubnetDeriver.DeriveSubnetFromInterfaceOutput(ipRouteGet, ipAddrShow, NullLogger.Instance);
        Assert.Null(cidr);
    }
}
