using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace OpenWrtApStats.Services.Vendor;

/// <summary>
/// Singleton OUI loader. Reads the embedded <c>Data/Resources/oui.csv</c> at construction
/// (~30k entries, 1-2 MB heap; one-time cost) into a <see cref="FrozenDictionary{TKey, TValue}"/>
/// keyed by uppercase 6-hex-char OUI prefix. Pitfall 9: ctor catches its own exceptions
/// and falls back to an empty dictionary so a malformed/missing CSV does NOT take down the app.
/// </summary>
public sealed class OuiLookup : IOuiLookup
{
    private readonly FrozenDictionary<string, string> _byPrefix;

    public OuiLookup(ILogger<OuiLookup> log)
    {
        try
        {
            var asm = typeof(OuiLookup).Assembly;
            using var stream = asm.GetManifestResourceStream("OpenWrtApStats.Data.Resources.oui.csv")
                ?? throw new InvalidOperationException("Embedded oui.csv missing");
            using var reader = new StreamReader(stream);
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            string? line;
            bool first = true;
            while ((line = reader.ReadLine()) is not null)
            {
                if (first) { first = false; continue; }    // header row
                var (prefix, vendor) = ParseRow(line);
                if (!string.IsNullOrEmpty(prefix) && !dict.ContainsKey(prefix))
                    dict[prefix] = vendor;
            }
            _byPrefix = dict.ToFrozenDictionary(StringComparer.Ordinal);
            log.LogInformation("OUI lookup loaded: {Count} entries", _byPrefix.Count);
        }
        catch (Exception ex)
        {
            // Pitfall 9 — degrade gracefully (vendor column renders empty) instead of taking the app down.
            log.LogWarning(ex, "OUI lookup failed to load — vendor column will be empty");
            _byPrefix = FrozenDictionary<string, string>.Empty;
        }
    }

    public string? GetVendor(string macAddress)
    {
        if (string.IsNullOrEmpty(macAddress) || macAddress.Length < 8) return null;
        // MAC arrives uppercased per Phase 2 D-07. Be tolerant of separators.
        var compact = macAddress.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (compact.Length < 6) return null;
        return _byPrefix.TryGetValue(compact[..6], out var vendor) ? vendor : null;
    }

    /// <summary>
    /// Parse one row of the IEEE OUI CSV (https://standards-oui.ieee.org/oui/oui.csv).
    /// Format: <c>Registry,Assignment,Organization Name,Organization Address</c>.
    /// The Assignment column is the 6-hex-char OUI prefix; OrganizationName is the vendor.
    /// Tolerant of quoted fields with embedded commas and quote-doubling.
    /// </summary>
    internal static (string prefix, string vendor) ParseRow(string line)
    {
        // Tolerant CSV split — RFC-4180-ish: handle quoted fields with embedded commas.
        var fields = SplitCsv(line);
        if (fields.Count < 3) return ("", "");
        var prefix = fields[1].Trim().ToUpperInvariant();
        // Strip everything except hex characters (the IEEE format sometimes ships with hyphens).
        prefix = new string(prefix.Where(c => Uri.IsHexDigit(c)).ToArray());
        if (prefix.Length != 6) return ("", "");
        var vendor = fields[2].Trim();
        return (prefix, vendor);
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;   // escaped quote
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
