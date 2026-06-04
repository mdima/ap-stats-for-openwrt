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

// Quick task 260604-a5u (QUAL-02) — overflow-gated sibling of initTooltips.
//
// WHY a SEPARATE export with a DISTINCT data-bs-toggle value: the Dashboard vendor
// column (Dashboard.razor) clamps long OUI names to two lines (.two-line-ellipsis,
// site.css). The full name should reveal in a tooltip ONLY when the clamp actually
// truncated it — a short name that fits two lines must show NO tooltip. The standard
// initTooltips selector ([data-bs-toggle="tooltip"]) is unconditional, so these
// elements opt in with the distinct value data-bs-toggle="tooltip-overflow"; that
// keeps initTooltips from double-initializing them (no tooltip on names that fit).
//
// The gate is a vertical-truncation test: with -webkit-line-clamp the rendered box is
// capped at clientHeight while the full (unclamped) content measures scrollHeight.
//
// ROUNDING TOLERANCE (260604-a5u post-ship fix): a strict scrollHeight > clientHeight
// fired on NON-truncated one-line names. .two-line-ellipsis sets line-height:1.2, so a
// line's height is fractional (e.g. 14px font -> 16.8px); scrollHeight rounds UP while
// clientHeight rounds to nearest, so a fully-visible span can report e.g. 17 vs 16 — a
// permanent off-by-one. A genuinely clamped line differs by a FULL line-height
// (>= ~17px), so a small constant tolerance (> 2px) cleanly separates rounding noise
// from real truncation at any sane font size or zoom level.
//
// DISPOSE-FIRST, UNCONDITIONALLY: an element may have STOPPED overflowing since the
// last render (rows replace every poll Cycle, container can resize), so we always drop
// any stale instance before re-evaluating; we only construct a fresh tooltip when the
// element currently overflows. PLAIN TEXT only (no html:true) per the 260531 pitfall.

// RESIZE RE-EVALUATION (260604-a5u follow-up, operator live finding): truncation state
// changes when the window — and with it the table column — resizes, but Blazor only
// re-renders on the next poll Cycle, so the gate went stale between cycles. A single
// debounced window-resize listener re-runs the overflow evaluation. It is wired ONCE on
// the first successful initOverflowTooltips call (the function runs after EVERY render;
// the module-scoped flag keeps listeners from stacking) and intentionally persists for
// the page's lifetime: this module is a page-scoped singleton, and the handler is a
// harmless no-op on pages with no opt-in elements (querySelectorAll returns empty).
let overflowResizeWired = false;
let overflowResizeTimer = 0;
let overflowLastSelector = '[data-bs-toggle="tooltip-overflow"]';

/**
 * (Re)initialize Bootstrap tooltips ONLY on opt-in elements that currently overflow
 * their -webkit-line-clamp box (scrollHeight > clientHeight). Dispose-then-recreate
 * per element, same try/catch-safe shape as initTooltips (silent no-op under prerender
 * / sandbox where global `bootstrap` is absent).
 *
 * @param {string} rootSelector CSS selector for opt-in elements; defaults to every
 *                              element carrying data-bs-toggle="tooltip-overflow".
 */
export function initOverflowTooltips(rootSelector = '[data-bs-toggle="tooltip-overflow"]') {
    try {
        // Bundle not parsed yet / unavailable (prerender, sandbox) — no-op.
        if (typeof bootstrap === 'undefined' || !bootstrap.Tooltip) return;

        // Remember the selector and arm the one-time debounced resize re-evaluation.
        overflowLastSelector = rootSelector;
        if (!overflowResizeWired) {
            overflowResizeWired = true;
            window.addEventListener('resize', () => {
                clearTimeout(overflowResizeTimer);
                overflowResizeTimer = setTimeout(
                    () => initOverflowTooltips(overflowLastSelector), 150);
            });
        }

        const elements = document.querySelectorAll(rootSelector);
        for (const el of elements) {
            try {
                // Always drop any prior instance first — the element may have stopped
                // overflowing since the last render, in which case it gets no tooltip.
                bootstrap.Tooltip.getInstance(el)?.dispose();
                // > 2px tolerance: see ROUNDING TOLERANCE note above — strict > fires
                // on every cell because fractional line-height rounds asymmetrically.
                if (el.scrollHeight - el.clientHeight > 2) {
                    new bootstrap.Tooltip(el);
                }
            } catch (e) {
                // A single element failing must not abort the rest of the page.
            }
        }
    } catch (e) {
        // bootstrap / DOM unavailable — silently no-op (matches chart-interop.js shape).
    }
}
