using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Polling;
using OpenWrtApStats.Services.Polling.Ssh;
using OpenWrtApStats.Services.Settings;
using OpenWrtApStats.Tests.TestSupport;
using Xunit;

namespace OpenWrtApStats.Tests.Polling;

/// <summary>
/// Phase 10 SC1 acceptance-signal gate (ROADMAP §"Phase 10: Orchestrate &amp; Surface" Success Criteria).
/// Verifiable form of the v0.9.1 acceptance signal:
///   - ≥80% of associated WLAN stations show non-null IPAddress within one full enrichment cycle.
///   - ≥80% of stations-with-IP show non-null Hostname (assuming the network DNS chain has DHCP
///     leases registered).
/// Runs through PollOrchestrator.RunCycleAsync — the same coordinator transaction the production
/// path takes. The synthetic 20-station fixture is sized so the test fails red on regression in
/// any link of the chain (refresh trigger, enrichmentFactory wiring, sweep parsing, DNS parsing,
/// orchestrator merge, UpsertCycleAsync).
/// </summary>
[CollectionDefinition("Phase10AcceptanceSignalTests", DisableParallelization = true)]
public sealed class Phase10AcceptanceSignalTestsCollection { }

[Collection("Phase10AcceptanceSignalTests")]
public sealed class Phase10AcceptanceSignalTests(SqliteInMemoryFixture fixture, ITestOutputHelper output)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task EnrichmentCycle_AchievesGoalRate_AtLeast80PercentIPAndHostname()
    {
        var ct = TestContext.Current.CancellationToken;

        // ARRANGE — seed 1 node + 1 network + 20 stations, LastEnrichmentRefreshAt=null forces
        // the composite refresh trigger to fire on the first cycle.
        await using (var db = _fixture.CreateContext())
        {
            // CompositionRootValidationTests-style setup: encrypted password isn't needed here
            // because the enrichmentFactory we install bypasses the SSH connect path entirely
            // (uses ScriptedSshSession directly).
            var node = new WiFiNode
            {
                Hostname = $"sc1-{Guid.NewGuid():N}",
                Username = "operator",
                Password = "",
                Status = Status.Offline,
                SubnetScanEnabled = true,
                SubnetScanRanges = "10.99.0.0/24",
                LastEnrichmentRefreshAt = null,
            };
            db.WiFiNodes.Add(node);
            await db.SaveChangesAsync(ct);

            var network = new WiFiNetwork
            {
                WiFiNodeId = node.Id,
                MacAddress = $"FE:00:00:99:99:01",
                SSID = "TestSSID",
            };
            db.WiFiNetworks.Add(network);
            await db.SaveChangesAsync(ct);

            // Capture once for the closures below.
            _seedNodeId = node.Id;
            _seedNetworkId = network.Id;
            _seedNetworkBssid = network.MacAddress;
        }

        // 20 stations, assigned MACs `AA:BB:CC:DD:00:01..14` (uppercase byte 0 = 0xAA → U/L bit = 0,
        // so IsRandomMac = false on insert). All start without IPAddress / Hostname.
        var macs = Enumerable.Range(1, 20).Select(i => $"AA:BB:CC:DD:00:{i:X2}").ToList();

        // SCRIPTED SSH SESSION — handles arping (returns 17 of 20 MAC↔IP pairs, ≥85%) and
        // nslookup (resolves 14 of 17 IPs, ≥82% of stations-with-IP). The 3 unresolved IPs
        // emit NXDOMAIN so ParseNslookupOutput collapses them to null.
        var ipsByMac = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < 17; i++)
        {
            ipsByMac[macs[i]] = $"10.99.0.{i + 10}";
        }
        var hostsByIp = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = 0; i < 14; i++)
        {
            hostsByIp[$"10.99.0.{i + 10}"] = $"host{i}.michelecasa";
        }
        for (int i = 14; i < 17; i++)
        {
            hostsByIp[$"10.99.0.{i + 10}"] = null;   // NXDOMAIN
        }

        var scripted = new ScriptedSshSession(ipsByMac, hostsByIp);

        // ACT — wire the orchestrator with a probe factory that returns the 20 MACs as Online,
        // and an enrichmentFactory that drives the SubnetSweepProbe + ReverseDnsProbe pair through
        // the scripted session.
        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var nodeRepo = new NodeRepo(dpFactory);
        var networkRepo = new NetworkRepo(dpFactory);
        var stationRepo = new StationRepo(dpFactory);
        var settings = new StubSettings();
        var notifier = new PollNotifier();

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeFactory = (n, c) =>
        {
            if (n.Id != _seedNodeId)
                return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Failed(n.Id, "not-this-test"));

            var network = new WiFiNetwork { WiFiNodeId = n.Id, MacAddress = _seedNetworkBssid!, SSID = "TestSSID" };
            var stations = macs.Select(m => new WLANStation
            {
                MacAddress = m,
                Bssid = _seedNetworkBssid,
                LastPollDate = DateTime.UtcNow,
            }).ToList();
            return Task.FromResult<NodeProbeResult>(new NodeProbeResult.Success(
                NodeId: n.Id,
                Networks: new[] { network },
                Stations: stations,
                NewlyTrustedFingerprint: null));
        };

        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> enrichmentFactory =
            async (n, ipsForDns, peerIp, c) =>
            {
                // Drive the real probes through the scripted session. peerIp must be a valid IPv4
                // (T-09-03-05 defense-in-depth). Use 10.99.0.1 — same /24 as the SubnetScanRanges
                // for sanity, though this branch uses the CIDR-list path so peerIp is only validated.
                var sweep = new SubnetSweepProbe(scripted, NullLogger<SubnetSweepProbe>.Instance);
                var dns = new ReverseDnsProbe(scripted, NullLogger<ReverseDnsProbe>.Instance);

                var sweepRes = await sweep.PollAsync(n, "10.99.0.1", c);
                var dnsRes = await dns.PollAsync(n, ipsForDns, c);

                var sweepMap = sweepRes is SubnetSweepProbeResult.Success ss
                    ? ss.MacToIp
                    : (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);
                var dnsMap = dnsRes is ReverseDnsProbeResult.Success ds
                    ? ds.IpToHostname
                    : (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.Ordinal);

                return new EnrichmentDelta(n.Id, sweepMap, dnsMap);
            };

        var orchestrator = new PollOrchestrator(
            dpFactory, nodeRepo, networkRepo, stationRepo, probeFactory, notifier,
            NullLogger<PollOrchestrator>.Instance, settings, enrichmentFactory, TimeProvider.System);

        // First cycle: probe emits 20 stations with null IPAddress; orchestrator's enrichmentFactory
        // populates IPs via the sweep; ipsForDns is empty on this cycle (all probe stations had null
        // IP at the time the orchestrator built the set, and there were no existing rows yet). Per
        // ORCH-08, Hostname=null persists and is retried on the next refresh tick.
        await orchestrator.RunCycleAsync(ct);

        // Reset LastEnrichmentRefreshAt to null so the trigger fires again on the next cycle.
        // In production this would happen naturally after RefreshIntervalSeconds elapses; the test
        // forces it to keep the wall-clock budget tight while exercising ORCH-08's retry-on-next-tick.
        await using (var dbReset = _fixture.CreateContext())
        {
            await dbReset.WiFiNodes.Where(n => n.Id == _seedNodeId)
                .ExecuteUpdateAsync(u => u.SetProperty(n => n.LastEnrichmentRefreshAt, (DateTimeOffset?)null), ct);
        }

        // Second cycle: probe still emits null IPAddress (no neigh hits in this synthetic shape).
        // DB-as-cache cascade reuses cycle 1's persisted IPs; the orchestrator now identifies the
        // 17 stations with IP-but-null-Hostname and fires nslookup for them. This mirrors the
        // real-world cadence where DNS may take a couple of refresh ticks to populate.
        await orchestrator.RunCycleAsync(ct);

        // ASSERT — reload from DB; compute achieved rates; emit to ITestOutputHelper for CI visibility.
        await using var verify = _fixture.CreateContext();
        var stations = await verify.WLANStations.AsNoTracking()
            .Where(s => s.Network != null && s.Network.WiFiNodeId == _seedNodeId)
            .ToListAsync(ct);

        Assert.Equal(20, stations.Count);

        var ipPct = stations.Count(s => s.IPAddress != null) * 100.0 / stations.Count;
        var withIp = stations.Where(s => s.IPAddress != null).ToList();
        var hostPct = withIp.Count == 0
            ? 0.0
            : withIp.Count(s => s.Hostname != null) * 100.0 / withIp.Count;

        _output.WriteLine($"Phase 10 SC1 achieved: IPAddress={ipPct:F1}% / Hostname-of-IP={hostPct:F1}% on {stations.Count}-station fixture.");

        Assert.True(ipPct >= 80.0, $"SC1 IPAddress threshold not met: {ipPct:F1}% < 80%.");
        Assert.True(hostPct >= 80.0, $"SC1 Hostname-of-IP threshold not met: {hostPct:F1}% < 80%.");
    }

    private int _seedNodeId;
    private int _seedNetworkId;
    private string? _seedNetworkBssid;

    /// <summary>
    /// Inline ISshSession that responds to the SubnetSweepProbe + ReverseDnsProbe command pattern
    /// without any fixture-file lookups. Encodes:
    /// - `command -v arping` → exit 0 (arping available).
    /// - any `arping`/`ping` loop command → synthesized stdout matching ParseArpingPingOutput's
    ///   line shape (`&lt;ip&gt; ether &lt;mac&gt; ...`) for every (mac, ip) pair in <c>_ipsByMac</c>.
    /// - `nslookup &lt;ip&gt;` → "Name: &lt;host&gt;" for resolved IPs, "** server can't find ...: NXDOMAIN"
    ///   for the unresolved 3.
    /// </summary>
    private sealed class ScriptedSshSession(
        IReadOnlyDictionary<string, string> ipsByMac,
        IReadOnlyDictionary<string, string?> hostsByIp) : ISshSession
    {
        public Task ConnectAsync(WiFiNode node, string plaintextPassword, string? expectedFingerprint,
            Action<string>? onHostKeyAccepted, CancellationToken ct)
            => Task.CompletedTask;

        public Task<string> RunCommandAsync(string command, CancellationToken ct)
        {
            // Quick task 260511-lct (2026-05-11): ReverseDnsProbe now issues ONE batched shell
            // command of the shape `(echo "==<ip>=="; nslookup -timeout=1 -retry=1 <ip> 2>&1) & ...`
            // per probe invocation. Detect by the literal `nslookup -timeout=1 -retry=1` token
            // (present once per IP in the batched body), extract IPs from the `==<ip>==`
            // sentinels in the command body, and synthesize the sentinel-delimited stdout that
            // ParseBatchedNslookupOutput expects.
            if (command.Contains("nslookup -timeout=1 -retry=1", StringComparison.Ordinal))
            {
                var sb = new System.Text.StringBuilder();
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    command, @"==(?<ip>[\d.]+)==");
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    var ip = m.Groups["ip"].Value;
                    sb.Append("==").Append(ip).Append("==\n");
                    if (hostsByIp.TryGetValue(ip, out var hostname) && hostname is not null)
                    {
                        sb.Append($"Server: 127.0.0.1\nAddress: 127.0.0.1#53\n\nName:\t{hostname}\nAddress: {ip}\n");
                    }
                    else
                    {
                        sb.Append($"** server can't find {ip}: NXDOMAIN\n");
                    }
                }
                return Task.FromResult(sb.ToString());
            }

            // Legacy nslookup branch (kept for symmetry; the production probe no longer issues
            // single-IP `nslookup <ip>` commands as of 260511-lct, but other tests may stub).
            if (command.StartsWith("nslookup ", StringComparison.Ordinal))
            {
                var ip = command["nslookup ".Length..].Trim();
                if (hostsByIp.TryGetValue(ip, out var hostname) && hostname is not null)
                {
                    return Task.FromResult($"Server: 127.0.0.1\nAddress: 127.0.0.1#53\n\nName:\t{hostname}\nAddress: {ip}\n");
                }
                return Task.FromResult($"** server can't find {ip}: NXDOMAIN\n");
            }

            // arping/ping loop branch: synthesize lines matching ParseArpingPingOutput's regex.
            // Format: "10.99.0.10 ether AA:BB:CC:DD:00:01 REACHABLE" (the parser only requires
            // an IPv4 + a MAC on the same line).
            if (command.Contains("arping", StringComparison.Ordinal)
                || command.StartsWith("for ip in ", StringComparison.Ordinal)
                || command.Contains(" ping -c1", StringComparison.Ordinal))
            {
                var lines = ipsByMac.Select(kv => $"{kv.Value} ether {kv.Key.ToLowerInvariant()} REACHABLE");
                return Task.FromResult(string.Join('\n', lines) + "\n");
            }

            // Default: empty stdout (avoids FileNotFoundException — the orchestrator's NodePoller
            // path never reaches this session because probeFactory is overridden in the test).
            return Task.FromResult(string.Empty);
        }

        public Task<(int ExitCode, string StdOut, string StdErr)> RunCommandWithExitCodeAsync(string command, CancellationToken ct)
        {
            // `command -v arping` → exit 0, stdout "/usr/sbin/arping" (capability detect succeeds).
            if (command.Contains("command -v arping", StringComparison.Ordinal))
            {
                return Task.FromResult((0, "/usr/sbin/arping\n", string.Empty));
            }
            return Task.FromResult((0, string.Empty, string.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSettings : IAppSettingsService
    {
        public int     PollingIntervalSeconds => 30;
        public string? UIPasswordHash         => null;
        public bool    WizardDismissed        => false;
        public int     RefreshIntervalSeconds => 3600;
        public IReadOnlyList<string> LocalDomainSuffixes => Array.Empty<string>();
        public event Action<IAppSettingsService>? Changed { add { } remove { } }

        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
