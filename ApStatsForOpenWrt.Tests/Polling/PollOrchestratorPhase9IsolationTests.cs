using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;
using ApStatsForOpenWrt.Services.Enrichment;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Polling;

/// <summary>
/// Phase 9 SC4 / D-10 validation: the orchestrator's RunCycleAsync against a fixture-replay node
/// MUST issue exactly the v0.9 SSH command set — no Phase-9-new commands. This test is the
/// green-or-red signal that the isolation guarantee holds. Implemented as a focused
/// FakeSshSession-based command recorder.
///
/// SC4 contract is fundamentally NEGATIVE: the orchestrator must NOT issue any of the new
/// Phase 9 commands (`arping`, `ping <ip>`, `nslookup`, `ip route get`, `ip -4 addr show`,
/// `command -v arping`). A sanity gate (CommandsIssued.Count >= 1) avoids a false-pass on a
/// no-op short-circuit.
/// </summary>
public sealed class PollOrchestratorPhase9IsolationTests(SqliteInMemoryFixture fixture)
    : IClassFixture<SqliteInMemoryFixture>
{
    private readonly SqliteInMemoryFixture _fixture = fixture;

    // The set of substrings that, if any commands contain them, indicate Phase-9-new code paths
    // leaked into the orchestrator's poll cycle. RunCycleAsync MUST NOT issue any of these.
    private static readonly string[] Phase9NewCommandTokens =
    {
        "arping",          // SubnetSweepProbe arping path + capability detect
        "nslookup",        // ReverseDnsProbe
        "ip route get",    // SubnetDeriver auto-derivation step 1
        "ip -4 addr show", // SubnetDeriver auto-derivation step 2
    };

    /// <summary>
    /// Phase 10 C-10 — Inverted SC4 Case A: when the composite refresh trigger does NOT fire
    /// (LastEnrichmentRefreshAt = recent), the orchestrator must NOT issue any of the new
    /// Phase 9 commands on either the NodePoller path or the enrichmentFactory path. Mirrors
    /// the original Phase 9 SC4 invariant unchanged.
    /// </summary>
    [Fact]
    public async Task RunCycleAsync_TriggerNotFired_DoesNotIssuePhase9NewCommands()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed one WiFiNode with an encrypted password matching how Phase 2 stores it. Use
        // an ephemeral DataProtector to encrypt a dummy password.
        await using var db = _fixture.CreateContext();
        var dpp = new EphemeralDataProtectionProvider();
        var protector = dpp.CreateProtector("node-creds");
        var encrypted = protector.Protect("dummy-password");

        // Use a unique hostname to avoid collision if other tests run in parallel against the
        // shared fixture DB.
        var hostname = $"isolation-node-{Guid.NewGuid():N}";
        db.WiFiNodes.Add(new WiFiNode
        {
            Hostname = hostname,
            Username = "operator",
            Password = encrypted,
            Status = Status.Offline,
            HostKeyFingerprint = null,    // First-connect TOFU
            // Phase 10: LastEnrichmentRefreshAt = UtcNow ensures the trigger does NOT fire
            // (interval = 3600s, elapsed < 1s). This validates the inverted SC4 invariant
            // for Case A: no Phase 9 commands when the trigger is silent.
            LastEnrichmentRefreshAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        // Build a recording FakeSshSession bound to the 23.05-SNAPSHOT fixture folder
        // (full v0.9 command coverage present).
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var recordingSession = new FakeSshSession(fixtureDir);

        // Build a PollOrchestrator with the recording session. Constructor signature
        // matches the verbatim ctor used by PollOrchestratorTests.BuildOrchestrator
        // (see ApStatsForOpenWrt.Tests/Polling/PollOrchestratorTests.cs:43-58).
        // Per CLAUDE.md / PER-04: PollOrchestrator is the only carve-out that consumes
        // IDbContextFactory directly outside the repository tier.
        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var notifier = new PollNotifier();
        var adapterCache = new OpenWrtAdapterCache();
        var nodeRepo = new NodeRepo(dpFactory);
        var networkRepo = new NetworkRepo(dpFactory);
        var stationRepo = new StationRepo(dpFactory);
        var settings = new StubSettings(pollingIntervalSeconds: 30);

        // Probe factory uses the recording session for every node. NodePoller is the
        // v0.9 poll path; SubnetSweepProbe and ReverseDnsProbe are NOT injected because
        // the orchestrator ctor signature is unchanged from v0.9 (SC4 invariant).
        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeFactory =
            (n, c) =>
            {
                var poller = new NodePoller(n, recordingSession, adapterCache, dpp,
                    NullLogger<NodePoller>.Instance);
                return poller.PollAsync(c);
            };

        // Verbatim ctor signature from PollOrchestratorTests.cs:49-57.
        var orchestrator = new PollOrchestrator(
            dpFactory,
            nodeRepo,
            networkRepo,
            stationRepo,
            probeFactory,
            notifier,
            NullLogger<PollOrchestrator>.Instance,
            settings,
            (_, _, _, _) => Task.FromResult<EnrichmentDelta?>(null),     // Phase 10 default: no enrichment this cycle (preserves the existing v0.9 command-set assertions)
            TimeProvider.System,
            new ForceEnrichmentService());

        // Run one cycle. Failures are acceptable: the assertion is on the COMMAND SET,
        // not on the cycle's success outcome.
        await orchestrator.RunCycleAsync(ct);

        // SC4 invariant: no Phase-9-new commands issued. Negative anchors only
        // (Option 2 from revision 1 review): the positive anchor on `command -v iwinfo`
        // is informational and brittle (cycle may short-circuit before the capability
        // probe under certain TOFU paths). The SC4 contract is fundamentally negative.
        foreach (var token in Phase9NewCommandTokens)
        {
            Assert.DoesNotContain(recordingSession.CommandsIssued,
                c => c.Contains(token, StringComparison.Ordinal));
        }

        // The `ping` substring is special-cased: `ping ` (with trailing space) catches
        // `ping -c1 -W1 ...` from SubnetSweepProbe but does NOT catch unrelated tokens.
        Assert.DoesNotContain(recordingSession.CommandsIssued,
            c => c.StartsWith("ping ", StringComparison.Ordinal) || c == "ping");

        // `command -v arping` is the canonical Phase 9 capability-detect probe.
        Assert.DoesNotContain(recordingSession.CommandsIssued,
            c => c.Contains("command -v arping", StringComparison.Ordinal));

        // Sanity gate: the cycle did SOMETHING (avoids false-pass on a no-op short-
        // circuit). At minimum one command should have issued; if zero issued,
        // either the test setup is broken or the orchestrator changed shape, both
        // of which warrant a failure here.
        Assert.True(recordingSession.CommandsIssued.Count >= 1,
            "Expected at least one SSH command to be issued during the cycle; " +
            "zero commands suggests the orchestrator short-circuited before reaching " +
            "the probe path. Either fix the test setup or revisit the SC4 invariant.");
    }

    /// <summary>
    /// Phase 10 C-10 — Inverted SC4 Case B: when the composite refresh trigger fires AND
    /// SubnetScanEnabled is true, the orchestrator MUST invoke the enrichmentFactory.
    /// We assert this by recording into a SECOND FakeSshSession bound to the factory delegate.
    /// </summary>
    [Fact]
    public async Task RunCycleAsync_TriggerFired_AndScanEnabled_InvokesEnrichmentFactory()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var db = _fixture.CreateContext();
        var dpp = new EphemeralDataProtectionProvider();
        var protector = dpp.CreateProtector("node-creds");
        var encrypted = protector.Protect("dummy-password");
        var hostname = $"isolation-trigger-{Guid.NewGuid():N}";

        db.WiFiNodes.Add(new WiFiNode
        {
            Hostname = hostname,
            Username = "operator",
            Password = encrypted,
            Status = Status.Offline,
            HostKeyFingerprint = null,
            SubnetScanEnabled = true,
            LastEnrichmentRefreshAt = null,    // null forces trigger ON (cold-start case).
        });
        await db.SaveChangesAsync(ct);

        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var nodeSession = new FakeSshSession(fixtureDir);

        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var notifier = new PollNotifier();
        var adapterCache = new OpenWrtAdapterCache();
        var nodeRepo = new NodeRepo(dpFactory);
        var networkRepo = new NetworkRepo(dpFactory);
        var stationRepo = new StationRepo(dpFactory);
        var settings = new StubSettings(pollingIntervalSeconds: 30);

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeFactory = (n, c) =>
            new NodePoller(n, nodeSession, adapterCache, dpp, NullLogger<NodePoller>.Instance).PollAsync(c);

        var enrichmentInvocationCount = 0;
        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> enrichmentFactory =
            (n, ips, peerIp, c) =>
            {
                enrichmentInvocationCount++;
                // Return a minimal non-null delta to confirm the orchestrator integrates with the factory.
                return Task.FromResult<EnrichmentDelta?>(new EnrichmentDelta(
                    n.Id,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    new Dictionary<string, string?>(StringComparer.Ordinal)));
            };

        var orchestrator = new PollOrchestrator(
            dpFactory, nodeRepo, networkRepo, stationRepo, probeFactory, notifier,
            NullLogger<PollOrchestrator>.Instance, settings, enrichmentFactory, TimeProvider.System,
            new ForceEnrichmentService());

        await orchestrator.RunCycleAsync(ct);

        // Trigger fired (LastEnrichmentRefreshAt = null + SubnetScanEnabled = true) → factory invoked.
        Assert.True(enrichmentInvocationCount >= 1,
            $"Expected enrichmentFactory to be invoked at least once when trigger fires; actual={enrichmentInvocationCount}");

        // The NodePoller path STILL must not leak Phase-9-new commands — those flow through the
        // factory's own session, not nodeSession.
        foreach (var token in Phase9NewCommandTokens)
        {
            Assert.DoesNotContain(nodeSession.CommandsIssued, c => c.Contains(token, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Phase 10 C-10 — Inverted SC4 Case C: when SubnetScanEnabled is false, the trigger
    /// short-circuits to false regardless of other inputs (D-09 operator opt-out). The
    /// enrichmentFactory MUST NOT be invoked.
    /// </summary>
    [Fact]
    public async Task RunCycleAsync_TriggerCondition_ButScanDisabled_DoesNotInvokeEnrichmentFactory()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var db = _fixture.CreateContext();
        var dpp = new EphemeralDataProtectionProvider();
        var protector = dpp.CreateProtector("node-creds");
        var encrypted = protector.Protect("dummy-password");
        var hostname = $"isolation-scandisabled-{Guid.NewGuid():N}";

        db.WiFiNodes.Add(new WiFiNode
        {
            Hostname = hostname,
            Username = "operator",
            Password = encrypted,
            Status = Status.Offline,
            HostKeyFingerprint = null,
            SubnetScanEnabled = false,    // operator opt-out → trigger MUST short-circuit
            LastEnrichmentRefreshAt = null,
        });
        await db.SaveChangesAsync(ct);

        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var nodeSession = new FakeSshSession(fixtureDir);

        var dpFactory = new TestDbContextFactory(_fixture.Options);
        var notifier = new PollNotifier();
        var adapterCache = new OpenWrtAdapterCache();
        var nodeRepo = new NodeRepo(dpFactory);
        var networkRepo = new NetworkRepo(dpFactory);
        var stationRepo = new StationRepo(dpFactory);
        var settings = new StubSettings(pollingIntervalSeconds: 30);

        Func<WiFiNode, CancellationToken, Task<NodeProbeResult>> probeFactory = (n, c) =>
            new NodePoller(n, nodeSession, adapterCache, dpp, NullLogger<NodePoller>.Instance).PollAsync(c);

        var enrichmentInvocationCount = 0;
        Func<WiFiNode, IReadOnlyList<string>, string, CancellationToken, Task<EnrichmentDelta?>> enrichmentFactory =
            (n, ips, peerIp, c) =>
            {
                enrichmentInvocationCount++;
                return Task.FromResult<EnrichmentDelta?>(null);
            };

        var orchestrator = new PollOrchestrator(
            dpFactory, nodeRepo, networkRepo, stationRepo, probeFactory, notifier,
            NullLogger<PollOrchestrator>.Instance, settings, enrichmentFactory, TimeProvider.System,
            new ForceEnrichmentService());

        await orchestrator.RunCycleAsync(ct);

        // Trigger short-circuited on SubnetScanEnabled=false → enrichmentFactory was NOT invoked.
        Assert.Equal(0, enrichmentInvocationCount);

        // No Phase-9-new commands appear on the nodeSession path either (orchestrator's NodePoller
        // path is the v0.9 command set only).
        foreach (var token in Phase9NewCommandTokens)
        {
            Assert.DoesNotContain(nodeSession.CommandsIssued, c => c.Contains(token, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Local copy of <see cref="PollOrchestratorTests"/>'s private nested StubSettings
    /// (PollOrchestratorTests.cs:1142). Replicated here so the SC4 isolation test does
    /// not depend on private nested types from sibling test classes.
    /// </summary>
    private sealed class StubSettings(int pollingIntervalSeconds) : IAppSettingsService
    {
        public int     PollingIntervalSeconds => pollingIntervalSeconds;
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
        public int SessionVersion => 0;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        // Phase 17 Plan 17-01 M-UPD-02 — minimal mutable update-check stub members.
        public bool UpdateCheckEnabled { get; private set; } = true;
        public bool HistoryEnabled => true;
        public Task SetHistoryEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public string? LatestKnownVersion { get; private set; }
        public string? LatestReleaseUrl { get; private set; }
        public DateTime? LastCheckedAtUtc { get; private set; }
        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) { UpdateCheckEnabled = enabled; return Task.CompletedTask; }
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) { LatestKnownVersion = version; return Task.CompletedTask; }
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) { LatestReleaseUrl = url; return Task.CompletedTask; }
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default) { LastCheckedAtUtc = checkedAtUtc; return Task.CompletedTask; }
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default) { LatestKnownVersion = version; LatestReleaseUrl = url; LastCheckedAtUtc = checkedAtUtc; return Task.CompletedTask; }
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
