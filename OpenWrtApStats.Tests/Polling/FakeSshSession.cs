using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Services.Polling.Ssh;

namespace OpenWrtApStats.Tests.Polling;

/// <summary>
/// Test ISshSession that replays canned strings from a fixture folder. Identified by the
/// directory path passed to the constructor (typically <c>Fixtures/ssh-output/openwrt-{version}/</c>).
/// Connect records nothing; RunCommand looks up the canned string by command name.
/// Set <see cref="ThrowOnConnect"/> to simulate D-03 mismatch in tests.
/// </summary>
internal sealed class FakeSshSession(string fixtureDir) : ISshSession
{
    public string FixtureDir { get; } = fixtureDir;

    /// <summary>Set this to a factory returning ANY Exception to throw on Connect — simulates D-03 (HostKeyMismatchException) AND auth-failed paths (SshAuthenticationException, etc.). Broadened in Plan 02-02 per W4.</summary>
    public Func<Exception>? ThrowOnConnect { get; set; }

    /// <summary>Override the captured fingerprint surfaced via onHostKeyAccepted (default fake fingerprint otherwise).</summary>
    public string CapturedFingerprintToReport { get; set; } = "FAKE+SHA256+FINGERPRINT+1234567890abcdef";

    /// <summary>
    /// Optional simulated delay in <see cref="ConnectAsync"/> — POLL-04 timeout-test hook (Plan 02-03).
    /// When set, ConnectAsync awaits this delegate before invoking <paramref name="onHostKeyAccepted"/>.
    /// </summary>
    public Func<CancellationToken, Task>? ConnectDelay { get; set; }

    /// <summary>
    /// Optional simulated delay in <see cref="DisposeAsync"/> — POLL-14 dispose-hang test hook (Plan 02-03).
    /// </summary>
    public Func<Task>? DisposeDelay { get; set; }

    public async Task ConnectAsync(WiFiNode node, string plaintextPassword, string? expectedFingerprint,
        Action<string>? onHostKeyAccepted, CancellationToken ct)
    {
        if (ConnectDelay is { } d) await d(ct);
        if (ThrowOnConnect is { } f) throw f();
        // First-connect TOFU simulation: invoke callback with a fake fingerprint when caller passes null.
        if (expectedFingerprint is null) onHostKeyAccepted?.Invoke(CapturedFingerprintToReport);
    }

    public Task<string> RunCommandAsync(string command, CancellationToken ct) =>
        Task.FromResult(LoadFixture(command));

    public Task<(int ExitCode, string StdOut, string StdErr)> RunCommandWithExitCodeAsync(string command, CancellationToken ct)
    {
        var name = ToFilename(command);
        var stdout = LoadFixture(command);
        var exitFile = Path.Combine(FixtureDir, name + ".exit");
        var exit = File.Exists(exitFile) ? int.Parse(File.ReadAllText(exitFile).Trim()) : 0;
        return Task.FromResult((exit, stdout, ""));
    }

    public async ValueTask DisposeAsync()
    {
        if (DisposeDelay is { } d) await d();
    }

    private string LoadFixture(string command)
    {
        var name = ToFilename(command);
        var path = Path.Combine(FixtureDir, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Fixture missing: {path} (for command: {command})", path);
        return File.ReadAllText(path);
    }

    /// <summary>Convert e.g. "iwinfo wlan0 assoclist" -> "iwinfo-wlan0-assoclist.txt". LOCKED — must match the
    /// committed fixture filenames AND the (Plan 02-04) capture tool's output filenames.
    /// Plan 02-04 extension: also replaces ':' (forbidden in Windows filenames) and collapses runs of dashes,
    /// so e.g. "cat /etc/openwrt_release" -> "cat-etc-openwrt_release.txt" and
    /// "iw dev wlan0 station get AA:BB:CC:DD:EE:01" -> "iw-dev-wlan0-station-get-AA-BB-CC-DD-EE-01.txt".</summary>
    private static string ToFilename(string command)
    {
        var s = command.Trim().Replace(' ', '-').Replace('/', '-').Replace(':', '-');
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-");
        return s.Trim('-') + ".txt";
    }
}
