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
            var command = $"ubus call hostapd.{iface} del_client '{payloadJson}'";

            var result = await session.RunCommandWithExitCodeAsync(command, sct);

            // Exit-code + stderr-pattern mapping (D-12):
            //   - exit 0 -> Success.
            //   - stderr contains "Command not found" / "Object not found"
            //     OR ("hostapd." AND "not found") -> NotSupported.
            //   - stderr contains "no such station" / "station not found" -> NoSuchStation.
            //   - any other non-zero -> UnknownError(stderr).
            if (result.ExitCode == 0)
            {
                return new DisconnectResult.Success();
            }

            var raw = !string.IsNullOrEmpty(result.StdErr) ? result.StdErr
                    : !string.IsNullOrEmpty(result.StdOut) ? result.StdOut
                    : "";

            if (raw.Contains("Command not found", StringComparison.OrdinalIgnoreCase)
             || raw.Contains("Object not found", StringComparison.OrdinalIgnoreCase)
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
}
