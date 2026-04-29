using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Services.Polling;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

public sealed class OpenWrtOutputParserTests
{
    public static TheoryData<string> AllVersionFolders() => new()
    {
        // Plan 02-04 update: openwrt-23.05-SNAPSHOT replaces the original 23.05.5 slot. The
        // operator's real OpenWRT AP runs `DISTRIB_RELEASE='23.05-SNAPSHOT'` (a snapshot build of
        // the 23.05 branch), and the FixtureCapture tool derives folder names from the AP's
        // actual DISTRIB_RELEASE. The 23.05.5 skeleton folder was deleted in Task 4 W8 LOCK
        // cleanup since no fixture content ever landed there.
        Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT"),
        Path.Combine("Fixtures", "ssh-output", "openwrt-22.03.7"),
        Path.Combine("Fixtures", "ssh-output", "openwrt-21.02.7"),
    };

    [Theory]
    [MemberData(nameof(AllVersionFolders))]
    public void ParseIwinfoSummary_AcrossAllSupportedVersions_ProducesValidNetworks(string fixtureDir)
    {
        var path = Path.Combine(fixtureDir, "iwinfo.txt");
        Assert.SkipUnless(File.Exists(path), $"Fixture not yet captured (Plan 02-04 will populate {fixtureDir}/iwinfo.txt)");
        var raw = File.ReadAllText(path);
        var networks = OpenWrtOutputParser.ParseIwinfoSummary(raw, NullLogger.Instance);
        Assert.NotEmpty(networks);
        Assert.All(networks, n => Assert.Equal(n.MacAddress.ToUpperInvariant(), n.MacAddress));
    }

    [Theory]
    [MemberData(nameof(AllVersionFolders))]
    public void ParseIpNeigh_AcrossAllSupportedVersions_ProducesUppercaseKeyDictionary(string fixtureDir)
    {
        var path = Path.Combine(fixtureDir, "ip-neigh-show.txt");
        Assert.SkipUnless(File.Exists(path), $"Fixture not yet captured (Plan 02-04 will populate {fixtureDir}/ip-neigh-show.txt)");
        var raw = File.ReadAllText(path);
        var ipMap = OpenWrtOutputParser.ParseIpNeigh(raw, NullLogger.Instance);
        Assert.All(ipMap.Keys, mac => Assert.Equal(mac.ToUpperInvariant(), mac));
    }

    [Fact]
    public void ParseIwinfoSummary_TolerantOfDeliberatelyCorruptLine_LogsWarningAndContinues()
    {
        // D-05 — tolerant per-line; partial result, never empty solely from malformed lines.
        // Use a synthetic minimal-viable iwinfo block to avoid Plan-02-04 fixture dependency.
        const string syntheticGood = """
            wlan0     ESSID: "TestNet"
                      Mode: Master  Channel: 6
                      Access Point: AA:BB:CC:DD:EE:01
                      Tx-Power: 20 dBm
                      Encryption: WPA2 PSK
            """;
        var raw = syntheticGood + "\n\nGARBAGE LINE WITH NO RECOGNIZABLE FIELDS\n";
        var networks = OpenWrtOutputParser.ParseIwinfoSummary(raw, NullLogger.Instance);
        // The good block should still parse despite trailing garbage. Empty result is reserved for
        // connection failure, not "all lines bad" (D-05 contract).
        // B8 LOCK: the synthetic above contains the verbatim recognition keys (ESSID:, Access Point:,
        // Channel:, Tx-Power:, Encryption:) declared as private const fields in OpenWrtOutputParser;
        // the executor does NOT need to adjust the synthetic. The Assert.NotEmpty contract is locked.
        Assert.NotEmpty(networks);
        Assert.Equal("AA:BB:CC:DD:EE:01", networks[0].MacAddress);
        Assert.Equal("TestNet", networks[0].SSID);
    }

    [Fact]
    public void ParseIpNeigh_TolerantOfBlankIPLines_KeepsEntryWithNullValue()
    {
        // POLL-13: STALE / no-IP entries map to null, not skipped.
        const string raw = """
            192.168.1.42 dev wlan0 lladdr aa:bb:cc:dd:ee:01 REACHABLE
            fe80::1 dev wlan0 lladdr aa:bb:cc:dd:ee:02 STALE
            """;
        var ipMap = OpenWrtOutputParser.ParseIpNeigh(raw, NullLogger.Instance);
        Assert.True(ipMap.ContainsKey("AA:BB:CC:DD:EE:01"));
        Assert.True(ipMap.ContainsKey("AA:BB:CC:DD:EE:02"));
        Assert.Equal("192.168.1.42", ipMap["AA:BB:CC:DD:EE:01"]);
        // POLL-13 / B9 LOCK: STALE entries map to null (must_haves.truths contract).
        // The parser detects STALE via the trailing " STALE" or " FAILED" token on the line and
        // sets the value to null even if an IP-shaped token preceded " dev " on the same line.
        Assert.Null(ipMap["AA:BB:CC:DD:EE:02"]);
    }

    [Fact]
    public void ParseIpNeigh_EmptyInput_ReturnsEmpty_NoWarning()
    {
        // D-05 empty rule: empty input is not an error.
        var ipMap = OpenWrtOutputParser.ParseIpNeigh("", NullLogger.Instance);
        Assert.Empty(ipMap);
    }

    [Fact]
    public void ParseIwinfoSummary_EmitsUppercaseMacs_FromMixedCaseInput()
    {
        // D-07 regression: even with lowercase BSSID input, every emitted WiFiNetwork.MacAddress is uppercase.
        const string raw = """
            wlan0     ESSID: "MixedCase"
                      Mode: Master  Channel: 11
                      Access Point: aa:bb:cc:dd:ee:0f
                      Tx-Power: 20 dBm
                      Encryption: WPA2 PSK
            """;
        var networks = OpenWrtOutputParser.ParseIwinfoSummary(raw, NullLogger.Instance);
        Assert.NotEmpty(networks);
        Assert.All(networks, n => Assert.Equal(n.MacAddress.ToUpperInvariant(), n.MacAddress));
        Assert.Equal("AA:BB:CC:DD:EE:0F", networks[0].MacAddress);
    }

    [Fact]
    public void ParseIwDevSummary_SyntheticInput_EmitsUppercaseMac_AndIfaceName()
    {
        // Synthetic-input regression for the iw fallback parser (POLL-12 fallback path). Closes the
        // ParseIwDevSummary line/branch coverage gap during the Plan 02-04 fixture-pending window.
        // The recognition key for Interface lines is "Interface " (no leading whitespace) per
        // OpenWrtOutputParser.IwDevInterfaceKey -- subsequent attribute lines may be indented but
        // are matched on TrimStart-then-StartsWith for "addr "/"ssid ".
        const string raw = "Interface wlan0\n" +
                           "\taddr aa:bb:cc:dd:ee:11\n" +
                           "\tssid IwTestNet\n" +
                           "\ttype AP\n" +
                           "\tchannel 6\n";
        var networks = OpenWrtOutputParser.ParseIwDevSummary(raw, NullLogger.Instance);
        Assert.NotEmpty(networks);
        Assert.Equal("AA:BB:CC:DD:EE:11", networks[0].MacAddress);
        Assert.Equal("IwTestNet", networks[0].SSID);
        Assert.Equal("wlan0", networks[0].IfaceName);
    }

    [Fact]
    public void ParseIwDevStations_SyntheticInput_EmitsStationWithUppercaseMacAndRSS()
    {
        // Synthetic-input regression for the iw station parser (POLL-12 fallback path). Closes the
        // ParseIwDevStations line/branch coverage gap during the Plan 02-04 fixture-pending window.
        const string raw = "Station 11:22:33:44:55:01 (on wlan0)\n" +
                           "\tsignal:\t-45 dBm\n" +
                           "\ttx bitrate: 54.0 MBit/s\n";
        var stations = OpenWrtOutputParser.ParseIwDevStations(raw, wiFiNetworkId: 42, NullLogger.Instance);
        Assert.NotEmpty(stations);
        Assert.Equal("11:22:33:44:55:01", stations[0].MacAddress);
        Assert.Equal(42, stations[0].WiFiNetworkId);
        Assert.Equal(-45, stations[0].RSS);
    }

    [Fact]
    public void ParseIwDevStations_EmptyInput_ReturnsEmpty()
    {
        // D-05 empty rule.
        var stations = OpenWrtOutputParser.ParseIwDevStations("", wiFiNetworkId: 1, NullLogger.Instance);
        Assert.Empty(stations);
    }

    [Fact]
    public void ParseIwDevStations_MultipleStations_EmitsAll()
    {
        // Branch coverage: tests the "if (cur is not null) result.Add(cur);" path between blocks
        // and the trailing flush after the loop.
        const string raw = "Station aa:bb:cc:dd:ee:01 (on wlan0)\n" +
                           "\tsignal:\t-40 dBm\n" +
                           "Station aa:bb:cc:dd:ee:02 (on wlan0)\n" +
                           "\tsignal:\t-60 dBm\n";
        var stations = OpenWrtOutputParser.ParseIwDevStations(raw, wiFiNetworkId: 7, NullLogger.Instance);
        Assert.Equal(2, stations.Count);
        Assert.Equal("AA:BB:CC:DD:EE:01", stations[0].MacAddress);
        Assert.Equal("AA:BB:CC:DD:EE:02", stations[1].MacAddress);
        Assert.Equal(-40, stations[0].RSS);
        Assert.Equal(-60, stations[1].RSS);
    }
}
