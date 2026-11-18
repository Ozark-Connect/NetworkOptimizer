// The Path Map: every routed latency target's route, merged into one tree from the site, drawn above
// the Latency & Packet Loss charts and following their category, window, and WAN scope
// (latency-charts.js 'latency-view'). Hostnames come from PTR records, which anyone can set, so every
// label is written as text, never HTML.

import {
    loadLeaflet, el, createMap, fitPoints, unwrapLongitudes, clearTips, destroyMap, isFullscreen, toggleFullscreen, SOURCE_COLOR,
    SOURCE_TEXT, legText,
} from './map-core.js?v=4';

const HEALTH = ['unknown', 'normal', 'elevated', 'degraded', 'unreachable'];
const HEALTH_TEXT = { unknown: 'No baseline yet', normal: 'Normal', elevated: 'Elevated', degraded: 'Degraded', unreachable: 'Unreachable' };
const CATEGORY_TEXT = { AccessIsp: 'ISP', Transit: 'Transit', InternetService: 'Internet', Custom: 'Custom' };
const ROUTERS = new Set(['AccessIsp', 'Transit']);
// The fullscreen Targets list's groups, in the charts' category order.
const CATEGORY_GROUPS = [['AccessIsp', 'ISP'], ['Transit', 'Transit'], ['InternetService', 'Internet'], ['Custom', 'Custom']];
const PHONE = '(max-width: 768px)';
// The app's rest delay for packed hover targets (App.razor LT_REST_TIP_DELAY).
const REST_MS = 500;
const POLL_OPEN_MS = 15000;
const POLL_TRACING_MS = 5000;
const POLL_CLOSED_MS = 30000;
// A target that went bad or recovered keeps a fading ring this long.
const CHANGE_RING_MS = 10 * 60000;
// Graph palette (CLAUDE.md): legs are SVG presentation attributes, where var() does not resolve.
const LEG_COLOR = '#2ba89a';
const LEG_UNSURE = '#9ca3af';
// A target this far from the rest, past this many times the spread of the rest, is an edge chip. Past a
// coast-to-coast country (4,500 km): a site near one coast fits the other, another continent is a chip.
const FAR_MIN_KM = 5000;
const FAR_SPREAD = 3;
// Local's reach from the site's start: a metro and its region, short of the next state's backbone cities.
const LOCAL_KM = 300;
// The map's views. Each brings part of the map forward and fits it; none removes anything.
const PRESET_ICON = {
    all: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9"/><ellipse cx="12" cy="12" rx="4" ry="9"/><path d="M3 12h18"/></svg>',
    transit: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="18" r="2.2"/><circle cx="12" cy="6" r="2.2"/><circle cx="19" cy="15" r="2.2"/><path d="M6.2 16.2 10.8 7.9M13.5 7.6l4.3 5.6"/></svg>',
    local: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 11.5 12 5l8 6.5"/><path d="M6.5 10v9h11v-9"/><path d="M10.5 19v-5h3v5"/></svg>',
};
const PRESETS = [
    { key: 'all', label: 'All', tip: 'Every route' },
    { key: 'transit', label: 'Transit', tip: 'The networks between you and each Target' },
    { key: 'local', label: 'Local', tip: 'Your ISP and where they hand off' },
];

let current = null;

/**
 * Starts the map in its card.
 * @param {HTMLElement} card - the Path Map chart card
 * @param {object} dotNet - told when the map has no routes to draw (OnNoRouteChanged)
 * @param {boolean} expanded - whether the card opens expanded
 * @param {string|null} preset - the reader's saved view: 'all', 'transit', or 'local'
 */
export async function mount(card, dotNet, expanded, preset) {
    unmount();
    const charts = card.closest('#latency-charts-container');
    const body = card.querySelector('.path-map-body');
    if (!charts || !body) return;
    // Built here rather than by Blazor: the list moves between the map and the body, and Blazor
    // must never track a node something else moves.
    body.textContent = '';
    const host = el('div', 'trace-map path-map path-map-canvas');
    const problems = el('div', 'path-map-problems is-empty');
    // A click in the list never reaches the map. Leaflet finds the list's click guard by walking up from the clicked
    // element, and a row click re-renders the list, so the clicked row is gone by then: the map took it for a click on
    // empty space and dropped the pick the row had just made.
    problems.addEventListener('click', e => e.stopPropagation());
    const legend = el('div', 'trace-map-legend path-map-legend');
    const note = el('div', 'trace-map-note');
    body.append(host, problems, legend, note);
    const state = current = {
        card, charts, dotNet, expanded, host, problems, legend, note,
        chips: card.querySelector('.path-map-chips'),
        view: null, data: null, core: null, layers: null,
        selected: null, hovered: null, fitAll: false, fanned: null,
        // Unknown until the first view: the page keeps its own flag across a remount (leaving the tab on LAN and
        // coming back on a WAN), so the first answer is always reported, changed or not.
        visible: true, lastFetch: 0, fetching: null, noRoute: null,
        preset: PRESETS.some(p => p.key === preset) ? preset : 'all', focus: null,
    };

    state.onView = e => {
        // A new WAN selection is a new tree to fit; a category or window change keeps the view.
        const scope = JSON.stringify(e.detail.wanScope ?? null);
        if (scope !== state.scopeKey) { state.scopeKey = scope; state.fitDone = false; state.userMoved = false; }
        state.view = e.detail;
        refresh(state, true);
    };
    state.onHover = e => { if (!pinned(state)) highlight(state, e.detail.targetIds); };
    charts.addEventListener('latency-view', state.onView);
    charts.addEventListener('latency-target-hover', state.onHover);
    state.onFilterCleared = () => { if (state.selected) select(state, null); };
    charts.addEventListener('latency-filter-cleared', state.onFilterCleared);
    state.observer = new IntersectionObserver(([entry]) => { state.visible = entry.isIntersecting; }, { threshold: 0 });
    state.observer.observe(card);
    state.media = window.matchMedia(PHONE);
    state.onMedia = () => placeProblems(state);
    state.media.addEventListener('change', state.onMedia);
    state.timer = setInterval(() => refresh(state, false), 5000);
    // The header is the toggle: Enter and Space press it, as a button's would.
    state.header = card.querySelector('.path-map-header');
    state.onHeaderKey = e => {
        if ((e.key === 'Enter' || e.key === ' ') && e.target === state.header) { e.preventDefault(); state.header.click(); }
    };
    state.header?.addEventListener('keydown', state.onHeaderKey);

    // The chart module may have mounted first: ask it for the view it is on.
    window.__latencyCharts?.currentMapView?.();
}

/** Marks a target's route, for a link that solos it in the charts (they solo it themselves). */
export function markTarget(targetId) {
    const state = current;
    if (!state || !targetId) return;
    if (state.change) selectChange(state, null);
    state.selected = targetId;
    highlight(state, [targetId]);
}

/** Opens or closes the map; the chips stay live either way. */
export function setExpanded(on) {
    const state = current;
    if (!state) return;
    state.expanded = on;
    if (!on) return;
    // A map already built: the same data redraws nothing, so only its size needs telling.
    if (state.core) setTimeout(() => {
        state.core?.map.invalidateSize();
        // The same data draws nothing new, so the pill's request to show the list is met here.
        if (state.revealChanges && !state.pendingDraw) { state.revealChanges = false; revealChanges(state); }
    }, 350);
    if (!state.core && state.data) drawWhenIdle(state);
    refresh(state, true);
}

export function unmount() {
    const state = current;
    if (!state) return;
    current = null;
    clearInterval(state.timer);
    clearTimeout(state.drawRetry);
    state.charts.removeEventListener('latency-view', state.onView);
    state.charts.removeEventListener('latency-filter-cleared', state.onFilterCleared);
    state.charts.removeEventListener('latency-target-hover', state.onHover);
    state.observer.disconnect();
    state.media.removeEventListener('change', state.onMedia);
    state.header?.removeEventListener('keydown', state.onHeaderKey);
    if (state.fetching) state.fetching.abort();
    closeSheet();
    if (state.core) destroyMap(state.core);
}

// ─── Data ───

function noRouteScope(view) {
    return !view || view.category === 'Fabric' || !!view.wanScope?.lan;
}

function query(state, trace) {
    const v = state.view;
    const p = new URLSearchParams();
    const scope = v.wanScope;
    if (!scope || scope.all) p.set('all', 'true');
    else p.set('wans', (scope.selected || []).join(','));
    if (scope?.primaryKey) p.set('primary', scope.primaryKey);
    if (v.from && v.to) { p.set('from', v.from); p.set('to', v.to); }
    else p.set('rangeHours', String(v.rangeHours ?? 1));
    if (trace) p.set('trace', 'true');
    return p.toString();
}

/** Fetches when due: at once on a view change, else every 15 s open and on screen, 30 s otherwise. */
async function refresh(state, now) {
    if (!state.view || current !== state) return;
    const noRoute = noRouteScope(state.view);
    if (noRoute !== state.noRoute) {
        state.noRoute = noRoute;
        state.card.classList.toggle('path-map-noroute', noRoute);
        state.dotNet?.invokeMethodAsync('OnNoRouteChanged', noRoute).catch(() => { });
    }
    if (noRoute) { renderChips(state); return; }

    const open = state.expanded && state.visible && document.visibilityState === 'visible';
    // While targets are still waiting for their first trace, each one lands within seconds: look often.
    const interval = open ? (state.data?.waitingTargets > 0 ? POLL_TRACING_MS : POLL_OPEN_MS) : POLL_CLOSED_MS;
    if (!now && (!state.view.live || Date.now() - state.lastFetch < interval)) return;
    if (state.fetching) state.fetching.abort();
    const controller = state.fetching = new AbortController();
    state.lastFetch = Date.now();
    try {
        const resp = await fetch(`/api/monitoring/path-map?${query(state, open && state.view.live)}`,
            { credentials: 'same-origin', signal: controller.signal });
        if (!resp.ok) return;
        const text = await resp.text();
        if (current !== state || controller.signal.aborted) return;
        const changed = text !== state.lastText || state.drawnCategory !== state.view.category;
        state.lastText = text;
        state.data = JSON.parse(text);
        renderChips(state);
        if (state.expanded && (changed || !state.core || state.pendingDraw)) await drawWhenIdle(state);
    } catch (e) {
        if (e.name !== 'AbortError') console.debug('Path Map fetch failed', e);
    } finally {
        if (state.fetching === controller) state.fetching = null;
    }
}

function health(t) { return HEALTH[t.health] || 'unknown'; }

// A selected target or route change holds the highlight against hovers.
function pinned(state) { return !!(state.selected || state.change); }

// ─── Header chips ───

function renderChips(state) {
    const chips = state.chips;
    if (!chips) return;
    chips.textContent = '';
    if (state.noRoute) {
        chips.appendChild(el('span', 'status-badge status-neutral', state.view?.category === 'Fabric' && !state.view?.wanScope?.lan
            ? 'Fabric targets have no route' : 'LAN targets have no route'));
        return;
    }
    const data = state.data;
    if (!data) return;
    const counts = { unreachable: 0, degraded: 0, elevated: 0 };
    for (const id of data.problems) {
        const t = data.targets.find(x => x.targetId === id);
        if (t) counts[health(t)]++;
    }
    const add = (text, cls) => chips.appendChild(el('span', `status-badge ${cls}`, text));
    if (counts.unreachable) add(`${counts.unreachable} unreachable`, 'status-error');
    if (counts.degraded) add(`${counts.degraded} degraded`, 'status-error');
    if (counts.elevated) add(`${counts.elevated} elevated`, 'status-warning');
    const changes = routeChanges(data).length;
    if (changes) {
        // Shorthand for the header; the list and the cards spell it out.
        const pill = add(`${changes} route Δ`, 'status-info pm-change-pill');
        pill.setAttribute('role', 'button');
        pill.setAttribute('tabindex', '0');
        pill.setAttribute('aria-label', `${changes} route ${changes === 1 ? 'change' : 'changes'}`);
        pill.setAttribute('data-tooltip', 'Route changes in this window');
        const reveal = e => {
            // Collapsed: the header's own click opens the card, and the list shows once it draws.
            if (!state.expanded) { state.revealChanges = true; return; }
            e.stopPropagation();
            e.preventDefault();
            revealChanges(state);
        };
        pill.addEventListener('click', reveal);
        pill.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') reveal(e); });
    }
    if (!data.problems.length && !changes && data.targets.length)
        add(`${data.targets.length} ${data.targets.length === 1 ? 'target' : 'targets'}, all normal`, 'status-neutral');
    if (!data.live && data.asOf) add(`as of ${window.noTime ? window.noTime.timeLabel(data.asOf, false) : new Date(data.asOf).toLocaleTimeString()}`, 'status-neutral');
}

// ─── Drawing ───

// A redraw rebuilds every marker, which would close a card under the pointer or a fanned place:
// while one is open, the redraw waits for the next poll.
async function drawWhenIdle(state) {
    const busy = sheet || (state.core?.tips || []).some(t => t.state.isVisible);
    if (busy) { state.pendingDraw = true; return; }
    state.pendingDraw = false;
    await draw(state);
}

async function draw(state) {
    const data = state.data;
    if (!data || !state.host) return;
    // No routed targets at all: nothing to frame. Targets still waiting for their first trace keep
    // the frame at full height with the progress in it, so the map draws in place as routes land.
    // Set before the height check below: a hidden frame measures 0 and would never draw.
    state.host.classList.toggle('path-map-empty', !data.targets.length);
    // Still opening (the expand animation): a map built and fitted at part of its height fits wrong.
    // Only while the card is open: a closed card measures 0 too, and draws when it opens.
    if (data.nodes.length && state.expanded && state.host.clientHeight < 200) {
        clearTimeout(state.drawRetry);
        state.drawRetry = setTimeout(() => { if (current === state) drawWhenIdle(state); }, 350);
        return;
    }
    renderLegend(state);
    renderNote(state);
    renderWaiting(state);
    if (!data.nodes.length) {
        renderProblems(state);
        return;
    }
    await loadLeaflet();
    if (current !== state) return;
    const L = window.L;
    if (!state.core) {
        state.core = createMap(state.host, {
            fitLabel: 'Fit the map',
            // The Fit button hands the view back: the map refits as places are added again.
            onFit: () => { state.userMoved = false; fit(state, false); },
            onFullscreen: () => { placeProblems(state); renderProblems(state); },
        });
        // A drag, wheel, pinch, or zoom button is the reader taking the view: no more refits until Fit.
        // A zoom the map starts itself (a fit) sets fitting around it.
        state.core.map.on('dragstart', () => { state.userMoved = true; });
        state.core.map.on('zoomstart', () => { if (!state.fitting) state.userMoved = true; });
        state.core.map.on('click', () => select(state, null));
        state.core.map.on('move zoom', () => placeEdgeChips(state));
        state.core.map.on('zoomstart', () => unfan(state));
        addPresets(state);
    }
    const { layers } = state.core;
    clearTips(state.core);
    layers.clearLayers();
    state.legLayers = [];
    state.targetMarkers = new Map();
    state.drawnCategory = state.view.category;

    const roots = data.nodes.filter(n => n.source === 'start').map(n => n.id);
    state.position = unwrapLongitudes(data.nodes, orderedLegs(data, roots), roots.length ? roots : [0]);
    const pos = i => [state.position[i].lat, state.position[i].lng];
    state.focus = focusOf(state);
    // Before any marker: a marker added before the map has a view gets its element only after it.
    state.core.map.invalidateSize();
    // Refit while the tree grows (routes landing on a first view), unless the reader has taken the view.
    const placed = data.nodes.filter(n => n.hops.length || n.source === 'start').length;
    if (!state.fitDone || (!state.userMoved && placed !== state.fittedPlaces)) {
        fit(state, state.fitAll && state.fitDone);
        state.fitDone = true;
        state.fittedPlaces = placed;
    }

    const category = state.view.category;
    const legTargets = new Map();
    for (const t of data.targets) {
        for (let i = 1; i < t.path.length; i++) {
            const key = `${t.path[i - 1]}>${t.path[i]}>${t.wanKey}`;
            if (!legTargets.has(key)) legTargets.set(key, []);
            legTargets.get(key).push(t);
        }
    }
    const wanOrder = (state.view.wanScope?.selected || []).map(k => k.toLowerCase());
    const many = new Set(data.legs.map(l => l.wanKey)).size > 1;

    // The selected route change's old routes: thin and solid, under everything (dashed already means "unsure").
    // Only on selection: every old route at once buries the map.
    state.oldRoutes = L.layerGroup().addTo(layers);

    for (const leg of data.legs) {
        // A stretch whose hops did not answer bends through the place other routes most likely took.
        const points = leg.via != null ? [pos(leg.from), pos(leg.via), pos(leg.to)] : [pos(leg.from), pos(leg.to)];
        const crossing = legTargets.get(`${leg.from}>${leg.to}>${leg.wanKey}`) || [];
        const strong = crossing.some(t => t.category === category);
        const wanIndex = many ? Math.max(0, wanOrder.indexOf(leg.wanKey)) : 0;
        const weight = [0, 2, 3.5, 5][leg.weight] || 2;
        // Dotted: hops between the two places answered nothing, so the way between them is not known.
        const outside = state.focus && !leg.located && !state.focus.legs.has(`${leg.from}>${leg.to}>${leg.wanKey}`);
        const cls = ['pm-leg', leg.silentHops > 0 ? 'pm-leg-silent' : leg.unsure ? 'trace-leg-unsure' : '', strong ? '' : 'pm-faint', outside ? 'pm-outside' : '', `pm-wan-${wanIndex % 3}`].filter(Boolean).join(' ');
        if (leg.halo) L.polyline(points, { color: LEG_COLOR, weight: weight + 12, opacity: 1, interactive: false, className: 'pm-halo' }).addTo(layers);
        // A line style per WAN: the first solid, the second a tube (a line with a dark core), the
        // third a wider tube. Dashes stay free to mean "placed by GeoLite2 only".
        if (wanIndex % 3 > 0) {
            L.polyline(points, { color: LEG_COLOR, weight: weight + (wanIndex % 3 === 1 ? 3 : 6), opacity: 1, interactive: false, className: `${cls} pm-casing` }).addTo(layers);
        }
        const line = L.polyline(points, { color: leg.unsure ? LEG_UNSURE : LEG_COLOR, weight, opacity: 1, interactive: false, className: `${cls}${leg.located ? ' pm-located' : ''}` }).addTo(layers);
        // An invisible band carries the hover: a thin line, and a dotted one most of all, is hard to land on.
        const hit = L.polyline(points, { color: '#000', weight: 16, opacity: 0, className: 'pm-leg-hit' }).addTo(layers);
        const a = data.nodes[leg.from], b = data.nodes[leg.to];
        attachCard(state, hit, () => legCard(state, leg, crossing, many), {
            atPointer: true, title: legText(a.city, b.city, leg.kilometers), ids: crossing.map(t => t.targetId),
        });
        state.legLayers.push({ line, from: leg.from, to: leg.to, wan: leg.wanKey });
    }

    // Places: the start in the Route Map's own marker, hops as small points (hollow when only GeoLite2 placed them).
    const labelAll = data.targets.length <= 3;
    const labeled = new Set();
    for (const node of data.nodes) {
        if (!node.hops.length && node.source !== 'start') continue;
        const isStart = node.source === 'start';
        // A preset names the places it brings forward, each city once.
        const named = !isStart && !!state.focus?.labels.has(node.id) && !labeled.has(node.city);
        if (named) labeled.add(node.city);
        const wrap = isStart ? el('div', 'trace-node trace-node-start') : el('div', `pm-place${node.source === 'geolite2' ? ' pm-unsure' : ''}${node.located ? ' pm-onset' : ''}`);
        if (isStart) {
            wrap.style.setProperty('--trace-node-color', SOURCE_COLOR.start);
            wrap.appendChild(el('span', 'trace-node-dot', ''));
        }
        if (isStart || labelAll || named) wrap.appendChild(el('span', 'trace-node-label', named ? placeLabel(state, node) : node.city));
        if (state.focus && !isStart && !state.focus.shown.has(node.id)) wrap.classList.add('pm-outside');
        const size = isStart ? 26 : 12;
        const marker = L.marker(pos(node.id), {
            icon: L.divIcon({ html: wrap, className: 'trace-node-icon', iconSize: [size, size], iconAnchor: [size / 2, size / 2] }),
            keyboard: false, zIndexOffset: isStart ? 300 : 0,
        }).addTo(layers);
        const through = data.targets.filter(t => t.endNode === node.id || t.path.includes(node.id));
        attachCard(state, marker, () => placeCard(state, node, through), { title: node.city, ids: through.map(t => t.targetId) });
    }

    drawTargets(state);
    drawChangeMarks(state);
    renderProblems(state);
    placeProblems(state);
    placeEdgeChips(state);
    state.hovered = undefined;
    if (state.change && !routeChanges(data).some(c => c.id === state.change)) state.change = null;
    if (state.change) selectChange(state, state.change);
    else if (state.selected) highlight(state, [state.selected]);
    if (state.revealChanges) { state.revealChanges = false; revealChanges(state); }
}

// ─── Route changes ───

function routeChanges(data) {
    return data?.routeChanges || [];
}

/** "Cox: via Pensacola, now via Kansas City". */
function changeText(c) {
    const places = list => list.join(' > ');
    const how = c.before.length && c.after.length ? `via ${places(c.before)}, now via ${places(c.after)}`
        : c.after.length ? `now via ${places(c.after)}` : `no longer via ${places(c.before)}`;
    return c.network ? `${c.network}: ${how}` : how.charAt(0).toUpperCase() + how.slice(1);
}

function rttChangeText(m) {
    if (m.rttBeforeMs == null || m.rttAfterMs == null) return '';
    const d = m.rttAfterMs - m.rttBeforeMs;
    return `${Math.round(m.rttBeforeMs)} → ${Math.round(m.rttAfterMs)} ms${Math.abs(d) >= 1 ? ` (${d > 0 ? '+' : ''}${Math.round(d)})` : ''}`;
}

/** A small Δ where each route change's old and new routes part; one mark per place, counting its changes. */
function drawChangeMarks(state) {
    const L = window.L;
    const at = new Map();
    for (const c of routeChanges(state.data)) {
        if (!c.diverge) continue;
        const key = c.diverge.join(',');
        if (!at.has(key)) at.set(key, []);
        at.get(key).push(c);
    }
    state.changeMarks = [];
    for (const changes of at.values()) {
        const wrap = el('div', 'pm-change-mark', changes.length > 1 ? `Δ${changes.length}` : 'Δ');
        wrap.setAttribute('aria-label', `${changes.length} route ${changes.length === 1 ? 'change' : 'changes'}`);
        const marker = L.marker(changes[0].diverge, {
            // Up and right of the place, clear of its own point and the targets drawn on it.
            icon: L.divIcon({ html: wrap, className: 'trace-node-icon', iconSize: null, iconAnchor: [-6, 22] }),
            keyboard: false, zIndexOffset: 500,
        }).addTo(state.core.layers);
        const title = changes.length > 1 ? `${changes.length} route changes` : 'Route change';
        const byId = new Map(state.data.targets.map(t => [t.targetId, t]));
        attachCard(state, marker, () => {
            const card = el('div', 'pm-card');
            card.appendChild(el('div', 'pm-card-head pm-card-name', title));
            for (const c of changes) {
                card.appendChild(el('div', 'pm-card-section', `${changeText(c)} · ${clock(c.at)}`));
                card.appendChild(targetRows(state, c.targets.map(m => byId.get(m.targetId)).filter(Boolean)));
            }
            card.appendChild(el('div', 'pm-card-meta', 'Click to show the old routes'));
            return card;
        }, {
            title,
            clickOnPhone: true,
            // A click selects the place's first change, then the next, round.
            onClick: () => {
                const i = changes.findIndex(c => c.id === state.change);
                selectChange(state, i >= 0 && i < changes.length - 1 ? changes[i + 1].id : i === changes.length - 1 ? null : changes[0].id);
            },
        });
        state.changeMarks.push({ marker, ids: changes.map(c => c.id) });
    }
}

/** Shows one route change: its targets' new routes lit, their old routes drawn, everything else faded. Null clears. */
function selectChange(state, id) {
    const change = routeChanges(state.data).find(c => c.id === id) || null;
    state.change = change ? change.id : null;
    state.selected = null;
    state.oldRoutes?.clearLayers();
    if (change && state.oldRoutes && window.L) {
        for (const m of change.targets) {
            if (m.oldPoints.length < 2) continue;
            window.L.polyline(m.oldPoints, { color: LEG_COLOR, weight: 2, opacity: 0.6, interactive: false, className: 'pm-shift-old' }).addTo(state.oldRoutes);
        }
    }
    for (const mark of state.changeMarks || []) {
        mark.marker.getElement()?.classList.toggle('is-active', !!change && mark.ids.includes(change.id));
    }
    state.problems?.querySelectorAll('[data-change-id]').forEach(row => {
        const on = !!change && row.dataset.changeId === change.id;
        row.classList.toggle('is-active', on);
        row.querySelector('.pm-change-head')?.setAttribute('aria-pressed', String(on));
    });
    highlight(state, change ? change.targets.map(m => m.targetId) : null, true);
}

/** Opens the card's Route changes list: in view, and the only change selected when there is one. */
function revealChanges(state) {
    const changes = routeChanges(state.data);
    if (changes.length === 1) selectChange(state, changes[0].id);
    const title = state.problems?.querySelector('.pm-changes-title');
    if (!title) return;
    // Inside the map the list scrolls on its own; under the map on a phone, the page does.
    if (state.problems.parentElement === state.host) state.problems.scrollTop = title.offsetTop - 6;
    else title.scrollIntoView({ behavior: 'smooth', block: 'center' });
}

// Legs in drawing order from the roots, so each point's longitude runs on from the one before it.
function orderedLegs(data, roots) {
    const placed = new Set(roots.length ? roots : [0]);
    const out = [];
    let left = data.legs.slice();
    while (left.length) {
        const next = left.filter(l => placed.has(l.from));
        if (!next.length) break;
        next.forEach(l => { out.push(l); placed.add(l.to); });
        left = left.filter(l => !next.includes(l));
    }
    return out.concat(left);
}

function wanToken(state, key) {
    return state.view.wanScope?.tokens?.[key] || String(key).toUpperCase();
}

function placeTip(node) {
    const tip = el('div', 'trace-tip');
    tip.appendChild(el('div', 'trace-tip-city', node.city));
    if (node.asnName || node.asn) tip.appendChild(el('div', 'trace-tip-source', node.asnName ? `${node.asnName} (AS${node.asn})` : `AS${node.asn}`));
    if (node.source === 'start') tip.appendChild(el('div', 'trace-tip-source', SOURCE_TEXT.start));
    for (const hop of node.hops) {
        const row = el('div', 'trace-tip-hop');
        row.appendChild(el('span', 'trace-tip-num', ''));
        row.appendChild(el('span', 'trace-tip-name', hop.hostname || hop.address));
        row.appendChild(el('span', 'trace-tip-rtt', hop.rttMs != null ? `${hop.rttMs.toFixed(1)} ms` : ''));
        tip.appendChild(row);
        tip.appendChild(el('div', 'pm-tip-placed', hop.placedBy));
        if (hop.seenAt) tip.appendChild(el('div', 'pm-tip-placed', `Did not answer this time; answered at ${new Date(hop.seenAt).toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })}.`));
    }
    if (node.absolved) tip.appendChild(el('div', 'pm-tip-note', 'Its own replies are slow or lossy, but the targets beyond it are clean: the router deprioritizes ICMP, the path is fine.'));
    if (node.lossPct != null && node.lossPct >= 3) tip.appendChild(el('div', 'pm-tip-note', `${node.lossPct.toFixed(0)}% of the traces' probes through here were lost, and the hops beyond lost as many.`));
    return tip;
}

// ─── Targets ───

function drawTargets(state) {
    const L = window.L;
    const data = state.data;
    const groups = new Map();
    for (const t of data.targets) {
        if (t.endNode == null) continue;
        if (!groups.has(t.endNode)) groups.set(t.endNode, []);
        groups.get(t.endNode).push(t);
    }
    state.groups = groups;
    state.clusterMarkers = [];
    for (const [node, targets] of groups) {
        if (targets.length === 1 || state.fanned === node) {
            targets.forEach((t, i) => addTargetMarker(state, t, node, targets.length > 1 ? fanOffset(i, targets.length) : [0, 0]));
            continue;
        }
        // One badge per place, colored by its worst member; a click fans the targets out.
        const worst = targets.reduce((w, t) => (t.health > w.health ? t : w), targets[0]);
        const strong = targets.some(t => t.category === state.view.category);
        const wrap = el('div', `pm-cluster pm-${health(worst)}${strong ? '' : ' pm-dim'}`);
        wrap.appendChild(el('span', 'pm-cluster-n', String(targets.length)));
        const marker = L.marker([state.position[node].lat, state.position[node].lng], {
            icon: L.divIcon({ html: wrap, className: 'trace-node-icon', iconSize: [24, 24], iconAnchor: [12, 12] }),
            keyboard: false, zIndexOffset: 600 + worst.health * 10,
        }).addTo(state.core.layers);
        const title = `${targets.length} targets in ${data.nodes[node].city}`;
        attachCard(state, marker, () => {
            const card = el('div', 'pm-card');
            card.appendChild(el('div', 'pm-card-head pm-card-name', title));
            card.appendChild(el('div', 'pm-card-meta', 'Click to spread them out'));
            card.appendChild(targetRows(state, targets));
            return card;
        }, { title, ids: targets.map(t => t.targetId), onClick: () => fan(state, node) });
        state.clusterMarkers.push(marker);
        targets.forEach(t => state.targetMarkers.set(t.targetId, { marker, cluster: true }));
    }
}

function fanOffset(i, n) {
    const radius = 26 + 3 * n;
    const angle = (2 * Math.PI * i) / n - Math.PI / 2;
    return [Math.round(Math.cos(angle) * radius), Math.round(Math.sin(angle) * radius)];
}

function fan(state, node) {
    state.fanned = node;
    draw(state);
}

function unfan(state) {
    if (state.fanned == null) return;
    state.fanned = null;
    draw(state);
}

function addTargetMarker(state, t, node, [dx, dy]) {
    const L = window.L;
    const h = health(t);
    const strong = t.category === state.view.category;
    const router = ROUTERS.has(t.category);
    const changed = t.changedAt ? Date.now() - new Date(t.changedAt).getTime() : Infinity;
    const wrap = el('div', `pm-target pm-${h}${router ? ' pm-router' : ''}${strong ? '' : ' pm-dim'}`);
    if (changed < CHANGE_RING_MS) {
        wrap.classList.add('pm-changed');
        wrap.style.setProperty('--pm-ring', String(1 - changed / CHANGE_RING_MS));
    }
    wrap.appendChild(el('span', 'pm-mark'));
    // Healthy targets stay quiet; a target off its baseline names how far.
    if (h === 'elevated' || h === 'degraded' || h === 'unreachable') wrap.appendChild(el('span', 'trace-node-label', labelText(t)));
    const size = 22;
    const marker = L.marker([state.position[node].lat, state.position[node].lng], {
        icon: L.divIcon({ html: wrap, className: 'trace-node-icon', iconSize: [size, size], iconAnchor: [size / 2 - dx, size / 2 - dy] }),
        keyboard: false, zIndexOffset: 700 + t.health * 10 + (strong ? 100 : 0),
    }).addTo(state.core.layers);
    marker.on('click', e => {
        L.DomEvent.stopPropagation(e);
        if (window.matchMedia(PHONE).matches) openSheet(state, t);
        else select(state, t);
    });
    const icon = marker.getElement();
    if (icon && window.tippy) {
        const tip = window.tippy(icon, {
            content: targetCard(state, t, false),
            placement: 'top',
            theme: 'custom',
            appendTo: document.body,
            interactive: true,
            delay: [REST_MS, 80],
            touch: false,
            maxWidth: 'min(100vw - 10px, 440px)',
            onShow: () => { if (!pinned(state)) highlight(state, [t.targetId]); },
            onHidden: () => { if (!pinned(state)) highlight(state, null); },
        });
        state.core.tips.push(tip);
    }
    state.targetMarkers.set(t.targetId, { marker, cluster: false });
}

function labelText(t) {
    const h = health(t);
    if (h === 'unreachable') return `${t.name} · unreachable`;
    if (t.riseMs != null && t.riseMs >= 1) return `${t.name} · +${Math.round(t.riseMs)} ms`;
    if (t.lossPct != null && t.lossPct >= 1) return `${t.name} · ${t.lossPct.toFixed(0)}% loss`;
    return t.name;
}

function stateText(t) {
    const h = health(t);
    if (h === 'normal' || h === 'unknown') return HEALTH_TEXT[h];
    if (h === 'unreachable') return 'Unreachable';
    if (t.riseMs != null && t.riseMs >= 1) return `+${Math.round(t.riseMs)} ms`;
    if (t.lossPct != null) return `${t.lossPct.toFixed(1)}% loss`;
    return HEALTH_TEXT[h];
}

function vantageText(v) {
    return !v || v === 'server' ? 'This server' : 'On-Site Agent';
}

/** The hover card (and the phone sheet's body). */
function targetCard(state, t, sheet) {
    const card = el('div', 'pm-card');
    const head = el('div', 'pm-card-head');
    head.appendChild(el('span', `pm-dot pm-${health(t)}`));
    head.appendChild(el('span', 'pm-card-name', t.name));
    card.appendChild(head);
    // A WAN is named only where there are several to tell apart.
    const wan = !state.view.wanScope ? null : t.unpinned ? 'Not pinned to a WAN' : t.wanLabel || wanToken(state, t.wanKey);
    const meta = [CATEGORY_TEXT[t.category] || t.category, vantageText(t.vantage), wan].filter(Boolean);
    card.appendChild(el('div', 'pm-card-meta', meta.join(' · ')));

    const h = health(t);
    if (t.absolved) {
        card.appendChild(el('div', 'pm-card-state', 'Normal: its own replies are slow, but every target beyond it is clean'));
    } else if (t.baselineRttMs != null && t.rttMs != null && (h === 'elevated' || h === 'degraded')) {
        const rise = t.rttMs - t.baselineRttMs;
        card.appendChild(el('div', 'pm-card-state', `${rise >= 0 ? '+' : ''}${Math.round(rise)} ms over usual (${Math.round(t.baselineRttMs)} to ${Math.round(t.rttMs)} ms)`));
    } else {
        // An unreachable target's last RTT is from before it went quiet: the state says it all.
        card.appendChild(el('div', 'pm-card-state', t.rttMs != null && h !== 'unreachable' ? `${HEALTH_TEXT[h]} · ${t.rttMs.toFixed(1)} ms` : HEALTH_TEXT[h]));
    }
    if (t.lossPct != null && t.lossPct > 0) card.appendChild(el('div', 'pm-card-line', `Loss ${t.lossPct.toFixed(1)}%`));
    const spark = sparkline(t.spark);
    if (spark) card.appendChild(spark);

    if (t.checkedAt) card.appendChild(el('div', 'pm-card-line', `Route checked ${ago(t.checkedAt)}`));
    else card.appendChild(el('div', 'pm-card-line', 'Not traced yet'));
    const change = t.routeChange && routeChanges(state.data).find(c => c.id === t.routeChange);
    if (change) {
        const m = change.targets.find(x => x.targetId === t.targetId);
        const rtt = m ? rttChangeText(m) : '';
        card.appendChild(el('div', 'pm-card-line pm-card-shift', `Route change at ${clock(change.at)}: ${changeText(change)}${rtt ? `. ${rtt}` : ''}`));
    }

    // What placed each place on its route.
    const places = t.path.map(i => state.data.nodes[i]).filter(n => n && n.source !== 'start');
    if (places.length) {
        const list = el('div', 'pm-card-places');
        // Each place: city and its lowest round trip, then its network. What placed it is said only when it is a
        // GeoLite2 guess, the one case the reader may act on; the place's own hover has the rest.
        for (const n of places) {
            const row = el('div', 'pm-card-place');
            row.appendChild(el('span', 'pm-card-city', n.city));
            const rtts = n.hops.map(h => h.rttMs).filter(v => v != null);
            row.appendChild(el('span', 'pm-card-rtt', rtts.length ? `${Math.min(...rtts).toFixed(1)} ms` : ''));
            const net = n.asnName && n.asn ? `${n.asnName} · AS${n.asn}` : n.asn ? `AS${n.asn}` : n.asnName || 'Network unknown';
            row.appendChild(el('span', 'pm-card-net', net));
            row.appendChild(el('span', 'pm-card-source', n.source === 'geolite2' ? 'Approximate (GeoLite2)' : ''));
            list.appendChild(row);
        }
        card.appendChild(list);
    }

    const actions = el('div', 'pm-card-actions');
    const show = el('button', 'btn btn-sm btn-primary', sheet ? 'Show in charts' : 'Open in the chart');
    show.type = 'button';
    show.addEventListener('click', () => {
        closeSheet();
        window.tippy?.hideAll?.();
        // The charts are behind a fullscreen map, so opening one leaves fullscreen.
        if (isFullscreen(state.core)) toggleFullscreen(state.core);
        select(state, t);
    });
    actions.appendChild(show);
    const trace = el('a', 'btn btn-sm btn-contrast', 'Trace Route');
    trace.href = traceUrl(t);
    // No tooltip of its own: a second tippy opening over the card closes the card.
    actions.appendChild(trace);
    card.appendChild(actions);
    return card;
}

function sparkline(points) {
    const values = (points || []).filter(v => v != null);
    if (values.length < 2) return null;
    const w = 220, h = 32, pad = 2;
    const min = Math.min(...values), max = Math.max(...values);
    const span = max - min || 1;
    const step = (w - pad * 2) / Math.max(1, points.length - 1);
    const coords = [];
    points.forEach((v, i) => { if (v != null) coords.push(`${(pad + i * step).toFixed(1)},${(h - pad - ((v - min) / span) * (h - pad * 2)).toFixed(1)}`); });
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('class', 'pm-spark');
    svg.setAttribute('viewBox', `0 0 ${w} ${h}`);
    svg.setAttribute('preserveAspectRatio', 'none');
    const line = document.createElementNS(ns, 'polyline');
    line.setAttribute('points', coords.join(' '));
    svg.appendChild(line);
    return svg;
}

function clock(iso) {
    return window.noTime ? window.noTime.timeLabel(iso, false) : new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function ago(iso) {
    const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
    if (s < 60) return 'just now';
    const m = Math.round(s / 60);
    if (m < 60) return `${m} min ago`;
    const hours = Math.round(m / 60);
    if (hours < 48) return `${hours} ${hours === 1 ? 'hour' : 'hours'} ago`;
    return `at ${clock(iso)}`;
}

// ─── Highlight and selection ───

/** Marks the targets' routes and fades the rest; null clears. Tells the charts to mark the same targets. */
function highlight(state, targetIds, force) {
    const key = targetIds && targetIds.length ? targetIds.join(',') : null;
    if (key === state.hovered && !state.selected && !force) return;
    state.hovered = key;
    window.__latencyCharts?.focusTargets?.(targetIds || null);
    if (!state.core || !state.data) return;
    const ids = targetIds ? new Set(targetIds) : null;
    const on = new Set();
    if (ids) {
        for (const t of state.data.targets) {
            if (!ids.has(t.targetId)) continue;
            for (let i = 1; i < t.path.length; i++) on.add(`${t.path[i - 1]}>${t.path[i]}>${t.wanKey}`);
        }
    }
    for (const leg of state.legLayers || []) {
        const el2 = leg.line.getElement();
        if (!el2) continue;
        const k = `${leg.from}>${leg.to}>${leg.wan}`;
        el2.classList.toggle('pm-lit', !!ids && on.has(k));
        el2.classList.toggle('pm-off', !!ids && !on.has(k));
    }
    for (const [id, m] of state.targetMarkers || []) {
        const icon = m.marker.getElement();
        if (!icon) continue;
        icon.classList.toggle('pm-lit', !!ids && ids.has(id));
        icon.classList.toggle('pm-off', !!ids && !ids.has(id) && !m.cluster);
    }
    state.problems?.querySelectorAll('[data-target-id]').forEach(row =>
        row.classList.toggle('is-active', !!ids && ids.has(row.dataset.targetId)));
}

/**
 * The click on a target (on the map or in the problems list): solos it in the charts below, through
 * its own category when it sits in another, marks its route, and brings Round-trip time into view
 * when it is not on screen.
 */
function select(state, t) {
    if (state.change) selectChange(state, null);
    state.selected = t ? t.targetId : null;
    highlight(state, t ? [t.targetId] : null);
    if (!t) { if (isFullscreen(state.core)) renderProblems(state); return; }
    window.__latencyCharts?.soloInCategory?.(t.category, t.targetId);
    if (isFullscreen(state.core)) { renderProblems(state); return; }
    const rtt = state.charts.querySelector('.latency-rtt-chart');
    if (!rtt) return;
    const box = rtt.getBoundingClientRect();
    if (box.top >= 0 && box.bottom <= window.innerHeight) return;
    // The chip row above the chart stays in view with it.
    const anchor = state.charts.querySelector('.latency-filter-badges') || rtt;
    const top = anchor.getBoundingClientRect().top + window.scrollY - 72;
    window.scrollTo({ top: Math.max(0, top), behavior: 'smooth' });
}

// ─── Problems list ───

function renderProblems(state) {
    const box = state.problems;
    if (!box || !state.data) return;
    box.textContent = '';
    const full = state.core && isFullscreen(state.core);
    const byId = new Map(state.data.targets.map(t => [t.targetId, t]));
    // In fullscreen it is the side panel: every target on the WANs drawn, worst first.
    const rows = full
        ? state.data.targets.slice().sort((a, b) => b.health - a.health || (b.riseMs ?? 0) - (a.riseMs ?? 0)
            || a.name.localeCompare(b.name, undefined, { numeric: true }))
        : state.data.problems.map(id => byId.get(id)).filter(Boolean);
    const changes = routeChanges(state.data);
    box.classList.toggle('is-empty', rows.length === 0 && changes.length === 0);
    box.classList.toggle('is-full', !!full);
    if (rows.length) {
        const title = el('div', 'pm-problems-title', full ? 'Targets' : 'Off baseline');
        // In fullscreen the map fills the screen, so Clear does what a click on the empty map does.
        if (full) {
            const clear = el('button', 'pm-problems-clear', 'Clear');
            clear.type = 'button';
            clear.addEventListener('click', () => select(state, null));
            title.appendChild(clear);
        }
        box.appendChild(title);
    }
    // In fullscreen every Target is listed, grouped by category under headings, the charts' own category marked.
    // No row is dimmed by category there: a whole category in white read as a selection of many Targets. Only the
    // Target picked is marked, as its route is on the map.
    const groups = full
        ? CATEGORY_GROUPS.map(([cat, label]) => ({ cat, label, rows: rows.filter(t => t.category === cat) }))
            .concat([{ cat: null, label: 'Other', rows: rows.filter(t => !CATEGORY_GROUPS.some(([c]) => c === t.category)) }])
            .filter(g => g.rows.length)
        : [{ cat: null, label: null, rows }];
    for (const g of groups) {
        if (g.label) {
            const current = g.cat === state.view.category;
            const head = el('div', `pm-group-title${current ? ' is-current' : ''}`, g.label);
            if (current) head.appendChild(el('span', 'pm-group-note', 'in the charts'));
            box.appendChild(head);
        }
        for (const t of g.rows) addProblemRow(state, box, t, full);
    }
    if (changes.length) renderChanges(state, box, changes, byId);
}

/** Fits the map to a Target's whole route, clear of the fullscreen list. The reader's view is theirs after it. */
function showRoute(state, t) {
    const points = t.path.map(i => state.position[i]).filter(Boolean).map(p => [p.lat, p.lng]);
    if (!points.length) return;
    const phone = window.matchMedia(PHONE).matches;
    state.userMoved = true;
    state.fitting = true;
    try { fitPoints(state.core, points, 0, phone ? 0 : 300, (phone ? Math.round(window.innerHeight * 0.4) : 0) + 56); }
    finally { state.fitting = false; }
    placeEdgeChips(state);
}

/** One row of the problems list or the fullscreen Targets list. */
function addProblemRow(state, box, t, full) {
    const dim = !full && t.category !== state.view.category;
    const row = el('button', `pm-problem${dim ? ' pm-dim' : ''}${full && t.targetId === state.selected ? ' is-selected' : ''}`);
    row.type = 'button';
    row.dataset.targetId = t.targetId;
    row.appendChild(el('span', `pm-dot pm-${health(t)}`));
    row.appendChild(el('span', 'pm-problem-name', t.name));
    row.appendChild(el('span', 'pm-problem-state', stateText(t)));
    row.addEventListener('click', () => {
        if (window.matchMedia(PHONE).matches && !full) openSheet(state, t);
        else {
            select(state, t);
            // A marker is clicked where it is seen; a row in the fullscreen list is not, so its route is brought into view.
            if (full && state.selected === t.targetId) showRoute(state, t);
        }
    });
    row.addEventListener('mouseenter', () => { if (!pinned(state)) highlight(state, [t.targetId]); });
    row.addEventListener('mouseleave', () => { if (!pinned(state)) highlight(state, null); });
    box.appendChild(row);
}

/** The Route changes section: one row per change, its targets and their round trips under it. */
function renderChanges(state, box, changes, byId) {
    box.appendChild(el('div', 'pm-problems-title pm-changes-title', 'Route changes'));
    for (const c of changes) {
        const item = el('div', `pm-change${c.id === state.change ? ' is-active' : ''}`);
        item.dataset.changeId = c.id;
        const head = el('button', 'pm-change-head');
        head.type = 'button';
        head.setAttribute('aria-pressed', String(c.id === state.change));
        head.appendChild(el('span', 'pm-change-glyph', 'Δ'));
        head.appendChild(el('span', 'pm-change-text', changeText(c)));
        head.appendChild(el('span', 'pm-problem-state', clock(c.at)));
        head.addEventListener('click', () => selectChange(state, state.change === c.id ? null : c.id));
        head.addEventListener('mouseenter', () => { if (!pinned(state)) highlight(state, c.targets.map(m => m.targetId)); });
        head.addEventListener('mouseleave', () => { if (!pinned(state)) highlight(state, null); });
        item.appendChild(head);
        for (const m of c.targets) {
            const t = byId.get(m.targetId);
            const row = el('button', 'pm-change-target');
            row.type = 'button';
            row.appendChild(el('span', 'pm-problem-name', m.name));
            row.appendChild(el('span', 'pm-problem-state', rttChangeText(m)));
            // A target opens its charts, as a row of the list above does.
            row.addEventListener('click', () => { if (t) select(state, t); });
            item.appendChild(row);
        }
        box.appendChild(item);
    }
}

// The list sits in a corner of the map, and in fullscreen beside it; on a phone, under the map.
function placeProblems(state) {
    const box = state.problems;
    if (!box || !state.host) return;
    const inside = !window.matchMedia(PHONE).matches || (state.core && isFullscreen(state.core));
    if (inside && box.parentElement !== state.host) {
        state.host.appendChild(box);
        if (window.L) { window.L.DomEvent.disableClickPropagation(box); window.L.DomEvent.disableScrollPropagation(box); }
    } else if (!inside && box.parentElement === state.host) {
        state.host.after(box);
    }
}

// ─── Views: All, Transit, Local ───

function offBaseline(t) {
    const h = health(t);
    return !t.absolved && (h === 'elevated' || h === 'degraded' || h === 'unreachable');
}

/**
 * What the view brings forward: legs, places to fit, and places to name. Transit: each route up to where it
 * enters its target's own network (an ISP or Transit target's route in full). Local: each route up to its
 * first place off the access network. A target off its baseline keeps its whole route at full strength, so a
 * problem past the cut still shows where it is, without pulling the fit out to it. Null for All.
 */
function focusOf(state) {
    const data = state.data;
    if (state.preset === 'all' || !data) return null;
    const local = state.preset === 'local';
    // fit: the places the view frames; shown: those drawn at full strength, a problem target's whole route too.
    const legs = new Set(), fit = new Set(), shown = new Set(), labels = new Set();
    for (const t of data.targets) {
        const p = t.path;
        if (p.length < 2) continue;
        const asn = i => data.nodes[p[i]].asn;
        const access = asn(1);
        let end = p.length - 1;
        if (local) {
            const out = p.findIndex((_, i) => i >= 1 && asn(i) != null && asn(i) !== access);
            if (out > 0) end = out;
            // A handoff can be states away: Local also stops at the first place past LOCAL_KM, keeping the first.
            const at = i => [data.nodes[p[i]].latitude, data.nodes[p[i]].longitude];
            const far = p.findIndex((_, i) => i >= 2 && km(at(0), at(i)) > LOCAL_KM);
            if (far > 0 && far - 1 < end) end = far - 1;
        } else if (!ROUTERS.has(t.category) && t.endNode != null) {
            const own = data.nodes[t.endNode].asn;
            const into = own == null || own === access ? -1 : p.findIndex((_, i) => i >= 1 && asn(i) === own);
            if (into > 0) end = into;
        }
        const whole = offBaseline(t);
        for (let i = 1; i < p.length; i++) {
            if (i <= end || whole) legs.add(`${p[i - 1]}>${p[i]}>${t.wanKey}`);
        }
        p.forEach((n, i) => {
            if (i > end && !whole) return;
            shown.add(n);
            if (i <= end) fit.add(n);
            // Transit names the networks past the access ISP; Local names every place up to the handoff.
            if (i >= 1 && i <= end && (local || asn(i) !== access)) labels.add(n);
        });
    }
    return fit.size > 1 ? { legs, fit, shown, labels } : null;
}

/** A named place: its city, and in Local its lowest round trip, as the Route Map gives each hop's. */
function placeLabel(state, node) {
    if (state.preset !== 'local') return node.city;
    const rtts = node.hops.map(h => h.rttMs).filter(v => v != null);
    return rtts.length ? `${node.city} · ${Math.min(...rtts).toFixed(0)} ms` : node.city;
}

/** The view switch, on the map along its bottom. */
function addPresets(state) {
    const bar = el('div', 'pm-presets');
    bar.setAttribute('role', 'group');
    bar.setAttribute('aria-label', 'Map view');
    // The highlight slides between views (placeThumb).
    bar.appendChild(el('span', 'pm-preset-thumb'));
    for (const p of PRESETS) {
        const b = el('button', `pm-preset${p.key === state.preset ? ' active' : ''}`);
        b.innerHTML = PRESET_ICON[p.key];
        b.appendChild(el('span', null, p.label));
        b.type = 'button';
        b.dataset.preset = p.key;
        b.setAttribute('aria-pressed', String(p.key === state.preset));
        b.setAttribute('data-tooltip', p.tip);
        b.setAttribute('data-tooltip-hover-only', '');
        b.addEventListener('click', () => { setPreset(state, p.key); b.blur(); });
        bar.appendChild(b);
    }
    window.L.DomEvent.disableClickPropagation(bar);
    window.L.DomEvent.disableScrollPropagation(bar);
    state.host.appendChild(bar);
    state.presetBar = bar;
    // Measured once the bar is laid out; no slide on the first placement.
    requestAnimationFrame(() => placeThumb(state, false));
}

function placeThumb(state, animate) {
    const bar = state.presetBar;
    const on = bar?.querySelector('.pm-preset.active');
    const thumb = bar?.querySelector('.pm-preset-thumb');
    if (!on || !thumb || !on.offsetWidth) return;
    thumb.classList.toggle('is-instant', !animate);
    thumb.style.width = `${on.offsetWidth}px`;
    thumb.style.transform = `translateX(${on.offsetLeft - 3}px)`;
}

function setPreset(state, key) {
    if (key === state.preset) return;
    state.preset = key;
    state.presetBar?.querySelectorAll('.pm-preset').forEach(b => {
        const on = b.dataset.preset === key;
        b.classList.toggle('active', on);
        b.setAttribute('aria-pressed', String(on));
    });
    placeThumb(state, true);
    // A new view is a new fit, whatever the reader had panned to.
    state.userMoved = false;
    state.fitDone = false;
    state.dotNet?.invokeMethodAsync('OnPathMapPresetChanged', key).catch(() => { });
    draw(state);
}

// ─── Fit and edge chips ───

function targetPoint(state, t) {
    const p = state.position?.[t.endNode];
    return p ? [p.lat, p.lng] : null;
}

/**
 * Fits the dense region: a target far from the rest (farther than FAR_MIN_KM and FAR_SPREAD times the
 * spread of the others) becomes an edge chip instead of shrinking every other route to a dot.
 */
function fit(state, all) {
    if (!state.core || !state.data) return;
    state.fitAll = all;
    const focus = all ? null : state.focus;
    const drawn = state.data.nodes.filter(n => (n.hops.length || n.source === 'start') && (!focus || focus.fit.has(n.id)));
    const points = drawn.map(n => [state.position[n.id].lat, state.position[n.id].lng]);
    // A preset fits its own part of the map: what lies outside it is meant to run off the edge.
    state.far = all || focus ? [] : farTargets(state, points);
    const farNodes = new Set(state.far.map(t => t.endNode));
    const kept = drawn.filter(n => !farNodes.has(n.id)).map(n => [state.position[n.id].lat, state.position[n.id].lng]);
    // The problems list covers the map's top left on a desktop, and the side panel in fullscreen.
    // On a phone in fullscreen it is a panel along the bottom.
    const full = isFullscreen(state.core);
    const phone = window.matchMedia(PHONE).matches;
    const listWest = phone ? 0 : full ? 300 : state.data.problems.length || routeChanges(state.data).length ? 260 : 0;
    // The view switch floats over the bottom: keep the routes above it.
    const listSouth = (phone && full ? Math.round(window.innerHeight * 0.4) : 0) + 56;
    state.fitting = true;
    try { fitPoints(state.core, kept.length ? kept : points, state.data.targets.length <= 3 ? 96 : 0, listWest, listSouth); }
    finally { state.fitting = false; }
    placeEdgeChips(state);
}

function km(a, b) {
    const r = Math.PI / 180;
    const dLat = (b[0] - a[0]) * r, dLon = (b[1] - a[1]) * r;
    const x = Math.sin(dLat / 2) ** 2 + Math.cos(a[0] * r) * Math.cos(b[0] * r) * Math.sin(dLon / 2) ** 2;
    return 12742 * Math.asin(Math.sqrt(x));
}

function farTargets(state, points) {
    if (points.length < 4) return [];
    const lat = points.map(p => p[0]).sort((a, b) => a - b)[Math.floor(points.length / 2)];
    const lng = points.map(p => p[1]).sort((a, b) => a - b)[Math.floor(points.length / 2)];
    const center = [lat, lng];
    const dist = points.map(p => km(center, p)).sort((a, b) => a - b);
    const spread = dist[Math.floor(dist.length * 0.8)] || 0;
    return state.data.targets.filter(t => {
        const p = targetPoint(state, t);
        if (!p) return false;
        const d = km(center, p);
        return d > FAR_MIN_KM && d > FAR_SPREAD * spread;
    });
}

function placeEdgeChips(state) {
    const host = state.host;
    host.querySelectorAll('.pm-edge-chip').forEach(c => c.remove());
    if (!state.core || !state.far?.length) return;
    const map = state.core.map;
    const size = map.getSize();
    const bounds = map.getBounds();
    const seen = new Set();
    for (const t of state.far) {
        const p = targetPoint(state, t);
        if (!p || bounds.contains(p) || seen.has(t.endNode)) continue;
        seen.add(t.endNode);
        const others = state.far.filter(o => o.endNode === t.endNode);
        const worst = others.reduce((w, o) => (o.health > w.health ? o : w), others[0]);
        const city = state.data.nodes[t.endNode].city;
        const text = others.length > 1 ? `${city}, ${others.length} targets` : `${city}, ${HEALTH_TEXT[health(worst)].toLowerCase()}`;
        const chip = el('button', `pm-edge-chip pm-${health(worst)}`);
        chip.type = 'button';
        chip.appendChild(el('span', `pm-dot pm-${health(worst)}`));
        chip.appendChild(el('span', null, text));
        // On the edge of the open part of the map (right of the fullscreen side panel), toward the target.
        const left = isFullscreen(state.core) && !window.matchMedia(PHONE).matches ? 300 : 0;
        const at = map.latLngToContainerPoint(p);
        const cx = (left + size.x) / 2, cy = size.y / 2;
        const halfW = (size.x - left) / 2 - 70, halfH = cy - 24;
        const dx = at.x - cx, dy = at.y - cy;
        const scale = Math.min(Math.abs(halfW / (dx || 1e-6)), Math.abs(halfH / (dy || 1e-6)));
        chip.style.left = `${Math.round(cx + dx * scale)}px`;
        chip.style.top = `${Math.round(cy + dy * scale)}px`;
        chip.addEventListener('click', e => { e.stopPropagation(); fit(state, true); });
        window.L.DomEvent.disableClickPropagation(chip);
        host.appendChild(chip);
    }
}

// ─── Phone sheet (Live Traffic's .lt-fpop, on the page, outside any scroll container) ───

let sheet = null;

function openSheet(state, t) {
    openSheetWith(state, t.name, targetCard(state, t, true), [t.targetId]);
}

/** The phone sheet with any card: a place, a leg, a cluster, or a target. */
function openSheetWith(state, title, content, ids) {
    closeSheet();
    const backdrop = el('div', 'lt-fpop-backdrop');
    const pop = el('div', 'lt-fpop path-map-sheet');
    pop.setAttribute('role', 'dialog');
    pop.setAttribute('aria-label', title);
    pop.appendChild(el('div', 'lt-fpop-grab'));
    const head = el('div', 'lt-fpop-head');
    head.appendChild(el('span', 'lt-fpop-title', 'Path Map'));
    pop.appendChild(head);
    pop.appendChild(content);
    backdrop.addEventListener('click', closeSheet);
    const onKey = e => { if (e.key === 'Escape') closeSheet(); };
    document.addEventListener('keydown', onKey);
    document.body.appendChild(backdrop);
    document.body.appendChild(pop);
    sheet = { backdrop, pop, onKey };
    if (ids?.length) highlight(state, ids);
}

// ─── Cards on places, legs, and clusters ───

/**
 * One interaction for everything on the map: rest the pointer on it (REST_MS) and a card opens on the page, flipped
 * to fit (a Leaflet tooltip is cut off at the map's edge), that the pointer can move into and click. On a phone a
 * tap opens the same card in the sheet. A desktop click does `onClick`.
 * @param {object} opts - atPointer: anchor at the pointer (a leg, which is too long to anchor whole); title and
 *   ids: the sheet's label and the targets to light; onClick; clickOnPhone: a tap does onClick too
 */
function attachCard(state, layer, build, opts) {
    const L = window.L;
    const anchor = layer.getElement?.();
    let at = null;
    if (opts.atPointer) layer.on('mousemove', e => { at = e.originalEvent; });
    if (anchor && window.tippy) {
        const tip = window.tippy(anchor, {
            content: '',
            placement: 'top',
            theme: 'custom',
            appendTo: document.body,
            interactive: true,
            interactiveBorder: 12,
            delay: [REST_MS, 120],
            touch: false,
            maxWidth: 'min(100vw - 10px, 440px)',
            ...(opts.atPointer ? { getReferenceClientRect: () => (at ? new DOMRect(at.clientX, at.clientY, 0, 0) : anchor.getBoundingClientRect()) } : {}),
            onShow: inst => {
                inst.setContent(build());
                if (!pinned(state) && opts.ids?.length) highlight(state, opts.ids);
            },
            onHidden: () => { if (!pinned(state)) highlight(state, null); },
        });
        state.core.tips.push(tip);
    }
    layer.on('click', e => {
        L.DomEvent.stopPropagation(e);
        if (!window.matchMedia(PHONE).matches) { opts.onClick?.(e); return; }
        if (opts.clickOnPhone) opts.onClick?.(e);
        openSheetWith(state, opts.title, build(), opts.ids);
    });
}

/** Network Tools, running a traceroute to the target from the vantage that measures it. */
function traceUrl(t) {
    return t.probeUrl.includes('#') ? t.probeUrl.replace('#', '&run=trace#') : `${t.probeUrl}&run=trace`;
}

/** The targets on a place, leg, or cluster: a row opens one in the charts; Trace runs its traceroute. */
function targetRows(state, targets) {
    const box = el('div', 'pm-card-targets');
    const sorted = targets.slice().sort((a, b) => b.health - a.health || a.name.localeCompare(b.name, undefined, { numeric: true }));
    for (const t of sorted) {
        const row = el('div', 'pm-card-target');
        const open = el('button', 'pm-card-target-open');
        open.type = 'button';
        open.appendChild(el('span', `pm-dot pm-${health(t)}`));
        open.appendChild(el('span', 'pm-card-target-name', t.name));
        open.appendChild(el('span', 'pm-card-target-state', stateText(t)));
        open.addEventListener('click', () => { closeSheet(); window.tippy?.hideAll?.(); select(state, t); });
        open.addEventListener('mouseenter', () => { if (!pinned(state)) highlight(state, [t.targetId]); });
        row.appendChild(open);
        const trace = el('a', 'pm-card-trace', 'Trace');
        trace.href = traceUrl(t);
        trace.setAttribute('aria-label', `Trace the route to ${t.name}`);
        row.appendChild(trace);
        box.appendChild(row);
    }
    return box;
}

function netOf(n) {
    return n.asnName && n.asn ? `${n.asnName} · AS${n.asn}` : n.asn ? `AS${n.asn}` : n.asnName || null;
}

function legCard(state, leg, crossing, many) {
    const data = state.data;
    const a = data.nodes[leg.from], b = data.nodes[leg.to];
    const card = el('div', 'pm-card');
    card.appendChild(el('div', 'pm-card-head pm-card-name', legText(a.city, b.city, leg.kilometers)));
    // Which networks it joins: a city pair alone does not say.
    const nets = a.source === 'start' ? [netOf(b)] : [netOf(a), netOf(b)];
    const shown = [...new Set(nets.filter(Boolean))];
    if (shown.length) card.appendChild(el('div', 'pm-card-meta', shown.join(' to ')));
    if (leg.silentHops > 0) {
        const either = leg.viaEither?.length ? `${leg.viaEither.slice(0, -1).join(', ')} or ${leg.viaEither[leg.viaEither.length - 1]}` : null;
        const via = either ? ` Likely through ${either}.` : leg.via != null ? ` Likely through ${data.nodes[leg.via].city}.` : '';
        card.appendChild(el('div', 'pm-card-line', `${leg.silentHops} ${leg.silentHops === 1 ? 'hop' : 'hops'} did not answer.${via}`));
    }
    if (leg.halo) card.appendChild(el('div', 'pm-card-line', leg.halo));
    if (leg.located) card.appendChild(el('div', 'pm-card-line', 'Path Localization placed the problem here: the last clean router to the first elevated one.'));
    card.appendChild(el('div', 'pm-card-section', `${crossing.length} ${crossing.length === 1 ? 'target' : 'targets'} cross it${many ? ` on ${wanToken(state, leg.wanKey)}` : ''}`));
    card.appendChild(targetRows(state, crossing));
    return card;
}

function placeCard(state, node, through) {
    const card = el('div', 'pm-card');
    card.appendChild(placeTip(node));
    if (through.length) {
        card.appendChild(el('div', 'pm-card-section', `${through.length} ${through.length === 1 ? 'target routes' : 'targets route'} through here`));
        card.appendChild(targetRows(state, through));
    }
    return card;
}

function closeSheet() {
    if (!sheet) return;
    sheet.backdrop.remove();
    sheet.pop.remove();
    document.removeEventListener('keydown', sheet.onKey);
    sheet = null;
}

// ─── Legend and note ───

function renderLegend(state) {
    const legend = state.legend;
    if (!legend) return;
    legend.textContent = '';
    const item = (cls, text) => {
        const i = el('span', 'trace-legend-item');
        i.appendChild(el('span', cls));
        i.appendChild(document.createTextNode(text));
        legend.appendChild(i);
    };
    item('pm-dot pm-normal', 'Normal');
    item('pm-dot pm-elevated', 'Elevated');
    item('pm-dot pm-degraded', 'Degraded');
    item('pm-dot pm-unreachable', 'Unreachable');
    item('pm-dot pm-unknown', 'No baseline');
    item('pm-dot pm-normal pm-router', 'ISP / Transit');
    item('pm-key pm-key-unsure', 'GeoLite2 only');
    if (state.data.legs.some(l => l.silentHops > 0)) item('pm-key pm-key-silent', 'Hops did not answer');
    if (routeChanges(state.data).length) item('pm-key pm-key-shift', 'Before a route change (select one)');
    if (state.data.legs.some(l => l.halo)) item('pm-key pm-key-halo', 'Every target beyond is slow');
    if (state.data.legs.some(l => l.located)) item('pm-key pm-key-located', 'Located by Path Localization');
    const wans = [...new Set(state.data.legs.map(l => l.wanKey))];
    if (wans.length > 1) {
        const order = (state.view.wanScope?.selected || []).map(k => k.toLowerCase());
        wans.forEach(w => item(`pm-key pm-key-wan-${Math.max(0, order.indexOf(w)) % 3}`, wanToken(state, w)));
    }
}

function renderNote(state) {
    const note = state.note;
    if (!note) return;
    const data = state.data;
    // While the map is live the waiting targets show as progress on the map instead.
    note.textContent = !data.targets.length ? 'No routed targets on this WAN yet.'
        : !data.live && data.untracedTargets > 0
            ? `${data.untracedTargets} ${data.untracedTargets === 1 ? 'target was' : 'targets were'} not traced in this window.`
            : '';
}

/** "Tracing routes: 3 of 14", with a spinner, over the map while targets wait for their first trace. */
function renderWaiting(state) {
    const data = state.data;
    let box = state.host.querySelector(':scope > .path-map-wait');
    const waiting = data.live && data.targets.length > 0 && data.waitingTargets > 0;
    if (!waiting) { box?.remove(); return; }
    if (!box) {
        box = el('div', 'path-map-wait');
        if (window.L) window.L.DomEvent.disableClickPropagation(box);
        state.host.appendChild(box);
    }
    box.textContent = '';
    box.classList.toggle('is-empty', !data.nodes.length);
    box.appendChild(el('span', 'spinner-sm'));
    const done = data.targets.length - data.waitingTargets;
    box.appendChild(el('span', null, `Tracing routes: ${done} of ${data.targets.length}`));
}
