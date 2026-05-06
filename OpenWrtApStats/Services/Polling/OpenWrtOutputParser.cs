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
                if (string.IsNullOrEmpty(bssid) || string.IsNullOrEmpty(ssid)
                    || bssid.Equals("00:00:00:00:00:00", StringComparison.Ordinal)
                    || ssid.Equals("unknown", StringComparison.OrdinalIgnoreCase))
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
                    // RSS may also be on the same line: "A4:CF:12:..  -67 dBm".
                    // Only assign when parse succeeds — null means absent, not zero (CR-03).
                    // Search AFTER the MAC — MAC bytes contain decimal digits (e.g. "A4" → "4")
                    // that FirstSignedNumber would otherwise capture before the real RSS value.
                    var afterMac = line.Substring(macMatch.Index + macMatch.Length);
                    var dbmIdx = afterMac.IndexOf(" dBm", StringComparison.Ordinal);
                    if (dbmIdx > 0)
                    {
                        var seg = afterMac.Substring(0, dbmIdx);
                        if (int.TryParse(FirstSignedNumber(seg), out var rss))
                            cur.RSS = rss;
                    }
                }
                else if (cur is not null)
                {
                    // Continuation lines: "\tRX: 144.4 MBit/s, MCS 7, 20MHz   31337 Pkts."
                    //                     "\tTX: 144.4 MBit/s, MCS 7, 20MHz   12345 Pkts."
                    // Extract the first decimal number after the recognition key. B7/B8 lock —
                    // recognition keys remain present in the source for grep contracts.
                    if (line.Contains("RX:", StringComparison.Ordinal))
                    {
                        var rxIdx = line.IndexOf("RX:", StringComparison.Ordinal);
                        if (FirstDecimalBeforeMbitS(line.Substring(rxIdx + 3)) is { } rx)
                            cur.RxBitrate = rx;
                    }
                    if (line.Contains("TX:", StringComparison.Ordinal))
                    {
                        var txIdx = line.IndexOf("TX:", StringComparison.Ordinal);
                        if (FirstDecimalBeforeMbitS(line.Substring(txIdx + 3)) is { } tx)
                            cur.TxBitrate = tx;
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
                if (!string.IsNullOrEmpty(addr) && !string.IsNullOrEmpty(ssid)
                    && !addr.Equals("00:00:00:00:00:00", StringComparison.Ordinal)
                    && !ssid.Equals("unknown", StringComparison.OrdinalIgnoreCase))
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
                        // Only assign when parse succeeds — null means absent, not zero (CR-03).
                        if (int.TryParse(FirstSignedNumber(line), out var rss))
                            cur.RSS = rss;
                    }
                    else if (line.StartsWith("tx bitrate:", StringComparison.Ordinal))
                    {
                        // "tx bitrate:\t1300.0 MBit/s" — first decimal before " MBit/s".
                        if (FirstDecimalBeforeMbitS(line.Substring("tx bitrate:".Length)) is { } tx)
                            cur.TxBitrate = tx;
                    }
                    else if (line.StartsWith("rx bitrate:", StringComparison.Ordinal))
                    {
                        if (FirstDecimalBeforeMbitS(line.Substring("rx bitrate:".Length)) is { } rx)
                            cur.RxBitrate = rx;
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
    private static double? FirstDecimalBeforeMbitS(string s)
    {
        // Captures the first decimal number that appears BEFORE a " MBit/s" sentinel,
        // e.g. "144.4" from " 144.4 MBit/s, MCS 7, 20MHz   31337 Pkts." or "1300.0"
        // from "\t1300.0 MBit/s". Returns null when the sentinel is absent or the
        // capture fails to parse — keeps the CR-03 zero-vs-absent contract.
        var mbitIdx = s.IndexOf(" MBit/s", StringComparison.Ordinal);
        if (mbitIdx <= 0) return null;
        var seg = s.Substring(0, mbitIdx);
        var match = System.Text.RegularExpressions.Regex.Match(seg, @"\d+(?:\.\d+)?");
        if (!match.Success) return null;
        return double.TryParse(
            match.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var v) ? v : null;
    }
}
