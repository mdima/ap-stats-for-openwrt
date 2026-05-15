using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using Renci.SshNet.Common;

namespace ApStatsForOpenWrt.Services.Polling;

/// <summary>
/// Phase 9 ENRICH-03 + ENRICH-05. Reverse-DNS probe via ONE batched SSH command per non-empty
/// invocation (quick task 260511-lct, 2026-05-11). The AP-side shell loop concatenates
/// <c>(echo "==&lt;ip&gt;=="; nslookup -timeout=1 -retry=1 &lt;ip&gt; 2&gt;&amp;1) &amp;</c>
/// subshells in groups of 32, joined per-chunk by <c>wait</c>, joined across chunks by
/// <c>;</c>. Output is sentinel-attributed by <see cref="OpenWrtOutputParser.ParseBatchedNslookupOutput"/>
/// against background-process interleaving. Negative results (NXDOMAIN/SERVFAIL/timeout —
/// D-12) collapse to <c>Hostname = null</c> in the result map. FQDN normalization is
/// preserved via composition over the existing <see cref="OpenWrtOutputParser.ParseNslookupOutput"/>
/// single-IP parser. Returns POCO; never touches the DB (PER-04). Orchestrator wiring lands
/// in Phase 10 for the ORCH-05 cases (first INSERT / IP changed / Hostname IS NULL on refresh).
/// </summary>
public sealed class ReverseDnsProbe(ISshSession session, ILogger<ReverseDnsProbe> log)
{
    private const int BatchBudgetSeconds = 30;

    // Quick task 260511-lct: groups of 32 backgrounded nslookup subshells per chunk. nslookup
    // is heavier than arping (DNS round-trip vs local ARP); 32-fanout is well within OpenWRT
    // capability without flooding the resolver. Mirrors SubnetSweepProbe.BuildSweepCommand
    // discipline (StringBuilder, hand-rolled chunking, POSIX BusyBox ash/hush safe — no bash-isms).
    private const int ChunkSize = 32;

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

        try
        {
            // ONE SSH round-trip per non-empty probe invocation. The AP-side loop runs all
            // nslookup invocations concurrently as backgrounded subshells; each chunk waits
            // for its own subshells before the next chunk fires.
            var cmd = BuildBatchedNslookupCommand(ipList);
            string raw;
            try
            {
                raw = await session.RunCommandAsync(cmd, sct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host shutdown — propagate.
                throw;
            }
            catch (OperationCanceledException)
            {
                // Linked-CTS budget exhausted (BatchBudgetSeconds = 30) — surface as whole-probe timeout.
                log.LogWarning(
                    "Node {Hostname} (Id={NodeId}): reverse-DNS batch timed out after {Budget}s ({Total} IPs).",
                    node.Hostname, node.Id, BatchBudgetSeconds, ipList.Count);
                return new ReverseDnsProbeResult.Failed(node.Id, "timeout");
            }

            var parsed = OpenWrtOutputParser.ParseBatchedNslookupOutput(raw, log);

            // Result-map invariant (orchestrator merge step relies on this): EVERY requested IP
            // appears as a key, even if the AP-side loop truncated output or a sentinel block
            // went missing. Missing → null (negative-result equivalent).
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var ip in ipList)
            {
                result[ip] = parsed.TryGetValue(ip, out var v) ? v : null;
            }

            // Phase 10 debug session p10-trigger-and-nslookup: per-cycle resolution summary at
            // probe boundary. Discriminates among (a) Total=0 orchestrator-merge bug (this
            // branch isn't reached when Total=0 — early-return path above), (b) Total>0 ∧
            // Successful=0 runtime shape divergence, (c) Successful>0 ∧ Negative<Total
            // transient (will self-heal on next refresh). One line per probe invocation, NOT
            // per IP. Field names Successful / Total / Negative are LOAD-BEARING — operator's
            // structured-log grep tooling pins them; quick task 260511-lct preserves verbatim.
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

    /// <summary>
    /// Build the AP-side batched-loop shell command. Chunks the IPs in groups of
    /// <see cref="ChunkSize"/> (32). Each chunk is a sequence of backgrounded subshells
    /// <c>( nslookup -timeout=1 -retry=1 &lt;ip&gt; &gt; "$TMPDIR/&lt;ip&gt;" 2&gt;&amp;1 ) &amp;</c>
    /// terminated by <c>wait</c>; chunks are joined by <c>;</c>. After ALL chunks, a single
    /// sequential <c>for f in "$TMPDIR"/*; do echo "==$(basename "$f")=="; cat "$f"; done</c>
    /// loop emits sentinels + bodies non-interleaved. ONE SSH command total. POSIX BusyBox
    /// ash/hush safe — no bash-isms. IPs are interpolated verbatim — input is validated
    /// upstream (orchestrator only passes IPs sourced from station IPAddress strings parsed
    /// by the iwinfo/arping flow).
    ///
    /// History — quick task 260515-rds (2026-05-15) replaced the pre-260515 in-subshell
    /// echo shape <c>(echo "==&lt;ip&gt;=="; nslookup ... 2>&amp;1) &amp;</c>. That earlier
    /// shape claimed atomicity-via-parens, but parens give sequential ordering only WITHIN
    /// one subshell; concurrent subshells inherited a shared stdout fd back over SSH, and
    /// BusyBox <c>nslookup</c>'s multi-line writes interleaved at line granularity in the
    /// multiplexed pipe (kernel pipe atomicity is bounded by <c>PIPE_BUF</c> ~4096 B per
    /// single <c>write(2)</c>; nslookup answers exceed that across multiple writes). The
    /// parser's sentinel-slicing then attributed each answer block to whichever
    /// <c>==&lt;ip&gt;==</c> sentinel happened to land just before it — not to the IP that
    /// produced it. Operator audit on 2026-05-15 exposed 5+ ground-truth mismatches plus
    /// 9 FQDNs duplicated across 2-4 rows. See
    /// <c>.planning/debug/resolved/reverse-dns-regression.md</c> §E7 + §E8.
    ///
    /// The new shape (Option A in the debug session's §Fix direction): each backgrounded
    /// <c>nslookup</c> writes ONLY to its own <c>$TMPDIR/&lt;ip&gt;</c> path; isolation is
    /// by file path, not by stdout ordering. Cross-subshell stdout interleaving is no
    /// longer possible by construction. The post-<c>wait</c> <c>for ... cat</c> loop runs
    /// sequentially in the parent shell, so the parser sees clean non-interleaved sentinel
    /// blocks identical in shape to the test fixtures already covered by Tests 2/3/4/5 in
    /// <c>ReverseDnsProbeTests</c>. Parser (<c>OpenWrtOutputParser.ParseBatchedNslookupOutput</c>)
    /// is intentionally unchanged this quick task.
    ///
    /// <c>mktemp -d</c> is a BusyBox applet on OpenWRT 23.05+ (CONFIG_MKTEMP default).
    /// The <c>|| TMPDIR=/tmp/rdns-$$</c> fallback covers images without it; the parent
    /// shell PID (<c>$$</c>) gives multi-AP / multi-cycle isolation.
    /// </summary>
    private static string BuildBatchedNslookupCommand(IReadOnlyList<string> ipList)
    {
        var sb = new StringBuilder();

        // Per-IP capture root. `mktemp -d` is the canonical BusyBox path; the OR-fallback
        // handles images without it. Quoting `"$TMPDIR"` everywhere downstream tolerates
        // any whitespace inserted by the shell into the path.
        sb.Append("TMPDIR=$(mktemp -d 2>/dev/null) || TMPDIR=/tmp/rdns-$$; mkdir -p \"$TMPDIR\"; ");

        for (var i = 0; i < ipList.Count; i += ChunkSize)
        {
            var end = Math.Min(i + ChunkSize, ipList.Count);
            for (var j = i; j < end; j++)
            {
                if (j > i) sb.Append(' ');
                var ip = ipList[j];
                // Per-IP file redirect — each backgrounded subshell's stdout never enters
                // the shared SSH pipe. Atomicity is by path, not by ordering.
                sb.Append("( nslookup -timeout=1 -retry=1 ");
                sb.Append(ip);
                sb.Append(" > \"$TMPDIR/");
                sb.Append(ip);
                sb.Append("\" 2>&1 ) &");
            }
            sb.Append(" wait; ");
        }

        // Sequential post-wait emission — sentinels + bodies are written to the SSH pipe
        // by ONE shell process (the parent), iteratively, so the parser sees clean blocks.
        // `basename "$f"` recovers the IP from the path. `rm -rf "$TMPDIR"` is best-effort
        // (the OS reaps /tmp on reboot anyway).
        sb.Append("for f in \"$TMPDIR\"/*; do echo \"==$(basename \"$f\")==\"; cat \"$f\"; done; ");
        sb.Append("rm -rf \"$TMPDIR\"");

        return sb.ToString();
    }

    private static bool ContainsAlgoNegotiationKeyword(string? msg)
        => msg is not null
            && (msg.Contains("negotiate", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("algorithm", StringComparison.OrdinalIgnoreCase));
}
