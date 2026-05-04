// local-time.js — render <time data-local-time datetime="...Z"> elements in the browser's
// local timezone. Server-side renders happen in UTC (the container's TZ), so without this
// the operator sees "2026-05-04 06:42:31Z" instead of e.g. "5/4/2026, 8:42:31 AM" in CEST.
//
// Format style is opinionated for this app:
//   - Default: full date + time in the browser's locale + timezone (toLocaleString).
//   - data-local-time="time" → time only (toLocaleTimeString).
//   - data-local-time="date" → date only (toLocaleDateString).
//
// Survives Blazor InteractiveServer remounts the same way /js/sidebar-toggle.js does:
// MutationObserver on the document body re-formats any <time data-local-time> nodes that
// land in the DOM after the initial parse — Blazor's render diff can replace these elements
// when a parent component re-renders, so a one-shot DOMContentLoaded scan would miss them.
(function () {
    'use strict';

    var SELECTOR = 'time[data-local-time]';
    var FORMATTED_FLAG = 'data-local-time-formatted';

    function format(el) {
        if (el.getAttribute(FORMATTED_FLAG) === '1') return;
        var iso = el.getAttribute('datetime');
        if (!iso) return;
        var d = new Date(iso);
        if (isNaN(d.getTime())) return;
        var mode = el.getAttribute('data-local-time');
        var text;
        switch (mode) {
            case 'time': text = d.toLocaleTimeString(); break;
            case 'date': text = d.toLocaleDateString(); break;
            default:     text = d.toLocaleString();     break;
        }
        el.textContent = text;
        el.setAttribute(FORMATTED_FLAG, '1');
    }

    function formatAll(root) {
        var nodes = (root || document).querySelectorAll(SELECTOR);
        for (var i = 0; i < nodes.length; i++) format(nodes[i]);
    }

    function init() {
        formatAll();
        var obs = new MutationObserver(function (mutations) {
            for (var i = 0; i < mutations.length; i++) {
                var m = mutations[i];
                for (var j = 0; j < m.addedNodes.length; j++) {
                    var n = m.addedNodes[j];
                    if (n.nodeType !== 1) continue;
                    if (n.matches && n.matches(SELECTOR)) format(n);
                    formatAll(n);
                }
                if (m.type === 'attributes' && m.target.matches && m.target.matches(SELECTOR)) {
                    m.target.removeAttribute(FORMATTED_FLAG);
                    format(m.target);
                }
            }
        });
        obs.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['datetime', 'data-local-time'],
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
