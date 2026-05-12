// OpenWrtApStats/wwwroot/js/theme.js
// Phase 8 — D-13/D-31. Runtime theme application helpers.
// The anti-FOUC inline script in App.razor handles the pre-attach
// synchronous case. THIS module handles in-session toggles after the
// Blazor circuit is up and the user clicks the ThemeToggle button.

export function applyTheme(value) {
    try {
        if (value === 'dark') {
            document.documentElement.setAttribute('data-bs-theme', 'dark');
        } else {
            document.documentElement.removeAttribute('data-bs-theme');
        }
        return true;
    } catch (e) { return false; }
}

export function getSystemPreference() {
    try {
        const mql = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)');
        return (mql && mql.matches) ? 'dark' : 'light';
    } catch (e) { return 'light'; }
}
