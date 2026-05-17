using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Services.Admin;

/// <summary>
/// Write-side SSH responsibility class (CONTEXT 14 §D-09). Separate from <c>NodePoller</c>
/// (which stays read-only). v0.11 Plan 14-04 introduces a single operation: disconnect a
/// station from a WLAN via <c>ubus call hostapd.&lt;iface&gt; del_client {...}</c> (D-08).
/// The service constructs a FRESH <c>ISshSession</c> per call (matches NodePoller's per-call
/// pattern, not a per-node singleton). Does NOT inject <c>IDbContextFactory&lt;DatabaseContext&gt;</c>
/// (D-09 architectural contract; pinned by ArchitectureTests).
/// </summary>
public interface INodeAdminService
{
    /// <summary>
    /// Issue <c>ubus call hostapd.&lt;iface&gt; del_client</c> against <paramref name="node"/>
    /// for station <paramref name="mac"/> on <paramref name="iface"/>. Always-try-and-handle-error
    /// posture (D-11): no capability probe; failure modes surface via <see cref="DisconnectResult"/>.
    /// SSH timeout discipline (DISC-02): 5s <c>ConnectionInfo.Timeout</c> + 5s linked CTS.
    /// </summary>
    Task<DisconnectResult> DisconnectStationAsync(
        WiFiNode node, string mac, string iface, CancellationToken ct);
}
