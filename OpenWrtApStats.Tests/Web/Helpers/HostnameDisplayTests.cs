using OpenWrtApStats.Web.Helpers;
using Xunit;

namespace OpenWrtApStats.Tests.Web.Helpers;

/// <summary>
/// Phase 10 DASH-18 / DASH-19 unit tests for the suffix-stripping helper.
/// CHARTER §"Hostname storage and display" semantics: case-insensitive, first-match wins,
/// FQDN as-is when no match. The persisted Hostname column is always the full FQDN.
/// </summary>
public sealed class HostnameDisplayTests
{
    [Fact]
    public void Null_ReturnsNull()
        => Assert.Null(HostnameDisplay.StripLocalSuffix(null, new[] { "michelecasa" }));

    [Fact]
    public void Empty_ReturnsEmpty()
        => Assert.Equal("", HostnameDisplay.StripLocalSuffix("", new[] { "michelecasa" }));

    [Fact]
    public void NullSuffixList_ReturnsFqdn()
        => Assert.Equal("foo.bar", HostnameDisplay.StripLocalSuffix("foo.bar", null));

    [Fact]
    public void EmptySuffixList_ReturnsFqdn()
        => Assert.Equal("foo.bar", HostnameDisplay.StripLocalSuffix("foo.bar", Array.Empty<string>()));

    [Fact]
    public void SimpleStrip()
        => Assert.Equal("test", HostnameDisplay.StripLocalSuffix("test.michelecasa", new[] { "michelecasa" }));

    [Fact]
    public void CaseInsensitiveMatch_PreservesOriginalCase()
        => Assert.Equal("TEST", HostnameDisplay.StripLocalSuffix("TEST.MICHELECASA", new[] { "michelecasa" }));

    [Fact]
    public void MultiLabelSuffix_StripsCorrectly()
        => Assert.Equal("test", HostnameDisplay.StripLocalSuffix("test.lab.local", new[] { "lab.local" }));

    [Fact]
    public void NoMatch_ReturnsFqdnAsIs()
        => Assert.Equal("test.example.org", HostnameDisplay.StripLocalSuffix("test.example.org", new[] { "michelecasa" }));

    [Fact]
    public void FirstMatchWins()
        => Assert.Equal("foo.bar", HostnameDisplay.StripLocalSuffix("foo.bar.michelecasa", new[] { "michelecasa", "bar.michelecasa" }));

    [Fact]
    public void HostEqualsSuffix_PassesThrough()
        => Assert.Equal("michelecasa", HostnameDisplay.StripLocalSuffix("michelecasa", new[] { "michelecasa" }));

    [Fact]
    public void WhitespaceSuffixesSkipped()
        => Assert.Equal("test", HostnameDisplay.StripLocalSuffix("test.michelecasa", new[] { "", "  ", "michelecasa" }));

    [Fact]
    public void LeadingDotSuffixNormalized()
        => Assert.Equal("test", HostnameDisplay.StripLocalSuffix("test.michelecasa", new[] { ".michelecasa" }));
}
