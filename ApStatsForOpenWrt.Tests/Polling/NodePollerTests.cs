using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Polling.Exceptions;
using Renci.SshNet.Common;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Polling;

/// <summary>
/// NodePoller tests. POLL-04 (5s timeout belt + braces), POLL-12 (iwinfo→iw fallback + cache),
/// POLL-14 (dispose-hang safe), SEC-07 (plaintext password never logged), B2/POLL-03 (per-node
/// re-entry gate).
/// </summary>
public sealed class NodePollerTests
{
    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ssh-output", "openwrt-23.05.5");

    /// <summary>
    /// Inline IDataProtectionProvider stub: returns a constant plaintext from Unprotect regardless
    /// of input ciphertext. Lets tests exercise the SEC-07 password-leak invariant without a real
    /// key ring.
    /// </summary>
    private sealed class ConstantPlaintextProtectionProvider(string plaintext) : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new ConstantProtector(plaintext);

        private sealed class ConstantProtector(string plaintext) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => this;
            public byte[] Protect(byte[] plaintextBytes) => plaintextBytes;
            public byte[] Unprotect(byte[] ciphertext) => System.Text.Encoding.UTF8.GetBytes(plaintext);
        }
    }

    /// <summary>Captured-entries logger for SEC-07 leakage checks.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    /// <summary>Build a minimal in-memory fixture directory containing iwinfo + ip-neigh fakes
    /// so happy-path tests can run without Plan 02-04 fixtures landing.</summary>
    private static string BuildSyntheticFixtureDir(string prefix, bool iwinfoExits127 = false, bool useIw = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"npt-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        // command -v iwinfo. Plan 02-04 W4-LOCK extension: filenames now collapse runs of '-'
        // and replace ':' (Windows-safe), so "command -v iwinfo" maps to "command-v-iwinfo.txt"
        // (single internal dash) rather than the older "command--v-iwinfo.txt" double-dash form.
        var probeFilename = "command-v-iwinfo.txt";
        if (iwinfoExits127)
        {
            File.WriteAllText(Path.Combine(dir, probeFilename), "");
            File.WriteAllText(Path.Combine(dir, probeFilename + ".exit"), "127");
        }
        else
        {
            File.WriteAllText(Path.Combine(dir, probeFilename), "/usr/sbin/iwinfo\n");
            File.WriteAllText(Path.Combine(dir, probeFilename + ".exit"), "0");
        }

        if (useIw || iwinfoExits127)
        {
            // iw dev
            File.WriteAllText(Path.Combine(dir, "iw-dev.txt"),
                "phy#0\n\tInterface wlan0\n\t\taddr aa:bb:cc:dd:ee:01\n\t\tssid TestNet\n\t\ttype AP\n\t\tchannel 6\n");
            // iw dev wlan0 station dump
            File.WriteAllText(Path.Combine(dir, "iw-dev-wlan0-station-dump.txt"),
                "Station 11:22:33:44:55:01 (on wlan0)\n\tsignal:\t-45 dBm\n\ttx bitrate: 54.0 MBit/s\n");
        }
        else
        {
            // iwinfo
            File.WriteAllText(Path.Combine(dir, "iwinfo.txt"),
                "wlan0     ESSID: \"TestNet\"\n          Access Point: AA:BB:CC:DD:EE:01\n          Mode: Master  Channel: 6\n          Tx-Power: 20 dBm\n          Encryption: WPA2 PSK\n");
            // iwinfo wlan0 assoclist
            File.WriteAllText(Path.Combine(dir, "iwinfo-wlan0-assoclist.txt"),
                "11:22:33:44:55:01  -45 dBm / -95 dBm (SNR 50)  0 ms ago\n");
        }

        // ip neigh show
        File.WriteAllText(Path.Combine(dir, "ip-neigh-show.txt"),
            "192.168.1.50 dev wlan0 lladdr 11:22:33:44:55:01 REACHABLE\n");

        return dir;
    }

    [Fact]
    public async Task SEC07_FirstConnect_AcceptsHostKey_AndCapturesFingerprint()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("sec07-tofu");
        try
        {
            var fake = new FakeSshSession(fixtureDir);
            var node = new WiFiNode { Id = 101, Hostname = "ap-tofu", Username = "root", Password = "Y2lwaGVydGV4dA==", HostKeyFingerprint = null };
            var dpp = new ConstantPlaintextProtectionProvider("dummy-plaintext");
            var poller = new NodePoller(node, fake, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);

            var result = await poller.PollAsync(ct);

            var success = Assert.IsType<NodeProbeResult.Success>(result);
            Assert.Equal("FAKE+SHA256+FINGERPRINT+1234567890abcdef", success.NewlyTrustedFingerprint);
            Assert.NotEmpty(success.Networks);
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { /* test cleanup */ } }
    }

    [Fact]
    public async Task SEC07_HostKeyMismatch_ThrowsHostKeyMismatchException()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("sec07-mismatch");
        try
        {
            var fake = new FakeSshSession(fixtureDir)
            {
                ThrowOnConnect = () => new HostKeyMismatchException("EXPECTED-FP", "ACTUAL-FP")
            };
            var node = new WiFiNode { Id = 102, Hostname = "ap-mismatch", Username = "root", Password = "Y2lwaGVydGV4dA==", HostKeyFingerprint = "EXPECTED-FP" };
            var dpp = new ConstantPlaintextProtectionProvider("dummy");
            var poller = new NodePoller(node, fake, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);

            await Assert.ThrowsAsync<HostKeyMismatchException>(async () => await poller.PollAsync(ct));
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    [Fact]
    public async Task POLL12_IwinfoExit127_FallsBackToIwDev_AndCachesChoice()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("poll12", iwinfoExits127: true);
        try
        {
            var fake = new FakeSshSession(fixtureDir);
            var node = new WiFiNode { Id = 103, Hostname = "ap-iw", Username = "root", Password = "Y2lwaGVydGV4dA==" };
            var dpp = new ConstantPlaintextProtectionProvider("dummy");
            var cache = new OpenWrtAdapterCache();
            var poller = new NodePoller(node, fake, cache, dpp, NullLogger<NodePoller>.Instance);

            var result = await poller.PollAsync(ct);

            Assert.IsType<NodeProbeResult.Success>(result);
            Assert.Equal("iw", cache.TryGet(node.Id));

            // Second poll on same node — even if probe would now report exit 0, the cache stays "iw".
            // Filenames track the W4-LOCK extension (single dash for "command -v iwinfo").
            File.WriteAllText(Path.Combine(fixtureDir, "command-v-iwinfo.txt.exit"), "0");
            File.WriteAllText(Path.Combine(fixtureDir, "command-v-iwinfo.txt"), "/usr/sbin/iwinfo\n");
            // Also need a fresh fake session (Pollr may dispose the existing one).
            var fake2 = new FakeSshSession(fixtureDir);
            var poller2 = new NodePoller(node, fake2, cache, dpp, NullLogger<NodePoller>.Instance);
            var result2 = await poller2.PollAsync(ct);
            Assert.IsType<NodeProbeResult.Success>(result2);
            Assert.Equal("iw", cache.TryGet(node.Id));   // still iw — cached choice is sticky
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    [Fact]
    public async Task POLL04_ConnectionTimeout_ObservesFiveSecondBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("poll04");
        try
        {
            var fake = new FakeSshSession(fixtureDir)
            {
                // Simulate a 10-second hang on Connect; the linked CTS should fire at ~5s.
                ConnectDelay = async cct => await Task.Delay(TimeSpan.FromSeconds(10), cct)
            };
            var node = new WiFiNode { Id = 104, Hostname = "ap-slow", Username = "root", Password = "Y2lwaGVydGV4dA==" };
            var dpp = new ConstantPlaintextProtectionProvider("dummy");
            var poller = new NodePoller(node, fake, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);

            var sw = Stopwatch.StartNew();
            var result = await poller.PollAsync(ct);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7),
                $"PollAsync exceeded 5s + tolerance budget; actual={sw.Elapsed}");
            // Expect a Failed result with reason "timeout" (linkedCts) — NOT an unhandled exception.
            var failed = Assert.IsType<NodeProbeResult.Failed>(result);
            Assert.Equal("timeout", failed.Reason);
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    [Fact]
    public async Task POLL14_DisposeDoesNotBlock_HostShutdown()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("poll14");
        try
        {
            // FakeSshSession's DisposeAsync would normally complete instantly. Verify the wrapping
            // pattern: NodePoller awaits DisposeAsync inside `finally` and bounds its impact —
            // here we simulate a fast-success Dispose to confirm the call shape; the pessimistic
            // hang case is exercised by the production RealSshSession's swallow-catch (Pitfall 5).
            var fake = new FakeSshSession(fixtureDir);
            var disposeWatch = new TaskCompletionSource();
            fake.DisposeDelay = () =>
            {
                disposeWatch.TrySetResult();
                return Task.CompletedTask;
            };
            var node = new WiFiNode { Id = 105, Hostname = "ap-dispose", Username = "root", Password = "Y2lwaGVydGV4dA==" };
            var dpp = new ConstantPlaintextProtectionProvider("dummy");
            var poller = new NodePoller(node, fake, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);

            var sw = Stopwatch.StartNew();
            var result = await poller.PollAsync(ct);
            sw.Stop();

            Assert.IsType<NodeProbeResult.Success>(result);
            Assert.True(disposeWatch.Task.IsCompleted, "DisposeAsync was not invoked");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"PollAsync wall-clock exceeded budget: {sw.Elapsed}");
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    [Fact]
    public async Task SEC07_PasswordNeverAppearsInLogs()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("sec07-leak");
        try
        {
            const string secret = "super-secret-password-12345";
            var dpp = new ConstantPlaintextProtectionProvider(secret);
            var fake = new FakeSshSession(fixtureDir)
            {
                ThrowOnConnect = () => new HostKeyMismatchException("EXPECTED", "ACTUAL")
            };
            var node = new WiFiNode { Id = 106, Hostname = "ap-leak", Username = "root", Password = "Y2lwaGVydGV4dA==", HostKeyFingerprint = "EXPECTED" };
            var capLogger = new CapturingLogger<NodePoller>();
            var poller = new NodePoller(node, fake, new OpenWrtAdapterCache(), dpp, capLogger);

            try { await poller.PollAsync(ct); } catch (HostKeyMismatchException) { /* expected */ }

            Assert.DoesNotContain(capLogger.Messages, m => m.Contains(secret, StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    [Fact]
    public async Task SEC07_PasswordNeverAppearsInLogs_AuthFailedPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("sec07-auth-leak");
        try
        {
            const string secret = "another-secret-99999";
            var dpp = new ConstantPlaintextProtectionProvider(secret);
            var fake = new FakeSshSession(fixtureDir)
            {
                ThrowOnConnect = () => new SshAuthenticationException("auth failed: bad password")
            };
            var node = new WiFiNode { Id = 107, Hostname = "ap-auth-leak", Username = "root", Password = "Y2lwaGVydGV4dA==" };
            var capLogger = new CapturingLogger<NodePoller>();
            var poller = new NodePoller(node, fake, new OpenWrtAdapterCache(), dpp, capLogger);

            var result = await poller.PollAsync(ct);

            var failed = Assert.IsType<NodeProbeResult.Failed>(result);
            Assert.Equal("auth-failed", failed.Reason);
            Assert.DoesNotContain(capLogger.Messages, m => m.Contains(secret, StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    [Fact]
    public async Task AlgoNegotiation_TranslatesToAlgoNegotiateFailedReason()
    {
        // Branch coverage: SshConnectionException with "negotiate" in the message exercises the
        // ContainsAlgoNegotiationKeyword filter + algo-negotiate-failed translation.
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("algo-negotiate");
        try
        {
            var fake = new FakeSshSession(fixtureDir)
            {
                ThrowOnConnect = () => new SshConnectionException("could not negotiate any algorithm")
            };
            var node = new WiFiNode { Id = 200, Hostname = "ap-algo", Username = "root", Password = "Y2lwaGVydGV4dA==" };
            var dpp = new ConstantPlaintextProtectionProvider("dummy");
            var poller = new NodePoller(node, fake, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);

            var result = await poller.PollAsync(ct);

            var failed = Assert.IsType<NodeProbeResult.Failed>(result);
            Assert.Equal("algo-negotiate-failed", failed.Reason);
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    [Fact]
    public async Task SocketException_TranslatesToNetworkErrorReason()
    {
        // Branch coverage: SocketException -> "network-error" translation.
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("net-error");
        try
        {
            var fake = new FakeSshSession(fixtureDir)
            {
                ThrowOnConnect = () => new SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)
            };
            var node = new WiFiNode { Id = 201, Hostname = "ap-net", Username = "root", Password = "Y2lwaGVydGV4dA==" };
            var dpp = new ConstantPlaintextProtectionProvider("dummy");
            var poller = new NodePoller(node, fake, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);

            var result = await poller.PollAsync(ct);

            var failed = Assert.IsType<NodeProbeResult.Failed>(result);
            Assert.Equal("network-error", failed.Reason);
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    [Fact]
    public async Task GenericException_TranslatesToProbeFailedReason()
    {
        // Branch coverage: any other Exception -> "probe-failed" translation (top-level catch).
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("generic-error");
        try
        {
            var fake = new FakeSshSession(fixtureDir)
            {
                ThrowOnConnect = () => new InvalidOperationException("synthetic non-SSH failure")
            };
            var node = new WiFiNode { Id = 202, Hostname = "ap-generic", Username = "root", Password = "Y2lwaGVydGV4dA==" };
            var dpp = new ConstantPlaintextProtectionProvider("dummy");
            var poller = new NodePoller(node, fake, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);

            var result = await poller.PollAsync(ct);

            var failed = Assert.IsType<NodeProbeResult.Failed>(result);
            Assert.Equal("probe-failed", failed.Reason);
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    [Fact]
    public async Task PerNodeSemaphore_PreventsWithinCycleReentry()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("b2-reentry");
        try
        {
            // Use a unique node id so test isolation is preserved against the static
            // _perNodeGate dictionary in NodePoller.
            var nodeId = 90000 + Random.Shared.Next(0, 9999);
            var release = new TaskCompletionSource();
            var fake1 = new FakeSshSession(fixtureDir)
            {
                ConnectDelay = async _ => await release.Task   // hold the first call until released
            };
            var fake2 = new FakeSshSession(fixtureDir);
            var dpp = new ConstantPlaintextProtectionProvider("dummy");
            var node = new WiFiNode { Id = nodeId, Hostname = "ap-reentry", Username = "root", Password = "Y2lwaGVydGV4dA==" };

            var poller1 = new NodePoller(node, fake1, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);
            var poller2 = new NodePoller(node, fake2, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);

            // Start the first call (held by ConnectDelay).
            var task1 = poller1.PollAsync(ct);

            // Allow the first call to enter and grab the per-node gate before the second arrives.
            // Since ConnectDelay runs INSIDE the gate, by the time we observe the suspension we
            // know the gate is held; spin briefly until task1 hasn't yet completed.
            for (int i = 0; i < 50 && task1.IsCompleted; i++) await Task.Delay(10, ct);

            // Second call should return immediately with within-cycle-reentry.
            var sw = Stopwatch.StartNew();
            var result2 = await poller2.PollAsync(ct);
            sw.Stop();

            var failed = Assert.IsType<NodeProbeResult.Failed>(result2);
            Assert.Equal("within-cycle-reentry", failed.Reason);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
                $"Second PollAsync should return quickly (no SSH wait); actual={sw.Elapsed}");

            // Release the first call and ensure it succeeds.
            release.SetResult();
            var result1 = await task1;
            Assert.IsType<NodeProbeResult.Success>(result1);
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    // ── M-HRD-07 (v1.0 / plan 21-05): the within-cycle re-entry invariant pinned as
    //    "exactly one of two concurrent same-node polls is skipped". This complements
    //    PerNodeSemaphore_PreventsWithinCycleReentry above by asserting the partition of the
    //    two results directly (exactly one Failed/within-cycle-reentry, exactly one Success),
    //    so a future refactor that let BOTH proceed (racing the SSH session) goes provably red.
    [Fact]
    public async Task WithinCycleReentry_ExactlyOneConcurrentSameNodePollIsSkipped_M_HRD_07()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("m-hrd-07-reentry");
        try
        {
            // Unique id keeps us isolated from the static _perNodeGate across the suite.
            var nodeId = 91000 + Random.Shared.Next(0, 8999);
            NodePoller.RemoveNodeGate(nodeId); // belt + braces: ensure no leftover gate for this id

            var release = new TaskCompletionSource();
            // The call that wins the gate blocks inside ConnectAsync until released, guaranteeing
            // the gate is held when the second call attempts its WaitAsync(0).
            var holder = new FakeSshSession(fixtureDir) { ConnectDelay = async _ => await release.Task };
            var contender = new FakeSshSession(fixtureDir);
            var dpp = new ConstantPlaintextProtectionProvider("dummy");
            var node = new WiFiNode { Id = nodeId, Hostname = "ap-m-hrd-07", Username = "root", Password = "Y2lwaGVydGV4dA==" };

            var pollerA = new NodePoller(node, holder, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);
            var pollerB = new NodePoller(node, contender, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);

            var taskA = pollerA.PollAsync(ct);
            // Spin until A is in-flight (holding the gate) before launching B.
            for (int i = 0; i < 50 && !taskA.IsCompleted; i++)
            {
                if (NodePoller.HasNodeGate(nodeId)) break;
                await Task.Delay(10, ct);
            }

            var resultB = await pollerB.PollAsync(ct);
            release.SetResult();
            var resultA = await taskA;

            // Exactly one within-cycle-reentry skip across the two results.
            var results = new[] { resultA, resultB };
            var reentries = results.OfType<NodeProbeResult.Failed>()
                                   .Where(f => f.Reason == "within-cycle-reentry")
                                   .ToList();
            var successes = results.OfType<NodeProbeResult.Success>().ToList();

            Assert.Single(reentries);
            Assert.Single(successes);
            Assert.Equal(nodeId, reentries[0].NodeId);
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }

    // ── M-HRD-09 (v1.0 / plan 21-05): RemoveNodeGate removes a previously-present gate entry ──
    [Fact]
    public async Task RemoveNodeGate_RemovesPreviouslyPresentGateEntry_M_HRD_09()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = BuildSyntheticFixtureDir("m-hrd-09-gate");
        try
        {
            var nodeId = 92000 + Random.Shared.Next(0, 8999);
            NodePoller.RemoveNodeGate(nodeId); // clean slate

            // A successful poll populates _perNodeGate[nodeId] via GetOrAdd inside PollAsync.
            var fake = new FakeSshSession(fixtureDir);
            var dpp = new ConstantPlaintextProtectionProvider("dummy");
            var node = new WiFiNode { Id = nodeId, Hostname = "ap-gate-cleanup", Username = "root", Password = "Y2lwaGVydGV4dA==" };
            var poller = new NodePoller(node, fake, new OpenWrtAdapterCache(), dpp, NullLogger<NodePoller>.Instance);

            var result = await poller.PollAsync(ct);
            Assert.IsType<NodeProbeResult.Success>(result);
            Assert.True(NodePoller.HasNodeGate(nodeId), "precondition: poll should have added a gate entry");

            NodePoller.RemoveNodeGate(nodeId);

            Assert.False(NodePoller.HasNodeGate(nodeId), "M-HRD-09: gate entry must be gone after RemoveNodeGate");
            // Idempotent / harmless no-op on a missing id.
            NodePoller.RemoveNodeGate(nodeId);
            Assert.False(NodePoller.HasNodeGate(nodeId));
        }
        finally { try { Directory.Delete(fixtureDir, true); } catch { } }
    }
}
