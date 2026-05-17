namespace ApStatsForOpenWrt.Web.Helpers;

/// <summary>
/// Phase 10 DASH-18 / DASH-19 display-time suffix-stripping helper. Pure function over
/// (fqdn, suffixes). Case-insensitive, first-match wins; returns FQDN as-is when no match
/// (CHARTER §"Hostname storage and display"). The persisted <c>WLANStation.Hostname</c> column
/// is always the full FQDN — suffix-stripping is display-only (CHARTER §"Hostname storage and display"
/// + Plan 10-01 ORCH-02/05).
/// <para>
/// Edge cases:
/// - null fqdn → null
/// - empty fqdn → empty
/// - null/empty suffix list → fqdn unchanged
/// - whitespace suffix tokens → skipped
/// - leading-dot suffix → normalized (".localnetwork" treated as "localnetwork")
/// - host-only label that equals a suffix → pass-through (don't produce empty string)
/// </para>
/// </summary>
public static class HostnameDisplay
{
    public static string? StripLocalSuffix(string? fqdn, IReadOnlyList<string>? suffixes)
    {
        if (string.IsNullOrEmpty(fqdn)) return fqdn;
        if (suffixes is null || suffixes.Count == 0) return fqdn;

        foreach (var suffix in suffixes)
        {
            if (string.IsNullOrWhiteSpace(suffix)) continue;
            // Normalize the suffix to ".localnetwork" form so the EndsWith check is unambiguous.
            var sfx = suffix.StartsWith('.') ? suffix : "." + suffix;
            if (fqdn.EndsWith(sfx, StringComparison.OrdinalIgnoreCase))
            {
                return fqdn[..^sfx.Length];
            }
            // Host-only label that equals a suffix → pass through (do not produce empty string).
            if (string.Equals(fqdn, suffix, StringComparison.OrdinalIgnoreCase))
            {
                return fqdn;
            }
        }
        return fqdn;
    }
}
