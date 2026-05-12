namespace ApStatsForOpenWrt.Services.Nodes;

/// <summary>WLAN-01 result of one Discover probe (D-12 / D-15).</summary>
public abstract record DiscoveryOutcome(string UserMessage, OutcomeSeverity Severity)
{
    /// <summary><paramref name="DiscoveredCount"/> = total visible WLANs after this probe.
    /// <paramref name="DisabledOnAbsence"/> = how many existing rows became Enabled=false
    /// because the probe didn't see them (D-15). NULL = "we don't track count" (Phase 3
    /// implementation may compute this lazily).</summary>
    public sealed record Success(int DiscoveredCount, int? DisabledOnAbsence)
        : DiscoveryOutcome(
            BuildSuccessCopy(DiscoveredCount, DisabledOnAbsence),
            OutcomeSeverity.Success);

    public sealed record NodeNotFound()
        : DiscoveryOutcome("Node not found.", OutcomeSeverity.Danger);

    public sealed record HostKeyMismatch()
        : DiscoveryOutcome(
            "Host key changed since first trust — clear the known-hosts cache via Settings or this node's \"Re-trust\" button to re-trust.",
            OutcomeSeverity.Danger);

    public sealed record Busy()
        : DiscoveryOutcome(
            "Node is busy with the regular polling cycle — try again in a moment.",
            OutcomeSeverity.Info);

    public sealed record Generic(string Reason)
        : DiscoveryOutcome($"Discovery failed: {Reason} (see logs).", OutcomeSeverity.Danger);

    // UI-SPEC §Node Detail §Discover SUCCESS banner copy (lines 384-388)
    private static string BuildSuccessCopy(int n, int? disabled)
    {
        var disabledClause = disabled is { } d && d >= 1
            ? $", {d} previously-discovered WLANs were marked disabled because they are no longer broadcast"
            : "";
        return $"Discovery complete. {n} WLANs are now visible{disabledClause}.";
    }
}
