using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling.Exceptions;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using Renci.SshNet.Common;

namespace ApStatsForOpenWrt.Services.Polling;

/// <summary>
/// One per node per cycle (constructed via the <c>Func&lt;WiFiNode, CancellationToken,
/// Task&lt;NodeProbeResult&gt;&gt;</c> probe factory in <c>PollOrchestrator</c>). Decrypts the
/// password just-in-time, opens an <see cref="ISshSession"/>, runs the command sequence, parses,
/// returns POCO <see cref="NodeProbeResult"/>. Never touches the DB (POLL-07). Always disposes
/// inside try/catch (Pitfall 5 — SSH.NET dispose-hang risk).
/// </summary>
public sealed class NodePoller(
    WiFiNode node,
    ISshSession session,
    OpenWrtAdapterCache adapterCache,
    IDataProtectionProvider dpp,
    ILogger<NodePoller> log)
{
    // POLL-03 / B2 LOCK: per-node SemaphoreSlim prevents within-cycle re-entry for the same node.
    // The global cycleGate at PollingHostedService already prevents cross-cycle overlap; this
    // belt-and-braces semaphore catches pathological within-cycle fan-out (e.g., if a future
    // refactor loosens the orchestrator's fan-out shape and accidentally spawns two NodePollers
    // for the same NodeId in one cycle).
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _perNodeGate = new();

    /// <summary>
    /// M-HRD-09 (v1.0 / plan 21-05): in-memory cleanup entry point invoked from
    /// <c>NodeRepo.DeleteAsync</c> AFTER the row deletion commits. Removes the per-node
    /// <see cref="SemaphoreSlim"/> mapping for <paramref name="nodeId"/> so a reused id gets a
    /// fresh gate, closing the stale-gate footgun. No DB write — the per-node gate is
    /// process-scoped polling-plane state only (mirrors
    /// <see cref="OpenWrtAdapterCache.ClearForNode"/>'s <c>TryRemove</c> shape). Harmless no-op
    /// when no entry exists for the id.
    /// <para>
    /// CR-01 (21-REVIEW): deliberately does NOT <c>Dispose()</c> the removed semaphore. The UI
    /// delete path and the polling fan-out are not mutually exclusive — a concurrent in-flight
    /// <see cref="PollAsync"/> may still hold this instance and will call <c>Release()</c> in its
    /// <c>finally</c>. Disposing here would throw <see cref="ObjectDisposedException"/> from that
    /// outer finally (outside the broad probe catch), faulting the cycle. An orphaned
    /// <see cref="SemaphoreSlim"/> with no materialized wait handle is GC-reclaimable, so dropping
    /// the entry alone is sufficient — and leaking one int→semaphore entry per deleted id is
    /// negligible.
    /// </para>
    /// </summary>
    internal static void RemoveNodeGate(int nodeId)
    {
        // Remove the mapping (do NOT Dispose — see XML-doc CR-01 note above).
        _perNodeGate.TryRemove(nodeId, out _);
    }

    /// <summary>
    /// Test-only observation seam for the M-HRD-09 cleanup pin: reports whether a per-node gate
    /// entry currently exists for <paramref name="nodeId"/>. The dictionary is private static;
    /// this avoids reflection in the cleanup test (<c>RemoveNodeGate</c> removes; this observes).
    /// </summary>
    internal static bool HasNodeGate(int nodeId) => _perNodeGate.ContainsKey(nodeId);

    public async Task<NodeProbeResult> PollAsync(CancellationToken ct)
    {
        // POLL-03 / B2 LOCK: acquire per-node semaphore non-blockingly.
        var gate = _perNodeGate.GetOrAdd(node.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
        {
            return new NodeProbeResult.Failed(node.Id, "within-cycle-reentry");
        }

        try
        {
            // Decrypt at boundary (SEC-07). Plaintext lives only in this stack frame.
            string plaintext;
            try
            {
                plaintext = dpp.CreateProtector("node-creds").Unprotect(node.Password);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                log.LogWarning(ex,
                    "Node {Hostname} (Id={NodeId}): failed to decrypt stored password (key ring may be missing/corrupt).",
                    node.Hostname, node.Id);
                return new NodeProbeResult.Failed(node.Id, "decrypt-failed");
            }

            // POLL-04 belt + braces.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(5));
            var sct = linkedCts.Token;

            string? capturedFingerprint = null;

            try
            {
                await session.ConnectAsync(node, plaintext,
                    expectedFingerprint: string.IsNullOrEmpty(node.HostKeyFingerprint) ? null : node.HostKeyFingerprint,
                    onHostKeyAccepted: fp => capturedFingerprint = fp,
                    ct: sct);

                // POLL-12: capability probe (cached after first poll per Pitfall 2).
                var useIwinfo = await ResolveUseIwinfoAsync(sct);

                var networks = new List<WiFiNetwork>();
                var stations = new List<WLANStation>();

                // Top-level interface listing.
                if (useIwinfo)
                {
                    var raw = await session.RunCommandAsync("iwinfo", sct);
                    networks.AddRange(OpenWrtOutputParser.ParseIwinfoSummary(raw, log));
                }
                else
                {
                    var raw = await session.RunCommandAsync("iw dev", sct);
                    networks.AddRange(OpenWrtOutputParser.ParseIwDevSummary(raw, log));
                }

                // B4 LOCK: build an iface->BSSID lookup so we can stamp each station with the
                // correct BSSID; the orchestrator resolves BSSID -> WiFiNetworkId post-UpsertMany.
                var ifaceToBssid = networks
                    .Where(n => !string.IsNullOrEmpty(n.IfaceName))
                    .ToDictionary(n => n.IfaceName!, n => n.MacAddress, StringComparer.Ordinal);

                foreach (var iface in EnumerateIfaces(networks))
                {
                    try
                    {
                        var rawAssoc = useIwinfo
                            ? await session.RunCommandAsync($"iwinfo {iface} assoclist", sct)
                            : await session.RunCommandAsync($"iw dev {iface} station dump", sct);

                        var emitted = useIwinfo
                            ? OpenWrtOutputParser.ParseIwinfoAssocList(rawAssoc, /*placeholder*/0, log)
                            : OpenWrtOutputParser.ParseIwDevStations(rawAssoc, /*placeholder*/0, log);
                        if (ifaceToBssid.TryGetValue(iface, out var bssid))
                        {
                            foreach (var s in emitted) s.Bssid = bssid;
                        }
                        stations.AddRange(emitted);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex,
                            "Node {Hostname} (Id={NodeId}): probe of iface {Iface} failed; continuing.",
                            node.Hostname, node.Id, iface);
                    }
                }

                // POLL-13: best-effort IP via ip neigh.
                try
                {
                    var rawNeigh = await session.RunCommandAsync("ip neigh show", sct);
                    var ipMap = OpenWrtOutputParser.ParseIpNeigh(rawNeigh, log);
                    foreach (var s in stations)
                    {
                        s.IPAddress = ipMap.TryGetValue(s.MacAddress, out var ip) ? ip : null;
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex,
                        "Node {Hostname} (Id={NodeId}): ip-neigh probe failed; stations will have null IPAddress.",
                        node.Hostname, node.Id);
                }

                return new NodeProbeResult.Success(node.Id, networks, stations, capturedFingerprint);
            }
            catch (HostKeyMismatchException)
            {
                // Bubble to orchestrator (D-03 logging happens there).
                throw;
            }
            catch (SshConnectionException ex) when (ContainsAlgoNegotiationKeyword(ex.Message))
            {
                log.LogWarning(ex,
                    "Node {Hostname} (Id={NodeId}): SSH server does not advertise an algorithm we accept.",
                    node.Hostname, node.Id);
                return new NodeProbeResult.Failed(node.Id, "algo-negotiate-failed");
            }
            catch (SshAuthenticationException ex)
            {
                // SEC-07: explicitly DO NOT include the password value.
                log.LogWarning(ex,
                    "Node {Hostname} (Id={NodeId}): SSH authentication failed (check stored credentials).",
                    node.Hostname, node.Id);
                return new NodeProbeResult.Failed(node.Id, "auth-failed");
            }
            catch (SocketException ex)
            {
                log.LogWarning(ex,
                    "Node {Hostname} (Id={NodeId}): SSH connection failed (network error).",
                    node.Hostname, node.Id);
                return new NodeProbeResult.Failed(node.Id, "network-error");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host shutdown — propagate.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // linkedCts (5s budget) expired.
                log.LogWarning(ex,
                    "Node {Hostname} (Id={NodeId}): SSH connection timed out (5 s).",
                    node.Hostname, node.Id);
                return new NodeProbeResult.Failed(node.Id, "timeout");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex,
                    "Node {Hostname} (Id={NodeId}): SSH probe failed (see exception details).",
                    node.Hostname, node.Id);
                return new NodeProbeResult.Failed(node.Id, "probe-failed");
            }
            finally
            {
                // POLL-14 + Pitfall 5: always dispose; ISshSession swallows hangs internally.
                try { await session.DisposeAsync(); } catch { /* belt + braces */ }
            }
        }
        finally
        {
            // POLL-03 / B2 LOCK: release per-node semaphore so the next cycle can probe this node.
            gate.Release();
        }
    }

    private async Task<bool> ResolveUseIwinfoAsync(CancellationToken ct)
    {
        var cached = adapterCache.TryGet(node.Id);
        if (cached is "iwinfo") return true;
        if (cached is "iw") return false;

        var probe = await session.RunCommandWithExitCodeAsync("command -v iwinfo", ct);
        var useIwinfo = probe.ExitCode == 0 && !string.IsNullOrWhiteSpace(probe.StdOut);
        adapterCache.Set(node.Id, useIwinfo ? "iwinfo" : "iw");
        return useIwinfo;
    }

    private static bool ContainsAlgoNegotiationKeyword(string? msg)
        => msg is not null
            && (msg.Contains("negotiate", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("algorithm", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateIfaces(IEnumerable<WiFiNetwork> networks)
    {
        // B4 LOCK: option (a). The parser populates WiFiNetwork.IfaceName ([NotMapped] transient
        // added in Plan 02-01 Task 4). NodePoller reads it directly here -- no reflection,
        // no fallback regex.
        return networks
            .Where(n => !string.IsNullOrEmpty(n.IfaceName))
            .Select(n => n.IfaceName!)
            .Distinct(StringComparer.Ordinal);
    }
}
