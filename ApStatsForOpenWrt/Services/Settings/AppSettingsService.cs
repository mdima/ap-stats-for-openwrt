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
    private const string KeySessionVersion         = "SessionVersion";
    // M-UPD-02 — the four update-check keys. RESOLVED FINDING #1 Path B: NO HasData row, NO EF
    // migration; the warm-time defaults below (UpdateCheckEnabled=true, three nulls) ARE the seed.
    private const string KeyUpdateCheckEnabled     = "UpdateCheckEnabled";
    private const string KeyLatestKnownVersion     = "LatestKnownVersion";
    private const string KeyLatestReleaseUrl       = "LatestReleaseUrl";
    private const string KeyLastCheckedAtUtc       = "LastCheckedAtUtc";

    private static readonly IReadOnlyList<string> EmptySuffixList = Array.Empty<string>();

    // Immutable snapshot; replaced atomically via Interlocked.Exchange.
    private sealed record Snapshot(
        int PollingIntervalSeconds,
        string? UIPasswordHash,
        bool WizardDismissed,
        int RefreshIntervalSeconds,
        IReadOnlyList<string> LocalDomainSuffixes,
        int SessionVersion,
        bool UpdateCheckEnabled,
        string? LatestKnownVersion,
        string? LatestReleaseUrl,
        DateTime? LastCheckedAtUtc);
    // safe defaults until WarmAsync runs (3600 matches Phase 9 SCHEMA-06 seed; SessionVersion default 0 per M-SEC-04;
    // M-UPD-02 Path B seed — UpdateCheckEnabled=true (D-07 ship-armed) + three nulls, NO DB row required).
    private Snapshot _current = new(30, null, false, 3600, EmptySuffixList, 0, true, null, null, null);

    public int     PollingIntervalSeconds => Volatile.Read(ref _current).PollingIntervalSeconds;
    public string? UIPasswordHash         => Volatile.Read(ref _current).UIPasswordHash;
    public bool    WizardDismissed        => Volatile.Read(ref _current).WizardDismissed;
    public int     RefreshIntervalSeconds => Volatile.Read(ref _current).RefreshIntervalSeconds;
    public IReadOnlyList<string> LocalDomainSuffixes => Volatile.Read(ref _current).LocalDomainSuffixes;
    public int     SessionVersion         => Volatile.Read(ref _current).SessionVersion;
    public bool    UpdateCheckEnabled     => Volatile.Read(ref _current).UpdateCheckEnabled;
    public string? LatestKnownVersion     => Volatile.Read(ref _current).LatestKnownVersion;
    public string? LatestReleaseUrl       => Volatile.Read(ref _current).LatestReleaseUrl;
    public DateTime? LastCheckedAtUtc      => Volatile.Read(ref _current).LastCheckedAtUtc;

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

        // M-UPD-02 LastCheckedAtUtc: AppSetting has no DateTime column, so it is persisted as an
        // ISO-8601 round-trip (`o`) string. SQLite/EF round-trips DateTimes back with Kind=Unspecified,
        // so re-attach DateTimeKind.Utc on read (mirrors Dashboard.razor RelativeTime — T-17-01-01).
        DateTime? lastCheckedAtUtc = null;
        if (rows.TryGetValue(KeyLastCheckedAtUtc, out var lc)
            && !string.IsNullOrWhiteSpace(lc.StringValue)
            && DateTime.TryParse(
                lc.StringValue,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            lastCheckedAtUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        var snap = new Snapshot(
            PollingIntervalSeconds:    rows.TryGetValue(KeyPollingInterval,        out var pi) ? pi.IntValue ?? 30   : 30,
            UIPasswordHash:            rows.TryGetValue(KeyUIPasswordHash,         out var uh) ? uh.StringValue       : null,
            WizardDismissed:           rows.TryGetValue(KeyWizardDismissed,        out var wd) ? wd.BoolValue ?? false: false,
            RefreshIntervalSeconds:    rows.TryGetValue(KeyRefreshIntervalSeconds, out var ri) ? ri.IntValue ?? 3600 : 3600,
            LocalDomainSuffixes:       suffixes,
            SessionVersion:            rows.TryGetValue(KeySessionVersion,         out var sv) ? sv.IntValue ?? 0    : 0,
            // M-UPD-02 single seeding path: no DB row needed; the in-memory default satisfies M-UPD-02.
            // UpdateCheckEnabled defaults TRUE (D-07 ship-armed) whether the key is absent OR present-without-a-bool.
            UpdateCheckEnabled:        rows.TryGetValue(KeyUpdateCheckEnabled,     out var uc) ? uc.BoolValue ?? true : true,
            LatestKnownVersion:        rows.TryGetValue(KeyLatestKnownVersion,     out var lv) ? lv.StringValue       : null,
            LatestReleaseUrl:          rows.TryGetValue(KeyLatestReleaseUrl,       out var lu) ? lu.StringValue       : null,
            LastCheckedAtUtc:          lastCheckedAtUtc);
        Interlocked.Exchange(ref _current, snap);
    }

    public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeyPollingInterval, intValue: seconds, ct: ct);

    public async Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default)
    {
        // M-SEC-04 — sequence both upserts through the same IAppSettingsRepo writer (single-
        // writer invariant preserved). The bump happens on BOTH set and clear (null) paths —
        // no branching. Plan 15-04 reads SessionVersion from OnValidatePrincipal to invalidate
        // every existing cookie once the password changes.
        await UpsertAndRefreshAsync(KeyUIPasswordHash, stringValue: hash, ct: ct);
        await BumpSessionVersionAsync(ct);
    }

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

    public Task SetSessionVersionAsync(int version, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeySessionVersion, intValue: version, ct: ct);

    public async Task BumpSessionVersionAsync(CancellationToken ct = default)
    {
        // M-SEC-04 — read current snapshot, compute +1, persist through the same single-writer
        // path as every other setter. Called atomically inside SetUIPasswordHashAsync.
        var next = Volatile.Read(ref _current).SessionVersion + 1;
        await UpsertAndRefreshAsync(KeySessionVersion, intValue: next, ct: ct);
    }

    // ── Phase 17 Plan 17-01 M-UPD-02 — the four update-check setters ────────────

    public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeyUpdateCheckEnabled, boolValue: enabled, ct: ct);

    public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeyLatestKnownVersion, stringValue: version, ct: ct);

    public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeyLatestReleaseUrl, stringValue: url, ct: ct);

    public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default)
        // Round-trippable ISO-8601 `o` format; null clears the row's StringValue (T-17-01-01).
        => UpsertAndRefreshAsync(
            KeyLastCheckedAtUtc,
            stringValue: checkedAtUtc?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ct: ct);

    public async Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(url);

        // M-UPD-02 — atomic 3-value advance: upsert all three through the single-writer repo,
        // THEN re-warm + raise Changed EXACTLY ONCE (not three chained UpsertAndRefreshAsync calls,
        // which would fire Changed three times → three badge re-renders / spurious toast triggers).
        await UpsertOnlyAsync(KeyLatestKnownVersion, stringValue: version, ct: ct);
        await UpsertOnlyAsync(KeyLatestReleaseUrl, stringValue: url, ct: ct);
        await UpsertOnlyAsync(
            KeyLastCheckedAtUtc,
            stringValue: checkedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ct: ct);

        await WarmAsync(ct);
        Changed?.Invoke(this);
    }

    private Task UpsertAndRefreshAsync(
        string key,
        string? stringValue = null,
        int?    intValue    = null,
        bool?   boolValue   = null,
        CancellationToken ct = default)
        => UpsertAndRefreshCoreAsync(key, stringValue, intValue, boolValue, ct);

    private async Task UpsertAndRefreshCoreAsync(
        string key,
        string? stringValue,
        int?    intValue,
        bool?   boolValue,
        CancellationToken ct)
    {
        await UpsertOnlyAsync(key, stringValue, intValue, boolValue, ct);

        // Refresh snapshot from the just-written DB state — single source of truth.
        await WarmAsync(ct);
        Changed?.Invoke(this);
    }

    /// <summary>
    /// Persists a single AppSetting row via the repo WITHOUT re-warming or raising
    /// <see cref="Changed"/>. Used by the combined <see cref="SetLatestReleaseAsync"/> so a
    /// multi-key advance re-warms + fires Changed exactly once at the end.
    /// </summary>
    private Task UpsertOnlyAsync(
        string key,
        string? stringValue = null,
        int?    intValue    = null,
        bool?   boolValue   = null,
        CancellationToken ct = default)
        // Persist via the repo (PER-04 — only repos touch the DbContext factory).
        => repo.UpsertAsync(
            key,
            new AppSetting
            {
                Key         = key,
                StringValue = stringValue,
                IntValue    = intValue,
                BoolValue   = boolValue
            },
            ct);
}
