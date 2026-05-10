using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Services.Polling.Ssh;
using Renci.SshNet.Common;

namespace OpenWrtApStats.Services.Polling;

/// <summary>
/// Phase 9 ENRICH-01..02, ENRICH-04, ENRICH-06. Active subnet-sweep probe via batched parallel
/// <c>arping</c> (with <c>ping</c> fallback). Capability detection runs once per invocation
/// (D-03 — no cross-tick cache). Subnet derivation: <see cref="WiFiNode.SubnetScanRanges"/>
/// when non-null; auto-derived /24 around the SSH-peer-serving interface when null (D-04).
/// Returns a POCO <see cref="SubnetSweepProbeResult"/>; never touches the DB (POLL-07
/// carry-over). Phase 9 SC4: orchestrator does NOT yet invoke this probe — Plan 09-05 wires
/// the call site (gated on the SC4 isolation test).
/// </summary>
public sealed class SubnetSweepProbe(ISshSession session, ILogger<SubnetSweepProbe> log)
{
    private const int ChunkSize = 16;             // D-01: groups of 16 backgrounded probes per batch.
    private const int LoopBudgetSeconds = 60;     // arping/ping wait budget for one full sweep.

    // T-09-03-05 defense-in-depth: re-validate the iface name at the shell-command construction
    // site before interpolating into `ip -4 addr show dev <iface>` (the SubnetDeriver regex
    // already restricts charset, but a typed-name passed through this path must not regress).
    private static readonly Regex IfaceNameRegex =
        new(@"^[a-zA-Z0-9._\-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<SubnetSweepProbeResult> PollAsync(WiFiNode node, string peerIp, CancellationToken ct)
    {
        // T-09-03-05 defense-in-depth: validate peerIp at PollAsync entry. The arg flows into
        // `ip route get <peerIp>` shell-command construction; reject anything that is not a
        // dotted-quad IPv4 before any SSH command issues.
        if (string.IsNullOrEmpty(peerIp) || !IPAddress.TryParse(peerIp, out _))
        {
            log.LogWarning(
                "Node {Hostname} (Id={NodeId}): SubnetSweepProbe received invalid peerIp '{PeerIp}'; refusing to sweep.",
                node.Hostname, node.Id, peerIp);
            return new SubnetSweepProbeResult.Failed(node.Id, "invalid-peer-ip");
        }

        if (!node.SubnetScanEnabled)
        {
            return new SubnetSweepProbeResult.Success(node.Id,
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(LoopBudgetSeconds));
        var sct = linkedCts.Token;

        try
        {
            // Step 1: determine scan IPs (CIDR-list branch OR auto-derivation branch).
            IReadOnlyList<string> scanIps;
            string? iface = null;

            if (!string.IsNullOrWhiteSpace(node.SubnetScanRanges))
            {
                var cidrs = SubnetDeriver.ParseCidrList(node.SubnetScanRanges, log);
                if (cidrs.Count == 0)
                {
                    log.LogInformation(
                        "Node {Hostname} (Id={NodeId}): SubnetScanRanges is set but contained no valid CIDRs; skipping sweep.",
                        node.Hostname, node.Id);
                    return new SubnetSweepProbeResult.Success(node.Id,
                        new Dictionary<string, string>(StringComparer.Ordinal));
                }
                var hosts = new List<string>();
                foreach (var cidr in cidrs)
                {
                    try { hosts.AddRange(SubnetDeriver.ExpandCidr(cidr, log)); }
                    catch (ArgumentException ex) { log.LogWarning(ex, "Failed to expand CIDR '{Cidr}'.", cidr); }
                }
                scanIps = hosts;
                // iface stays null — let the AP route picker decide on -I omission.
            }
            else
            {
                // D-04: auto-derive /24 from the SSH-peer-serving interface.
                var routeOut = await session.RunCommandAsync($"ip route get {peerIp}", sct);
                iface = SubnetDeriver.ExtractInterfaceFromIpRouteGet(routeOut);
                // T-09-03-05 defense-in-depth: even though SubnetDeriver's regex restricts the
                // iface charset, re-validate at the shell-command construction site before
                // interpolating into `ip -4 addr show dev <iface>`.
                if (iface is not null && !IfaceNameRegex.IsMatch(iface))
                {
                    log.LogInformation(
                        "Node {Hostname} (Id={NodeId}): rejected iface '{Iface}' (charset gate); skipping sweep.",
                        node.Hostname, node.Id, iface);
                    return new SubnetSweepProbeResult.Failed(node.Id, "subnet-derivation-failed");
                }
                if (iface is null)
                {
                    log.LogInformation(
                        "Node {Hostname} (Id={NodeId}): subnet auto-derivation failed (no iface from `ip route get {Peer}`); set Node.SubnetScanRanges to enable enrichment.",
                        node.Hostname, node.Id, peerIp);
                    return new SubnetSweepProbeResult.Failed(node.Id, "subnet-derivation-failed");
                }
                var addrOut = await session.RunCommandAsync($"ip -4 addr show dev {iface}", sct);
                var ipv4 = SubnetDeriver.ExtractIPv4FromIpAddrShow(addrOut);
                if (ipv4 is null)
                {
                    log.LogInformation(
                        "Node {Hostname} (Id={NodeId}): subnet auto-derivation failed (no IPv4 on iface '{Iface}'); set Node.SubnetScanRanges to enable enrichment.",
                        node.Hostname, node.Id, iface);
                    return new SubnetSweepProbeResult.Failed(node.Id, "subnet-derivation-failed");
                }
                var derived = SubnetDeriver.MaskToSlash24(ipv4);
                scanIps = SubnetDeriver.ExpandCidr(derived, log);
            }

            if (scanIps.Count == 0)
            {
                return new SubnetSweepProbeResult.Success(node.Id,
                    new Dictionary<string, string>(StringComparer.Ordinal));
            }

            // Step 2: capability detect (D-03 — fresh per invocation, no cache).
            var capProbe = await session.RunCommandWithExitCodeAsync("command -v arping", sct);
            var useArping = capProbe.ExitCode == 0 && !string.IsNullOrWhiteSpace(capProbe.StdOut);

            // Step 3: build the AP-side batched-loop command. D-01: groups of 16, hand-rolled
            // shell loop, one SSH command total per node per refresh tick.
            var loopCmd = BuildSweepCommand(scanIps, iface, useArping);

            // Step 4: run the loop, parse the interleaved stdout, return.
            var raw = await session.RunCommandAsync(loopCmd, sct);
            var macToIp = OpenWrtOutputParser.ParseArpingPingOutput(raw, log);
            return new SubnetSweepProbeResult.Success(node.Id, macToIp);
        }
        catch (SshConnectionException ex) when (ContainsAlgoNegotiationKeyword(ex.Message))
        {
            log.LogWarning(ex,
                "Node {Hostname} (Id={NodeId}): SSH server does not advertise an algorithm we accept (subnet sweep).",
                node.Hostname, node.Id);
            return new SubnetSweepProbeResult.Failed(node.Id, "algo-negotiate-failed");
        }
        catch (SocketException ex)
        {
            log.LogWarning(ex,
                "Node {Hostname} (Id={NodeId}): SSH connection failed during subnet sweep (network error).",
                node.Hostname, node.Id);
            return new SubnetSweepProbeResult.Failed(node.Id, "network-error");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;     // Host shutdown — propagate.
        }
        catch (OperationCanceledException ex)
        {
            log.LogWarning(ex,
                "Node {Hostname} (Id={NodeId}): subnet sweep timed out ({Budget}s).",
                node.Hostname, node.Id, LoopBudgetSeconds);
            return new SubnetSweepProbeResult.Failed(node.Id, "timeout");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Node {Hostname} (Id={NodeId}): subnet sweep failed (see exception details).",
                node.Hostname, node.Id);
            return new SubnetSweepProbeResult.Failed(node.Id, "probe-failed");
        }
    }

    /// <summary>
    /// D-01: build the AP-side batched-loop shell command. Chunks scan IPs into groups of 16,
    /// fires backgrounded <c>arping -c1 -w1 [-I &lt;iface&gt;] $ip</c> (or <c>ping -c1 -W1 $ip</c>
    /// fallback) per chunk, awaits each chunk with <c>wait</c>, concatenates chunks with <c>;</c>.
    /// One SSH command total. The <paramref name="iface"/> argument is null in the CIDR-list branch
    /// (no auto-derivation); when null the <c>-I</c> flag is omitted and the AP's routing picks
    /// the iface for arping.
    /// </summary>
    private static string BuildSweepCommand(IReadOnlyList<string> scanIps, string? iface, bool useArping)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < scanIps.Count; i += ChunkSize)
        {
            if (i > 0) sb.Append("; ");
            sb.Append("for ip in ");
            // Inline chunk emission to avoid an extra LINQ allocation per batch.
            var end = Math.Min(i + ChunkSize, scanIps.Count);
            for (var j = i; j < end; j++)
            {
                if (j > i) sb.Append(' ');
                sb.Append(scanIps[j]);
            }
            sb.Append("; do ");
            if (useArping)
            {
                sb.Append("arping -c1 -w1 ");
                if (!string.IsNullOrEmpty(iface))
                {
                    sb.Append("-I ");
                    sb.Append(iface);
                    sb.Append(' ');
                }
                sb.Append("$ip");
            }
            else
            {
                sb.Append("ping -c1 -W1 $ip");
            }
            sb.Append(" & done; wait");
        }
        return sb.ToString();
    }

    private static bool ContainsAlgoNegotiationKeyword(string? msg)
        => msg is not null
            && (msg.Contains("negotiate", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("algorithm", StringComparison.OrdinalIgnoreCase));
}
