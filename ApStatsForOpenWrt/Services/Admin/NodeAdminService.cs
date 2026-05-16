using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using Renci.SshNet.Common;

namespace ApStatsForOpenWrt.Services.Admin;

/// <summary>
/// Production implementation of <see cref="INodeAdminService"/>. v0.11 Plan 14-04. References:
/// CONTEXT 14 §D-08 (ubus mechanism; reason=5/deauth=true/ban_time=0), §D-09 (dedicated class
/// separate from NodePoller; per-call fresh <c>ISshSession</c>), §D-11 (always-try-and-handle-error;
/// no capability probe), §D-12 (four failure cases mapped to DisconnectResult variants).
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

            // CONTEXT 14 §D-08 + §Specifics #3:
            //   reason = 5 (DISASSOC_AP_BUSY)
            //   deauth = true (sticky-client friendly; upgrades disassoc -> deauth frame)
            //   ban_time = 0 (permits immediate re-association -- the OPERATOR'S INTENT;
            //                 we want roaming, not an AP-side lockout)
            // JsonSerializer.Serialize on the payload eliminates injection risk on the MAC arg.
            var payloadObj = new { addr = mac, reason = 5, deauth = true, ban_time = 0 };
            var payloadJson = JsonSerializer.Serialize(payloadObj);
            var ubusCommand = $"ubus call hostapd.{iface} del_client '{payloadJson}'";

            var ubusResult = await session.RunCommandWithExitCodeAsync(ubusCommand, sct);
            var ubusClassification = Classify(ubusResult);

            // 260516-iwf — CONTEXT 14 §D-08 amended 2026-05-16: cascade to `iw dev <iface>
            // station del <mac>` when the ubus path is classified as NotSupported. Other
            // failure classes (Success / Timeout / NoSuchStation / UnknownError) bypass the
            // fallback entirely -- they aren't "wrong mechanism" symptoms, so retrying with
            // a different mechanism would only add latency without changing the outcome.
            //
            // Research basis (quick task 260516-iwf): on AP classes where hostapd's ubus
            // object isn't registered under exactly `hostapd.<iface>` (multi-BSS virtual
            // ifaces, older OpenWRT naming like `hostapd.phy0-ap0`, or APs that simply
            // aren't running hostapd on this iface), the primary path fails with
            // UBUS_STATUS_NOT_FOUND. The cfg80211-level `iw dev <iface> station del <mac>`
            // is the universal kick path -- it's been in every OpenWRT release since Chaos
            // Calmer (2015+), and Phase 9's NodePoller already uses `iw dev <iface> station
            // dump` on the SAME iface for station enumeration, so the binary is provably
            // present and the iface is known-good for this app.
            //
            // Trade-off vs ubus: `iw station del` kernel-drops the station without sending a
            // proper deauth frame (no reason code, no deauth: true, no ban_time). Operationally
            // good enough -- once dropped from the kernel's auth table, the station re-scans
            // and may pick a better AP (the OPERATOR'S INTENT per §Specifics #3). Documented
            // here so future maintainers understand we accept the lighter cfg80211-only kick
            // on the fallback path.
            if (ubusClassification is not DisconnectResult.NotSupported)
            {
                return ubusClassification;
            }

            log.LogInformation(
                "NodeAdmin: ubus path returned NotSupported for Node {Hostname} (Id={NodeId}) Iface={Iface}; trying iw fallback (260516-iwf).",
                node.Hostname, node.Id, iface);

            var iwCommand = $"iw dev {iface} station del {mac}";
            var iwResult = await session.RunCommandWithExitCodeAsync(iwCommand, sct);

            if (iwResult.ExitCode == 0)
            {
                return new DisconnectResult.Success();
            }

            // Both paths failed -- carry both stderrs in one combined raw so the operator's
            // Serilog audit captures both failure modes in a single log line. Format is
            // verbatim what the LIVE-UAT log reader needs to diagnose the AP-specific issue.
            var ubusRaw = !string.IsNullOrEmpty(ubusResult.StdErr) ? ubusResult.StdErr
                        : !string.IsNullOrEmpty(ubusResult.StdOut) ? ubusResult.StdOut
                        : "";
            var iwRaw   = !string.IsNullOrEmpty(iwResult.StdErr) ? iwResult.StdErr
                        : !string.IsNullOrEmpty(iwResult.StdOut) ? iwResult.StdOut
                        : "";
            return new DisconnectResult.NotSupported($"ubus: {ubusRaw} ; iw: {iwRaw}");
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
    //   - exit 0 -> Success.
    //   - stderr matches any of the following -> NotSupported (D-12 case 2):
    //       * "Command not found"                  -- /bin/sh-emitted: ubus binary absent
    //       * "Object not found"                   -- older ubus variant: "Command failed: Object not found"
    //       * "Command failed: Not found"          -- UBUS_STATUS_NOT_FOUND verbatim (the hostapd.<iface>
    //                                                 object is not registered; surfaced verbatim during
    //                                                 v0.11 QUAL-02 LIVE-UAT surface (d), 2026-05-16) [260516-ncf]
    //       * "Command failed: Method not found"   -- UBUS_STATUS_METHOD_NOT_FOUND: hostapd registered but
    //                                                 del_client method missing (older hostapd builds /
    //                                                 non-AP modes) [260516-ncf]
    //       * "hostapd." AND "not found"           -- defensive co-occurrence catch-all for messages that
    //                                                 explicitly cite the hostapd ubus object by name
    //   - stderr contains "no such station" / "station not found" -> NoSuchStation.
    //   - any other non-zero -> UnknownError(stderr).
    //
    // 260516-iwf — Extracted into a helper method (was inline in DisconnectStationAsync pre-quick).
    // The cascade fix invokes Classify() on the ubus path's result to decide whether to invoke the
    // iw fallback (only when the result is NotSupported). Pure function -- no I/O, no DI.
    private static DisconnectResult Classify((int ExitCode, string StdOut, string StdErr) result)
    {
        if (result.ExitCode == 0)
        {
            return new DisconnectResult.Success();
        }

        var raw = !string.IsNullOrEmpty(result.StdErr) ? result.StdErr
                : !string.IsNullOrEmpty(result.StdOut) ? result.StdOut
                : "";

        if (raw.Contains("Command not found", StringComparison.OrdinalIgnoreCase)
         || raw.Contains("Object not found", StringComparison.OrdinalIgnoreCase)
         || raw.Contains("Command failed: Not found", StringComparison.OrdinalIgnoreCase)         // 260516-ncf: UBUS_STATUS_NOT_FOUND
         || raw.Contains("Command failed: Method not found", StringComparison.OrdinalIgnoreCase)  // 260516-ncf: UBUS_STATUS_METHOD_NOT_FOUND
         || (raw.Contains("hostapd.", StringComparison.OrdinalIgnoreCase)
             && raw.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return new DisconnectResult.NotSupported(raw);
        }

        if (raw.Contains("no such station", StringComparison.OrdinalIgnoreCase)
         || raw.Contains("station not found", StringComparison.OrdinalIgnoreCase))
        {
            return new DisconnectResult.NoSuchStation(raw);
        }

        return new DisconnectResult.UnknownError(raw);
    }
}
