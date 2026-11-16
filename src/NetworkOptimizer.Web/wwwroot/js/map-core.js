// The shell every Leaflet route map shares (the Network Tools Route Map, the Path Map): Leaflet
// loading, the dark tiles, the wheel gate, the 2D LAN map's controls, fullscreen, fit, and the
// page tooltips that follow their points. Each map draws its own layers into `state.layers`.

const LEAFLET_JS = 'https://unpkg.com/leaflet@1.9.4/dist/leaflet.js';
const LEAFLET_CSS = 'https://unpkg.com/leaflet@1.9.4/dist/leaflet.css';

/** What placed a point, by TraceMapNode.Source: the Route Map's marker colors and their words. */
export const SOURCE_COLOR = { start: '#ededef', set: '#a78bfa', hostname: '#2ba89a', neighbor: '#3b82f6', learned: '#a0a0a8', geolite2: '#f59e0b' };
export const SOURCE_TEXT = {
    start: 'Where the trace started',
    set: 'Set by hand',
    hostname: "From the router's hostname",
    neighbor: 'From a hop a few ms away',
    learned: 'Learned from an earlier trace',
    geolite2: 'Where GeoLite2 places the address',
};

/** "Dallas to Chicago · 1,290 km": a leg's tooltip text. */
export function legText(fromCity, toCity, km) {
    const distance = km >= 100 ? Math.round(km / 10) * 10 : Math.round(km);
    return `${fromCity} to ${toCity} · ${distance.toLocaleString()} km`;
}

let leafletLoading = null;

export function loadLeaflet() {
    if (window.L) return Promise.resolve();
    if (leafletLoading) return leafletLoading;
    leafletLoading = new Promise((resolve, reject) => {
        if (!document.querySelector(`link[href="${LEAFLET_CSS}"]`)) {
            const link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = LEAFLET_CSS;
            document.head.appendChild(link);
        }
        const existing = document.querySelector(`script[src="${LEAFLET_JS}"]`);
        const script = existing || document.createElement('script');
        script.addEventListener('load', () => resolve());
        script.addEventListener('error', () => { leafletLoading = null; reject(new Error('Leaflet failed to load')); });
        if (!existing) {
            script.src = LEAFLET_JS;
            document.head.appendChild(script);
        } else if (window.L) {
            resolve();
        }
    });
    return leafletLoading;
}

/** An element with a class and text content. Text only: PTR hostnames are anyone's to set. */
export function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
}

const FS_ENTER_ICON = '<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 8 3 3 8 3"></polyline><polyline points="16 3 21 3 21 8"></polyline><polyline points="21 16 21 21 16 21"></polyline><polyline points="8 21 3 21 3 16"></polyline></svg>';
const FS_EXIT_ICON = '<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="4 10 10 10 10 4"></polyline><polyline points="14 4 14 10 20 10"></polyline><polyline points="20 14 14 14 14 20"></polyline><polyline points="10 20 10 14 4 14"></polyline></svg>';
const FIT_ICON = '<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="4 14 4 20 10 20"></polyline><polyline points="20 10 20 4 14 4"></polyline><line x1="14" y1="10" x2="20" y2="4"></line><line x1="4" y1="20" x2="10" y2="14"></line></svg>';

const FULLSCREEN = 'lan-flow-map-fullscreen';

/**
 * Builds the map in the host, with its tiles, wheel gate, and controls.
 * @param {HTMLElement} host
 * @param {{fitLabel?: string, onFit: Function, onFullscreen?: Function}} options -
 *   onFit brings the drawing back into view; onFullscreen(on) runs after the host enters or leaves fullscreen.
 * @returns {object} the map's state: { host, map, layers, tips, ... }
 */
export function createMap(host, options) {
    const L = window.L;
    const map = L.map(host, {
        scrollWheelZoom: false,
        // Quarter steps, so a route fits closely instead of dropping a whole level; the tiles
        // overlap by a pixel (app.css) so a fractional zoom shows no seams.
        zoomSnap: 0.25,
        minZoom: 2,
        worldCopyJump: false,
        attributionControl: true,
        // The 2D LAN map's controls replace Leaflet's: fullscreen top right, zoom and fit below it.
        zoomControl: false,
    });
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 12,
        attribution: '© OpenStreetMap',
        className: 'map-tiles-dark',
    }).addTo(map);
    map.attributionControl.setPrefix('<a href="https://leafletjs.com" target="_blank" rel="noopener">Leaflet</a>');
    // The wheel zooms after the pointer rests on the map or a click (wheel-gate.js).
    // Always armed in fullscreen, as the 2D LAN map.
    if (window.WheelGate) window.WheelGate.leaflet(map, { exempt: () => host.classList.contains(FULLSCREEN) });

    const state = { host, map, layers: L.layerGroup().addTo(map), tips: [], options };
    state.fsButton = addControls(state);
    state.resize = new ResizeObserver(() => map.invalidateSize());
    state.resize.observe(host);
    // Esc is a key press, so the fullscreen button the click left focused would show its focus ring: drop the focus.
    state.onKey = e => {
        if (e.key !== 'Escape' || !host.classList.contains(FULLSCREEN)) return;
        toggleFullscreen(state);
        if (document.activeElement?.tagName === 'BUTTON') document.activeElement.blur();
    };
    document.addEventListener('keydown', state.onKey);
    map.on('move zoom', () => followTips(state));
    return state;
}

// The 2D LAN map's controls and classes (lan-flow-map-2d.js): fullscreen top right, then zoom in,
// zoom out, and fit in a column below it. Fit carries no tooltip. Returns the fullscreen button.
function addControls(state) {
    const { host, map, options } = state;
    const fsButton = el('button', 'lan-flow-map-fullscreen-btn');
    fsButton.type = 'button';
    fsButton.setAttribute('data-tooltip', 'Fullscreen');
    fsButton.setAttribute('data-tooltip-hover-only', '');
    fsButton.setAttribute('aria-label', 'Fullscreen');
    fsButton.innerHTML = FS_ENTER_ICON;
    fsButton.addEventListener('click', () => toggleFullscreen(state));

    const bar = el('div', 'lfm2d-toolbar');
    const button = (label, html, onClick) => {
        const b = el('button', 'lfm2d-btn');
        b.type = 'button';
        b.setAttribute('aria-label', label);
        b.innerHTML = html;
        b.addEventListener('click', () => { onClick(); b.blur(); });
        bar.appendChild(b);
    };
    button('Zoom in', '+', () => map.zoomIn());
    button('Zoom out', '&minus;', () => map.zoomOut());
    button(options.fitLabel || 'Fit the route', FIT_ICON, () => options.onFit());

    for (const node of [fsButton, bar]) {
        window.L.DomEvent.disableClickPropagation(node);
        window.L.DomEvent.disableScrollPropagation(node);
        host.appendChild(node);
    }
    return fsButton;
}

/** Whether the map's host is in fullscreen. */
export function isFullscreen(state) {
    return !!state && state.host.classList.contains(FULLSCREEN);
}

export function toggleFullscreen(state) {
    if (!state) return;
    const on = state.host.classList.toggle(FULLSCREEN);
    state.fsButton.innerHTML = on ? FS_EXIT_ICON : FS_ENTER_ICON;
    state.fsButton.setAttribute('data-tooltip', on ? 'Exit fullscreen (Esc)' : 'Fullscreen');
    state.fsButton.setAttribute('aria-label', on ? 'Exit fullscreen' : 'Fullscreen');
    requestAnimationFrame(() => requestAnimationFrame(() => {
        state.map.invalidateSize();
        if (state.options.onFullscreen) state.options.onFullscreen(on);
        state.options.onFit();
    }));
}

/**
 * Fits the points, a marker's width from the edge, never closer than a metro.
 * @param {Array<[number, number]>} points
 * @param {number} [extraEast] - room on the right for a label beside the easternmost point
 * @param {number} [extraWest] - room on the left for a panel over the map
 * @param {number} [extraSouth] - room at the bottom for a panel over the map
 */
export function fitPoints(state, points, extraEast = 0, extraWest = 0, extraSouth = 0) {
    if (!state || !points || points.length === 0) return;
    // Places are city centers, so the closest fit is a metro across the map (11), never streets.
    if (points.length === 1) {
        state.map.setView(points[0], 10);
        return;
    }
    const pad = 28;
    state.map.fitBounds(points, {
        paddingTopLeft: [pad + extraWest, pad],
        paddingBottomRight: [pad + extraEast, pad + extraSouth],
        maxZoom: 11,
    });
}

/**
 * Each point's longitude, run on from the point before it along the legs, so a route that crosses
 * the antimeridian draws on one copy of the world.
 * @param {Array<{latitude: number, longitude: number}>} nodes
 * @param {Array<{from: number, to: number}>} legs - in drawing order, from the root out
 * @param {Iterable<number>} roots - nodes that keep their own longitude
 */
export function unwrapLongitudes(nodes, legs, roots = [0]) {
    const position = nodes.map(n => ({ lat: n.latitude, lng: n.longitude }));
    const placed = new Set(roots);
    for (const leg of legs) {
        if (placed.has(leg.to) || !placed.has(leg.from)) continue;
        let lng = position[leg.to].lng;
        while (lng - position[leg.from].lng > 180) lng -= 360;
        while (lng - position[leg.from].lng < -180) lng += 360;
        position[leg.to].lng = lng;
        placed.add(leg.to);
    }
    return position;
}

/**
 * A page tooltip (Tippy) on a marker, drawn on the page and flipped to fit: a Leaflet tooltip lives
 * inside the map and is cut off at its edge. Falls back to Leaflet's own without Tippy.
 * @param {object} [extra] - Tippy options over the defaults
 */
export function markerTip(state, marker, content, extra) {
    const icon = marker.getElement();
    if (window.tippy && icon) {
        const tip = window.tippy(icon, {
            content,
            placement: 'top',
            theme: 'custom',
            appendTo: document.body,
            trigger: 'mouseenter',
            // A tap shows it too: touch has no hover, and with touch off Tippy refuses even the
            // marker click's show().
            touch: true,
            maxWidth: 'min(100vw - 10px, 420px)',
            ...(extra || {}),
        });
        state.tips.push(tip);
        return tip;
    }
    marker.bindTooltip(content, { direction: 'top', offset: [0, -14], className: 'trace-map-tooltip', opacity: 1 });
    return null;
}

/** Destroys every page tooltip the map made. */
export function clearTips(state) {
    (state.tips || []).forEach(t => t.destroy());
    state.tips = [];
}

// An open point tooltip sits on the page, not the map: it follows its point as the map moves.
function followTips(state) {
    (state.tips || []).forEach(t => { if (t.state.isVisible && t.popperInstance) t.popperInstance.update(); });
}

/** Removes the map and everything it hung on the page. */
export function destroyMap(state) {
    if (!state) return;
    clearTips(state);
    state.resize.disconnect();
    document.removeEventListener('keydown', state.onKey);
    state.host.classList.remove(FULLSCREEN);
    state.map.remove();
}
