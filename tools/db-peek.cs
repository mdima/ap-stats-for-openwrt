#:package Microsoft.Data.Sqlite@10.0.0
using System;
using System.IO;
using Microsoft.Data.Sqlite;

var src = @"C:\temp\data\AP-Stats-for-OpenWrt.db";
var snap = Path.Combine(Path.GetTempPath(), "ap-db-snap.db");
File.Copy(src, snap, overwrite: true);
foreach (var ext in new[] { "-wal", "-shm" })
{
    var f = src + ext; if (File.Exists(f)) File.Copy(f, snap + ext, overwrite: true);
}

using var conn = new SqliteConnection($"Data Source={snap}");
conn.Open();

void Run(string title, string sql)
{
    Console.WriteLine();
    Console.WriteLine($"==== {title} ====");
    using var c = conn.CreateCommand();
    c.CommandText = sql;
    using var r = c.ExecuteReader();
    var headers = new string[r.FieldCount];
    for (int i = 0; i < r.FieldCount; i++) headers[i] = r.GetName(i);
    Console.WriteLine(string.Join(" | ", headers));
    while (r.Read())
    {
        var row = new string[r.FieldCount];
        for (int i = 0; i < r.FieldCount; i++)
            row[i] = r.IsDBNull(i) ? "<null>" : Convert.ToString(r.GetValue(i)) ?? "";
        Console.WriteLine(string.Join(" | ", row));
    }
}

Run("WiFiNodes", "SELECT Id, Hostname, Status, LastPollDate, LastEnrichmentRefreshAt, SubnetScanEnabled, SubnetScanRanges FROM WiFiNodes ORDER BY Id");
Run("WiFiNetworks", "SELECT Id, WiFiNodeId, MacAddress, SSID, IfaceName, Enabled FROM WiFiNetworks ORDER BY WiFiNodeId, Id");
Run("Station counters", @"
    SELECT COUNT(*) AS Total,
           SUM(CASE WHEN IPAddress IS NULL OR IPAddress='' THEN 1 ELSE 0 END) AS NoIP,
           SUM(CASE WHEN Hostname IS NULL OR Hostname=''  THEN 1 ELSE 0 END) AS NoHostname,
           SUM(CASE WHEN IsRandomMac=1 THEN 1 ELSE 0 END) AS RandomMac,
           SUM(CASE WHEN Status=0 THEN 1 ELSE 0 END) AS StatusOnline,
           SUM(CASE WHEN Status=1 THEN 1 ELSE 0 END) AS StatusOffline,
           SUM(CASE WHEN Status=2 THEN 1 ELSE 0 END) AS StatusStale
    FROM WLANStations");
Run("WLANStations missing IP and/or hostname",
    @"SELECT s.MacAddress, COALESCE(s.IPAddress,'<null>') AS IP, COALESCE(s.Hostname,'<null>') AS HostName,
             s.IsRandomMac, s.Status, n.SSID, w.Hostname AS Ap
      FROM WLANStations s
      LEFT JOIN WiFiNetworks n ON s.WiFiNetworkId = n.Id
      LEFT JOIN WiFiNodes w ON n.WiFiNodeId = w.Id
      WHERE s.IPAddress IS NULL OR s.IPAddress='' OR s.Hostname IS NULL OR s.Hostname=''
      ORDER BY w.Hostname, s.MacAddress");

File.Delete(snap);
foreach (var ext in new[] { "-wal", "-shm" })
{
    var f = snap + ext; if (File.Exists(f)) File.Delete(f);
}
