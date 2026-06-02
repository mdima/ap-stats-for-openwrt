// ApStatsForOpenWrt/wwwroot/js/chart-interop.js
// Phase 19 (M-HIST-05 / M-HIST-07 / D-02) — Chart.js interop ES module.
//
// Mirrors preferences.js's named-export + try/catch-safe idiom. Imported once per
// component via IJSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/chart-interop.js")
// — and ONLY from OnAfterRenderAsync(firstRender), never OnInitialized (interop is
// FORBIDDEN during prerender; see PITFALLS.md / CLAUDE.md).
//
// This module references the GLOBAL `window.Chart` provided by the vendored UMD bundle
// (App.razor loads <script src="/lib/chart.js/chart.umd.min.js"> before blazor.web.js).
// It does NOT `import` Chart.js — the UMD build is not an ES module.
//
// D-02 dark-mode theming: chart chrome + segment colors are read at call time from the
// Bootstrap `--bs-*` CSS variables on <html>. One idempotent MutationObserver on
// document.documentElement[data-bs-theme] re-reads those colors and calls chart.update()
// on every live chart — decoupled from the ThemeToggle button so it covers the button,
// system-preference, and anti-FOUC paths uniformly.
//
// Security (T-19-01): every DTO label string (SSID / AP name) is passed as DATA into the
// Chart.js config object. Chart.js renders to <canvas>, not the DOM — labels never become
// markup, and this module never touches innerHTML / builds HTML strings.

// canvasId -> Chart instance
const charts = new Map();

// D-02: guard so the MutationObserver is installed exactly once per module instance.
let _observerInstalled = false;

/**
 * Read the six theme tokens from the live computed style of <html> at call time.
 * NEVER hardcode hex — these re-resolve per theme (D-02).
 *   - success / warning / secondary : the StatusBadge triad (Online / Stale / Offline)
 *   - body / border / secondaryText : axis ticks, gridlines, legend, tooltip chrome
 *   - bodyBg                        : tooltip BACKGROUND — must contrast with `body` text in
 *                                     both themes (Chart.js's default tooltip bg is dark, which
 *                                     renders body-color text black-on-black in light mode).
 */
function readThemeColors() {
    const cs = getComputedStyle(document.documentElement);
    const read = (name) => cs.getPropertyValue(name).trim();
    return {
        success: read('--bs-success'),
        warning: read('--bs-warning'),
        secondary: read('--bs-secondary'),
        body: read('--bs-body-color'),
        border: read('--bs-border-color'),
        secondaryText: read('--bs-secondary-color'),
        bodyBg: read('--bs-body-bg'),
    };
}

/** Resolve a <canvas> 2d context by id, or null if the element is gone (W4 race). */
function getCtx(canvasId) {
    try {
        const el = document.getElementById(canvasId);
        return el ? el.getContext('2d') : null;
    } catch (e) {
        return null;
    }
}

/**
 * Install (once) the single MutationObserver on <html data-bs-theme> that re-themes
 * every live chart on a theme change. Idempotent — guarded by _observerInstalled.
 */
function ensureThemeObserver() {
    if (_observerInstalled) return;
    try {
        const observer = new MutationObserver(() => {
            const colors = readThemeColors();
            for (const chart of charts.values()) {
                try {
                    applyChromeColors(chart, colors);
                    reapplySegmentColors(chart, colors);
                    chart.update();
                } catch (e) { /* a single chart failing must not abort the rest */ }
            }
        });
        observer.observe(document.documentElement, {
            attributes: true,
            attributeFilter: ['data-bs-theme'],
        });
        _observerInstalled = true;
    } catch (e) {
        // MutationObserver unavailable (very old/sandboxed) — charts simply won't
        // re-theme live; initial render still uses the correct colors.
    }
}

/** Apply axis/grid/legend/tooltip chrome colors from the theme tokens onto a built chart. */
function applyChromeColors(chart, colors) {
    const opts = chart.options;
    if (opts.scales) {
        for (const key of Object.keys(opts.scales)) {
            const scale = opts.scales[key];
            if (scale.ticks) scale.ticks.color = colors.secondaryText;
            if (scale.grid) scale.grid.color = colors.border;
            if (scale.title) scale.title.color = colors.body;
        }
    }
    if (opts.plugins) {
        if (opts.plugins.legend && opts.plugins.legend.labels) {
            opts.plugins.legend.labels.color = colors.body;
        }
        if (opts.plugins.tooltip) {
            opts.plugins.tooltip.titleColor = colors.body;
            opts.plugins.tooltip.bodyColor = colors.body;
            opts.plugins.tooltip.borderColor = colors.border;
            opts.plugins.tooltip.backgroundColor = colors.bodyBg;
        }
    }
}

/**
 * Re-apply the Online/Stale/Offline triad colors to a KPI (stacked-bar) chart's datasets.
 * Stored on each dataset via a custom `_segment` key set at render time; line charts have
 * no `_segment` and are left untouched (their per-series colors are theme-independent).
 */
function reapplySegmentColors(chart, colors) {
    for (const ds of chart.data.datasets) {
        if (ds._segment === 'online') ds.backgroundColor = colors.success;
        else if (ds._segment === 'stale') ds.backgroundColor = colors.warning;
        else if (ds._segment === 'offline') ds.backgroundColor = colors.secondary;
    }
}

// ---------------------------------------------------------------------------
// KPI stacked-bar chart (M-HIST-05 / L-07)
// dto shape: { labels: string[], online: number[], stale: number[], offline: number[] }
// ---------------------------------------------------------------------------

function buildKpiConfig(dto, colors) {
    return {
        type: 'bar',
        data: {
            labels: dto?.labels ?? [],
            datasets: [
                { _segment: 'online', label: 'Online', data: dto?.online ?? [], backgroundColor: colors.success },
                { _segment: 'stale', label: 'Stale', data: dto?.stale ?? [], backgroundColor: colors.warning },
                { _segment: 'offline', label: 'Offline', data: dto?.offline ?? [], backgroundColor: colors.secondary },
            ],
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { mode: 'index', intersect: false },
            scales: {
                x: {
                    stacked: true,
                    ticks: { color: colors.secondaryText },
                    grid: { color: colors.border },
                },
                y: {
                    stacked: true,
                    beginAtZero: true,
                    ticks: { color: colors.secondaryText, precision: 0 },
                    grid: { color: colors.border },
                },
            },
            plugins: {
                legend: { labels: { color: colors.body } },
                tooltip: {
                    // L-07: hover-tooltips with exact counts (the reason Chart.js was chosen).
                    enabled: true,
                    titleColor: colors.body,
                    bodyColor: colors.body,
                    backgroundColor: colors.bodyBg, // contrast with body text in both themes (else black-on-black in light mode)
                    borderColor: colors.border,
                    borderWidth: 1,
                },
            },
        },
    };
}

export function renderKpiChart(canvasId, dto) {
    try {
        const existing = charts.get(canvasId);
        if (existing) { existing.destroy(); charts.delete(canvasId); }
        const ctx = getCtx(canvasId);
        if (!ctx || typeof Chart === 'undefined') return false;
        const colors = readThemeColors();
        const chart = new Chart(ctx, buildKpiConfig(dto, colors));
        charts.set(canvasId, chart);
        ensureThemeObserver();
        return true;
    } catch (e) {
        return false;
    }
}

export function updateKpiChart(canvasId, dto) {
    try {
        const chart = charts.get(canvasId);
        if (!chart) return false;
        chart.data.labels = dto?.labels ?? [];
        const map = { online: dto?.online ?? [], stale: dto?.stale ?? [], offline: dto?.offline ?? [] };
        for (const ds of chart.data.datasets) {
            if (ds._segment && map[ds._segment]) ds.data = map[ds._segment];
        }
        chart.update();
        return true;
    } catch (e) {
        return false;
    }
}

// ---------------------------------------------------------------------------
// RSSI multi-series line chart (M-HIST-07 / L-06)
// dto shape: {
//   labels: string[],                              // x-axis bucket labels (time)
//   series: [ { label: string, data: (number|null)[] }, ... ]   // one per (NodeId, NetworkId)
// }
// Y = RSSI dBm, suggested -90..-30 (extensible). legend = the series labels in the DTO.
// ---------------------------------------------------------------------------

// Stable, theme-independent palette for distinct (NodeId, NetworkId) series. Cycles if
// more series than colors. These are SERIES identities, not the Online/Stale/Offline triad,
// so they are intentionally fixed (D-02 governs chrome + the triad, not series hues).
const SERIES_PALETTE = [
    '#0d6efd', '#dc3545', '#198754', '#fd7e14',
    '#6f42c1', '#20c997', '#d63384', '#ffc107',
];

function buildRssiConfig(dto, colors) {
    const series = dto?.series ?? [];
    const datasets = series.map((s, i) => ({
        label: s?.label ?? '',
        data: s?.data ?? [],
        borderColor: SERIES_PALETTE[i % SERIES_PALETTE.length],
        backgroundColor: SERIES_PALETTE[i % SERIES_PALETTE.length],
        spanGaps: false, // null = gap (offline bucket), keep the break visible
        tension: 0.2,
        pointRadius: 2,
    }));
    return {
        type: 'line',
        data: { labels: dto?.labels ?? [], datasets },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { mode: 'index', intersect: false },
            scales: {
                x: {
                    ticks: { color: colors.secondaryText },
                    grid: { color: colors.border },
                },
                y: {
                    title: { display: true, text: 'RSSI (dBm)', color: colors.body },
                    suggestedMin: -90,
                    suggestedMax: -30,
                    ticks: { color: colors.secondaryText },
                    grid: { color: colors.border },
                },
            },
            plugins: {
                legend: { labels: { color: colors.body } },
                tooltip: {
                    enabled: true,
                    titleColor: colors.body,
                    bodyColor: colors.body,
                    backgroundColor: colors.bodyBg, // contrast with body text in both themes (else black-on-black in light mode)
                    borderColor: colors.border,
                    borderWidth: 1,
                },
            },
        },
    };
}

export function renderRssiChart(canvasId, dto) {
    try {
        const existing = charts.get(canvasId);
        if (existing) { existing.destroy(); charts.delete(canvasId); }
        const ctx = getCtx(canvasId);
        if (!ctx || typeof Chart === 'undefined') return false;
        const colors = readThemeColors();
        const chart = new Chart(ctx, buildRssiConfig(dto, colors));
        charts.set(canvasId, chart);
        ensureThemeObserver();
        return true;
    } catch (e) {
        return false;
    }
}

export function updateRssiChart(canvasId, dto) {
    try {
        const chart = charts.get(canvasId);
        if (!chart) return false;
        chart.data.labels = dto?.labels ?? [];
        const series = dto?.series ?? [];
        // Rebuild datasets so series add/remove across roams is reflected.
        chart.data.datasets = series.map((s, i) => ({
            label: s?.label ?? '',
            data: s?.data ?? [],
            borderColor: SERIES_PALETTE[i % SERIES_PALETTE.length],
            backgroundColor: SERIES_PALETTE[i % SERIES_PALETTE.length],
            spanGaps: false,
            tension: 0.2,
            pointRadius: 2,
        }));
        chart.update();
        return true;
    } catch (e) {
        return false;
    }
}

// ---------------------------------------------------------------------------
// Shared teardown
// ---------------------------------------------------------------------------

export function destroyChart(canvasId) {
    try {
        const chart = charts.get(canvasId);
        if (chart) { chart.destroy(); charts.delete(canvasId); }
        return true;
    } catch (e) {
        // Even if destroy throws, drop the handle so we don't leak / re-update a dead chart.
        charts.delete(canvasId);
        return false;
    }
}
