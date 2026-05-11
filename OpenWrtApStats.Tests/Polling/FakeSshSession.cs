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

    /// <summary>Plan 09-03 extension: every command passed to RunCommandAsync /
    /// RunCommandWithExitCodeAsync is appended here. Used by SubnetSweepProbeTests +
    /// the SC4 isolation test (Plan 09-05) to assert the issued command set.
    /// Backward compatible — pre-09-03 consumers simply do not read this list.</summary>
    public List<string> CommandsIssued { get; } = new();

    /// <summary>Plan 09-03 extension: substring-keyed override map. When a command sent to
    /// RunCommandAsync / RunCommandWithExitCodeAsync CONTAINS one of these keys, the value
    /// is treated as the fixture-base name (the command is rewritten to that base before
    /// the existing slug rule produces the .txt filename). This is the "Per-IP arping/ping
    /// problem" workaround documented in 09-PATTERNS.md §"Per-IP arping/ping problem"
    /// (lines 459-461) — the AP-side shell loop produces ONE long command string whose
    /// slugified filename would not be predictable, so tests map e.g. "arping -c1 -w1" →
    /// "arping" and the loop's output reads from arping.txt regardless of IP list.
    /// Order of insertion matters when multiple keys could match — first match wins.</summary>
    public Dictionary<string, string> CommandOverrides { get; } = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Quick task 260511-lct: when true, the NEXT <see cref="RunCommandAsync"/> call throws
    /// <see cref="OperationCanceledException"/> WITHOUT the host CancellationToken being cancelled
    /// — simulates the linked-CTS budget firing inside SSH I/O (the ReverseDnsProbe "timeout"
    /// disposition test). The flag self-clears after firing so a follow-up command can succeed
    /// normally (currently unused, but documented for symmetry). Does not affect
    /// <see cref="RunCommandWithExitCodeAsync"/>.
    /// </summary>
    public bool ThrowOcEOnNextCommand { get; set; }

    public async Task ConnectAsync(WiFiNode node, string plaintextPassword, string? expectedFingerprint,
        Action<string>? onHostKeyAccepted, CancellationToken ct)
    {
        if (ConnectDelay is { } d) await d(ct);
        if (ThrowOnConnect is { } f) throw f();
        // First-connect TOFU simulation: invoke callback with a fake fingerprint when caller passes null.
        if (expectedFingerprint is null) onHostKeyAccepted?.Invoke(CapturedFingerprintToReport);
    }

    public Task<string> RunCommandAsync(string command, CancellationToken ct)
    {
        CommandsIssued.Add(command);
        if (ThrowOcEOnNextCommand)
        {
            ThrowOcEOnNextCommand = false;
            // Linked-CTS budget exhaustion shape — host ct stays uncancelled by design.
            throw new OperationCanceledException("Simulated linked-CTS timeout (FakeSshSession.ThrowOcEOnNextCommand).");
        }
        var lookup = ResolveLookup(command);
        return Task.FromResult(LoadFixture(lookup));
    }

    public Task<(int ExitCode, string StdOut, string StdErr)> RunCommandWithExitCodeAsync(string command, CancellationToken ct)
    {
        CommandsIssued.Add(command);
        var lookup = ResolveLookup(command);
        var name = ToFilename(lookup);
        var stdout = LoadFixture(lookup);
        var exitFile = Path.Combine(FixtureDir, name + ".exit");
        var exit = File.Exists(exitFile) ? int.Parse(File.ReadAllText(exitFile).Trim()) : 0;
        return Task.FromResult((exit, stdout, ""));
    }

    /// <summary>If <paramref name="command"/> contains any registered override key, return that
    /// override value (used as the fixture-base lookup name); otherwise return the command verbatim
    /// (preserving the pre-09-03 fixture-lookup-by-slugified-command behavior).</summary>
    private string ResolveLookup(string command)
    {
        foreach (var kv in CommandOverrides)
        {
            if (command.Contains(kv.Key, StringComparison.Ordinal))
                return kv.Value;
        }
        return command;
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
