using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Services.Vendor;
using Xunit;

namespace OpenWrtApStats.Tests.Vendor;

/// <summary>
/// DASH-13 / D-21 OUI lookup tests. The fixture row "00-00-0C" → "Cisco Systems, Inc"
/// is one of the oldest IEEE registrations and is stable across decades of CSV revisions —
/// asserting against it gives Pitfall 9 detection without coupling to a churning row.
/// </summary>
public sealed class OuiLookupTests
{
    [Fact]
    public void GetVendor_KnownPrefix_Cisco_ReturnsVendor()
    {
        // 00:00:0C is Cisco Systems' first IEEE OUI registration (assigned 1985-12-09);
        // it WILL be in any non-degraded oui.csv. If the embedded resource is malformed or
        // missing, OuiLookup falls back to empty FrozenDictionary and this test fails —
        // that's the intended Pitfall 9 detection.
        var lookup = new OuiLookup(NullLogger<OuiLookup>.Instance);
        var vendor = lookup.GetVendor("00:00:0C:DE:AD:BE");
        Assert.NotNull(vendor);
        Assert.Contains("Cisco", vendor!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("00:00:0c:de:ad:be")]    // lowercase
    [InlineData("00-00-0C-DE-AD-BE")]    // dashes
    [InlineData("00000CDEADBE")]         // no separators
    public void GetVendor_HandlesSeparatorsAndCase(string mac)
    {
        var lookup = new OuiLookup(NullLogger<OuiLookup>.Instance);
        Assert.NotNull(lookup.GetVendor(mac));
    }

    [Fact]
    public void GetVendor_UnknownPrefix_ReturnsNull()
    {
        var lookup = new OuiLookup(NullLogger<OuiLookup>.Instance);
        // F2:FE:FD is locally-administered (private bit set) — never assigned by IEEE.
        Assert.Null(lookup.GetVendor("F2:FE:FD:00:00:01"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("AA:BB")]
    public void GetVendor_TooShort_ReturnsNull(string? mac)
    {
        var lookup = new OuiLookup(NullLogger<OuiLookup>.Instance);
        Assert.Null(lookup.GetVendor(mac ?? ""));
    }

    [Fact]
    public void ParseRow_QuotedFieldsWithCommas()
    {
        // RFC-4180 quoted-comma + double-quote escape — IEEE's CSV uses these.
        var (prefix, vendor) = OuiLookup.ParseRow("MA-L,000A0C,\"Acme, Inc\",\"123 Main, Suite \"\"5\"\"\"");
        Assert.Equal("000A0C", prefix);
        Assert.Equal("Acme, Inc", vendor);
    }
}
