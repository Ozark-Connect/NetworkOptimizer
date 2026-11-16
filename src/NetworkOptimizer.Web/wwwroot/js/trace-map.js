// The Network Tools trace map: a traced path drawn as places and straight legs on a Leaflet map.
// Hostnames come from PTR records, which anyone can set, so every label is written as text, never HTML.

const LEAFLET_JS = 'https://unpkg.com/leaflet@1.9.4/dist/leaflet.js';
const LEAFLET_CSS = 'https://unpkg.com/leaflet@1.9.4/dist/leaflet.css';

const SOURCE_COLOR = { start: '#ededef', hostname: '#2ba89a', neighbor: '#3b82f6', geolite2: '#f59e0b' };
const SOURCE_TEXT = {
    start: 'Where the trace started',
    hostname: "From the router's hostname",
    neighbor: 'From a hop a few ms away',
    geolite2: 'Where GeoLite2 places the address',
};
const LEG_COLOR = '#2ba89a';
const LEG_UNSURE = '#9ca3af';

let leafletLoading = null;
const hosts = new Set();

function loadLeaflet() {
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

function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
}

function nodeIcon(L, node, labeled) {
    const wrap = el('div', `trace-node trace-node-${node.source}`);
    wrap.style.setProperty('--trace-node-color', SOURCE_COLOR[node.source] || SOURCE_COLOR.geolite2);
    const first = node.hops[0];
    wrap.appendChild(el('span', 'trace-node-dot', node.source === 'start' ? '' : String(first.number)));
    if (labeled) wrap.appendChild(el('span', 'trace-node-label', node.city));
    return L.divIcon({ html: wrap, className: 'trace-node-icon', iconSize: [26, 26], iconAnchor: [13, 13] });
}

function nodeTip(node) {
    const tip = el('div', 'trace-tip');
    tip.appendChild(el('div', 'trace-tip-city', node.city));
    tip.appendChild(el('div', 'trace-tip-source', SOURCE_TEXT[node.source] || ''));
    for (const hop of node.hops) {
        const row = el('div', 'trace-tip-hop');
        row.appendChild(el('span', 'trace-tip-num', hop.number > 0 ? String(hop.number) : ''));
        row.appendChild(el('span', 'trace-tip-name', hop.label));
        row.appendChild(el('span', 'trace-tip-rtt', hop.rttMs != null ? `${hop.rttMs.toFixed(1)} ms` : ''));
        tip.appendChild(row);
    }
    return tip;
}

function legTip(from, to, km) {
    const distance = km >= 100 ? Math.round(km / 10) * 10 : Math.round(km);
    return el('div', 'trace-tip trace-tip-leg', `${from.city} to ${to.city} · ${distance.toLocaleString()} km`);
}

/**
 * Draws the path into the element, replacing whatever it drew before.
 * @param {HTMLElement} host - the map's container
 * @param {{nodes: Array, legs: Array}} path - TraceMapPath, as serialized
 * @param {object} [dotNet] - told the hop numbers of a place the reader clicks (OnPlaceClicked)
 */
export async function render(host, path, dotNet) {
    if (!host || !path || !path.nodes) return;
    await loadLeaflet();
    const L = window.L;

    // A host Blazor removed from the page never gets a dispose call, so sweep those here.
    for (const old of hosts) {
        if (!old.isConnected) dispose(old);
    }
    hosts.add(host);

    let state = host._traceMap;
    if (!state) {
        const map = L.map(host, {
            scrollWheelZoom: false,
            // Quarter steps, so a route fits closely instead of dropping a whole level; the tiles
            // overlap by a pixel (app.css) so a fractional zoom shows no seams.
            zoomSnap: 0.25,
            minZoom: 2,
            worldCopyJump: false,
            attributionControl: true,
        });
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 12,
            attribution: '© OpenStreetMap',
        }).addTo(map);
        map.attributionControl.setPrefix('<a href="https://leafletjs.com" target="_blank" rel="noopener">Leaflet</a>');
        // The wheel zooms after the pointer rests on the map or a click (wheel-gate.js).
        const wheelGate = window.WheelGate ? window.WheelGate.leaflet(map) : null;
        const fitControl = L.control({ position: 'topleft' });
        fitControl.onAdd = () => {
            const bar = el('div', 'leaflet-bar trace-map-fit');
            const button = el('a');
            button.href = '#';
            button.setAttribute('role', 'button');
            button.setAttribute('aria-label', 'Fit the route');
            button.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M15 3h6v6"/><path d="M9 21H3v-6"/><path d="M21 3l-7 7"/><path d="M3 21l7-7"/></svg>';
            L.DomEvent.disableClickPropagation(bar);
            L.DomEvent.on(button, 'click', e => {
                L.DomEvent.preventDefault(e);
                fit(host._traceMap);
                button.blur();
            });
            bar.appendChild(button);
            return bar;
        };
        fitControl.addTo(map);
        const layers = L.layerGroup().addTo(map);
        const resize = new ResizeObserver(() => map.invalidateSize());
        resize.observe(host);
        state = host._traceMap = { map, layers, resize };
    }
    const { map, layers } = state;
    layers.clearLayers();

    // Each node's longitude runs on from the node before it on the path, so the whole path draws
    // on one copy of the world.
    const position = path.nodes.map(n => ({ lat: n.latitude, lng: n.longitude }));
    const placed = new Set([0]);
    for (const leg of path.legs) {
        if (placed.has(leg.to)) continue;
        let lng = position[leg.to].lng;
        while (lng - position[leg.from].lng > 180) lng -= 360;
        while (lng - position[leg.from].lng < -180) lng += 360;
        position[leg.to].lng = lng;
        placed.add(leg.to);
    }

    const bounds = [];
    for (const leg of path.legs) {
        const from = path.nodes[leg.from], to = path.nodes[leg.to];
        // A straight line on the map: a great-circle arc bends over the pole and reads as a route
        // the path does not take.
        const points = [[position[leg.from].lat, position[leg.from].lng], [position[leg.to].lat, position[leg.to].lng]];
        const unsure = from.source === 'geolite2' || to.source === 'geolite2';
        const color = unsure ? LEG_UNSURE : LEG_COLOR;
        L.polyline(points, { color, weight: 9, opacity: 0.14, interactive: false, className: 'trace-leg-glow' }).addTo(layers);
        const line = L.polyline(points, {
            color, weight: 2.5, opacity: 0.95, className: unsure ? 'trace-leg trace-leg-unsure' : 'trace-leg',
        }).addTo(layers);
        line.bindTooltip(legTip(from, to, leg.kilometers), { sticky: true, className: 'trace-map-tooltip', opacity: 1 });
        L.polyline(points, { color: '#ffffff', weight: 2.5, opacity: 0.9, interactive: false, className: 'trace-leg-flow' }).addTo(layers);
    }

    const last = path.nodes.length - 1;
    state.markers = [];
    state.dotNet = dotNet || null;
    path.nodes.forEach((node, i) => {
        const at = [position[i].lat, position[i].lng];
        bounds.push(at);
        const marker = L.marker(at, {
            icon: nodeIcon(L, node, i === 0 || i === last),
            keyboard: false,
            zIndexOffset: i === 0 || i === last ? 500 : 0,
        }).addTo(layers);
        marker.bindTooltip(nodeTip(node), { direction: 'top', offset: [0, -14], className: 'trace-map-tooltip', opacity: 1 });
        const hops = node.hops.map(h => h.number).filter(n => n > 0);
        marker.on('click', () => {
            setActive(state, i);
            if (state.dotNet) state.dotNet.invokeMethodAsync('OnPlaceClicked', hops);
        });
        state.markers.push({ marker, hops });
    });

    state.bounds = bounds;
    map.invalidateSize();
    fit(state);
}

// Brings the whole route back into view.
function fit(state) {
    if (!state || !state.bounds || state.bounds.length === 0) return;
    if (state.bounds.length === 1) {
        state.map.setView(state.bounds[0], 6);
        return;
    }
    // The route fills the map, a marker's width from the edge. Only the start and the end carry a
    // label, to the right of the point: when one of them is the easternmost point, its label gets room.
    const pad = 28;
    const east = Math.max(...state.bounds.map(b => b[1]));
    const last = state.bounds.length - 1;
    const labelEast = state.bounds[0][1] === east || state.bounds[last][1] === east;
    state.map.fitBounds(state.bounds, {
        paddingTopLeft: [pad, pad],
        paddingBottomRight: [labelEast ? pad + 96 : pad, pad],
        maxZoom: 8,
    });
}

function setActive(state, index) {
    state.markers.forEach((m, i) => {
        const icon = m.marker.getElement();
        if (icon) icon.classList.toggle('trace-node-active', i === index);
        m.marker.setZIndexOffset(i === index ? 1000 : 0);
    });
}

/**
 * Marks the place a hop sits at, brings it into view, and shows its hops. A hop with no place
 * on the map clears the mark.
 */
export function highlight(host, hopNumber) {
    const state = host && host._traceMap;
    if (!state || !state.markers) return;
    const index = state.markers.findIndex(m => m.hops.includes(hopNumber));
    setActive(state, index);
    state.markers.forEach(m => m.marker.closeTooltip());
    if (index < 0) return;
    const marker = state.markers[index].marker;
    if (!state.map.getBounds().pad(-0.1).contains(marker.getLatLng())) state.map.panTo(marker.getLatLng());
    marker.openTooltip();
}

/** Removes the map from the element. */
export function dispose(host) {
    const state = host && host._traceMap;
    hosts.delete(host);
    if (!state) return;
    state.resize.disconnect();
    state.map.remove();
    host._traceMap = null;
}
