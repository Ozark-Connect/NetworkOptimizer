// LAN Health badges for the 2D and 3D LAN maps: one poll a minute, shared by both. Keyed by the
// map's own link id ("uplink-{child mac}"). A badge shows when a link is below healthy, and on
// hover for any fabric link; clicking it opens the link's timeline on Monitoring - LAN Health.

const ENDPOINT = '/api/monitoring/lan-flow-map/link-health';
const POLL_MS = 60000;

// AppIcon's warning and check glyphs (24 grid, stroke 1.6).
const ICON_WARN = '<path d="M12 2.500 21.500 18.500h-19L12 2.500z" fill="currentColor" fill-opacity="0.18"/><path d="M12 8.500v4.500M12 15.700v.1"/>';
const ICON_OK = '<path d="M5 12.500l4.500 4.500L19 7.500"/>';

let health = new Map();
let timer = null;
const listeners = new Set();

async function poll() {
    try {
        const res = await fetch(ENDPOINT, { credentials: 'same-origin' });
        if (!res.ok) return;
        const list = await res.json();
        health = new Map(list.map(h => [h.mapLinkId, h]));
        for (const fn of listeners) { try { fn(health); } catch { /* one map's error must not stop the other */ } }
    } catch { /* offline or signed out: keep the last badges */ }
}

/** Calls fn with the health map now and after every poll; returns the unsubscribe. */
export function subscribe(fn) {
    listeners.add(fn);
    if (!timer) { poll(); timer = setInterval(poll, POLL_MS); }
    fn(health);
    return () => {
        listeners.delete(fn);
        if (listeners.size === 0 && timer) { clearInterval(timer); timer = null; }
    };
}

/** The grade color, from the app's score palette. */
export function gradeColor(score) {
    if (score >= 90) return '#24bc70';
    if (score >= 75) return '#3b82f6';
    if (score >= 60) return '#e79613';
    return '#ee6368';
}

/** lanh-43. */
export function tooltip(h) {
    return `LAN Health ${h.score}: ${h.status}. Click for the link's timeline.`;
}

/** A badge element for one link: icon and score, tooltip, click through to the link. */
export function createBadge(h) {
    const el = document.createElement('span');
    el.className = 'lan-health-map-badge' + (h.healthy ? ' is-healthy' : '');
    el.style.color = gradeColor(h.score);
    el.innerHTML = `<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">${h.healthy ? ICON_OK : ICON_WARN}</svg><span>${h.score}</span>`;
    el.setAttribute('data-tooltip', tooltip(h));
    el.setAttribute('data-tooltip-hover-only', '');
    el.addEventListener('click', (e) => {
        e.stopPropagation();
        // Blazor's own navigation keeps the circuit; a full load is the fallback.
        if (window.Blazor?.navigateTo) window.Blazor.navigateTo(h.href); else window.location.href = h.href;
    });
    el.addEventListener('pointerdown', (e) => e.stopPropagation());
    return el;
}
