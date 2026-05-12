using System.Text.Json;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Data.Repositories;

namespace ApStatsForOpenWrt.Services.Settings;

/// <summary>
/// <see cref="IAppSettingsService"/> implementation. Per PER-04 this service consumes
/// <see cref="IAppSettingsRepo"/> — NEVER <c>IDbContextFactory&lt;DatabaseContext&gt;</c>
/// directly. Reads are lock-free via <see cref="Volatile.Read{T}"/> over an immutable
/// <see cref="Snapshot"/> record swapped via <see cref="Interlocked.Exchange{T}"/>; writes
/// upsert through the repo, re-warm the snapshot from the just-persisted DB state, and then
/// raise <see cref="Changed"/>. D-11 mandates the well-known keys live INSIDE the service.
/// </summary>
public sealed class AppSettingsService(IAppSettingsRepo repo) : IAppSettingsService
{
    // Well-known keys — never strings in callers (D-11)
    private const string KeyPollingInterval        = "PollingIntervalSeconds";
    private const string KeyUIPasswordHash         = "UIPasswordHash";
    private const string KeyWizardDismissed        = "WizardDismissed";
    private const string KeyRefreshIntervalSeconds = "RefreshIntervalSeconds";
    private const string KeyLocalDomainSuffixes    = "LocalDomainSuffixes";

    private static readonly IReadOnlyList<string> EmptySuffixList = Array.Empty<string>();

    // Immutable snapshot; replaced atomically via Interlocked.Exchange.
    private sealed record Snapshot(
        int PollingIntervalSeconds,
        string? UIPasswordHash,
        bool WizardDismissed,
        int RefreshIntervalSeconds,
        IReadOnlyList<string> LocalDomainSuffixes);
    private Snapshot _current = new(30, null, false, 3600, EmptySuffixList); // safe defaults until WarmAsync runs (3600 matches Phase 9 SCHEMA-06 seed)

    public int     PollingIntervalSeconds => Volatile.Read(ref _current).PollingIntervalSeconds;
    public string? UIPasswordHash         => Volatile.Read(ref _current).UIPasswordHash;
    public bool    WizardDismissed        => Volatile.Read(ref _current).WizardDismissed;
    public int     RefreshIntervalSeconds => Volatile.Read(ref _current).RefreshIntervalSeconds;
    public IReadOnlyList<string> LocalDomainSuffixes => Volatile.Read(ref _current).LocalDomainSuffixes;

    public event Action<IAppSettingsService>? Changed;

    public async Task WarmAsync(CancellationToken ct = default)
    {
        var rows = await repo.GetAllAsync(ct);

        IReadOnlyList<string> suffixes = EmptySuffixList;
        if (rows.TryGetValue(KeyLocalDomainSuffixes, out var ld) && !string.IsNullOrWhiteSpace(ld.StringValue))
        {
            try
            {
                var arr = JsonSerializer.Deserialize<string[]>(ld.StringValue);
                if (arr is not null) suffixes = arr;
            }
            catch (JsonException)
            {
                // Defensive: malformed value persists as empty list. Operator can re-save via the Settings page.
                suffixes = EmptySuffixList;
            }
        }

        var snap = new Snapshot(
            PollingIntervalSeconds:    rows.TryGetValue(KeyPollingInterval,        out var pi) ? pi.IntValue ?? 30   : 30,
            UIPasswordHash:            rows.TryGetValue(KeyUIPasswordHash,         out var uh) ? uh.StringValue       : null,
            WizardDismissed:           rows.TryGetValue(KeyWizardDismissed,        out var wd) ? wd.BoolValue ?? false: false,
            RefreshIntervalSeconds:    rows.TryGetValue(KeyRefreshIntervalSeconds, out var ri) ? ri.IntValue ?? 3600 : 3600,
            LocalDomainSuffixes:       suffixes);
        Interlocked.Exchange(ref _current, snap);
    }

    public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeyPollingInterval, intValue: seconds, ct: ct);

    public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeyUIPasswordHash, stringValue: hash, ct: ct);

    public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeyWizardDismissed, boolValue: dismissed, ct: ct);

    public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeyRefreshIntervalSeconds, intValue: seconds, ct: ct);

    public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(suffixes);
        // Defensive cleanup: trim, discard empties. Caller (Settings/Index.razor) already does this,
        // but mirror here so direct API consumers can't bypass.
        var cleaned = suffixes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToArray();
        var json = JsonSerializer.Serialize(cleaned);
        return UpsertAndRefreshAsync(KeyLocalDomainSuffixes, stringValue: json, ct: ct);
    }

    private async Task UpsertAndRefreshAsync(
        string key,
        string? stringValue = null,
        int?    intValue    = null,
        bool?   boolValue   = null,
        CancellationToken ct = default)
    {
        // Persist via the repo (PER-04 — only repos touch the DbContext factory).
        await repo.UpsertAsync(
            key,
            new AppSetting
            {
                Key         = key,
                StringValue = stringValue,
                IntValue    = intValue,
                BoolValue   = boolValue
            },
            ct);

        // Refresh snapshot from the just-written DB state — single source of truth.
        await WarmAsync(ct);
        Changed?.Invoke(this);
    }
}
