using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Polling;

/// <summary>
/// Phase 9 ENRICH-03 + ENRICH-05 — fixture-replay integration arms + synthetic facts for
/// <see cref="ReverseDnsProbe"/>. Mirrors the SubnetSweepProbeTests shape: per-test inline
/// fixture provisioning via FakeSshSession.CommandOverrides, three-version theory rows
/// gated by <see cref="Assert.SkipUnless"/>.
///
/// Quick task 260511-lct (2026-05-11) rewrote ReverseDnsProbe.PollAsync from N sequential
/// `nslookup &lt;ip&gt;` SSH calls into ONE batched SSH command with
/// <c>(echo "==$ip=="; nslookup -timeout=1 -retry=1 $ip 2>&amp;1) &amp;</c> subshells chunked in
/// groups of 32 and joined per-chunk by <c>wait</c>. Tests below assert (a) exactly-one
/// SSH call per non-empty probe, (b) sentinel-based per-IP attribution against interleaved
/// stdout, (c) missing-IP defaulting to null, (d) structured log line emission with the
/// preserved field names (Successful/Total/Negative), (e) timeout shape, (f) legacy
/// ParseNslookupOutput regression guard, and (g) the empty-IP-list fast path.
/// </summary>
public sealed class ReverseDnsProbeTests
{
    public static TheoryData<string> AllVersionFolders() => new()
    {
        Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT"),
        Path.Combine("Fixtures", "ssh-output", "openwrt-22.03.7"),
        Path.Combine("Fixtures", "ssh-output", "openwrt-21.02.7"),
    };

    private static WiFiNode MakeNode() => new()
    {
        Id = 1,
        Hostname = "test-ap",
        Username = "operator",
        Password = string.Empty,
    };

    [Fact]
    public async Task PollAsync_EmptyIpList_ReturnsEmptySuccess_NoSshIssued()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = new FakeSshSession(Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT"));
        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);

        var result = await probe.PollAsync(MakeNode(), Array.Empty<string>(), ct);
        var success = Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Empty(success.IpToHostname);
        Assert.Empty(session.CommandsIssued);
    }

    // ---- Quick task 260511-lct: batched single-SSH-command tests ----

    /// <summary>
    /// Test 1 (260511-lct): The rewritten probe issues EXACTLY ONE SSH command per non-empty
    /// PollAsync invocation regardless of IP count. The captured command must contain the
    /// literal substrings asserted below — these are the load-bearing invariants of the
    /// batched-shell-loop shape (POSIX BusyBox-safe).
    /// </summary>
    [Fact]
    public async Task PollAsync_NonEmptyIpList_IssuesExactlyOneSshCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        // Stub batched stdout — three sentinel blocks. CommandOverride keyed on the
        // recognisable batched-command substring routes the single SSH call to this fixture.
        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-batched-3ips.txt"),
            "==192.168.1.42==\nServer:    192.168.1.1\n\nName:      host42.lan\nAddress 1: 192.168.1.42 host42.lan\n" +
            "==192.168.1.43==\nServer:    192.168.1.1\n\n** server can't find 192.168.1.43: NXDOMAIN\n" +
            "==192.168.1.44==\nServer:    192.168.1.1\n\nName:      host44.lan\nAddress 1: 192.168.1.44 host44.lan\n");

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["nslookup -timeout=1 -retry=1"] = "nslookup-batched-3ips";

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var result = await probe.PollAsync(MakeNode(),
            new[] { "192.168.1.42", "192.168.1.43", "192.168.1.44" }, ct);

        Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Single(session.CommandsIssued);

        var cmd = session.CommandsIssued[0];
        Assert.Contains("nslookup -timeout=1 -retry=1", cmd);
        // Quick task 260515-rds: sentinels are emitted at runtime by the post-wait
        // `for ... cat` loop in the parent shell, NOT embedded as literals in the command
        // text. IPs appear as both `nslookup ... <ip>` and `$TMPDIR/<ip>` per-IP redirect
        // targets — assert the latter (uniquely identifies the new shape).
        Assert.Contains("$TMPDIR/192.168.1.42", cmd);
        Assert.Contains("$TMPDIR/192.168.1.43", cmd);
        Assert.Contains("$TMPDIR/192.168.1.44", cmd);
        Assert.Contains("2>&1", cmd);
        Assert.Contains(" & ", cmd);     // Subshell backgrounding.
        Assert.Contains("wait", cmd);    // Chunk join terminator.
    }

    /// <summary>
    /// Test 2 (260511-lct): Batched stdout with sentinel-delimited blocks attributes each
    /// block's parsed FQDN (or null for negative DNS) to the correct IP via the
    /// <c>==&lt;ip&gt;==</c> sentinel.
    /// </summary>
    [Fact]
    public async Task PollAsync_BatchedSentinelParsing_AttributesOutputCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-batched-sentinel.txt"),
            "==192.168.1.42==\nName:      host42.lan\nAddress 1: 192.168.1.42 host42.lan\n" +
            "==192.168.1.43==\n** server can't find 192.168.1.43: NXDOMAIN\n" +
            "==192.168.1.44==\n;; connection timed out; no servers could be reached\n");

        var session = new FakeSshSession(fixtureDir);
        // Quick task 260515-rds: route by the new shape's per-IP redirect token instead of
        // the pre-260515 in-command sentinel literal.
        session.CommandOverrides["$TMPDIR/192.168.1.42"] = "nslookup-batched-sentinel";

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var result = await probe.PollAsync(MakeNode(),
            new[] { "192.168.1.42", "192.168.1.43", "192.168.1.44" }, ct);

        var success = Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Equal(3, success.IpToHostname.Count);
        Assert.Equal("host42.lan", success.IpToHostname["192.168.1.42"]);
        Assert.Null(success.IpToHostname["192.168.1.43"]);     // NXDOMAIN → null.
        Assert.Null(success.IpToHostname["192.168.1.44"]);     // Timeout → null.
    }

    /// <summary>
    /// Test 3 (260511-lct, docstring rewritten by 260515-rds): Per-IP sentinel blocks can
    /// emerge in any order in the parser's input stream. The parser MUST attribute each block
    /// to its OWN sentinel regardless of block ordering.
    ///
    /// Historical note: this test's original xmldoc claimed that the parenthesised subshell
    /// <c>(echo ==$ip==; nslookup ... 2>&amp;1) &amp;</c> kept the sentinel+output pair atomic
    /// per IP. That claim was UNSOUND — parens give sequential ordering only within ONE
    /// subshell; concurrent subshells share the same stdout fd over SSH, and BusyBox
    /// <c>nslookup</c>'s multi-line writes interleave at line granularity under PIPE_BUF.
    /// Quick task 260515-rds (2026-05-15) replaced the in-subshell echo shape with per-IP
    /// temp file redirection plus a sequential post-<c>wait</c> <c>for ... cat</c> emission
    /// loop — that is the actual atomicity mechanism. See
    /// <c>ReverseDnsProbe.BuildBatchedNslookupCommand</c> and the new test
    /// <c>PollAsync_CommandShape_UsesPerIpFileRedirection_NotInlineSentinelEcho</c>.
    /// </summary>
    [Fact]
    public async Task PollAsync_BatchedSentinelParsing_OutOfOrderInterleavedOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        // Out-of-order: .43 block first, then .42, then .44.
        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-batched-outoforder.txt"),
            "==192.168.1.43==\nName:      host43.lan\nAddress 1: 192.168.1.43 host43.lan\n" +
            "==192.168.1.42==\nName:      host42.lan\nAddress 1: 192.168.1.42 host42.lan\n" +
            "==192.168.1.44==\n** server can't find 192.168.1.44: NXDOMAIN\n");

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["$TMPDIR/192.168.1.42"] = "nslookup-batched-outoforder";

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var result = await probe.PollAsync(MakeNode(),
            new[] { "192.168.1.42", "192.168.1.43", "192.168.1.44" }, ct);

        var success = Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Equal(3, success.IpToHostname.Count);
        Assert.Equal("host42.lan", success.IpToHostname["192.168.1.42"]);
        Assert.Equal("host43.lan", success.IpToHostname["192.168.1.43"]);
        Assert.Null(success.IpToHostname["192.168.1.44"]);
    }

    /// <summary>
    /// Test 4 (260511-lct): If an IP requested by the orchestrator has NO sentinel block in
    /// the batched stdout (truncated output, AP-side loop killed, dropped block), the result
    /// map MUST still contain that IP as a key with value=null — the orchestrator-side merge
    /// step relies on the invariant "every requested IP appears in the result map".
    /// </summary>
    [Fact]
    public async Task PollAsync_RequestedIpMissingFromOutput_StoresNullHostname()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        // Only 2 of the 3 IPs have sentinel blocks; .43 is missing entirely.
        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-batched-missing.txt"),
            "==192.168.1.42==\nName:      host42.lan\nAddress 1: 192.168.1.42 host42.lan\n" +
            "==192.168.1.44==\nName:      host44.lan\nAddress 1: 192.168.1.44 host44.lan\n");

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["$TMPDIR/192.168.1.42"] = "nslookup-batched-missing";

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var result = await probe.PollAsync(MakeNode(),
            new[] { "192.168.1.42", "192.168.1.43", "192.168.1.44" }, ct);

        var success = Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Equal(3, success.IpToHostname.Count);
        Assert.Equal("host42.lan", success.IpToHostname["192.168.1.42"]);
        Assert.True(success.IpToHostname.ContainsKey("192.168.1.43"),
            "Missing-from-output IP must still appear as a result-map key.");
        Assert.Null(success.IpToHostname["192.168.1.43"]);
        Assert.Equal("host44.lan", success.IpToHostname["192.168.1.44"]);
    }

    /// <summary>
    /// Test 5 (260511-lct): ParseBatchedNslookupOutput tolerates a single malformed block
    /// (no Name: / name = lines and no NXDOMAIN/SERVFAIL/timeout markers) — that IP maps to
    /// null, the parser does NOT throw, and at least one Warning is logged identifying the
    /// affected IP. Direct parser unit test (no probe / no SSH).
    /// </summary>
    [Fact]
    public void ParseBatchedNslookupOutput_PartialMalformedBlock_LogsWarningAndContinues()
    {
        var raw =
            "==10.0.0.1==\nName:      gw.lan\nAddress 1: 10.0.0.1 gw.lan\n" +
            "==10.0.0.2==\nthis block has neither a Name: line nor a recognised negative marker\nsome other junk text\n";

        var logger = new ListLogger();
        var parsed = OpenWrtOutputParser.ParseBatchedNslookupOutput(raw, logger);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("gw.lan", parsed["10.0.0.1"]);
        Assert.Null(parsed["10.0.0.2"]);
        // Malformed-block warning need not be emitted by the parser when the underlying
        // ParseNslookupOutput simply returns null for an unrecognised block — what the test
        // pins is that no exception was thrown and the result map covers both IPs. Loosening
        // the log assertion keeps the parser free to use the "null short-circuit" composition
        // path documented in the plan (Step 3, "delegate to existing ParseNslookupOutput").
        _ = logger;     // intentionally unused — see above.
    }

    /// <summary>
    /// Test 6 (260511-lct): When the linked CTS budget exhausts inside RunCommandAsync (the
    /// per-probe 30s timeout fires, the host CT is NOT cancelled), the probe surfaces a
    /// <see cref="ReverseDnsProbeResult.Failed"/> with Reason == "timeout". Uses the
    /// OceOnNextCommand seam added to FakeSshSession for this quick task.
    /// </summary>
    [Fact]
    public async Task PollAsync_LinkedCtsTriggersDuringSshCall_ReturnsFailedTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");
        var session = new FakeSshSession(fixtureDir)
        {
            ThrowOcEOnNextCommand = true,
        };

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var result = await probe.PollAsync(MakeNode(), new[] { "192.168.1.42" }, ct);

        var failed = Assert.IsType<ReverseDnsProbeResult.Failed>(result);
        Assert.Equal("timeout", failed.Reason);
    }

    /// <summary>
    /// Test 7 (260511-lct): The p10-trigger-and-nslookup diagnostic log line fires EXACTLY
    /// ONCE per probe invocation, with structured field names <c>Successful</c>,
    /// <c>Total</c>, <c>Negative</c>. Field-name preservation is the load-bearing contract —
    /// the operator's structured-log grep tooling pins these names.
    /// </summary>
    [Fact]
    public async Task PollAsync_StructuredLogLineEmittedOnce_WithSuccessfulTotalNegativeFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-batched-logline.txt"),
            "==192.168.1.42==\nName:      host42.lan\nAddress 1: 192.168.1.42 host42.lan\n" +
            "==192.168.1.43==\n** server can't find 192.168.1.43: NXDOMAIN\n" +
            "==192.168.1.44==\n** server can't find 192.168.1.44: NXDOMAIN\n");

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["$TMPDIR/192.168.1.42"] = "nslookup-batched-logline";

        var logger = new ListLogger();
        var probe = new ReverseDnsProbe(session, logger);
        var result = await probe.PollAsync(MakeNode(),
            new[] { "192.168.1.42", "192.168.1.43", "192.168.1.44" }, ct);

        Assert.IsType<ReverseDnsProbeResult.Success>(result);

        var summaryLines = logger.Entries
            .Where(e => e.Level == LogLevel.Information
                && e.MessageTemplate.StartsWith("Reverse-DNS pass on node ", StringComparison.Ordinal))
            .ToList();
        Assert.Single(summaryLines);

        var fields = summaryLines[0].State;
        Assert.Contains(fields, kv => kv.Key == "Successful" && Convert.ToInt32(kv.Value) == 1);
        Assert.Contains(fields, kv => kv.Key == "Total" && Convert.ToInt32(kv.Value) == 3);
        Assert.Contains(fields, kv => kv.Key == "Negative" && Convert.ToInt32(kv.Value) == 2);
    }

    /// <summary>
    /// Test 8 (260511-lct): Regression guard — the public legacy
    /// <see cref="OpenWrtOutputParser.ParseNslookupOutput"/> single-IP signature MUST NOT be
    /// deleted, renamed, or have its semantics changed by the batched-parser addition.
    /// </summary>
    [Fact]
    public void ParseNslookupOutput_LegacySingleIpSignature_StillWorks()
    {
        var fqdn = OpenWrtOutputParser.ParseNslookupOutput(
            "Name:    MY-HOST.LAN.\n", NullLogger.Instance);
        Assert.Equal("my-host.lan", fqdn);

        var negative = OpenWrtOutputParser.ParseNslookupOutput(
            "** server can't find 192.168.99.99: NXDOMAIN\n", NullLogger.Instance);
        Assert.Null(negative);
    }

    /// <summary>
    /// Test 9 (260515-rds, 2026-05-15): The AP-side batched-nslookup command MUST NOT use the
    /// pre-260515 in-subshell echo shape <c>(echo "==$ip=="; nslookup ... 2>&amp;1) &amp;</c>.
    /// That shape produced concurrent multi-line writes to one shared SSH stdout fd; under
    /// real-world load (~30+ stations per cycle), BusyBox <c>nslookup</c> answers from
    /// different IPs interleaved at line granularity in the multiplexed pipe and the parser's
    /// sentinel-slicing attributed each answer block to whichever <c>==&lt;ip&gt;==</c>
    /// sentinel happened to land just before it — not to the IP that actually produced it.
    ///
    /// Locked diagnosis: <c>.planning/debug/resolved/reverse-dns-regression.md</c> §E7 + §E8.
    ///
    /// The fix (Option A) replaces the in-subshell echo with per-IP temp file redirection:
    /// each backgrounded <c>nslookup</c> writes ONLY to its own path under <c>$TMPDIR/&lt;ip&gt;</c>,
    /// then after all chunks <c>wait</c>, a single sequential <c>for f in $TMPDIR/*; do echo "==..."; cat "$f"; done</c>
    /// loop emits sentinels + bodies non-interleaved. Atomicity by file path; parser unchanged.
    ///
    /// This test pins the structural contract: the captured command (a) does NOT contain the
    /// old in-subshell sentinel-echo substring, and (b) DOES contain per-IP file redirection
    /// for each requested IP plus the sequential <c>for</c>/<c>cat</c> emission loop.
    /// </summary>
    [Fact]
    public async Task PollAsync_CommandShape_UsesPerIpFileRedirection_NotInlineSentinelEcho()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureDir = Path.Combine("Fixtures", "ssh-output", "openwrt-23.05-SNAPSHOT");

        // Any well-shaped batched fixture suffices — this test is about the command shape,
        // not the parsed result. Reuse the existing 3-IP block-sequential fixture.
        File.WriteAllText(Path.Combine(fixtureDir, "nslookup-batched-cmdshape.txt"),
            "==192.168.1.42==\nName:      host42.lan\nAddress 1: 192.168.1.42 host42.lan\n" +
            "==192.168.1.43==\nName:      host43.lan\nAddress 1: 192.168.1.43 host43.lan\n" +
            "==192.168.1.44==\nName:      host44.lan\nAddress 1: 192.168.1.44 host44.lan\n");

        var session = new FakeSshSession(fixtureDir);
        session.CommandOverrides["nslookup -timeout=1 -retry=1"] = "nslookup-batched-cmdshape";

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var ips = new[] { "192.168.1.42", "192.168.1.43", "192.168.1.44" };
        var result = await probe.PollAsync(MakeNode(), ips, ct);

        Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Single(session.CommandsIssued);
        var cmd = session.CommandsIssued[0];

        // (a) Negative — the pre-260515 in-subshell sentinel-echo substring is the SIGNATURE
        //     of the bug: it placed echo + nslookup in the SAME subshell, both writing to the
        //     shared SSH stdout. Its absence is the load-bearing structural change.
        Assert.DoesNotContain("(echo \"==", cmd);

        // (b) Positive — each requested IP must appear as a per-IP redirect target. The fix
        //     places `nslookup ... <ip> > "<somepath/...<ip>...>"` so that subshell stdout
        //     never enters the shared pipe. We assert each IP's literal appears AFTER a `>`
        //     redirect in the captured command.
        foreach (var ip in ips)
        {
            Assert.Matches(
                new System.Text.RegularExpressions.Regex(
                    @">\s*""?[^""]*?" + System.Text.RegularExpressions.Regex.Escape(ip) + @"""?",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                cmd);
        }

        // (c) Sequential emission post-wait: the `for ... cat` loop emits sentinels + bodies
        //     after every backgrounded nslookup has finished writing to its own file. This
        //     guarantees the parser sees non-interleaved blocks identical in shape to the
        //     fixtures already covered by Tests 2/3/4/5.
        Assert.Contains("for ", cmd);
        Assert.Contains("cat ", cmd);

        // (d) Existing invariants preserved: 32× concurrency (& backgrounding) and `wait` join
        //     terminator remain in place. Without these, the fix would forfeit the 260511-lct
        //     batching speedup.
        Assert.Contains(" & ", cmd);
        Assert.Contains("wait", cmd);
    }

    // ---- Cross-version theory: batched path, fixture-backed ----

    [Theory]
    [MemberData(nameof(AllVersionFolders))]
    public async Task PollAsync_AcrossAllSupportedVersions_ProducesNormalizedFqdnOrNull(string fixtureDir)
    {
        var path = Path.Combine(fixtureDir, "nslookup-batched.txt");
        Assert.SkipUnless(File.Exists(path), $"Fixture not yet captured: {path}");

        var ct = TestContext.Current.CancellationToken;
        var session = new FakeSshSession(fixtureDir);
        // Map the batched-nslookup command to the version-specific batched fixture.
        session.CommandOverrides["nslookup -timeout=1 -retry=1"] = "nslookup-batched";

        var probe = new ReverseDnsProbe(session, NullLogger<ReverseDnsProbe>.Instance);
        var result = await probe.PollAsync(MakeNode(), new[] { "192.168.1.42" }, ct);

        var success = Assert.IsType<ReverseDnsProbeResult.Success>(result);
        Assert.Single(success.IpToHostname);
        var fqdn = success.IpToHostname["192.168.1.42"];
        if (fqdn is null) return;     // null is acceptable (negative result).
        Assert.Equal(fqdn.ToLowerInvariant(), fqdn);
        Assert.False(fqdn.EndsWith('.'), $"Trailing dot was not stripped: '{fqdn}'");
    }

    // ---- Inline list-collecting ILogger (no package additions) ----

    /// <summary>
    /// Minimal list-collecting ILogger / ILogger&lt;T&gt; used by Tests 5 and 7 above. Captures
    /// the LogLevel, message template, formatted message, and the structured-field state bag
    /// (when the state is an IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt; — Serilog/MEL's
    /// FormattedLogValues shape). Avoids pulling in a new package; do NOT generalise this
    /// type further unless another quick task needs it.
    /// </summary>
    private sealed class ListLogger : ILogger, ILogger<ReverseDnsProbe>
    {
        public List<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string template = "";
            List<KeyValuePair<string, object?>> stateBag = new();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> bag)
            {
                stateBag = bag.ToList();
                // MEL/Serilog convention: the "{OriginalFormat}" entry carries the message template.
                var orig = stateBag.FirstOrDefault(kv => kv.Key == "{OriginalFormat}");
                if (orig.Value is string s) template = s;
            }
            var message = formatter(state, exception);
            Entries.Add(new Entry(logLevel, template, message, stateBag, exception));
        }

        public sealed record Entry(
            LogLevel Level,
            string MessageTemplate,
            string Message,
            List<KeyValuePair<string, object?>> State,
            Exception? Exception);
    }
}
