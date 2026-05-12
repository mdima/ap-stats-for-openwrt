using Microsoft.Extensions.Logging;

namespace ApStatsForOpenWrt.Services;

/// <summary>
/// Phase 8 D-08 / D-10 — Singleton event-bus for toast emission.
/// <para>
/// <see cref="ToastHost"/> (a Blazor InteractiveServer component mounted once at the
/// MainLayout body root in Wave 3) subscribes to <see cref="OnShow"/> in
/// <c>OnInitialized</c> and unsubscribes in <c>Dispose</c>. The Wave 3
/// <c>EventDetectionService</c> (and any other in-process producer) calls
/// <see cref="Show"/> per detected event.
/// </para>
/// <para>
/// D-10 defense-in-depth: <see cref="Show"/> early-exits when
/// <see cref="INotificationsGate.IsEnabled"/> returns <c>false</c>. The check is on the
/// hot path because it gates every emission; the cached boolean on
/// <see cref="PreferencesService"/> (D-09) keeps the cost negligible.
/// </para>
/// <para>
/// Severity is restricted to the 4-element set {info, success, warning, danger}
/// (REQ-UI-41). Any other value throws <see cref="ArgumentException"/>; this protects
/// the downstream <c>ToastHost</c> render path from a CSS class injection attack
/// (T-08-20).
/// </para>
/// </summary>
public sealed class ToastService
{
    private static readonly HashSet<string> ValidSeverities =
        new(StringComparer.Ordinal) { "info", "success", "warning", "danger" };

    private readonly INotificationsGate _gate;
    private readonly ILogger<ToastService> _log;

    public ToastService(INotificationsGate gate, ILogger<ToastService> log)
    {
        _gate = gate;
        _log = log;
    }

    /// <summary>
    /// Raised once per <see cref="Show"/> call when the notifications gate is open.
    /// Subscribers run sequentially; an exception in one subscriber is caught and
    /// logged so the remaining subscribers still execute (T-08-18 resilience).
    /// </summary>
    public event Func<ToastEntry, Task>? OnShow;

    /// <summary>
    /// Emit a toast. No-ops when <see cref="INotificationsGate.IsEnabled"/> is false
    /// (D-10 hot-path gate). Validates <paramref name="title"/> and
    /// <paramref name="severity"/> defensively before consulting the gate so that
    /// programmer errors in callers fail loud regardless of preference state.
    /// </summary>
    /// <param name="title">Required. Non-empty, non-whitespace.</param>
    /// <param name="message">Optional body text. <c>null</c> is normalised to <see cref="string.Empty"/>.</param>
    /// <param name="severity">Required. One of {info, success, warning, danger}.</param>
    public async Task Show(string title, string message, string severity)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Toast title required.", nameof(title));
        }
        if (!ValidSeverities.Contains(severity))
        {
            throw new ArgumentException(
                $"Severity '{severity}' not in {{info, success, warning, danger}}.",
                nameof(severity));
        }

        // D-10 defense-in-depth gate. Cached property on PreferencesService is exposed
        // as INotificationsGate.IsEnabled — no async cost on the hot path.
        if (!_gate.IsEnabled)
        {
            return;
        }

        var entry = new ToastEntry(Guid.NewGuid(), title, message ?? string.Empty, severity);

        var handler = OnShow;
        if (handler is null)
        {
            return;
        }

        // Iterate per-handler so a single subscriber's exception doesn't kill the rest.
        // T-08-18: subscriber resilience.
        foreach (var del in handler.GetInvocationList().Cast<Func<ToastEntry, Task>>())
        {
            try
            {
                await del(entry);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ToastService subscriber threw during OnShow dispatch");
            }
        }
    }
}

/// <summary>
/// Phase 8 D-08 — toast queue entry. Severity is one of
/// {info, success, warning, danger} (REQ-UI-41); validated by
/// <see cref="ToastService.Show"/> before construction.
/// </summary>
public sealed record ToastEntry(Guid Id, string Title, string Message, string Severity);
