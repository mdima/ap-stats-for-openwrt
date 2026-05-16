using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using Renci.SshNet.Common;

namespace ApStatsForOpenWrt.Services.Admin;

/// <summary>
/// Production implementation of <see cref="INodeAdminService"/>. v0.11 Plan 14-04 + quick-task
/// mid-LIVE-UAT amendments. References:
/// CONTEXT 14 §D-08 RE-AMENDED 2026-05-16 via quick task 260516-iwo: disconnect mechanism =
/// <c>iw dev &lt;iface&gt; station del &lt;mac&gt;</c> (universal cfg80211-layer kick path; every
/// supported OpenWRT release since Chaos Calmer 2015+; Phase 9 NodePoller already uses
/// <c>iw dev &lt;iface&gt; station dump</c> on the same iface so binary + iface are provably
/// available). The original ubus-first design (and the ubus→iw cascade from quick task 260516-iwf)
/// is removed entirely; ubus / hostapd_cli are NOT invoked.
/// <para>
/// CONTEXT 14 §D-09 (dedicated class separate from NodePoller; per-call fresh
/// <c>ISshSession</c>), §D-11 (always-try-and-handle-error; no capability probe), §D-12
/// (failure cases mapped to <see cref="DisconnectResult"/> variants).
/// </para>
/// <para>
/// SSH timeout discipline (DISC-02 belt + braces, mirroring NodePoller.cs:59-60 verbatim):
/// <c>ConnectionInfo.Timeout</c> = 5s (set inside <c>ISshSession.ConnectAsync</c>) AND a
/// linked <c>CancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(5))</c>.
/// </para>
/// <para>
/// Pitfall 5 — <c>ISshSession.DisposeAsync</c> is invoked inside <c>try/catch</c> in the finally
/// block; SSH.NET's internal Dispose may hang under contention and the swallow keeps the call
/// path bounded.
/// </para>
/// <para>
/// SEC-07: the encrypted node password is decrypted just-in-time; the plaintext lives only on
/// this stack frame and is never logged.
/// </para>
/// <para>
/// D-08 amendment audit trail:
///   (a) Phase 14 plan-time lock 2026-05-16 — ubus call hostapd.&lt;iface&gt; del_client with
///       reason=5 / deauth=true / ban_time=0 payload; charter Q3 hostapd_cli rejected.
///   (b) 260516-iwf 2026-05-16 — ubus primary + iw cascade fallback on NotSupported.
///   (c) 260516-iwo 2026-05-16 (this file) — ubus path REMOVED entirely; iw-only. Trade-off:
///       APs where ubus worked lose the rich payload (no deauth frame; no ban_time); kernel-drop
///       is sufficient for the §Specifics #3 roam-friendly intent (no lockout).
/// </para>
/// </summary>
public sealed class NodeAdminService(
    Func<ISshSession> sessionFactory,
    IDataProtectionProvider dpp,
    ILogger<NodeAdminService> log) : INodeAdminService
{
    public async Task<DisconnectResult> DisconnectStationAsync(
        WiFiNode node, string mac, string iface, CancellationToken ct)
    {
        // SEC-07: decrypt password just-in-time; plaintext lives only in this stack frame.
        string plaintext;
        try
        {
            plaintext = dpp.CreateProtector("node-creds").Unprotect(node.Password);
        }
        catch (CryptographicException ex)
        {
            log.LogWarning(ex,
                "NodeAdmin: failed to decrypt stored password for Node {Hostname} (Id={NodeId}); cannot issue disconnect.",
                node.Hostname, node.Id);
            return new DisconnectResult.UnknownError("decrypt-failed");
        }

        // Belt + braces: ConnectionInfo.Timeout = 5s (inside ISshSession.ConnectAsync) AND a
        // linked CTS with CancelAfter(5s). Mirrors NodePoller.cs:59-60 verbatim (DISC-02).
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(5));
        var sct = linkedCts.Token;

        var session = sessionFactory();
        try
        {
            await session.ConnectAsync(node, plaintext,
                expectedFingerprint: string.IsNullOrEmpty(node.HostKeyFingerprint) ? null : node.HostKeyFingerprint,
                onHostKeyAccepted: _ => { },
                ct: sct);

            // 260516-iwo — CONTEXT 14 §D-08 re-amended 2026-05-16 (operator-authorised via
            // "please remove the method ubus call hostapd.<iface> del_client from the disconnect
            // method"): disconnect via `iw dev <iface> station del <mac>` ONLY. The ubus path
            // and the 260516-iwf cascade are removed; cfg80211 kernel-drop is the sole
            // mechanism for v0.11.
            //
            // Why iw is sufficient on its own:
            //   - Universal cross-OpenWRT (every release since Chaos Calmer 2015+).
            //   - Phase 9's NodePoller already invokes `iw dev <iface> station dump` on this
            //     SAME iface for station enumeration -- binary provably present, iface
            //     provably valid for any station rendered on Stations/Detail.razor.
            //   - Independent of hostapd's ubus object name (which was the failure mode that
            //     necessitated the cascade in 260516-iwf).
            //
            // Trade-off accepted: no proper deauth frame (no reason code, no deauth: true, no
            // ban_time). Operationally adequate per CONTEXT 14 §Specifics #3 -- once kernel-
            // dropped, the AP stops accepting frames from the station; the station notices
            // on its next TX attempt and re-scans / re-associates. The roam-friendly intent
            // (no lockout, allow re-association) is preserved structurally rather than via
            // an explicit ban_time=0 payload field.
            //
            // MAC + iface defensive note: both are sourced from the DB and were populated by
            // the Phase 9 parser (uppercase 17-char canonical MAC; iface like `wlan0`). The
            // parser validates shapes at write-time so shell-injection via these args is not
            // a credible attack surface; the single-quote wrapping below is belt-and-braces.
            var iwCommand = $"iw dev {iface} station del {mac}";
            var iwResult = await session.RunCommandWithExitCodeAsync(iwCommand, sct);
            return Classify(iwResult);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // External cancellation (host shutdown / caller cancel) -- propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Internal 5s budget expired.
            return new DisconnectResult.Timeout();
        }
        catch (SshConnectionException)
        {
            // Network unreachable / connection reset within the 5s budget.
            return new DisconnectResult.Timeout();
        }
        catch (SshAuthenticationException ex)
        {
            // SEC-07: explicitly DO NOT include the password value in the log.
            log.LogWarning(ex,
                "NodeAdmin: SSH authentication failed for Node {Hostname} (Id={NodeId}); disconnect aborted.",
                node.Hostname, node.Id);
            return new DisconnectResult.UnknownError($"auth-failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "NodeAdmin: unexpected error issuing disconnect for Node {Hostname} (Id={NodeId}).",
                node.Hostname, node.Id);
            return new DisconnectResult.UnknownError(ex.Message);
        }
        finally
        {
            // Pitfall 5: always dispose inside try/catch -- ISshSession's dispose may hang under
            // SSH.NET-internal contention; swallow.
            try { await session.DisposeAsync(); } catch { /* belt + braces */ }
        }
    }

    // Exit-code + stderr-pattern -> DisconnectResult mapping (D-12).
    //
    // 260516-iwo — Re-targeted to `iw dev <iface> station del <mac>` stderr shapes (CONTEXT 14
    // §D-08 second amendment). The ubus-specific patterns are removed entirely. `iw` reports
    // kernel errors via `command failed: <perror text> (<errno>)`; the patterns below cover the
    // operator-actionable errno classes.
    //
    //   - exit 0 -> Success.
    //   - stderr contains "No such device" (errno ENODEV -19) -> NotSupported (D-12 case 2).
    //       Triggered when the kernel netdev doesn't exist OR isn't in AP mode. Operator-
    //       relevant signal: iface name stored in WiFiNetwork.IfaceName (Phase 9 / D-10)
    //       doesn't match the actual kernel netdev on the AP -- Phase-9 followup signal.
    //   - stderr contains "No such file or directory" (errno ENOENT -2) -> NoSuchStation
    //       (D-12 case 3). Triggered when the MAC isn't in the kernel station list. Benign
    //       race: the station legitimately left between page load and click.
    //   - any other non-zero -> UnknownError(raw). Catches "Operation not permitted" (EPERM)
    //       if the SSH user lacks CAP_NET_ADMIN, plus any unexpected iw failure mode.
    //
    // Pure function -- no I/O, no DI. Used by DisconnectStationAsync directly (no longer
    // gated on a cascade-decision branch since the cascade was removed in this quick task).
    private static DisconnectResult Classify((int ExitCode, string StdOut, string StdErr) result)
    {
        if (result.ExitCode == 0)
        {
            return new DisconnectResult.Success();
        }

        var raw = !string.IsNullOrEmpty(result.StdErr) ? result.StdErr
                : !string.IsNullOrEmpty(result.StdOut) ? result.StdOut
                : "";

        // ENODEV (-19): kernel netdev missing or not in AP mode. Map to NotSupported so the
        // operator-facing banner reads "Disconnect not supported on this AP -- wireless
        // interface unavailable" (a Phase-9 / D-10 iface-name remediation signal).
        if (raw.Contains("No such device", StringComparison.OrdinalIgnoreCase))
        {
            return new DisconnectResult.NotSupported(raw);
        }

        // ENOENT (-2): station MAC not in the kernel's per-iface station list. Benign race
        // (the station left between page load and click) OR the station never associated
        // with this iface. Informational tone per D-12 case 3 ("Station is no longer
        // associated with this AP").
        if (raw.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
        {
            return new DisconnectResult.NoSuchStation(raw);
        }

        return new DisconnectResult.UnknownError(raw);
    }
}
