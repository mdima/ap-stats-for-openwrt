using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Services.Polling;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

public sealed class OpenWrtOutputParserTests
{
    public static TheoryData<string> AllVersionFolders() => new()
    {
        Path.Combine("Fixtures", "ssh-output", "openwrt-23.05.5"),
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
}
