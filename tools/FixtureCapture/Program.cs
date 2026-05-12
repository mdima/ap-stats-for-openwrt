// Plan 02-04 Task 1 — FixtureCapture tool.
//
// Reads ApStatsForOpenWrt.Tests/Fixtures/.live-credentials.local.env (gitignored, D-10 secret hygiene),
// SSHes into a real OpenWRT AP via the production RealSshSession, runs the canonical command set,
// and writes verbatim outputs to ApStatsForOpenWrt.Tests/Fixtures/ssh-output/openwrt-{detected}/.
//
// Filename rule MUST match FakeSshSession.ToFilename exactly (W4 LOCK from Plan 02-02):
//     command.Trim().Replace(' ', '-').Replace('/', '-') + ".txt"
//
// Secret hygiene (T-2-12): the password value is read from the .local.env file, passed ONLY to
// RealSshSession.ConnectAsync, and is NEVER printed to stdout/stderr. Diagnostic prints emit only
// hostname + username + non-secret state.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling;
using ApStatsForOpenWrt.Services.Polling.Ssh;

// ---------- locate credentials ----------
var repoRoot = FindRepoRoot();
var envPath = Path.Combine(repoRoot, "ApStatsForOpenWrt.Tests", "Fixtures", ".live-credentials.local.env");
if (!File.Exists(envPath))
{
    Console.Error.WriteLine($"FATAL: credentials file not found at {envPath}");
    Console.Error.WriteLine("Copy .live-credentials.local.env.example to .live-credentials.local.env and fill with real values.");
    return 1;
}

Dictionary<string, string> env;
try
{
    env = ParseEnv(envPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: failed to parse {envPath}: {ex.Message}");
    return 1;
}

string host, user, password;
try
{
    host = Require(env, "OPENWRT_HOST");
    user = Require(env, "OPENWRT_USER");
    password = Require(env, "OPENWRT_PASSWORD");
}
catch (InvalidOperationException ex)
{
    // T-2-12: never echo the password value here; the message comes from Require which only names the key.
    Console.Error.WriteLine($"FATAL: {ex.Message}");
    return 1;
}

// T-2-12: from this point onwards, ONLY emit host + user — never the password.
Console.WriteLine($"Connecting to {host} as {user} ...");

// ---------- build session ----------
var node = new WiFiNode { Hostname = host, Username = user, Password = password };
using var sessionLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var sessionLogger = sessionLoggerFactory.CreateLogger<RealSshSession>();

// W10 LOCK -- explicit try/finally with await session.DisposeAsync() in finally; mirrors NodePoller's
// belt+braces dispose pattern from CLAUDE.md Conventions (SSH.NET dispose-hang risk).
var session = new RealSshSession(sessionLogger);
string? capturedFp = null;
try
{
    await session.ConnectAsync(node, password,
        expectedFingerprint: null,                       // first-connect TOFU
        onHostKeyAccepted: fp => capturedFp = fp,
        ct: CancellationToken.None);

    Console.WriteLine($"Connected. Host key fingerprint: {capturedFp ?? "(unknown)"}");

    // ---------- detect version ----------
    var releaseRaw = await session.RunCommandAsync("cat /etc/openwrt_release", CancellationToken.None);
    var version = ExtractDistribRelease(releaseRaw);
    if (string.IsNullOrEmpty(version))
    {
        Console.Error.WriteLine("FATAL: could not extract DISTRIB_RELEASE from /etc/openwrt_release.");
        Console.Error.WriteLine("Raw output (truncated):");
        Console.Error.WriteLine(releaseRaw.Length > 500 ? releaseRaw[..500] + "..." : releaseRaw);
        return 1;
    }

    var fixtureDir = Path.Combine(repoRoot, "ApStatsForOpenWrt.Tests", "Fixtures", "ssh-output", $"openwrt-{version}");
    Directory.CreateDirectory(fixtureDir);
    Console.WriteLine($"Writing to {fixtureDir}");

    // ---------- canonical command set ----------
    // Persist /etc/openwrt_release first (we already have its raw content).
    await CaptureCore(fixtureDir, "cat /etc/openwrt_release", releaseRaw, exitCode: null);

    // Step: capability probe + exit code.
    var iwinfoProbe = await session.RunCommandWithExitCodeAsync("command -v iwinfo", CancellationToken.None);
    await CaptureCore(fixtureDir, "command -v iwinfo", iwinfoProbe.StdOut, iwinfoProbe.ExitCode);

    var useIwinfo = iwinfoProbe.ExitCode == 0 && !string.IsNullOrWhiteSpace(iwinfoProbe.StdOut);

    // Top-level WLAN summary — iwinfo (preferred) or iw dev fallback.
    var iwinfoRaw = useIwinfo
        ? await session.RunCommandAsync("iwinfo", CancellationToken.None)
        : await session.RunCommandAsync("iw dev", CancellationToken.None);
    await CaptureCore(fixtureDir, useIwinfo ? "iwinfo" : "iw dev", iwinfoRaw, exitCode: null);

    // ALWAYS capture iw dev too — public-sample-fallback path testing benefits from both fixtures.
    string iwDevRaw;
    if (useIwinfo)
    {
        iwDevRaw = await session.RunCommandAsync("iw dev", CancellationToken.None);
        await CaptureCore(fixtureDir, "iw dev", iwDevRaw, exitCode: null);
    }
    else
    {
        iwDevRaw = iwinfoRaw;
    }

    // ---------- discover ifaces and per-iface assoclist ----------
    ILogger nullLogger = NullLogger.Instance;
    var networks = useIwinfo
        ? OpenWrtOutputParser.ParseIwinfoSummary(iwinfoRaw, nullLogger)
        : OpenWrtOutputParser.ParseIwDevSummary(iwinfoRaw, nullLogger);

    var ifaces = ExtractIfaceNames(networks, useIwinfo ? iwinfoRaw : iwDevRaw);
    Console.WriteLine($"Discovered {ifaces.Count} WLAN iface(s): {string.Join(", ", ifaces)}");

    foreach (var iface in ifaces)
    {
        try
        {
            var assocCmd = useIwinfo ? $"iwinfo {iface} assoclist" : $"iw dev {iface} station dump";
            var assoc = await session.RunCommandAsync(assocCmd, CancellationToken.None);
            await CaptureCore(fixtureDir, assocCmd, assoc, exitCode: null);

            // Per-station detail.
            var stationsForIface = useIwinfo
                ? OpenWrtOutputParser.ParseIwinfoAssocList(assoc, 0, nullLogger)
                : OpenWrtOutputParser.ParseIwDevStations(assoc, 0, nullLogger);
            foreach (var s in stationsForIface)
            {
                var perStationCmd = $"iw dev {iface} station get {s.MacAddress}";
                try
                {
                    var perStation = await session.RunCommandAsync(perStationCmd, CancellationToken.None);
                    await CaptureCore(fixtureDir, perStationCmd, perStation, exitCode: null);
                }
                catch (Exception inner)
                {
                    Console.Error.WriteLine($"WARN: per-station capture failed for '{perStationCmd}': {inner.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN: failed capture for iface '{iface}': {ex.Message}");
        }
    }

    // ip neigh.
    var neigh = await session.RunCommandAsync("ip neigh show", CancellationToken.None);
    await CaptureCore(fixtureDir, "ip neigh show", neigh, exitCode: null);

    // ---------- SOURCE.md ----------
    var sourceMd = $"""
        # Fixture: openwrt-{version}

        provenance: live-capture
        captured: {DateTime.UtcNow:yyyy-MM-dd}
        captured_by: operator (hostname redacted)
        openwrt_release: |
        {IndentBlock(releaseRaw, "  ")}
        notes: |
          - Captured by tools/FixtureCapture (Plan 02-04).
          - Host key fingerprint at capture time: {capturedFp ?? "(unknown)"}
          - Ifaces discovered: {string.Join(", ", ifaces)}
          - Operator review required BEFORE commit:
            * Hostnames -> REDACTED (or generic placeholder).
            * Real-vendor station MACs -> locally-administered OUI (02:xx, 06:xx, 0A:xx, 0E:xx).
            * Public IPv4 -> 192.0.2.x (TEST-NET-1); RFC-1918 (10.x, 172.16-31.x, 192.168.x) keep verbatim.
            * AP own BSSIDs (device under test) MAY remain; document the choice here.
          - Document each rewrite below once the redaction pass is complete.
        """;
    var sourcePath = Path.Combine(fixtureDir, "SOURCE.md");
    await File.WriteAllTextAsync(sourcePath, sourceMd);
    Console.WriteLine($"Wrote {sourcePath}");

    Console.WriteLine();
    Console.WriteLine("Capture complete. NEXT STEPS (manual, before commit):");
    Console.WriteLine("  1. Review every committed file for identifying info (hostnames, vendor MACs, public IPs).");
    Console.WriteLine("  2. Apply RESEARCH.md Redaction policy where needed.");
    Console.WriteLine("  3. Document any rewrites in SOURCE.md.");
    Console.WriteLine("  4. Commit ONLY after manual review.");

    return 0;
}
finally
{
    // W10 LOCK -- belt+braces explicit dispose. Mirrors NodePoller's pattern; RealSshSession
    // already swallows hangs internally, but the explicit try/catch here is regression insurance
    // against any future RealSshSession refactor that might forget the swallow-catch.
    try { await session.DisposeAsync(); } catch { /* swallow -- SSH.NET dispose-hang risk */ }
}

// ============================================================
// Local helpers (file-scope methods)
// ============================================================

static async Task CaptureCore(string fixtureDir, string command, string content, int? exitCode)
{
    var name = ToFilename(command);
    var path = Path.Combine(fixtureDir, name);
    await File.WriteAllTextAsync(path, content);
    Console.WriteLine($"Wrote {path} ({content.Length} bytes)");
    if (exitCode is { } ec)
    {
        await File.WriteAllTextAsync(path + ".exit", ec.ToString());
        Console.WriteLine($"Wrote {path}.exit (exit code: {ec})");
    }
}

static string ToFilename(string command)
{
    // W4 LOCK extended in Plan 02-04: also replace ':' (forbidden in Windows filenames per
    // https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file#naming-conventions)
    // and collapse runs of dashes so leading-slash + space combos produce clean output:
    //   "cat /etc/openwrt_release" -> "cat-etc-openwrt_release.txt"
    //   "iw dev wlan0 station get AA:BB:CC:DD:EE:01" -> "iw-dev-wlan0-station-get-AA-BB-CC-DD-EE-01.txt"
    // FakeSshSession.ToFilename mirrors this logic exactly so fixtures captured by this tool can be
    // replayed verbatim by the parser tests.
    var s = command.Trim().Replace(' ', '-').Replace('/', '-').Replace(':', '-');
    while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-");
    return s.Trim('-') + ".txt";
}

static Dictionary<string, string> ParseEnv(string path)
{
    var dict = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var eq = line.IndexOf('=');
        if (eq < 0) continue;
        var k = line[..eq].Trim();
        var v = line[(eq + 1)..].Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
        dict[k] = v;
    }
    return dict;
}

static string Require(Dictionary<string, string> env, string key)
{
    if (!env.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
        throw new InvalidOperationException($"Missing or empty {key} in .live-credentials.local.env");
    return v;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ApStatsForOpenWrt.sln")))
        dir = dir.Parent;
    if (dir == null)
        throw new InvalidOperationException("Could not locate repo root (no ApStatsForOpenWrt.sln found in any ancestor).");
    return dir.FullName;
}

static string? ExtractDistribRelease(string releaseFile)
{
    foreach (var rawLine in releaseFile.Replace("\r\n", "\n").Split('\n'))
    {
        var line = rawLine.Trim();
        if (!line.StartsWith("DISTRIB_RELEASE", StringComparison.Ordinal)) continue;
        var eq = line.IndexOf('=');
        if (eq < 0) continue;
        var v = line[(eq + 1)..].Trim().Trim('\'').Trim('"');
        return v;
    }
    return null;
}

static List<string> ExtractIfaceNames(IReadOnlyList<WiFiNetwork> networks, string raw)
{
    // W3 LOCK -- direct .IfaceName access. The fallback regex is retained ONLY for malformed
    // public-sample fixtures where the parser might emit networks with null IfaceName.
    var fromParser = networks
        .Select(n => n.IfaceName)
        .Where(s => !string.IsNullOrEmpty(s))
        .Distinct(StringComparer.Ordinal)
        .Cast<string>()
        .ToList();
    if (fromParser.Count > 0) return fromParser;

    // Fallback regex scan -- only hit if parser emitted no IfaceName (malformed input).
    var ifaces = new HashSet<string>(StringComparer.Ordinal);
    foreach (System.Text.RegularExpressions.Match m in
             System.Text.RegularExpressions.Regex.Matches(raw, @"\bwlan\d+\b"))
    {
        ifaces.Add(m.Value);
    }
    return ifaces.ToList();
}

static string IndentBlock(string content, string indent)
{
    return string.Join('\n', content.Replace("\r\n", "\n").Split('\n').Select(l => indent + l));
}
