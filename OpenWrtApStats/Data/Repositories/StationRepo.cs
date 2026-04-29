using Microsoft.EntityFrameworkCore;
using OpenWrtApStats.Data.Models;

namespace OpenWrtApStats.Data.Repositories;

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

    public async Task SetNameAsync(string macAddress, string? name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WLANStations
            .Where(s => s.MacAddress == macAddress)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Name, name), ct);
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
                existingRow.IPAddress     = probe.IPAddress;
                existingRow.Hostname      = probe.Hostname;
                // existingRow.Name is operator-assigned (DASH-14) — preserve across cycles.
                existingRow.TxBitrate     = probe.TxBitrate;
                existingRow.RxBitrate     = probe.RxBitrate;
                existingRow.RSS           = probe.RSS;
                existingRow.Status        = Status.Online;
                existingRow.LastPollDate  = probe.LastPollDate;
            }
            else
            {
                probe.Status = Status.Online;
                db.WLANStations.Add(probe);
            }
        }
    }
}
