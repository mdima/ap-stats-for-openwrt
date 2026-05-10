using Microsoft.AspNetCore.DataProtection;
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

    [Fact]
    public async Task RunCycleAsync_AgainstSeededNode_DoesNotIssueAnyPhase9NewCommands()
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
        });
        await db.SaveChangesAsync(ct);

        // Build a recording FakeSshSession bound to the 23.05-SNAPSHOT fixture folder
        // (full v0.9 command coverage present).
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var recordingSession = new FakeSshSession(fixtureDir);

        // Build a PollOrchestrator with the recording session. Constructor signature
        // matches the verbatim ctor used by PollOrchestratorTests.BuildOrchestrator
        // (see OpenWrtApStats.Tests/Polling/PollOrchestratorTests.cs:43-58).
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
            settings);

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
    /// Local copy of <see cref="PollOrchestratorTests"/>'s private nested StubSettings
    /// (PollOrchestratorTests.cs:1142). Replicated here so the SC4 isolation test does
    /// not depend on private nested types from sibling test classes.
    /// </summary>
    private sealed class StubSettings(int pollingIntervalSeconds) : IAppSettingsService
    {
        public int     PollingIntervalSeconds => pollingIntervalSeconds;
        public string? UIPasswordHash         => null;
        public bool    WizardDismissed        => false;
        public event Action<IAppSettingsService>? Changed { add { } remove { } }

        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
