// Remember a chart tab's time window, per site, per tab, in this browser.
//
// The preset only. Not the shift offset, and not a custom range: a preset is a preference that is
// still true tomorrow, while both of those are pinned to a moment someone chose to look at once.
// Restoring either later lands on a window that has silently aged, which reads as missing data
// rather than as a stale filter.
//
// No default of its own. Tab defaults differ deliberately - Network Performance and Device Stats
// open at 1h, the hardware stat tabs at 24h - so a caller keeps its own literal and this only
// speaks when something was actually stored.

const PREFIX = 'netopt.win';

function storageKey(tab) {
    let slug = '';
    try { slug = new URLSearchParams(location.search).get('site') || ''; } catch { /* opaque URL */ }
    return `${PREFIX}.${slug}.${tab}`;
}

/// Whether the URL is asking for a window itself. A deep link is applied after mount and wins
/// either way; standing down stops the saved preset firing a query the link will throw away,
/// which on a saved 30d is as expensive as the user's largest window.
function urlSetsWindow() {
    try {
        const q = new URLSearchParams(location.search);
        return q.has('at') || q.has('span');
    } catch { return false; }
}

/// The stored preset, or null to leave the caller's own default alone. Null rather than a number
/// so that 0 - the 15m preset - survives the round trip.
export function loadWindowHours(tab) {
    if (urlSetsWindow()) return null;
    try {
        const raw = localStorage.getItem(storageKey(tab));
        if (raw === null) return null;
        const hours = Number(raw);
        return Number.isFinite(hours) ? hours : null;
    } catch { return null; }
}

/// Called from the preset buttons alone. Never from a deep link's frameMoment/frameCustomWindow:
/// a linked window is something the link did, not a preference the user set.
export function saveWindowHours(tab, hours) {
    try { localStorage.setItem(storageKey(tab), String(hours)); } catch { /* storage unavailable */ }
}

/// Tell the page the reader moved the window themselves, so a link's ?at= stops outliving the
/// window it framed - a reload would otherwise drag them back to the alert's moment. Same hook
/// the analysis charts use.
export function notifyWindowMoved() {
    try { window.__netoptLatencyRef?.invokeMethodAsync('OnTimelineMovedByUser'); }
    catch { /* no ref yet, or the circuit is gone - the window still moved */ }
}

/// The active class ships in the Razor markup on each tab's own default, so a restored preset has
/// to move it.
export function markActiveRange(container, hours) {
    container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
    container.querySelector(`[data-range="${hours}"]`)?.classList.add('active');
}
