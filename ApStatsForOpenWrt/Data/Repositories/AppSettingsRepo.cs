using Microsoft.EntityFrameworkCore;
using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Data.Repositories;

/// <summary>
/// <see cref="IAppSettingsRepo"/> implementation. The only type permitted to construct a
/// <see cref="DatabaseContext"/> for AppSettings access (PER-04 / PER-05). Single-row
/// upsert per call: AppSettings is a tiny key-value table and reads are bounded.
/// </summary>
public sealed class AppSettingsRepo(IDbContextFactory<DatabaseContext> factory) : IAppSettingsRepo
{
    public async Task<IReadOnlyDictionary<string, AppSetting>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AppSettings.AsNoTracking().ToDictionaryAsync(r => r.Key, ct);
    }

    public async Task UpsertAsync(string key, AppSetting value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(value.Key, key, StringComparison.Ordinal))
            throw new ArgumentException($"value.Key ('{value.Key}') must equal key ('{key}').", nameof(value));

        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.FindAsync([key], ct);
        if (row is null)
        {
            db.AppSettings.Add(value);
        }
        else
        {
            row.StringValue = value.StringValue;
            row.IntValue    = value.IntValue;
            row.BoolValue   = value.BoolValue;
        }
        await db.SaveChangesAsync(ct);
    }
}
