using OpenWrtApStats.Data.Models;

namespace OpenWrtApStats.Services.Polling.Ssh;

/// <summary>
/// Abstracts an SSH session against one OpenWRT node. <c>RealSshSession</c> wraps SSH.NET 2025.1.0
/// (lands in Plan 02-03); <c>FakeSshSession</c> (test-only, under <c>OpenWrtApStats.Tests/Polling/</c>)
/// replays canned strings from golden fixtures (D-09). Implementations MUST NOT inject DbContext —
/// PER-04 is implicit (the interface has no DB shape). The seam exists so CI never runs live SSH.
/// Throws <see cref="OpenWrtApStats.Services.Polling.Exceptions.HostKeyMismatchException"/> on
/// host-key mismatch (D-03 contract).
/// </summary>
public interface ISshSession : IAsyncDisposable
{
    /// <summary>
    /// Connect with TOFU/mismatch handling.
    /// If <paramref name="expectedFingerprint"/> is null, the implementation accepts whatever host key the server presents (D-02 first-connect TOFU) and invokes <paramref name="onHostKeyAccepted"/> with the captured fingerprint.
    /// If <paramref name="expectedFingerprint"/> is non-null, the implementation requires a strict-equal match (StringComparison.Ordinal) and throws <c>HostKeyMismatchException</c> on mismatch (D-03).
    /// </summary>
    Task ConnectAsync(
        WiFiNode node,
        string plaintextPassword,
        string? expectedFingerprint,
        Action<string>? onHostKeyAccepted,
        CancellationToken ct);

    /// <summary>Run a command and return stdout. Throws on connection-level error.</summary>
    Task<string> RunCommandAsync(string command, CancellationToken ct);

    /// <summary>Run a command and surface exit code (used by the `command -v iwinfo` capability probe — POLL-12 / Pitfall 2).</summary>
    Task<(int ExitCode, string StdOut, string StdErr)> RunCommandWithExitCodeAsync(string command, CancellationToken ct);
}
