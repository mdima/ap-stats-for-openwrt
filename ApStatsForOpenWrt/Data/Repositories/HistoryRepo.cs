using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Data.Repositories;

/// <summary>
/// <see cref="IHistoryRepo"/> implementation. PER-05 <see cref="IDbContextFactory{TContext}"/>
/// injection only (matches the <see cref="StationRepo"/> idiom and keeps the type in the
/// whitelisted <c>Data.Repositories</c> namespace) — both methods operate on the caller-owned
/// <c>db</c>, so the factory is currently unused but retained for any future repo-owned overload.
/// </summary>
public sealed class HistoryRepo(IDbContextFactory<DatabaseContext> factory) : IHistoryRepo
{
    // factory retained for the StationRepo idiom (PER-05); no own-db method needs it yet.
    private readonly IDbContextFactory<DatabaseContext> _factory = factory;

    public async Task WriteCycleAsync(DatabaseContext db, KpiHistorySnapshot snapshot, IEnumerable<RssiHistorySample> samples, CancellationToken ct)
    {
        db.KpiHistorySnapshots.Add(snapshot);
        db.RssiHistorySamples.AddRange(samples);
        await db.SaveChangesAsync(ct);
    }

    public async Task PruneAsync(DatabaseContext db, DateTime cutoff, CancellationToken ct)
    {
        await db.KpiHistorySnapshots.Where(k => k.TimestampUtc < cutoff).ExecuteDeleteAsync(ct);
        await db.RssiHistorySamples.Where(r => r.TimestampUtc < cutoff).ExecuteDeleteAsync(ct);
    }
}
