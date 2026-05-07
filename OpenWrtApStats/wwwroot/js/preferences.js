// OpenWrtApStats/wwwroot/js/preferences.js
// Phase 8 — D-09/D-12 strongly-typed preferences interop.
// Loaded by Services/PreferencesService.cs via dynamic import('./preferences.js').
// All keys are prefixed with 'openwrt-ap-stats:' to namespace localStorage.
// Wrapped in try/catch so private mode / sandboxed iframes silently no-op.
//
// Storage shape contract (closes plan-checker BLOCKER 1 — see PreferencesService.cs):
//   - 'theme'         is stored RAW via set/get (NOT setJson/getJson) so the
//                     App.razor anti-FOUC inline script can compare via
//                     `localStorage.getItem('openwrt-ap-stats:theme') === 'dark'`
//                     synchronously, before any JS module is parsed.
//   - 'search-text'   is stored RAW (free-form user text — double-encoding
//                     would lose round-trip parity for needles containing `"`).
//   - everything else is JSON-encoded via setJson/getJson.

const PREFIX = 'openwrt-ap-stats:';

function safeGetItem(fullKey) {
    try { return window.localStorage.getItem(fullKey); }
    catch (e) { return null; }
}
function safeSetItem(fullKey, value) {
    try {
        if (value === null || value === undefined) {
            window.localStorage.removeItem(fullKey);
        } else {
            window.localStorage.setItem(fullKey, value);
        }
        return true;
    } catch (e) { return false; }
}
function safeRemoveItem(fullKey) {
    try { window.localStorage.removeItem(fullKey); return true; }
    catch (e) { return false; }
}

export function get(key) {
    return safeGetItem(PREFIX + key);
}
export function set(key, value) {
    return safeSetItem(PREFIX + key, value);
}
export function getJson(key) {
    const raw = safeGetItem(PREFIX + key);
    if (raw === null || raw === undefined) return null;
    try { return JSON.parse(raw); } catch (e) { return null; }
}
export function setJson(key, value) {
    if (value === null || value === undefined) {
        return safeRemoveItem(PREFIX + key);
    }
    try { return safeSetItem(PREFIX + key, JSON.stringify(value)); }
    catch (e) { return false; }
}
export function clearKeys(keys) {
    if (!Array.isArray(keys)) return false;
    for (const k of keys) safeRemoveItem(PREFIX + k);
    return true;
}
