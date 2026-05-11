using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Services.Polling.Ssh;
using Renci.SshNet.Common;

namespace OpenWrtApStats.Services.Polling;

/// <summary>
/// Phase 9 ENRICH-03 + ENRICH-05. Reverse-DNS probe via per-IP <c>nslookup</c> invocation against
/// the AP's system resolver (D-12 — plain <c>nslookup &lt;ip&gt;</c>, no explicit server arg). Negative
/// results (NXDOMAIN/SERVFAIL/timeout) collapse to <c>Hostname = null</c> in the result map.
/// FQDN normalization happens in <see cref="OpenWrtOutputParser.ParseNslookupOutput"/> before
/// the POCO leaves this worker. Returns POCO; never touches the DB. Phase 9 SC4: orchestrator
/// does NOT yet invoke this probe. Phase 10 wires it in for the ORCH-05 cases (first INSERT /
/// IP changed / Hostname IS NULL on refresh).
/// </summary>
public sealed class ReverseDnsProbe(ISshSession session, ILogger<ReverseDnsProbe> log)
{
    private const int BatchBudgetSeconds = 30;

    public async Task<ReverseDnsProbeResult> PollAsync(
        WiFiNode node, IEnumerable<string> ips, CancellationToken ct)
    {
        var ipList = ips as IReadOnlyList<string> ?? ips.ToList();
        if (ipList.Count == 0)
        {
            return new ReverseDnsProbeResult.Success(node.Id,
                new Dictionary<string, string?>(StringComparer.Ordinal));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(BatchBudgetSeconds));
        var sct = linkedCts.Token;

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);

        try
        {
            foreach (var ip in ipList)
            {
                string raw;
                try
                {
                    raw = await session.RunCommandAsync($"nslookup {ip}", sct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Host shutdown — propagate.
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Per-IP timeout (linked CTS budget exhausted) — surface as whole-probe timeout.
                    log.LogWarning(
                        "Node {Hostname} (Id={NodeId}): reverse-DNS batch timed out at IP {Ip} ({Done}/{Total} done).",
                        node.Hostname, node.Id, ip, result.Count, ipList.Count);
                    return new ReverseDnsProbeResult.Failed(node.Id, "timeout");
                }
                catch (Exception ex)
                {
                    // Per-IP failure: log + null hostname, continue. Phase 2 D-05 / Phase 9 D-02
                    // tolerance pattern carried forward.
                    log.LogWarning(ex,
                        "Node {Hostname} (Id={NodeId}): nslookup failed for IP {Ip}; storing null Hostname.",
                        node.Hostname, node.Id, ip);
                    result[ip] = null;
                    continue;
                }

                var fqdn = OpenWrtOutputParser.ParseNslookupOutput(raw, log);
                result[ip] = fqdn;     // null for NXDOMAIN/SERVFAIL/timeout per D-12.
            }

            // Phase 10 debug session p10-trigger-and-nslookup: per-cycle resolution summary at
            // probe boundary. Discriminates among (a) Total=0 orchestrator-merge bug, (b) Total>0
            // ∧ Successful=0 runtime shape divergence, (c) Successful>0 ∧ Negative<Total transient
            // (will self-heal on next refresh). One line per probe invocation, NOT per IP.
            var totalCount = ipList.Count;
            var successfulCount = 0;
            foreach (var kv in result)
            {
                if (kv.Value is not null) successfulCount++;
            }
            var negativeCount = totalCount - successfulCount;
            log.LogInformation(
                "Reverse-DNS pass on node {Hostname} (Id={NodeId}): resolved {Successful}/{Total} IPs ({Negative} NXDOMAIN/timeout).",
                node.Hostname, node.Id, successfulCount, totalCount, negativeCount);

            return new ReverseDnsProbeResult.Success(node.Id, result);
        }
        catch (SshConnectionException ex) when (ContainsAlgoNegotiationKeyword(ex.Message))
        {
            log.LogWarning(ex,
                "Node {Hostname} (Id={NodeId}): SSH server does not advertise an algorithm we accept (reverse DNS).",
                node.Hostname, node.Id);
            return new ReverseDnsProbeResult.Failed(node.Id, "algo-negotiate-failed");
        }
        catch (SocketException ex)
        {
            log.LogWarning(ex,
                "Node {Hostname} (Id={NodeId}): SSH connection failed during reverse-DNS batch (network error).",
                node.Hostname, node.Id);
            return new ReverseDnsProbeResult.Failed(node.Id, "network-error");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Node {Hostname} (Id={NodeId}): reverse-DNS batch failed (see exception details).",
                node.Hostname, node.Id);
            return new ReverseDnsProbeResult.Failed(node.Id, "probe-failed");
        }
    }

    private static bool ContainsAlgoNegotiationKeyword(string? msg)
        => msg is not null
            && (msg.Contains("negotiate", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("algorithm", StringComparison.OrdinalIgnoreCase));
}
