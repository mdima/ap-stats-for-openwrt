using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using Renci.SshNet.Common;

namespace ApStatsForOpenWrt.Services.Polling;

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
    private const int ChunkSize = 64;             // D-01 (revised 2026-05-11, quick task 260511-lct): groups of 64 backgrounded probes per batch. BusyBox arping is cheap (small applet, no kernel netfilter state); 64-fanout cuts a /24 sweep from 16 chunks to 4 with no observed instability on tested hardware (Operator validation 2026-05-11).
    private const int LoopBudgetSeconds = 60;     // arping/ping wait budget for one full sweep.

    // Quick task 260720-q8i: sentinel echoed between the sweep output and the appended
    // `ip neigh show` dump (same SSH round-trip) so each section is parsed by its own parser —
    // `ip neigh show` lines carry a MAC + IPv4 that ParseArpingPingOutput would otherwise mis-read.
    // A fixed ASCII marker that cannot occur in arping / ping / ip-neigh output.
    private const string NeighSentinel = "===AP-STATS-NEIGH===";

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
            // Debug session p10-trigger-and-nslookup: distinguish "SubnetScanEnabled=false on this
            // node" from "ran arping but no devices responded". Single line per probe invocation.
            log.LogInformation(
                "Subnet-sweep pass on node {Hostname} (Id={NodeId}): skipped (SubnetScanEnabled=false).",
                node.Hostname, node.Id);
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
            string subnetDescription;

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
                subnetDescription = string.Join(",", cidrs);
                // iface stays null — let the AP route picker decide on -I omission.
            }
            else
            {
                // D-04: auto-derive /24 from the SSH-peer-serving interface.
                //
                // Debug session p10-trigger-and-nslookup (2026-05-11 fourth-order fix): the original
                // implementation used `ip route get {peerIp}`, where `peerIp` is the SSH destination
                // (the AP's own LAN IP after the factory's Dns.GetHostAddressesAsync resolution at
                // Program.cs:215-220). On Linux/BusyBox, `ip route get <self-IP>` returns the
                // loopback route (`local <ip> dev lo`) for any IP bound to a local interface — so
                // the regex extracted iface='lo', `ip -4 addr show dev lo` returned `127.0.0.1/8`,
                // MaskToSlash24 derived `127.0.0.0/24`, and arping swept loopback finding zero
                // mappings on every cycle. The original design intent (see class doc comment above)
                // was to derive from the SSH-peer-serving interface — i.e. the iface that talks to
                // the SSH client (the orchestrator host), NOT the SSH destination.
                //
                // Fix: use the `$SSH_CLIENT` env var inside the SSH session. OpenSSH sets this on
                // session start, format: `<client-ip> <client-port> <server-port>` (space-separated).
                // BusyBox ash/hush supports `${SSH_CLIENT%% *}` parameter expansion to extract the
                // first field. Route to SSH_CLIENT traverses the LAN-facing interface, which is the
                // correct subnet for the arping sweep.
                //
                // The T-09-03-05 invalid-peerIp gate above (lines 37-43) is preserved as defense-in-
                // depth — peerIp is still validated on entry but no longer used in shell command
                // construction here.
                var routeOut = await session.RunCommandAsync(
                    "ip route get \"${SSH_CLIENT%% *}\"", sct);
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
                        "Node {Hostname} (Id={NodeId}): subnet auto-derivation failed (no iface from `ip route get $SSH_CLIENT`); set Node.SubnetScanRanges to enable enrichment.",
                        node.Hostname, node.Id);
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
                subnetDescription = derived;
            }

            if (scanIps.Count == 0)
            {
                // Debug session p10-trigger-and-nslookup: distinguish "expanded to empty" from
                // "ran arping but no devices responded" (the latter is the line-133 main path).
                log.LogInformation(
                    "Subnet-sweep pass on node {Hostname} (Id={NodeId}): returned 0 MAC-IP mappings — subnet {Subnet} expanded to 0 hosts; investigate.",
                    node.Hostname, node.Id, subnetDescription);
                return new SubnetSweepProbeResult.Success(node.Id,
                    new Dictionary<string, string>(StringComparer.Ordinal));
            }

            // Step 2: capability detect (D-03 — fresh per invocation, no cache).
            var capProbe = await session.RunCommandWithExitCodeAsync("command -v arping", sct);
            var useArping = capProbe.ExitCode == 0 && !string.IsNullOrWhiteSpace(capProbe.StdOut);

            // Step 3: build the AP-side batched-loop command. D-01 (revised 2026-05-11): groups
            // of 64 (quick task 260511-lct), hand-rolled shell loop, one SSH command total per
            // node per refresh tick.
            //
            // Quick task 260720-q8i: the sweep (arping when available, ping otherwise) exists only
            // to PROVOKE ARP resolution on the AP — the kernel neighbour-table read below is the
            // source of truth for MAC↔IP. This makes enrichment work even when `arping` is absent
            // (e.g. a fresh OpenWrt 25.12 node without the iputils-arping package), where the ping
            // branch's stdout carries no MAC and ParseArpingPingOutput alone always yields 0
            // mappings. Root cause + evidence:
            // .planning/quick/260720-q8i-subnetsweepprobe-universal-ip-neigh-arp-/260720-q8i-PLAN.md.
            var loopCmd = BuildSweepCommand(scanIps, iface, useArping);

            // Step 4: run the sweep AND dump the neighbour table in ONE SSH round-trip, separated by
            // a sentinel echo after the loop's final `wait`. The `ip neigh show` read picks up the
            // ARP cache the sweep just populated. The sentinel is REQUIRED for correctness: an
            // `ip neigh show` line carries BOTH a MAC and an IPv4, so ParseArpingPingOutput would
            // mis-read neigh lines (and, via its last-wins rule, let a stale cache entry clobber a
            // fresh arping reading) if the two sections were parsed as one blob. Splitting on the
            // sentinel parses each section with the parser that understands it.
            var raw = await session.RunCommandAsync(
                loopCmd + $"; echo '{NeighSentinel}'; ip neigh show", sct);

            // Split at the sentinel: sweep output (arping/ping) before it, neighbour table after.
            // Sentinel absent (e.g. an arping-only fixture, or an AP that emitted nothing after the
            // sweep) → the whole output is the sweep portion and the neighbour portion is empty, so
            // the arping happy path stays byte-identical to pre-260720-q8i behaviour.
            var sentinelIdx = raw.IndexOf(NeighSentinel, StringComparison.Ordinal);
            var sweepPortion = sentinelIdx < 0 ? raw : raw.Substring(0, sentinelIdx);
            var neighPortion = sentinelIdx < 0 ? string.Empty : raw.Substring(sentinelIdx + NeighSentinel.Length);

            // Merge: neighbour-table entries FIRST (kernel cache; ParseIpNeigh maps STALE/FAILED to
            // null, which we drop), then the live sweep result OVERWRITES on any MAC conflict so a
            // stale cache entry can never clobber a fresh reading.
            var macToIp = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in OpenWrtOutputParser.ParseIpNeigh(neighPortion, log))
            {
                if (kv.Value is not null) macToIp[kv.Key] = kv.Value;
            }
            var sweepMap = OpenWrtOutputParser.ParseArpingPingOutput(sweepPortion, log);
            foreach (var kv in sweepMap) macToIp[kv.Key] = kv.Value;
            var neighOnlyCount = macToIp.Count - sweepMap.Count;   // MACs contributed only by the neighbour table.

            // Debug session p10-trigger-and-nslookup: per-cycle resolution summary at probe
            // boundary. Mirrors the ReverseDnsProbe.cs:86-89 pattern. One structured log line per
            // probe invocation, NOT per IP probed. The greppable `found {Count} MAC-IP mappings
            // ... on subnet {Subnet}` anchor is preserved (operator log tooling); {Probe} names the
            // ARP-provocation tool and {NeighCount} shows how many mappings came only from the
            // neighbour-table read (the arping-absent path resolves entirely via {NeighCount}).
            log.LogInformation(
                "Subnet-sweep pass on node {Hostname} (Id={NodeId}): found {Count} MAC-IP mappings (provoked via {Probe}; {NeighCount} from neighbour table) on subnet {Subnet}.",
                node.Hostname, node.Id, macToIp.Count, useArping ? "arping" : "ping", neighOnlyCount, subnetDescription);

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
    /// D-01 (revised 2026-05-11, quick task 260511-lct): build the AP-side batched-loop shell
    /// command. Chunks scan IPs into groups of 64, fires backgrounded
    /// <c>arping -c1 -w1 [-I &lt;iface&gt;] $ip</c> (or <c>ping -c1 -W1 $ip</c> fallback) per chunk,
    /// awaits each chunk with <c>wait</c>, concatenates chunks with <c>;</c>. One SSH command
    /// total. The <paramref name="iface"/> argument is null in the CIDR-list branch (no auto-
    /// derivation); when null the <c>-I</c> flag is omitted and the AP's routing picks the iface
    /// for arping.
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
