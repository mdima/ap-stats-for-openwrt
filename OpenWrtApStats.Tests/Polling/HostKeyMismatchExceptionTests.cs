using OpenWrtApStats.Services.Polling.Exceptions;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

public sealed class HostKeyMismatchExceptionTests
{
    [Fact]
    public void Constructor_StoresExpectedAndActual()
    {
        var ex = new HostKeyMismatchException("ABC123", "XYZ789");
        Assert.Equal("ABC123", ex.ExpectedFingerprint);
        Assert.Equal("XYZ789", ex.ActualFingerprint);
        Assert.Contains("ABC123", ex.Message);
        Assert.Contains("XYZ789", ex.Message);
    }
}
