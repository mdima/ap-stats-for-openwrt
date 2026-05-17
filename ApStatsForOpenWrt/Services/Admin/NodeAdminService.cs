using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using Renci.SshNet.Common;

namespace ApStatsForOpenWrt.Services.Admin;

/// <summary>
/// Production implementation of <see cref="INodeAdminService"/>. v0.11 Plan 14-04 + quick-task
/// mid-LIVE-UAT amendments. References:
/// CONTEXT 14 §D-08 RE-AMENDED 2026-05-17 via quick task 260517-hcd: disconnect mechanism =
/// belt-and-suspenders triple cascade per disconnect call. Step 1 <c>ubus call hostapd.&lt;iface&gt;
/// del_client</c> (best-effort, log only). Step 2 <c>iw dev &lt;iface&gt; station del &lt;mac&gt;</c>
/// (best-effort, log only). Step 3 <c>hostapd_cli -p &lt;socket-dir&gt; -i &lt;iface&gt;
/// deauthenticate &lt;mac&gt;</c> (AUTHORITATIVE — its outcome determines the returned
/// <see cref="DisconnectResult"/>). All three steps fire unconditionally per disconnect call.
/// <para>
/// CONTEXT 14 §D-09 (dedicated class separate from NodePoller; per-call fresh
/// <c>ISshSession</c>), §D-11 (always-try-and-handle-error; no capability probe), §D-12
/// (failure cases mapped to <see cref="DisconnectResult"/> variants).
/// </para>
/// <para>
/// SSH timeout discipline (DISC-02 belt + braces, mirroring NodePoller.cs:59-60 verbatim):
/// <c>ConnectionInfo.Timeout</c> = 5s (set inside <c>ISshSession.ConnectAsync</c>) AND a
/// linked <c>CancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(5))</c>. The 5s budget
/// covers ALL THREE STEPS combined (not 5s per step) — on a fast 1-2ms RTT LAN the three round
/// trips fit comfortably; on a slow/unreachable AP, OperationCanceledException from any step
/// short-circuits to <c>DisconnectResult.Timeout</c> via the outer catch.
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
///   (c) 260516-iwo 2026-05-16 — ubus path removed entirely; iw-only.
///   (d) 260517-hcd 2026-05-17 (this file) — belt-and-suspenders triple cascade: ubus + iw as
///       best-effort log-only steps, hostapd_cli deauthenticate as the AUTHORITATIVE success
///       determinant. Operator-authorised against a verified-working manual SSH session
///       against the live fleet (<c>hostapd_cli -p /var/run/hostapd-wifi0 -i wlan0
///       deauthenticate &lt;MAC&gt;</c> succeeded on 3 stations where iw silently no-op'd).
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
        // The 5s budget covers ALL THREE cascade steps combined (NOT 5s per step) — on a fast
        // LAN the three round trips fit comfortably; on a slow/unreachable AP, OCE from any
        // step short-circuits to Timeout via the outer catch.
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

            // 260517-hcd — CONTEXT 14 §D-08 fourth amendment 2026-05-17 (operator-authorised
            // against a verified-working manual SSH session against the live fleet): belt-and-
            // suspenders triple cascade. Three SSH commands are issued in fixed order per
            // disconnect call. Steps 1 and 2 are best-effort: their exit codes / raw outputs are
            // logged for forensics, but they do NOT short-circuit the call (a non-zero exit from
            // ubus or iw does not prevent step 3 from running). Step 3 is AUTHORITATIVE — its
            // outcome alone determines the returned DisconnectResult.
            //
            // Why belt-and-suspenders (operator-verbatim 2026-05-17): "fails or not fails we
            // must try this other command I tried myself and I can guarantee it works." Protects
            // against three observed failure modes:
            //   1. Silent-exit-0 (260516-iwo): `iw station del` exits 0 on hostapd-managed APs
            //      without actually deauth-ing. Hostapd_cli still gets the deauth done.
            //   2. ubus-different-object-name (260516-ncf, 260516-iwf): the operator's AP class
            //      has hostapd's ubus object under a name that doesn't match `hostapd.<iface>`.
            //      ubus is still tried first because on future AP classes where it does work,
            //      the rich payload (reason=5, deauth=true frame, ban_time=0) lands "for free."
            //   3. Heterogeneous-fleet: on a mixed fleet, one of the three steps will always
            //      be the path that delivers the deauth.

            // Step 1 — ubus (best-effort). JSON payload eliminates shell-quoting risk on the
            // MAC arg. On the operator's fleet this typically returns `Command failed: Not
            // found` (the hostapd ubus object isn't registered as `hostapd.<iface>`).
            var ubusPayload = JsonSerializer.Serialize(new
            {
                addr = mac,
                reason = 5,
                deauth = true,
                ban_time = 0,
            });
            var ubusCommand = $"ubus call hostapd.{iface} del_client '{ubusPayload}'";
            var ubusResult = await session.RunCommandWithExitCodeAsync(ubusCommand, sct);
            log.LogInformation(
                "NodeAdmin disconnect step 1 (ubus, best-effort): Node={Hostname} (Id={NodeId}) Iface={Iface} Mac={Mac} ExitCode={ExitCode} Raw={Raw}",
                node.Hostname, node.Id, iface, mac, ubusResult.ExitCode,
                !string.IsNullOrEmpty(ubusResult.StdErr) ? ubusResult.StdErr : ubusResult.StdOut);

            // Step 2 — iw (best-effort). On the operator's fleet this exits 0 but is a silent
            // no-op for hostapd-managed associations (the cfg80211 kernel-drop is suppressed by
            // hostapd's own STA list). On cfg80211-only firmwares (no hostapd) it does deauth.
            var iwCommand = $"iw dev {iface} station del {mac}";
            var iwResult = await session.RunCommandWithExitCodeAsync(iwCommand, sct);
            log.LogInformation(
                "NodeAdmin disconnect step 2 (iw, best-effort): Node={Hostname} (Id={NodeId}) Iface={Iface} Mac={Mac} ExitCode={ExitCode} Raw={Raw}",
                node.Hostname, node.Id, iface, mac, iwResult.ExitCode,
                !string.IsNullOrEmpty(iwResult.StdErr) ? iwResult.StdErr : iwResult.StdOut);

            // Step 3 — hostapd_cli (AUTHORITATIVE). Socket-directory discovery via shell-glob
            // covers two layouts:
            //   - /var/run/hostapd-<radio>/<iface>  (operator's fleet, e.g. wifi0 / wifi1)
            //   - /var/run/hostapd/<iface>          (stock OpenWRT default)
            // If neither layout has a socket file matching the iface, the loop emits
            // `no hostapd socket for <iface>` to stderr and exits 127 — our sentinel for
            // NotSupported. `exec hostapd_cli …` replaces the shell with the cli so the cli's
            // own exit code passes through cleanly (no `$?` indirection).
            //
            // MAC + iface defensive note: both are sourced from the DB and were populated by
            // the Phase 9 parser (uppercase 17-char canonical MAC; iface like `wlan0`). The
            // parser validates shapes at write-time so shell-injection via these args is not
            // a credible attack surface; no MAC regex guard at this entry point.
            var hostapdCommand =
                $"for d in /var/run/hostapd-* /var/run/hostapd; do " +
                $"if [ -S \"$d/{iface}\" ]; then " +
                $"exec hostapd_cli -p \"$d\" -i {iface} deauthenticate {mac}; " +
                $"fi; " +
                $"done; " +
                $"echo \"no hostapd socket for {iface}\" >&2; " +
                $"exit 127";
            var hostapdResult = await session.RunCommandWithExitCodeAsync(hostapdCommand, sct);
            log.LogInformation(
                "NodeAdmin disconnect step 3 (hostapd_cli, AUTHORITATIVE): Node={Hostname} (Id={NodeId}) Iface={Iface} Mac={Mac} ExitCode={ExitCode} Stdout={Stdout} Stderr={Stderr}",
                node.Hostname, node.Id, iface, mac, hostapdResult.ExitCode, hostapdResult.StdOut, hostapdResult.StdErr);

            return ClassifyHostapdCli(hostapdResult);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // External cancellation (host shutdown / caller cancel) -- propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Internal 5s budget expired (covers all 3 cascade steps combined).
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

    // hostapd_cli output -> DisconnectResult mapping (D-12). hostapd_cli has a specific protocol:
    //   - On success: stdout = "OK\n", exit 0.
    //   - On unknown station / unknown command: stdout = "FAIL\n", exit 0 (NOT non-zero).
    //   - On socket-not-found: hostapd_cli's own diagnostic to stderr "Failed to connect to
    //     hostapd - wpa_ctrl_open: <strerror>", non-zero exit.
    //   - Our shell-glob sentinel: when the loop finds no socket file at all, it emits
    //     "no hostapd socket for <iface>" to stderr and exits 127.
    //
    // OperationCanceledException / SshConnectionException are handled by the outer catch
    // (mapped to Timeout); this method only sees exit-code + stdout + stderr from a normal
    // command return.
    //
    // Pure function -- no I/O, no DI.
    private static DisconnectResult ClassifyHostapdCli((int ExitCode, string StdOut, string StdErr) result)
    {
        var raw = !string.IsNullOrEmpty(result.StdErr) ? result.StdErr
                : !string.IsNullOrEmpty(result.StdOut) ? result.StdOut
                : "";

        if (result.ExitCode == 0)
        {
            // hostapd_cli's `FAIL\n` shape on unknown station / unknown command. Map to
            // NoSuchStation per D-12 case 3 (informational tone). The trim guards against
            // a stray leading space if any future hostapd_cli build adds one.
            if (result.StdOut is { Length: > 0 } stdout
                && stdout.TrimStart().StartsWith("FAIL", StringComparison.OrdinalIgnoreCase))
            {
                return new DisconnectResult.NoSuchStation(raw);
            }

            // Default for exit 0: Success. Covers the "OK\n" happy path AND defensive
            // synthetic test cases that supply empty stdout (single-canned setup).
            return new DisconnectResult.Success();
        }

        // Our shell-glob sentinel: no socket file under /var/run/hostapd-* or /var/run/hostapd
        // for this iface. The AP doesn't have hostapd's ctrl-iface socket exposed where we
        // expect it -- NotSupported (D-12 case 2; D-11 always-try-and-handle-error posture).
        if (result.ExitCode == 127
            && raw.Contains("no hostapd socket for", StringComparison.OrdinalIgnoreCase))
        {
            return new DisconnectResult.NotSupported(raw);
        }

        // hostapd_cli's own connect-failure diagnostic (socket file exists but hostapd isn't
        // listening on the other end -- daemon stopped, permission denied, etc.). Same
        // operator-facing semantics as the sentinel above: the AP cannot service deauth via
        // hostapd_cli right now.
        if (raw.Contains("Failed to connect to hostapd", StringComparison.OrdinalIgnoreCase))
        {
            return new DisconnectResult.NotSupported(raw);
        }

        return new DisconnectResult.UnknownError(raw);
    }
}
