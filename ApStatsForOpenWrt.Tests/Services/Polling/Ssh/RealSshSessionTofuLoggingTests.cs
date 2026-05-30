using System.Text.RegularExpressions;
using ApStatsForOpenWrt.Services.Polling.Ssh;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Services.Polling.Ssh;

/// <summary>
/// M-SEC-06 (Plan 16-03) unit tests pinning the TOFU-capture Warning shape and the
/// branch isolation of <see cref="RealSshSession.OnHostKeyReceived"/> (match/mismatch
/// paths emit no TOFU log).
///
/// <para>
/// Tests 1-3 drive the <see cref="RealSshSession.EmitM_SEC_06_TofuCapture"/> internal
/// static helper directly — the SSH.NET <c>HostKeyEventArgs</c> is sealed and cannot be
/// faked cheaply, so the helper is the testable seam (parallel to 16-02's
/// <c>EmitM_SEC_08_Logs</c>). InternalsVisibleTo("ApStatsForOpenWrt.Tests") is granted in
/// ApStatsForOpenWrt.csproj.
/// </para>
///
/// <para>
/// Test 4 is a source-text gate: it confirms the helper is called exactly once inside the
/// TOFU branch and that no direct <c>_log.Log*</c> emission exists inside
/// <c>OnHostKeyReceived</c>. The template is asserted VERBATIM from CONTEXT.md Decision 1.
/// </para>
/// </summary>
[Trait("Category", "SecurityFix")]
public sealed class RealSshSessionTofuLoggingTests
{
    private const string TofuTemplate =
        "M-SEC-06: SSH host-key TOFU capture: NodeId={NodeId} Hostname={Hostname} Fingerprint={Fingerprint} (verify out-of-band: ssh-keyscan {Hostname} | ssh-keygen -lf -)";

    private static CapturingLogger Emit(int nodeId, string hostname, string fingerprint)
    {
        var logger = new CapturingLogger();
        RealSshSession.EmitM_SEC_06_TofuCapture(logger, nodeId, hostname, fingerprint);
        return logger;
    }

    [Fact]
    public void TofuCapture_EmitsWarning_WithVerbatimTemplate()
    {
        var logger = Emit(nodeId: 42, hostname: "ap-1.local", fingerprint: "SHA256:abc123...");

        var rec = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, rec.Level);
        Assert.Equal(TofuTemplate, rec.Template);

        // The template references {Hostname} twice (the Hostname= field + the recipe), so the
        // structured-args list carries the hostname value in TWO positions. Confirm both
        // positions resolve to the same string.
        var hostnameValues = rec.OrderedArgs
            .Where(kvp => kvp.Key == "Hostname")
            .Select(kvp => kvp.Value as string)
            .ToList();
        Assert.Equal(2, hostnameValues.Count);
        Assert.All(hostnameValues, v => Assert.Equal("ap-1.local", v));

        Assert.Equal(42, rec.NamedArg("NodeId"));
        Assert.Equal("SHA256:abc123...", rec.NamedArg("Fingerprint"));
    }

    [Fact]
    public void TofuCapture_EmptyHostname_NoExceptionAndArgsPreserved()
    {
        // Defensive: an empty hostname must not throw; the Warning still fires.
        var logger = Emit(nodeId: 7, hostname: "", fingerprint: "SHA256:def456");

        var rec = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, rec.Level);
        Assert.Equal(7, rec.NamedArg("NodeId"));
        Assert.Equal("SHA256:def456", rec.NamedArg("Fingerprint"));

        var hostnameValues = rec.OrderedArgs
            .Where(kvp => kvp.Key == "Hostname")
            .Select(kvp => kvp.Value as string)
            .ToList();
        Assert.Equal(2, hostnameValues.Count);
        Assert.All(hostnameValues, v => Assert.Equal("", v));
    }

    [Fact]
    public void TofuCapture_LogLevelIsExactlyWarning()
    {
        var logger = Emit(nodeId: 1, hostname: "ap-x", fingerprint: "SHA256:zzz");

        var rec = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, rec.Level);
        Assert.NotEqual(LogLevel.Information, rec.Level);
        Assert.NotEqual(LogLevel.Error, rec.Level);
    }

    [Fact]
    public void OnHostKeyReceived_OnlyTofuBranchLogs_SourceGate()
    {
        var source = ReadRealSshSessionSource();
        var body = ExtractOnHostKeyReceivedBody(source);

        // The helper is called exactly once, inside the TOFU branch.
        Assert.Equal(1, CountOccurrences(body, "EmitM_SEC_06_TofuCapture"));

        // No direct logger emission inside the method — the only emission is via the helper.
        Assert.Equal(0, CountOccurrences(body, "_log.LogWarning"));
        Assert.Equal(0, CountOccurrences(body, "_log.LogInformation"));
        Assert.Equal(0, CountOccurrences(body, "_log.LogError"));

        // The TOFU branch guard appears exactly once — confirms the helper call lives inside it.
        Assert.Equal(1, CountOccurrences(body, "_expectedFingerprint is null"));

        // The full template lives in the helper (not in OnHostKeyReceived) — assert the helper
        // template is present in the file as a whole, pinned verbatim.
        Assert.Contains(TofuTemplate, source, StringComparison.Ordinal);
    }

    // ── Source-gate helpers ──────────────────────────────────────────────────────

    private static string ReadRealSshSessionSource()
    {
        // Walk up from the test binary's base directory to the repo root (marked by
        // Directory.Build.props — robust across container builds and worktrees without .git),
        // mirroring ArchitectureTests.NoCsprojDeclaresVersion_DirectoryBuildPropsIsTheOnlySource.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(
            dir!.FullName,
            "ApStatsForOpenWrt", "Services", "Polling", "Ssh", "RealSshSession.cs");
        Assert.True(File.Exists(path), $"Could not locate RealSshSession.cs at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Extract the body of <c>OnHostKeyReceived</c> by brace-matching from its signature to the
    /// matching closing brace. This bounds the assertions to the handler method only, so the
    /// helper template (which lives in EmitM_SEC_06_TofuCapture, elsewhere in the file) does not
    /// leak into the branch-isolation counts.
    /// </summary>
    private static string ExtractOnHostKeyReceivedBody(string source)
    {
        var sigMatch = Regex.Match(
            source,
            @"private void OnHostKeyReceived\(object\? sender, HostKeyEventArgs e\)");
        Assert.True(sigMatch.Success, "OnHostKeyReceived signature not found (or changed shape)");

        var openBrace = source.IndexOf('{', sigMatch.Index + sigMatch.Length);
        Assert.True(openBrace >= 0, "Opening brace of OnHostKeyReceived not found");

        var depth = 0;
        for (var i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(openBrace, i - openBrace + 1);
            }
        }

        Assert.Fail("Matching closing brace of OnHostKeyReceived not found");
        return string.Empty; // unreachable
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    // ── Capture sink ───────────────────────────────────────────────────────────

    private sealed record CapturedLog(
        LogLevel Level, string Template, IReadOnlyList<KeyValuePair<string, object?>> OrderedArgs)
    {
        /// <summary>First-occurrence lookup of a named structured arg.</summary>
        public object? NamedArg(string key) =>
            OrderedArgs.First(kvp => kvp.Key == key).Value;
    }

    /// <summary>
    /// Minimal <see cref="ILogger"/> capturing each <c>Log(...)</c> call's level, message
    /// template (the <c>{OriginalFormat}</c> entry), and the ordered structured arguments.
    /// The ordered list (not a dictionary) is retained deliberately so a template that names
    /// the same slot twice (e.g. {Hostname}) preserves BOTH positional values.
    /// </summary>
    private sealed class CapturingLogger : ILogger<RealSshSession>
    {
        public List<CapturedLog> Records { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var template = "";
            var args = new List<KeyValuePair<string, object?>>();

            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kvp in kvps)
                {
                    if (kvp.Key == "{OriginalFormat}")
                        template = kvp.Value as string ?? "";
                    else
                        args.Add(kvp);
                }
            }

            Records.Add(new CapturedLog(logLevel, template, args));
        }
    }
}
