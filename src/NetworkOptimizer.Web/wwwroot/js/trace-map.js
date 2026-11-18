// The Network Tools trace map: a traced path drawn as places and straight legs on a Leaflet map.
// Hostnames come from PTR records, which anyone can set, so every label is written as text, never HTML.

import {
    loadLeaflet, el, createMap, fitPoints, unwrapLongitudes, markerTip, clearTips, destroyMap, SOURCE_COLOR, SOURCE_TEXT, legText,
} from './map-core.js?v=4';

const LEG_COLOR = '#2ba89a';
const LEG_UNSURE = '#9ca3af';

const hosts = new Set();

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

function legTip(from, to, km, silentHops) {
    const tip = el('div', 'trace-tip trace-tip-leg', legText(from.city, to.city, km));
    if (silentHops > 0) {
        tip.appendChild(el('div', 'trace-tip-source',
            `${silentHops} ${silentHops === 1 ? 'hop' : 'hops'} did not answer.`));
    }
    return tip;
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
    if (!state) state = host._traceMap = createMap(host, { onFit: () => fit(host._traceMap) });
    const { map, layers } = state;
    clearTips(state);
    layers.clearLayers();

    const position = unwrapLongitudes(path.nodes, path.legs);

    for (const leg of path.legs) {
        const from = path.nodes[leg.from], to = path.nodes[leg.to];
        // A straight line on the map: a great-circle arc bends over the pole and reads as a route
        // the path does not take.
        const points = [[position[leg.from].lat, position[leg.from].lng], [position[leg.to].lat, position[leg.to].lng]];
        const unsure = from.source === 'geolite2' || to.source === 'geolite2';
        const color = unsure ? LEG_UNSURE : LEG_COLOR;
        L.polyline(points, { color, weight: 9, opacity: 0.14, interactive: false, className: 'trace-leg-glow' }).addTo(layers);
        // Dotted: hops between the two places answered nothing, so the way between them is not known.
        const silent = leg.silentHops > 0;
        L.polyline(points, {
            color, weight: 2.5, opacity: 0.95, interactive: false,
            className: silent ? 'trace-leg trace-leg-silent' : unsure ? 'trace-leg trace-leg-unsure' : 'trace-leg',
        }).addTo(layers);
        // An invisible band carries the hover: a 2.5 px line, and a dotted one most of all, is hard to land on.
        L.polyline(points, { color: '#000', weight: 16, opacity: 0 }).addTo(layers)
            .bindTooltip(legTip(from, to, leg.kilometers, leg.silentHops), { sticky: true, className: 'trace-map-tooltip', opacity: 1 });
        // The flowing dashes would hide the dots.
        if (!silent) L.polyline(points, { color: '#ffffff', weight: 2.5, opacity: 0.9, interactive: false, className: 'trace-leg-flow' }).addTo(layers);
    }

    const last = path.nodes.length - 1;
    const bounds = [];
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
        const hops = node.hops.map(h => h.number).filter(n => n > 0);
        const entry = { marker, hops, node, tip: null };
        marker.on('click', () => {
            setActive(state, i);
            if (entry.tip) entry.tip.show();
            if (state.dotNet) state.dotNet.invokeMethodAsync('OnPlaceClicked', hops);
        });
        state.markers.push(entry);
    });

    state.bounds = bounds;
    map.invalidateSize();
    fit(state);

    // After the fit: a marker added before the map has a view gets its element only then.
    for (const entry of state.markers) entry.tip = markerTip(state, entry.marker, nodeTip(entry.node));
}

function fit(state) {
    if (!state || !state.bounds || state.bounds.length === 0) return;
    // Only the start and the end carry a label, to the right of the point: when one of them is the
    // easternmost point, its label gets room.
    const east = Math.max(...state.bounds.map(b => b[1]));
    const last = state.bounds.length - 1;
    const labelEast = state.bounds[0][1] === east || state.bounds[last][1] === east;
    fitPoints(state, state.bounds, labelEast ? 96 : 0);
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
    state.markers.forEach(m => (m.tip ? m.tip.hide() : m.marker.closeTooltip()));
    if (index < 0) return;
    const { marker, tip } = state.markers[index];
    if (!state.map.getBounds().pad(-0.1).contains(marker.getLatLng())) state.map.panTo(marker.getLatLng());
    if (tip) tip.show(); else marker.openTooltip();
}

/** Removes the map from the element. */
export function dispose(host) {
    const state = host && host._traceMap;
    hosts.delete(host);
    if (!state) return;
    destroyMap(state);
    host._traceMap = null;
}
