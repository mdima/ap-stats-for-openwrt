using Xunit;

namespace OpenWrtApStats.Tests.Polling;

public sealed class FixturePresenceTests
{
    /// <summary>
    /// CI-04 binding: at least one fixture folder must declare `provenance: live-capture` in its
    /// SOURCE.md. Plan 02-02 ships skeleton folders with `provenance: SKELETON-PENDING-CAPTURE`
    /// — the test is deliberately skipped in this plan. Plan 02-04 lands the operator-captured
    /// fixture, removes the [Skip = ...] argument, and the test flips green.
    /// </summary>
    [Fact(Skip = "Plan 02-04 lands real-device fixture; remove this Skip argument when fixture lands.")]
    public void RealDeviceFixture_Exists()
    {
        var root = Path.Combine("Fixtures", "ssh-output");
        Assert.True(Directory.Exists(root), $"Fixture root missing: {root}");
        var versionDirs = Directory.GetDirectories(root, "openwrt-*");
        Assert.NotEmpty(versionDirs);

        var hasLiveCapture = false;
        foreach (var dir in versionDirs)
        {
            var sourcePath = Path.Combine(dir, "SOURCE.md");
            if (!File.Exists(sourcePath)) continue;
            var content = File.ReadAllText(sourcePath);
            if (content.Contains("provenance: live-capture", StringComparison.Ordinal))
            {
                hasLiveCapture = true;
                break;
            }
        }
        Assert.True(hasLiveCapture, "CI-04: no fixture folder has 'provenance: live-capture' in SOURCE.md.");
    }
}
