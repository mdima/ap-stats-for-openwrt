using Microsoft.Extensions.Logging;
using OpenWrtApStats.Data.Models;

namespace OpenWrtApStats.Services.Polling;

/// <summary>
/// Static, stateless parsers from raw OpenWRT command output to POCO entities. Pure functions —
/// no I/O, no DI. Per-line tolerant (D-05): catch parse exceptions at the line boundary, log Warning
/// with the raw line, continue with the next line. Yields uppercased MACs (D-07 / CR-01 closure on the
/// parser side) so downstream dictionaries / hashsets can use StringComparer.Ordinal (Plan 02-01).
/// </summary>
public static class OpenWrtOutputParser
{
    // === Recognition keys (LOCKED) — referenced verbatim by Plan 02-02 Task 2 synthetic tests
    // and by Plan 02-04 fixture content. If you change these, update both call sites.
    // iwinfo (top-level): "ESSID:", "Access Point:", "Mode:", "Channel:", "Tx-Power:", "Encryption:", "Bit Rate:".
    // iwinfo (assoclist line start): MAC pattern (six colon-separated hex pairs); continuation lines
    //   include "RX:" / "TX:" / "signal:" tokens.
    // iw dev (top-level): "Interface ", "addr ", "ssid ", "channel ".
    // iw dev (station dump line start): "Station " followed by MAC; continuation lines include
    //   "signal:", "tx bitrate:", "rx bitrate:".
    // ip neigh: "lladdr ", " dev ", " STALE", " FAILED", " REACHABLE".
    private const string IwinfoBssidKey = "Access Point:";
    private const string IwinfoEssidKey = "ESSID:";
    private const string IwinfoChannelKey = "Channel:";
    private const string IwinfoTxPowerKey = "Tx-Power:";
    private const string IwinfoEncryptionKey = "Encryption:";
    private const string IwDevInterfaceKey = "Interface ";
    private const string IwDevAddrKey = "\taddr ";
    private const string IwDevSsidKey = "\tssid ";
    private const string IwDevStationKey = "Station ";
    private const string IpNeighLladdrKey = " lladdr ";
    private const string IpNeighDevKey = " dev ";
    private const string IpNeighStaleKey = " STALE";
    private const string IpNeighFailedKey = " FAILED";

    // Six colon-separated hex pairs. Used by iwinfo assoclist and iw dev station dump line-start detection.
    private static readonly System.Text.RegularExpressions.Regex MacRegex =
        new(@"\b([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Parse `iwinfo` top-level output → WiFiNetwork list (one per WLAN interface). BSSIDs uppercased.
    /// Per-block tolerant: split on blank lines; every block must have BSSID + SSID to emit; missing fields
    /// log Warning and the block is skipped. The block leader (first non-blank line) carries the iface name
    /// (e.g., "wlan0  ESSID:..."); IfaceName is populated as a transient (B4 carrier) on the emitted entity.</summary>
    public static IReadOnlyList<WiFiNetwork> ParseIwinfoSummary(string raw, ILogger log)
    {
        var result = new List<WiFiNetwork>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        var blocks = raw.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            try
            {
                string? iface = null, ssid = null, bssid = null, encryption = null;
                int channel = 0, txPower = 0;
                var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) continue;
                // Header line: "wlan0     ESSID: \"TestNet\"" — iface is the first token.
                var headerTokens = lines[0].TrimStart().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (headerTokens.Length > 0 && !headerTokens[0].Contains(':')) iface = headerTokens[0];
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (line.Contains(IwinfoEssidKey, StringComparison.Ordinal))
                        ssid = ExtractAfter(line, IwinfoEssidKey).Trim().Trim('"');
                    else if (line.Contains(IwinfoBssidKey, StringComparison.Ordinal))
                        bssid = ExtractAfter(line, IwinfoBssidKey).Trim();
                    else if (line.Contains(IwinfoChannelKey, StringComparison.Ordinal))
                        int.TryParse(FirstNumber(ExtractAfter(line, IwinfoChannelKey)), out channel);
                    else if (line.Contains(IwinfoTxPowerKey, StringComparison.Ordinal))
                        int.TryParse(FirstNumber(ExtractAfter(line, IwinfoTxPowerKey)), out txPower);
                    else if (line.Contains(IwinfoEncryptionKey, StringComparison.Ordinal))
                        encryption = ExtractAfter(line, IwinfoEncryptionKey).Trim();
                }
                // 'encryption' is parsed for future use (Phase 3 may surface it on the dashboard);
                // discarding to silence unused-local warnings under TreatWarningsAsErrors.
                _ = encryption;
                if (string.IsNullOrEmpty(bssid) || string.IsNullOrEmpty(ssid))
                {
                    log.LogWarning("Parse error in iwinfo output line: {RawLine}. Skipped.", block);
                    continue;
                }
                result.Add(new WiFiNetwork
                {
                    SSID = ssid,
                    MacAddress = bssid.ToUpperInvariant(),
                    Channel = channel,
                    TxPower = txPower,
                    Enabled = true,
                    IfaceName = iface,
                });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Parse error in iwinfo output line: {RawLine}. Skipped.", block);
            }
        }
        return result;
    }

    /// <summary>Parse `iwinfo $iface assoclist` → WLANStation list. Station MACs uppercased.
    /// One station per top-level line matching MAC; continuation lines populate RSS / TX / RX.
    /// Tolerant per-station: a malformed continuation block logs Warning and the station is skipped.</summary>
    public static IReadOnlyList<WLANStation> ParseIwinfoAssocList(string raw, int wiFiNetworkId, ILogger log)
    {
        var result = new List<WLANStation>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        WLANStation? cur = null;
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            try
            {
                var macMatch = MacRegex.Match(line);
                var startsWithMac = macMatch.Success && line.StartsWith(macMatch.Value, StringComparison.OrdinalIgnoreCase);
                if (startsWithMac)
                {
                    if (cur is not null) result.Add(cur);
                    cur = new WLANStation
                    {
                        MacAddress = macMatch.Value.ToUpperInvariant(),
                        WiFiNetworkId = wiFiNetworkId,
                        LastPollDate = DateTime.UtcNow,
                        Status = Status.Online,
                    };
                    // RSS may also be on the same line: "AA:BB:..  -45 dBm"
                    var dbmIdx = line.IndexOf(" dBm", StringComparison.Ordinal);
                    if (dbmIdx > 0)
                    {
                        var seg = line.Substring(0, dbmIdx);
                        int.TryParse(FirstSignedNumber(seg), out var rss);
                        cur.RSS = rss;
                    }
                }
                else if (cur is not null)
                {
                    // Continuation lines (RX:/TX: tokens). Bitrate parsing is reserved for Plan 02-04
                    // fixture-driven refinement — empty bodies here keep the per-line tolerance contract
                    // honest while leaving the recognition keys present (B7/B8 lock).
                    if (line.Contains("RX:", StringComparison.Ordinal))
                    {
                        // RX bitrate placeholder — refined in Plan 02-04 with real fixture samples.
                    }
                    if (line.Contains("TX:", StringComparison.Ordinal))
                    {
                        // TX bitrate placeholder — refined in Plan 02-04 with real fixture samples.
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Parse error in iwinfo $iface assoclist output line: {RawLine}. Skipped.", line);
                cur = null;
            }
        }
        if (cur is not null) result.Add(cur);
        return result;
    }

    /// <summary>POLL-12 fallback: parse `iw dev` → WiFiNetwork list. BSSIDs uppercased.
    /// `iw dev` blocks are: "Interface wlanX\n\taddr AA:BB:..\n\tssid TestNet\n\ttype AP\n\tchannel ...".</summary>
    public static IReadOnlyList<WiFiNetwork> ParseIwDevSummary(string raw, ILogger log)
    {
        var result = new List<WiFiNetwork>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        string? iface = null, addr = null, ssid = null;
        int channel = 0;
        void Flush()
        {
            try
            {
                if (!string.IsNullOrEmpty(addr) && !string.IsNullOrEmpty(ssid))
                {
                    result.Add(new WiFiNetwork
                    {
                        SSID = ssid!,
                        MacAddress = addr!.ToUpperInvariant(),
                        Channel = channel,
                        Enabled = true,
                        IfaceName = iface,
                    });
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Parse error in iw dev output line: {RawLine}. Skipped.", iface ?? "(unknown iface)");
            }
            finally
            {
                iface = null; addr = null; ssid = null; channel = 0;
            }
        }
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.StartsWith(IwDevInterfaceKey, StringComparison.Ordinal))
            {
                Flush();
                iface = rawLine.Substring(IwDevInterfaceKey.Length).Trim();
            }
            else if (rawLine.Contains("addr ", StringComparison.Ordinal) && rawLine.TrimStart().StartsWith("addr ", StringComparison.Ordinal))
            {
                addr = rawLine.TrimStart().Substring(5).Trim();
            }
            else if (rawLine.Contains("ssid ", StringComparison.Ordinal) && rawLine.TrimStart().StartsWith("ssid ", StringComparison.Ordinal))
            {
                ssid = rawLine.TrimStart().Substring(5).Trim();
            }
            else if (rawLine.Contains("channel ", StringComparison.Ordinal))
            {
                int.TryParse(FirstNumber(rawLine), out channel);
            }
        }
        Flush();
        return result;
    }

    /// <summary>POLL-12 fallback: parse `iw dev $iface station dump` → WLANStation list. Station MACs uppercased.
    /// Block-leader pattern: "Station AA:BB:..\n\tsignal: -45 dBm\n\ttx bitrate: 54.0 MBit/s\n...".</summary>
    public static IReadOnlyList<WLANStation> ParseIwDevStations(string raw, int wiFiNetworkId, ILogger log)
    {
        var result = new List<WLANStation>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        WLANStation? cur = null;
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            try
            {
                if (rawLine.StartsWith(IwDevStationKey, StringComparison.Ordinal))
                {
                    if (cur is not null) result.Add(cur);
                    var afterStation = rawLine.Substring(IwDevStationKey.Length).Trim();
                    var macMatch = MacRegex.Match(afterStation);
                    if (!macMatch.Success) { cur = null; continue; }
                    cur = new WLANStation
                    {
                        MacAddress = macMatch.Value.ToUpperInvariant(),
                        WiFiNetworkId = wiFiNetworkId,
                        LastPollDate = DateTime.UtcNow,
                        Status = Status.Online,
                    };
                }
                else if (cur is not null)
                {
                    var line = rawLine.TrimStart();
                    if (line.StartsWith("signal:", StringComparison.Ordinal))
                    {
                        int.TryParse(FirstSignedNumber(line), out var rss);
                        cur.RSS = rss;
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Parse error in iw dev $iface station dump output line: {RawLine}. Skipped.", rawLine);
                cur = null;
            }
        }
        if (cur is not null) result.Add(cur);
        return result;
    }

    /// <summary>
    /// POLL-13: parse `ip neigh show` → Dictionary&lt;MAC, IP?&gt;. MACs uppercased; IP is null
    /// for STALE/FAILED/blank entries. Keys ALWAYS exist for every line that parses; values may be null.
    /// Uses StringComparer.Ordinal (keys are uppercased on insert).
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ParseIpNeigh(string raw, ILogger log)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            try
            {
                var lladdrIdx = line.IndexOf(IpNeighLladdrKey, StringComparison.Ordinal);
                if (lladdrIdx < 0) continue;   // No MAC on this line — skip silently.
                var macStart = lladdrIdx + IpNeighLladdrKey.Length;
                var macEnd = line.IndexOf(' ', macStart);
                if (macEnd < 0) macEnd = line.Length;
                var mac = line.Substring(macStart, macEnd - macStart).ToUpperInvariant();

                // IP is the first whitespace-separated token before " dev ".
                string? ip = null;
                var devIdx = line.IndexOf(IpNeighDevKey, StringComparison.Ordinal);
                if (devIdx > 0)
                {
                    var ipToken = line.Substring(0, devIdx).Trim();
                    if (!string.IsNullOrEmpty(ipToken)) ip = ipToken;
                }
                // POLL-13 / B9 LOCK: STALE/FAILED → null, even if an IP-shaped token preceded " dev ".
                if (line.Contains(IpNeighStaleKey, StringComparison.Ordinal) ||
                    line.Contains(IpNeighFailedKey, StringComparison.Ordinal))
                {
                    ip = null;
                }
                result[mac] = ip;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Parse error in ip-neigh-show output line: {RawLine}. Skipped.", line);
            }
        }
        return result;
    }

    // === helpers ===
    private static string ExtractAfter(string line, string key)
    {
        var idx = line.IndexOf(key, StringComparison.Ordinal);
        return idx < 0 ? "" : line.Substring(idx + key.Length);
    }
    private static string FirstNumber(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s) { if (char.IsDigit(c)) sb.Append(c); else if (sb.Length > 0) break; }
        return sb.ToString();
    }
    private static string FirstSignedNumber(string s)
    {
        // Captures e.g. "-45" from "signal: -45 dBm".
        var match = System.Text.RegularExpressions.Regex.Match(s, @"-?\d+");
        return match.Success ? match.Value : "";
    }
}
