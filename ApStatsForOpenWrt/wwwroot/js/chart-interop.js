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

/**
 * 260604-rq7: Convert an ISO-8601 UTC label (the Razor ToInterop now emits `.ToString("o")`,
 * trailing `Z`) to a browser-LOCAL `HH:mm` string. `new Date(iso)` parses the UTC instant; the
 * locale-formatter renders it in the browser's timezone, so x-axis ticks AND tooltip titles read
 * in the operator's local time instead of UTC. Defensive: any non-string / unparseable value
 * passes through unchanged (so a pre-existing local label or a numeric index is never corrupted).
 */
function localHm(iso) {
    if (typeof iso !== 'string') return iso;
    const d = new Date(iso);
    if (isNaN(d.getTime())) return iso;
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
}

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
            opts.plugins.tooltip.footerColor = colors.body;
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

/**
 * Per-chart "no data" overlay (M-HIST-05 refinement): when the chart has nothing to plot,
 * draw a centered message on the canvas telling the operator history will appear there — instead
 * of a blank/empty axes frame. Themed via the live --bs-* tokens so it reads in both modes.
 * "No data" == no labels OR every datapoint across all datasets is null (a gap-only window).
 * A real zero-device snapshot (counts == 0, not null) is data and is NOT treated as empty.
 */
const noDataPlugin = {
    id: 'noData',
    afterDraw(chart) {
        const hasData =
            (chart.data?.labels?.length ?? 0) > 0 &&
            chart.data.datasets.some(ds => (ds.data ?? []).some(v => v !== null && v !== undefined));
        if (hasData) return;

        const area = chart.chartArea;
        if (!area) return;
        const text = chart.options?.plugins?.noData?.text ?? 'No data yet';
        const colors = readThemeColors();
        const ctx = chart.ctx;
        ctx.save();
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillStyle = colors.secondaryText || colors.body || '#6c757d';
        ctx.font = '500 14px system-ui, -apple-system, "Segoe UI", Roboto, sans-serif';
        ctx.fillText(text, (area.left + area.right) / 2, (area.top + area.bottom) / 2);
        ctx.restore();
    },
};

// RSSI quality-tier thresholds + zone colours (M-HIST-07 refinement). Mirrors the inline
// RssiSparkline tiers: good ≥ −67 dBm, fair −67…−80, weak < −80. Colours are intentionally
// FIXED (semantic quality, identical in both themes — only chart chrome is theme-driven, D-02).
const GOOD_FLOOR_DBM = -67;
const FAIR_FLOOR_DBM = -80;
const ZONE_GOOD = 'rgba(25,135,84,.07)';
const ZONE_FAIR = 'rgba(255,193,7,.10)';
const ZONE_WEAK = 'rgba(220,53,69,.07)';

/**
 * RSSI quality zones (M-HIST-07 refinement): three horizontal bands painted behind the
 * line(s) — green (good) at the top, yellow (fair) in the middle, red (weak) at the bottom —
 * so the detail chart reads the same way as the inline sparkline. dBm thresholds are mapped to
 * pixels via the LIVE y-scale (so the bands track the actual axis range, fixed −90..−30 by
 * default) and clamped to the chart area. Enabled per-chart via options.plugins.rssiZones.enabled.
 */
const rssiZonesPlugin = {
    id: 'rssiZones',
    beforeDatasetsDraw(chart) {
        if (!chart.options?.plugins?.rssiZones?.enabled) return;
        const area = chart.chartArea;
        const yScale = chart.scales?.y;
        if (!area || !yScale) return;

        const clampY = (py) => Math.max(area.top, Math.min(area.bottom, py));
        const yGood = clampY(yScale.getPixelForValue(GOOD_FLOOR_DBM)); // good/fair boundary
        const yFair = clampY(yScale.getPixelForValue(FAIR_FLOOR_DBM)); // fair/weak boundary

        const ctx = chart.ctx;
        const left = area.left;
        const width = area.right - area.left;
        const band = (y0, y1, color) => {
            const h = y1 - y0;
            if (h <= 0.5) return; // sub-pixel band not worth painting
            ctx.fillStyle = color;
            ctx.fillRect(left, y0, width, h);
        };

        ctx.save();
        band(area.top, yGood, ZONE_GOOD);    // ≥ −67 : axis top → −67 line
        band(yGood, yFair, ZONE_FAIR);       // −80…−67
        band(yFair, area.bottom, ZONE_WEAK); // < −80 : −80 line → axis bottom
        ctx.restore();
    },
};

// ---------------------------------------------------------------------------
// KPI stacked-bar chart (M-HIST-05 / L-07)
// dto shape: { labels: string[], online: number[], stale: number[], offline: number[] }
// ---------------------------------------------------------------------------

function buildKpiConfig(dto, colors) {
    return {
        type: 'bar',
        data: {
            labels: (dto?.labels ?? []).map(localHm), // 260604-rq7: UTC ISO -> browser-local HH:mm
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
                // M-HIST-05 refinement: centered overlay when the KPI chart has no history yet.
                noData: { text: 'History will appear here as it is collected' },
                tooltip: {
                    // L-07: hover-tooltips with exact counts (the reason Chart.js was chosen).
                    enabled: true,
                    titleColor: colors.body,
                    bodyColor: colors.body,
                    footerColor: colors.body, // else the "Total: N" footer renders white-on-light (invisible)
                    backgroundColor: colors.bodyBg, // contrast with body text in both themes (else black-on-black in light mode)
                    borderColor: colors.border,
                    borderWidth: 1,
                    callbacks: {
                        // M-HIST-05 refinement: show the per-bucket device total (sum of the stacked
                        // Online/Stale/Offline segments) as a footer beneath the per-segment counts.
                        footer: (items) => {
                            const total = items.reduce((sum, it) => sum + (it.parsed?.y ?? 0), 0);
                            return `Total: ${total} device${total === 1 ? '' : 's'}`;
                        },
                    },
                },
            },
        },
        // Per-chart registration of the no-data overlay (M-HIST-05 refinement).
        plugins: [noDataPlugin],
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
        // 260604-rq7: self-heal — no live instance (e.g. a prior render failed) -> render fresh
        // instead of a permanent silent no-op on every subsequent update.
        if (!chart) return renderKpiChart(canvasId, dto);
        chart.data.labels = (dto?.labels ?? []).map(localHm); // 260604-rq7: UTC ISO -> local HH:mm
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
        data: { labels: (dto?.labels ?? []).map(localHm), datasets }, // 260604-rq7: UTC ISO -> local HH:mm
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
                // M-HIST-07 refinement: green/yellow/red signal-quality bands behind the lines.
                rssiZones: { enabled: true },
                // 260604-rq7: centered overlay when the RSSI window has nothing to plot
                // (zero series or every datapoint null/gap) instead of bare axes.
                noData: { text: 'No samples in the last 12 h' },
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
        // Per-chart registration of the RSSI quality zones + the no-data overlay (260604-rq7).
        plugins: [rssiZonesPlugin, noDataPlugin],
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
        // 260604-rq7: self-heal — no live instance (e.g. a prior render failed) -> render fresh
        // instead of a permanent silent no-op on every subsequent update.
        if (!chart) return renderRssiChart(canvasId, dto);
        chart.data.labels = (dto?.labels ?? []).map(localHm); // 260604-rq7: UTC ISO -> local HH:mm
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
