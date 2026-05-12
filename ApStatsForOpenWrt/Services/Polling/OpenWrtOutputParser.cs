using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Services.Polling;

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
    private const string IwinfoHtModeKey = "HT Mode:";
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

    // IPv4 dotted-quad. Used by ParseArpingPingOutput line extraction (Phase 9 ENRICH-04).
    private static readonly System.Text.RegularExpressions.Regex IPv4Regex =
        new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    // HT Mode mnemonic → MHz width. Matches HT20/HT40/HT40+/HT40-/VHT20/VHT40/VHT80/VHT80+80/
    // VHT160/HE20/HE40/HE80/HE160/EHT20/EHT40/EHT80/EHT160/EHT320 etc. Captures the leading
    // digit run after the prefix (e.g. "VHT80+80" → 80; "HT40+" → 40). Falls back to 0 (default
    // int) when the line is missing, contains "NOHT", or otherwise doesn't match.
    private static readonly System.Text.RegularExpressions.Regex HtModeWidthRegex =
        new(@"\b(?:HT|VHT|HE|EHT)(\d{2,3})\b", System.Text.RegularExpressions.RegexOptions.Compiled);

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
                int channel = 0, txPower = 0, width = 0;
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
                    // HT Mode lives on the same physical line as Channel in real OpenWRT iwinfo
                    // output (e.g., "Mode: Master  Channel: 6 (2.437 GHz)  HT Mode: HT20"). The
                    // else-if chain above terminates on the first match (Channel:), so the HT Mode
                    // recognition runs as an INDEPENDENT branch — not chained — so both fields
                    // populate from the same line.
                    if (line.Contains(IwinfoHtModeKey, StringComparison.Ordinal))
                    {
                        var modeText = ExtractAfter(line, IwinfoHtModeKey);
                        var m = HtModeWidthRegex.Match(modeText);
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var w))
                            width = w;
                    }
                }
                // 'encryption' is parsed for future use (Phase 3 may surface it on the dashboard);
                // discarding to silence unused-local warnings under TreatWarningsAsErrors.
                _ = encryption;
                if (string.IsNullOrEmpty(bssid) || string.IsNullOrEmpty(ssid)
                    || bssid.Equals("00:00:00:00:00:00", StringComparison.Ordinal)
                    || ssid.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                {
                    // Inactive WLAN placeholder block (radio off / iface down / not associated / not
                    // broadcasting). NOT a parse error — silently skip. The catch block below stays
                    // LogWarning for genuine exceptions.
                    continue;
                }
                result.Add(new WiFiNetwork
                {
                    SSID = ssid,
                    MacAddress = bssid.ToUpperInvariant(),
                    Channel = channel,
                    Width = width,
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
    /// `iw dev` blocks are: "Interface wlanX\n\taddr AA:BB:..\n\tssid TestNet\n\ttype AP\n\tchannel ...".
    /// NOTE: `iw dev` top-level output does NOT include channel-width information (no HT Mode line) —
    /// `WiFiNetwork.Width` stays at its default 0 on this fallback path. The richer iwinfo path
    /// (ParseIwinfoSummary) populates Width from `HT Mode: HT20|VHT80|EHT160|...` etc.</summary>
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

    /// <summary>
    /// Phase 9 ENRICH-04: parse interleaved stdout from the AP-side `arping`/`ping` batched loop
    /// (D-01) → Dictionary&lt;MAC, IPv4&gt;. MACs uppercased (D-07). Tolerant per-line (D-02 / D-05) —
    /// lines without both a MAC and an IPv4 are SKIPPED silently (no warning); only structurally
    /// surprising lines (parse exceptions) log Warning and continue. Last-wins on duplicate MACs
    /// from the interleaved batch output. Uses StringComparer.Ordinal (keys are uppercased on insert).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseArpingPingOutput(string raw, ILogger log)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            try
            {
                var macMatch = MacRegex.Match(line);
                if (!macMatch.Success) continue;     // ping fallback / non-matching lines.
                var ipMatch = IPv4Regex.Match(line);
                if (!ipMatch.Success) continue;
                var mac = macMatch.Value.ToUpperInvariant();
                result[mac] = ipMatch.Value;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Parse error in arping/ping output line: {RawLine}. Skipped.", line);
            }
        }
        return result;
    }

    /// <summary>
    /// Phase 9 ENRICH-05: parse a single `nslookup &lt;ip&gt;` invocation's stdout → normalized
    /// FQDN (trailing dot stripped, case lowered) or null on NXDOMAIN/SERVFAIL/timeout/empty (D-12).
    /// Handles BusyBox shape (`Name: my-host.lan`) and BIND fallback shape (`name = my-host.lan.`).
    /// Tolerant per-line — exceptions log Warning and continue scanning.
    /// </summary>
    public static string? ParseNslookupOutput(string raw, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // D-12 negative-result short-circuit. Operates on the whole input so we don't mistake a
        // server-name line for the answer when the lookup actually failed.
        if (raw.Contains("NXDOMAIN", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("SERVFAIL", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("can't find", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("connection timed out", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            try
            {
                // BusyBox shape: "Name:    my-host.lan"
                if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                {
                    var fqdn = line.Substring("Name:".Length).Trim();
                    if (fqdn.Length == 0) continue;
                    return Normalize(fqdn);
                }
                // BIND shape: "42.1.168.192.in-addr.arpa name = my-host.lan."
                var bindIdx = line.IndexOf("name = ", StringComparison.OrdinalIgnoreCase);
                if (bindIdx >= 0)
                {
                    var fqdn = line.Substring(bindIdx + "name = ".Length).Trim();
                    if (fqdn.Length == 0) continue;
                    return Normalize(fqdn);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Parse error in nslookup output line: {RawLine}. Skipped.", line);
            }
        }
        return null;

        static string Normalize(string fqdn)
        {
            if (fqdn.EndsWith('.')) fqdn = fqdn[..^1];
            return fqdn.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Quick task 260511-lct (2026-05-11): parse the batched <c>nslookup</c> stdout produced by
    /// <see cref="ReverseDnsProbe"/>'s single-SSH-command shell loop → Dictionary&lt;IP, FQDN?&gt;.
    /// Each per-IP block in the stream is delimited by a sentinel line of the form
    /// <c>==&lt;ipv4&gt;==</c> emitted by the AP-side subshell BEFORE its nslookup invocation
    /// runs (the parenthesised subshell <c>(echo ==$ip==; nslookup ... 2>&amp;1) &amp;</c> shape
    /// keeps the sentinel-echo and the nslookup output atomic against background-process
    /// interleaving — see ReverseDnsProbe.BuildBatchedNslookupCommand).
    ///
    /// Composition: delegates each sentinel-delimited block body to the existing single-IP
    /// <see cref="ParseNslookupOutput"/> — that preserves FQDN normalization (lowercase,
    /// trailing-dot strip) plus the NXDOMAIN/SERVFAIL/can't find/connection-timed-out
    /// short-circuit (D-12) verbatim. Returns the dictionary using StringComparer.Ordinal
    /// (IPv4 dotted-quad keys are ASCII; matches the ParseIpNeigh / ParseArpingPingOutput
    /// convention above).
    ///
    /// Tolerant per-block: an unparseable block body simply yields null for its IP (no
    /// exception escapes). Last-wins on duplicate sentinels (defensive — the production
    /// command builder does not emit duplicates).
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ParseBatchedNslookupOutput(string raw, ILogger log)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var matches = BatchedNslookupSentinelRegex.Matches(raw);
        if (matches.Count == 0) return result;

        for (var i = 0; i < matches.Count; i++)
        {
            var ip = matches[i].Groups["ip"].Value;
            var blockStart = matches[i].Index + matches[i].Length;
            var blockEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : raw.Length;
            var block = raw.Substring(blockStart, blockEnd - blockStart);
            try
            {
                result[ip] = ParseNslookupOutput(block, log);
            }
            catch (Exception ex)
            {
                // Truncate the offending block body to keep log bloat bounded.
                var truncated = block.Length <= 200 ? block : block.Substring(0, 200) + "…";
                log.LogWarning(ex,
                    "ParseBatchedNslookupOutput: failed to parse block for IP {Ip}; storing null. Raw block (truncated): {RawBlock}",
                    ip, truncated);
                result[ip] = null;
            }
        }

        return result;
    }

    // Quick task 260511-lct: sentinel line matcher used by ParseBatchedNslookupOutput. The
    // sentinel shape is `==<ipv4>==` on its own line; the parenthesised subshell in the AP-side
    // command builder guarantees the echo+nslookup output pair is atomic against background
    // interleaving, so the match-then-substring slicing here is sufficient for attribution.
    private static readonly System.Text.RegularExpressions.Regex BatchedNslookupSentinelRegex =
        new(@"^==(?<ip>[\d.]+)==\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.Multiline
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
