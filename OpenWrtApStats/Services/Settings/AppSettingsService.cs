using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;

namespace OpenWrtApStats.Services.Settings;

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
    private const string KeyPollingInterval = "PollingIntervalSeconds";
    private const string KeyUIPasswordHash  = "UIPasswordHash";
    private const string KeySshKnownHosts   = "SshKnownHostsCacheJson";

    // Immutable snapshot; replaced atomically via Interlocked.Exchange.
    private sealed record Snapshot(int PollingIntervalSeconds, string? UIPasswordHash, string? SshKnownHostsCacheJson);
    private Snapshot _current = new(30, null, null); // safe defaults until WarmAsync runs

    public int     PollingIntervalSeconds => Volatile.Read(ref _current).PollingIntervalSeconds;
    public string? UIPasswordHash         => Volatile.Read(ref _current).UIPasswordHash;
    public string? SshKnownHostsCacheJson => Volatile.Read(ref _current).SshKnownHostsCacheJson;

    public event Action<IAppSettingsService>? Changed;

    public async Task WarmAsync(CancellationToken ct = default)
    {
        var rows = await repo.GetAllAsync(ct);
        var snap = new Snapshot(
            PollingIntervalSeconds: rows.TryGetValue(KeyPollingInterval, out var pi) ? pi.IntValue ?? 30 : 30,
            UIPasswordHash:         rows.TryGetValue(KeyUIPasswordHash,  out var uh) ? uh.StringValue : null,
            SshKnownHostsCacheJson: rows.TryGetValue(KeySshKnownHosts,   out var sh) ? sh.StringValue : null);
        Interlocked.Exchange(ref _current, snap);
    }

    public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeyPollingInterval, intValue: seconds, ct: ct);

    public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeyUIPasswordHash, stringValue: hash, ct: ct);

    public Task SetSshKnownHostsCacheJsonAsync(string? json, CancellationToken ct = default)
        => UpsertAndRefreshAsync(KeySshKnownHosts, stringValue: json, ct: ct);

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
