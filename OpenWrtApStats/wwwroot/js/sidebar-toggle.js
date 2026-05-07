// Sidebar toggle that survives Blazor InteractiveServer remounts.
//
// AdminLTE 4 RC7 wires `[data-lte-toggle="sidebar"]` once at DOMContentLoaded
// (no MutationObserver). Blazor's interactive remount replaces the prerendered
// <button> with a fresh node, orphaning AdminLTE's listener — clicking the
// visible toggle then no-ops. Debug session: .planning/debug/sidebar-toggle-no-collapse.md
//
// Fix: a click listener delegated from `document` (which Blazor never replaces)
// matching a project-scoped attribute (`data-app-toggle="sidebar"`) so AdminLTE
// won't bind to the same node and double-toggle. Toggle logic mirrors AdminLTE's
// PushMenu: `body.sidebar-collapse` on desktop, `body.sidebar-open` on mobile,
// breakpoint matched to `.sidebar-expand-lg::before { content: "991.98px" }`.
(function () {
    'use strict';

    var SIDEBAR_BREAKPOINT_PX = 992;
    var COLLAPSED = 'sidebar-collapse';
    var MOBILE_OPEN = 'sidebar-open';
    var TRIGGER_SELECTOR = '[data-app-toggle="sidebar"]';

    function isMobile() {
        return window.innerWidth <= SIDEBAR_BREAKPOINT_PX;
    }

    function toggleSidebar() {
        var body = document.body;
        if (body.classList.contains(COLLAPSED)) {
            body.classList.remove(COLLAPSED);
            if (isMobile()) {
                body.classList.add(MOBILE_OPEN);
            }
        } else {
            body.classList.remove(MOBILE_OPEN);
            body.classList.add(COLLAPSED);
        }

        // Phase 8 D-17 EXTENSION — dispatch custom event AFTER the toggle so
        // sidebar-state.js (the only listener) can persist the collapsed bool
        // to localStorage. The document-delegated handler binding above stays
        // unchanged; this is a pure additive notification.
        // Wrapped in try/catch in case CustomEvent constructor is unsupported
        // (very old browsers); UX must continue to work even if dispatch fails.
        try {
            document.dispatchEvent(new CustomEvent('openwrt-ap-stats:sidebar-toggled', {
                detail: { collapsed: body.classList.contains(COLLAPSED) }
            }));
        } catch (e) { /* CustomEvent constructor unsupported — ignore */ }
    }

    document.addEventListener('click', function (event) {
        var trigger = event.target.closest && event.target.closest(TRIGGER_SELECTOR);
        if (!trigger) return;
        event.preventDefault();
        toggleSidebar();
    });
})();
