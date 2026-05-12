using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OpenWrtApStats.Services.Polling;

/// <summary>
/// Static, stateless helpers for Phase 9 subnet-derivation logic (D-04 / D-06). Pure functions —
/// no I/O, no DI. Mirrors the shape of <see cref="OpenWrtOutputParser"/>.
/// </summary>
public static class SubnetDeriver
{
    // "192.168.1.1 dev br-lan src 192.168.1.42 uid 0 ..." → captures "br-lan".
    // Iface charset is restricted to [a-zA-Z0-9._-]+ to prevent shell-metacharacter
    // injection when iface flows into `ip -4 addr show dev <iface>` (T-09-03-05).
    private static readonly Regex IpRouteDevRegex =
        new(@"\bdev\s+(?<iface>[a-zA-Z0-9._\-]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "    inet 192.168.1.42/24 brd 192.168.1.255 scope global br-lan" → captures "192.168.1.42".
    private static readonly Regex IpAddrShowInetRegex =
        new(@"\binet\s+(?<ipv4>(?:\d{1,3}\.){3}\d{1,3})/(?<prefix>\d{1,2})\b",
            RegexOptions.Compiled);

    /// <summary>
    /// D-06: parse comma-separated CIDR list. Null/empty → empty list. Tolerant per-entry —
    /// malformed CIDRs (no <c>/</c>, non-numeric prefix, prefix out of range) log Warning + skip.
    /// </summary>
    public static IReadOnlyList<string> ParseCidrList(string? csvCidrs, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(csvCidrs)) return Array.Empty<string>();
        var result = new List<string>();
        foreach (var entry in csvCidrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slashIdx = entry.IndexOf('/');
            if (slashIdx <= 0
                || !int.TryParse(entry.AsSpan(slashIdx + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix)
                || prefix < 0 || prefix > 32)
            {
                log.LogWarning("Malformed CIDR entry in SubnetScanRanges: '{Entry}'. Skipped.", entry);
                continue;
            }
            result.Add(entry);
        }
        return result;
    }

    /// <summary>
    /// Expand a CIDR to its host IPs. Excludes network address and broadcast for prefixes ≤ 30.
    /// /31 returns both addresses (point-to-point convention); /32 returns the single host.
    /// Throws <see cref="ArgumentException"/> on malformed input.
    /// </summary>
    public static IReadOnlyList<string> ExpandCidr(string cidr, ILogger log)
    {
        var slashIdx = cidr.IndexOf('/');
        if (slashIdx <= 0) throw new ArgumentException($"Malformed CIDR: '{cidr}'.", nameof(cidr));
        var ipPart = cidr.Substring(0, slashIdx);
        if (!int.TryParse(cidr.AsSpan(slashIdx + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix))
            throw new ArgumentException($"Non-numeric prefix in CIDR: '{cidr}'.", nameof(cidr));
        if (prefix < 0 || prefix > 32) throw new ArgumentException($"Prefix out of range: '{cidr}'.", nameof(cidr));

        var octets = ipPart.Split('.');
        if (octets.Length != 4) throw new ArgumentException($"Malformed IPv4: '{ipPart}'.", nameof(cidr));
        uint baseAddr = 0;
        for (var i = 0; i < 4; i++)
        {
            if (!uint.TryParse(octets[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) || b > 255)
                throw new ArgumentException($"Malformed octet: '{octets[i]}' in '{cidr}'.", nameof(cidr));
            baseAddr = (baseAddr << 8) | b;
        }

        // Mask to network address.
        var mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        var network = baseAddr & mask;
        var hostBits = 32 - prefix;

        var hosts = new List<string>();
        if (prefix == 32)
        {
            hosts.Add(ipPart);
            return hosts;
        }

        // For /31 keep both addresses (point-to-point); for /≤30 skip .0 (network) and last (broadcast).
        var totalAddrs = (uint)(1L << hostBits);
        var startOffset = prefix >= 31 ? 0u : 1u;
        var endOffset = prefix >= 31 ? totalAddrs : totalAddrs - 1;
        for (var off = startOffset; off < endOffset; off++)
        {
            var addr = network + off;
            hosts.Add($"{(addr >> 24) & 0xFF}.{(addr >> 16) & 0xFF}.{(addr >> 8) & 0xFF}.{addr & 0xFF}");
        }
        return hosts;
    }

    /// <summary>
    /// D-04 step 1: extract <c>dev &lt;iface&gt;</c> from <c>ip route get</c> output. Null on no match.
    /// Iface charset is restricted to <c>[a-zA-Z0-9._-]+</c> at the regex level to prevent
    /// shell-metacharacter injection downstream (T-09-03-05).
    /// </summary>
    public static string? ExtractInterfaceFromIpRouteGet(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = IpRouteDevRegex.Match(output);
        return m.Success ? m.Groups["iface"].Value : null;
    }

    /// <summary>
    /// D-04 step 2: extract first <c>inet &lt;ipv4&gt;/&lt;prefix&gt;</c> from <c>ip -4 addr show</c> output.
    /// Returns the bare IPv4 (no prefix). Null on no match.
    /// </summary>
    public static string? ExtractIPv4FromIpAddrShow(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = IpAddrShowInetRegex.Match(output);
        return m.Success ? m.Groups["ipv4"].Value : null;
    }

    /// <summary>
    /// D-04 step 3: mask an IPv4 to /24 by zeroing the last octet.
    /// </summary>
    public static string MaskToSlash24(string ipv4)
    {
        var lastDot = ipv4.LastIndexOf('.');
        if (lastDot <= 0) throw new ArgumentException($"Malformed IPv4: '{ipv4}'.", nameof(ipv4));
        return $"{ipv4.Substring(0, lastDot)}.0/24";
    }

    /// <summary>
    /// D-04 end-to-end: combine <c>ip route get</c> and <c>ip -4 addr show</c> output into a /24 CIDR
    /// network around the SSH-peer-serving interface. Null on any failure (with one Information log).
    /// </summary>
    public static string? DeriveSubnetFromInterfaceOutput(
        string ipRouteGetOutput, string ipAddrShowOutput, ILogger log)
    {
        var iface = ExtractInterfaceFromIpRouteGet(ipRouteGetOutput);
        if (iface is null)
        {
            log.LogInformation("Subnet auto-derivation: ip route get output did not yield 'dev <iface>'.");
            return null;
        }
        var ipv4 = ExtractIPv4FromIpAddrShow(ipAddrShowOutput);
        if (ipv4 is null)
        {
            log.LogInformation("Subnet auto-derivation: ip -4 addr show on iface '{Iface}' did not yield an IPv4.", iface);
            return null;
        }
        return MaskToSlash24(ipv4);
    }
}
