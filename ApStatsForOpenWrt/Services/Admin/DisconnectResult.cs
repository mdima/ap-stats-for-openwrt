namespace ApStatsForOpenWrt.Services.Admin;

/// <summary>
/// Discriminated result of <see cref="INodeAdminService.DisconnectStationAsync"/>. Five variants;
/// UI maps each to a user-visible message (DISC-05 four failure cases + success). See CONTEXT 14
/// §D-12 for the failure-case mapping.
/// </summary>
public abstract record DisconnectResult
{
    /// <summary>SSH-level ubus call succeeded (exit 0). The station SHOULD leave the WLAN on the next poll cycle.</summary>
    public sealed record Success                   : DisconnectResult;

    /// <summary>SSH 5s budget expired (linked CTS CancelAfter(5s) fired or the underlying transport timed out).</summary>
    public sealed record Timeout                   : DisconnectResult;

    /// <summary>ubus object/command not found (D-12 case 2). Covers DISC-07 capability-missing.</summary>
    public sealed record NotSupported(string Raw)  : DisconnectResult;

    /// <summary>ubus reported "no such station" / "station not found" (D-12 case 3; informational tone).</summary>
    public sealed record NoSuchStation(string Raw) : DisconnectResult;

    /// <summary>Catch-all (D-12 case 4). <c>Raw</c> is stderr or the exception message.</summary>
    public sealed record UnknownError(string Raw)  : DisconnectResult;
}
