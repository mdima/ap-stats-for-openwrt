using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Data.Repositories;

/// <summary>
/// <see cref="IStationRepo"/> implementation. PER-05 IDbContextFactory injection only;
/// dual-overload pattern (D-07) routes repo-owned through caller-owned. UpsertCycle
/// preserves the operator-assigned <see cref="WLANStation.Name"/> across cycles
/// (DASH-14 forward-compat).
/// Phase 2 D-07 / CR-01 closure: case-strict MAC dedup — callers MUST uppercase upstream
/// (parser does, per <c>OpenWrtOutputParser</c> Plan 02-02).
/// </summary>
public sealed class StationRepo(IDbContextFactory<DatabaseContext> factory) : IStationRepo
{
    public async Task<IReadOnlyList<WLANStation>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WLANStations.AsNoTracking().ToListAsync(ct);
    }

    public async Task<WLANStation?> GetByMacAsync(string macAddress, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WLANStations.AsNoTracking()
            .FirstOrDefaultAsync(s => s.MacAddress == macAddress, ct);
    }

    // Phase 8 Plan 08-07 D-06 — read-only MAC enumeration for EventDetectionService bootstrap.
    // No SaveChangesAsync, no ExecuteUpdate, no ExecuteDelete: pure projection query.
    public async Task<IReadOnlyList<string>> GetAllMacsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WLANStations
            .AsNoTracking()
            .Select(s => s.MacAddress)
            .Distinct()
            .ToListAsync(ct);
    }

    // Phase 10 ORCH-01 — read-only per-node MAC enumeration for "newMacDetectedThisCycle".
    public async Task<IReadOnlyList<string>> GetMacsByNodeAsync(int nodeId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WLANStations
            .AsNoTracking()
            .Where(s => s.Network != null && s.Network.WiFiNodeId == nodeId)
            .Select(s => s.MacAddress)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task SetNameAsync(string macAddress, string? name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WLANStations
            .Where(s => s.MacAddress == macAddress)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Name, name), ct);
    }

    // Phase 10 DASH-22 / DASH-23 — operator-toggled override of the U/L-bit-derived random-MAC flag.
    // Single-property ExecuteUpdateAsync (CR-02-safe). Idempotent — calling on a non-existent MAC is a
    // no-op (no exception).
    public async Task SetIsRandomMacAsync(string macAddress, bool isRandomMac, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WLANStations
            .Where(s => s.MacAddress == macAddress)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsRandomMac, isRandomMac), ct);
    }

    public async Task DeleteByMacAsync(string macAddress, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WLANStations
            .Where(s => s.MacAddress == macAddress)
            .ExecuteDeleteAsync(ct);
    }

    // ── Repo-owned overloads delegate to caller-owned ───────────────────────
    public async Task MarkAllOfflineAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await MarkAllOfflineAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    public Task MarkAllOfflineAsync(DatabaseContext db, CancellationToken ct = default) =>
        db.WLANStations
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, Status.Offline), ct);

    public async Task UpsertCycleAsync(IEnumerable<WLANStation> live, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await UpsertCycleAsync(db, live, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertCycleAsync(DatabaseContext db, IEnumerable<WLANStation> live, CancellationToken ct = default)
    {
        var liveList = live.ToList();
        var macs = liveList.Select(s => s.MacAddress).ToHashSet(StringComparer.Ordinal);   // D-07 / CR-01 closure: callers uppercase upstream

        var existing = await db.WLANStations
            .Where(s => macs.Contains(s.MacAddress))
            .ToListAsync(ct);
        var existingByMac = existing.ToDictionary(s => s.MacAddress, StringComparer.Ordinal);          // D-07 / CR-01 closure

        foreach (var probe in liveList)
        {
            if (existingByMac.TryGetValue(probe.MacAddress, out var existingRow))
            {
                // Station hopped WLANs? Update WiFiNetworkId. Update telemetry. Flip to Online.
                existingRow.WiFiNetworkId = probe.WiFiNetworkId;
                // ORCH-04 DB-as-cache cascade: probe.IPAddress is null when neither neigh nor the
                // refresh-tick sweep observed an IP this cycle; reuse the persisted value
                // (CHARTER §"DB-as-cache": neigh > DB > null).
                existingRow.IPAddress     = probe.IPAddress ?? existingRow.IPAddress;
                // ORCH-05 / ORCH-08: probe.Hostname is null when no nslookup ran this cycle OR
                // when the lookup returned NXDOMAIN/SERVFAIL/timeout (Phase 9 D-12 collapses these
                // to null). Reuse persisted Hostname; ORCH-08 retry-on-next-tick is implicit
                // (next refresh tick will re-probe IPs whose Hostname IS NULL).
                existingRow.Hostname      = probe.Hostname ?? existingRow.Hostname;
                // existingRow.Name is operator-assigned (DASH-14) — preserve across cycles. (ORCH-02)
                // existingRow.IsRandomMac is INSERT-only (ORCH-03) — preserve across cycles. (ORCH-02)
                // existingRow.FirstSeen is INSERT-only (v0.9.2) — preserve across cycles. (ORCH-02)
                existingRow.TxBitrate     = probe.TxBitrate;
                existingRow.RxBitrate     = probe.RxBitrate;
                existingRow.RSS           = probe.RSS;
                existingRow.Status        = Status.Online;
                existingRow.LastPollDate  = probe.LastPollDate;
            }
            else
            {
                probe.Status = Status.Online;
                // ORCH-03: compute IsRandomMac from the U/L bit on MAC byte 0 — INSERT-only,
                // never updated thereafter. MAC stored uppercase per Phase 2 D-07; byte 0 is
                // the first two characters; U/L bit is the second-least-significant bit (0x02).
                // Equivalent to the migration's D-08 SQL backfill.
                if (probe.MacAddress.Length >= 2)
                {
                    probe.IsRandomMac = (Convert.ToByte(probe.MacAddress.Substring(0, 2), 16) & 0x02) != 0;
                }
                // v0.9.2 — FirstSeen captures the moment of first observation. INSERT-only per the
                // seed file's locked design: reuse the orchestrator-populated cycle timestamp so
                // FirstSeen == LastPollDate exactly for any station whose first appearance is this
                // cycle. Subsequent upsert cycles fall through the UPDATE branch above, which leaves
                // existingRow.FirstSeen untouched (ORCH-02 preservation).
                probe.FirstSeen = probe.LastPollDate;
                db.WLANStations.Add(probe);
            }
        }
    }
}
