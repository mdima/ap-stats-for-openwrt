// ApStatsForOpenWrt/wwwroot/js/bs-tooltips.js
// Quick task 260603-968 (QUAL-02) — Bootstrap tooltip (re)initializer ES module.
//
// Mirrors chart-interop.js / preferences.js's named-export + try/catch-safe idiom.
// Imported once per page via IJSRuntime.InvokeAsync<IJSObjectReference>("import",
// "./js/bs-tooltips.js") and called from OnAfterRenderAsync — and ONLY from
// OnAfterRenderAsync, never OnInitialized (interop is FORBIDDEN during prerender;
// see PITFALLS.md / CLAUDE.md).
//
// This module references the GLOBAL `bootstrap` provided by the vendored
// bootstrap.bundle.min.js (App.razor loads it before blazor.web.js). It does NOT
// `import` Bootstrap — the bundle is a global UMD build, not an ES module.
//
// WHY a project-owned initializer: the app initializes NO Bootstrap tooltip JS by
// default, so opt-in `data-bs-toggle="tooltip"` attributes are inert until something
// constructs a bootstrap.Tooltip for them. This module is that something — pointed at
// the dashboard RSSI sparkline (RssiSparkline.razor) which now carries the opt-in
// attribute instead of a native SVG <title>.
//
// IDEMPOTENCY (the critical invariant): the dashboard re-renders its Virtualized rows
// on EVERY poll Cycle (StateHasChanged), replacing the per-row sparkline DOM nodes.
// Without disposing the prior instance first, tooltip instances bound to replaced nodes
// leak/duplicate. So for each matched element we dispose any existing instance BEFORE
// creating a new one — the same destroy-before-create lifecycle chart-interop.js uses.
//
// PLAIN TEXT only: tooltips are created with default escaping (no html:true) — the
// 260531 UAT fix proved `<br>`/markup renders literally in this app's tooltip path.
// The sparkline title is a single line with a "·" separator; it is surfaced verbatim.

/**
 * (Re)initialize Bootstrap tooltips on every opt-in element under `rootSelector`.
 * Dispose-then-recreate per element keeps this leak-free across the dashboard's
 * per-Cycle re-render. Whole body is try/catch-safe so a prerender / sandboxed call
 * (no global `bootstrap`) silently no-ops.
 *
 * @param {string} rootSelector CSS selector for opt-in elements; defaults to every
 *                              element carrying data-bs-toggle="tooltip".
 */
export function initTooltips(rootSelector = '[data-bs-toggle="tooltip"]') {
    try {
        // Bundle not parsed yet / unavailable (prerender, sandbox) — no-op.
        if (typeof bootstrap === 'undefined' || !bootstrap.Tooltip) return;

        const elements = document.querySelectorAll(rootSelector);
        for (const el of elements) {
            try {
                // CRITICAL idempotency: drop any instance bound to a (possibly stale)
                // node before constructing a fresh one.
                bootstrap.Tooltip.getInstance(el)?.dispose();
                new bootstrap.Tooltip(el);
            } catch (e) {
                // A single element failing must not abort the rest of the page.
            }
        }
    } catch (e) {
        // bootstrap / DOM unavailable — silently no-op (matches chart-interop.js shape).
    }
}
