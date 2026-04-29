namespace OpenWrtApStats.Services.Polling.Exceptions;

/// <summary>
/// Thrown by RealSshSession.HostKeyReceived when the reported host key does NOT match the stored
/// WiFiNode.HostKeyFingerprint. Caught by PollOrchestrator.RunNodeProbeAsync; logged at Warning per
/// D-03; node skipped this cycle. No suspended-state machinery — every subsequent tick retries
/// until SET-03 (Phase 3) clears the column.
/// </summary>
public sealed class HostKeyMismatchException : Exception
{
    public string ExpectedFingerprint { get; }
    public string ActualFingerprint { get; }

    public HostKeyMismatchException(string expected, string actual)
        : base($"SSH host key mismatch — expected '{expected}', got '{actual}'.")
    {
        ExpectedFingerprint = expected;
        ActualFingerprint = actual;
    }
}
