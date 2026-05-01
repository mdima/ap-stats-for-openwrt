namespace OpenWrtApStats.Services.Nodes;

/// <summary>
/// NODE-02 / D-11 result of a Test Connection probe. The UI surface (Plan 03-04 Add/Edit
/// forms) consumes <see cref="UserMessage"/> verbatim — UI-SPEC §Copywriting Contract §Test
/// Connection lines 343-354 lock the operator-facing strings.
/// <see cref="Severity"/> drives the alert color (Success / Info / Danger).
/// </summary>
public abstract record TestConnectionOutcome(string UserMessage, OutcomeSeverity Severity)
{
    public sealed record Success(int NetworkCount, int StationCount)
        : TestConnectionOutcome(
            $"Connected. Discovered {NetworkCount} WLANs, observed {StationCount} associated stations.",
            OutcomeSeverity.Success);

    public sealed record NegotiateFailed()
        : TestConnectionOutcome(
            "This AP's SSH server does not advertise an algorithm we accept (try `opkg install openssh-server` for a modern stack).",
            OutcomeSeverity.Danger);

    public sealed record AuthFailed()
        : TestConnectionOutcome(
            "Authentication failed (check username/password).",
            OutcomeSeverity.Danger);

    public sealed record Timeout()
        : TestConnectionOutcome(
            "Connection timed out after 5 seconds (check hostname/network).",
            OutcomeSeverity.Danger);

    public sealed record DecryptFailed()
        : TestConnectionOutcome(
            "Stored password could not be decrypted (key ring may be missing or corrupt).",
            OutcomeSeverity.Danger);

    public sealed record HostKeyMismatch(string ExpectedFingerprint, string ActualFingerprint)
        : TestConnectionOutcome(
            "Host key changed since first trust — clear the known-hosts cache via Settings or this node's \"Re-trust\" button to re-trust.",
            OutcomeSeverity.Danger);

    public sealed record Busy()
        : TestConnectionOutcome(
            "Node is busy with the regular polling cycle — try again in a moment.",
            OutcomeSeverity.Info);

    public sealed record Generic(string Reason)
        : TestConnectionOutcome(
            $"Connection failed: {Reason} (see logs).",
            OutcomeSeverity.Danger);
}

public enum OutcomeSeverity
{
    Success,
    Info,
    Danger
}
