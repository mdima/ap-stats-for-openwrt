using Xunit;

namespace ApStatsForOpenWrt.Tests.TestSupport;

/// <summary>
/// Per-test-class temp directory fixture. Creates two children (data/, logs/) under a
/// unique Path.GetTempPath()/openwrtapstats-tests-&lt;guid&gt; root and removes them on
/// dispose. Tests that don't touch the filesystem skip this fixture entirely
/// (D-15 — parallel-safety).
/// </summary>
public sealed class TempDirectoryFixture : IAsyncLifetime
{
    public string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        $"openwrtapstats-tests-{Guid.NewGuid():N}");

    public string DataDir => Path.Combine(Root, "data");
    public string LogsDir => Path.Combine(Root, "logs");
    public string KeysDir => Path.Combine(DataDir, "keys");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — Windows file-handle quirks should not fail the test run.
        }
        return ValueTask.CompletedTask;
    }
}
