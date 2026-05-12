// OpenWrtApStats/wwwroot/js/sidebar-state.js
// Phase 8 — D-17. Side-effect module: listens for the
// 'openwrt-ap-stats:sidebar-toggled' CustomEvent (dispatched by the
// EXTENDED sidebar-toggle.js) and persists the resulting collapsed
// state to localStorage with the openwrt-ap-stats: prefix.
//
// DO NOT modify the document-delegated toggle handler in
// sidebar-toggle.js — that survives Blazor remounts and its binding
// shape is load-bearing (see .planning/debug/resolved/
// sidebar-toggle-no-collapse.md). This module only LISTENS.

(function () {
    try {
        document.addEventListener('openwrt-ap-stats:sidebar-toggled', function (e) {
            try {
                const collapsed = !!(e && e.detail && e.detail.collapsed);
                window.localStorage.setItem('openwrt-ap-stats:sidebar-collapsed', JSON.stringify(collapsed));
            } catch (err) { /* localStorage unavailable — silently no-op */ }
        });
    } catch (e) { /* document not available — silently no-op */ }
})();

// Optional explicit hook so C# can `await import('./js/sidebar-state.js')` and confirm load:
export function ensureLoaded() { return true; }
