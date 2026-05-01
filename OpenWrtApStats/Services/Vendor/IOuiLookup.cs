namespace OpenWrtApStats.Services.Vendor;

/// <summary>
/// DASH-13 vendor seam. Looks up the OUI prefix (first 3 octets) of a MAC address in
/// the embedded IEEE registry snapshot. Returns vendor name or null if unknown.
/// </summary>
public interface IOuiLookup
{
    string? GetVendor(string macAddress);
}
