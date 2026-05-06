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
    public void ParseIwinfoSummary_DropsBlock_WhenBssidIsAllZeros()
    {
        // Defect 2: OpenWRT iwinfo emits placeholder blocks for ifaces that exist but aren't
        // associated. BSSID 00:00:00:00:00:00 is the all-zero sentinel. Must be filtered.
        const string raw = """
            mld-wifi0 ESSID: "PlausibleSSID"
                      Mode: Master  Channel: 6
                      Access Point: 00:00:00:00:00:00
                      Tx-Power: 0 dBm
                      Encryption: unknown
            """;
        var networks = OpenWrtOutputParser.ParseIwinfoSummary(raw, NullLogger.Instance);
        Assert.Empty(networks);
    }

    [Fact]
    public void ParseIwinfoSummary_DropsBlock_WhenSsidIsLiteralUnknown()
    {
        // Defect 2: literal "unknown" (lowercase, as OpenWRT emits it) is the iwinfo placeholder
        // for a non-broadcasting iface even when the BSSID is real.
        const string raw = """
            wifi0     ESSID: unknown
                      Mode: Master  Channel: 9
                      Access Point: 94:83:C4:C8:ED:8E
                      Tx-Power: 20 dBm
                      Encryption: unknown
            """;
        var networks = OpenWrtOutputParser.ParseIwinfoSummary(raw, NullLogger.Instance);
        Assert.Empty(networks);
    }

    [Fact]
    public void ParseIwinfoSummary_DropsBlock_WhenSsidIsLiteralUnknown_CaseInsensitive()
    {
        // Defect 2: defensive case-insensitive match. Firmware variance may emit "Unknown" or
        // "UNKNOWN" — all variants must be treated as the placeholder sentinel.
        const string raw = """
            wifi0     ESSID: Unknown
                      Mode: Master  Channel: 36
                      Access Point: 94:83:C4:C8:ED:8F
                      Tx-Power: 20 dBm
                      Encryption: WPA2 PSK
            """;
        var networks = OpenWrtOutputParser.ParseIwinfoSummary(raw, NullLogger.Instance);
        Assert.Empty(networks);
    }

    [Fact]
    public void ParseIwDevSummary_DropsBlock_WhenAddrIsAllZeros()
    {
        // Defect 2 (iw-dev fallback): same all-zero BSSID filter applies on the iw-dev path.
        const string raw = "Interface mld-wifi0\n" +
                           "\taddr 00:00:00:00:00:00\n" +
                           "\tssid PlausibleSSID\n" +
                           "\ttype AP\n" +
                           "\tchannel 6\n";
        var networks = OpenWrtOutputParser.ParseIwDevSummary(raw, NullLogger.Instance);
        Assert.Empty(networks);
    }

    [Fact]
    public void ParseIwDevSummary_DropsBlock_WhenSsidIsLiteralUnknown()
    {
        // Defect 2 (iw-dev fallback): literal "unknown" placeholder filtered on iw-dev path.
        const string raw = "Interface wifi0\n" +
                           "\taddr 94:83:C4:C8:ED:8E\n" +
                           "\tssid unknown\n" +
                           "\ttype AP\n" +
                           "\tchannel 9\n";
        var networks = OpenWrtOutputParser.ParseIwDevSummary(raw, NullLogger.Instance);
        Assert.Empty(networks);
    }

    [Theory]
    [InlineData("HT Mode: HT20",       20)]
    [InlineData("HT Mode: HT40+",      40)]
    [InlineData("HT Mode: HT40-",      40)]
    [InlineData("HT Mode: VHT80",      80)]
    [InlineData("HT Mode: VHT160",    160)]
    [InlineData("HT Mode: HE160",     160)]
    [InlineData("HT Mode: EHT320",    320)]
    public void ParseIwinfoSummary_ExtractsWidth_FromHtMode(string htModeLine, int expectedWidth)
    {
        // Defect 3: WiFiNetwork.Width must be populated from the HT Mode mnemonic. Covers the
        // full PHY family — HT (n), VHT (ac), HE (ax / Wi-Fi 6), EHT (be / Wi-Fi 7). Variants
        // with +/- suffix (HT40+/HT40-) yield the same MHz as the bare prefix.
        var raw = $$"""
            wlan0     ESSID: "WidthTest"
                      Mode: Master  Channel: 6  {{htModeLine}}
                      Access Point: AA:BB:CC:DD:EE:01
                      Tx-Power: 20 dBm
                      Encryption: WPA2 PSK
            """;
        var networks = OpenWrtOutputParser.ParseIwinfoSummary(raw, NullLogger.Instance);
        Assert.NotEmpty(networks);
        Assert.Equal(expectedWidth, networks[0].Width);
    }

    [Fact]
    public void ParseIwinfoSummary_DefaultsWidthToZero_WhenHtModeMissing()
    {
        // Defect 3: when the HT Mode line is absent entirely (older firmware / minimal output),
        // Width falls back to 0 — the model's default int value.
        const string raw = """
            wlan0     ESSID: "NoHtMode"
                      Mode: Master  Channel: 6
                      Access Point: AA:BB:CC:DD:EE:02
                      Tx-Power: 20 dBm
                      Encryption: WPA2 PSK
            """;
        var networks = OpenWrtOutputParser.ParseIwinfoSummary(raw, NullLogger.Instance);
        Assert.NotEmpty(networks);
        Assert.Equal(0, networks[0].Width);
    }

    [Fact]
    public void ParseIwinfoSummary_DefaultsWidthToZero_WhenHtModeUnrecognized()
    {
        // Defect 3: when HT Mode line is present but doesn't match the HT/VHT/HE/EHT prefix
        // (e.g., "NOHT" on a non-broadcasting iface, or junk from a future firmware), Width
        // falls back to 0 rather than throwing.
        const string raw = """
            wlan0     ESSID: "BogusHtMode"
                      Mode: Master  Channel: 6  HT Mode: bogus-string
                      Access Point: AA:BB:CC:DD:EE:03
                      Tx-Power: 20 dBm
                      Encryption: WPA2 PSK
            """;
        var networks = OpenWrtOutputParser.ParseIwinfoSummary(raw, NullLogger.Instance);
        Assert.NotEmpty(networks);
        Assert.Equal(0, networks[0].Width);
    }

    [Fact]
    public void ParseIwinfoSummary_FixtureBacked_22_03_7_PopulatesWidthForBothBlocks()
    {
        // Defect 3 fixture-backed regression: the openwrt-22.03.7/iwinfo.txt fixture has
        // wlan0 = HT20 (Width=20) and wlan1 = VHT80 (Width=80). This test pins the parser
        // against the captured fixture so Width extraction is verified end-to-end on real
        // OpenWRT output, not just synthetic inputs.
        var path = Path.Combine("Fixtures", "ssh-output", "openwrt-22.03.7", "iwinfo.txt");
        Assert.SkipUnless(File.Exists(path), "Fixture not present");
        var raw = File.ReadAllText(path);
        var networks = OpenWrtOutputParser.ParseIwinfoSummary(raw, NullLogger.Instance);
        Assert.Equal(2, networks.Count);
        var wlan0 = networks.Single(n => n.SSID == "OpenWrt-Lab-Net");
        var wlan1 = networks.Single(n => n.SSID == "OpenWrt-Lab-5G");
        Assert.Equal(20, wlan0.Width);
        Assert.Equal(80, wlan1.Width);
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

    [Theory]
    // Regression: ParseIwinfoAssocList must extract RSS from AFTER the MAC.
    // Pre-fix, FirstSignedNumber matched the first decimal digit run anywhere in the line —
    // which is inside the MAC byte (e.g. "A4" → 4) — instead of the actual dBm value.
    [InlineData("A4:CF:12:D9:B9:B2  -67 dBm / -95 dBm (SNR 28)  3220 ms ago", "A4:CF:12:D9:B9:B2", -67)]
    [InlineData("5C:01:3B:70:A1:B8  -45 dBm / -95 dBm (SNR 50)  100 ms ago",  "5C:01:3B:70:A1:B8", -45)]
    [InlineData("EC:FA:BC:50:DF:65  -82 dBm / -95 dBm (SNR 13)  500 ms ago",  "EC:FA:BC:50:DF:65", -82)]
    [InlineData("02:00:5F:AA:BB:01  -45 dBm / -95 dBm (SNR 50)  100 ms ago",  "02:00:5F:AA:BB:01", -45)]
    public void ParseIwinfoAssocList_RssExtractedFromAfterMac_NotFromMacDigits(
        string rawLine, string expectedMac, int expectedRss)
    {
        var stations = OpenWrtOutputParser.ParseIwinfoAssocList(rawLine, wiFiNetworkId: 1, NullLogger.Instance);
        Assert.Single(stations);
        Assert.Equal(expectedMac, stations[0].MacAddress);
        Assert.Equal(expectedRss, stations[0].RSS);
    }

    [Fact]
    public void ParseIwinfoAssocList_PopulatesTxAndRxBitrate_FromContinuationLines()
    {
        // Real iwinfo assoclist format. RX/TX continuation lines start with a tab and the
        // recognition key "RX:" / "TX:" followed by the bitrate in MBit/s.
        const string raw =
            "02:00:5F:AA:BB:01  -45 dBm / -95 dBm (SNR 50)  100 ms ago\n" +
            "\tRX: 144.4 MBit/s, MCS 7, 20MHz                  31337 Pkts.\n" +
            "\tTX: 86.7 MBit/s, MCS 5, 20MHz                  12345 Pkts.\n" +
            "\texpected throughput: 100.0 MBit/s\n";
        var stations = OpenWrtOutputParser.ParseIwinfoAssocList(raw, wiFiNetworkId: 1, NullLogger.Instance);
        Assert.Single(stations);
        Assert.Equal(144.4, stations[0].RxBitrate);
        Assert.Equal(86.7,  stations[0].TxBitrate);
    }

    [Fact]
    public void ParseIwDevStations_PopulatesTxAndRxBitrate_FromBlockLines()
    {
        // Real iw dev wlanX station dump format — block leader + indented "tx bitrate:" /
        // "rx bitrate:" lines with tab-separated values in MBit/s.
        const string raw =
            "Station 02:00:12:23:52:61 (on wlan0)\n" +
            "\tsignal:  \t-45 dBm\n" +
            "\ttx bitrate:\t1300.0 MBit/s\n" +
            "\trx bitrate:\t100.0 MBit/s\n";
        var stations = OpenWrtOutputParser.ParseIwDevStations(raw, wiFiNetworkId: 7, NullLogger.Instance);
        Assert.Single(stations);
        Assert.Equal(1300.0, stations[0].TxBitrate);
        Assert.Equal(100.0,  stations[0].RxBitrate);
    }
}
