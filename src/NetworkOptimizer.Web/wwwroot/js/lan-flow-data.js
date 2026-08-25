// Shared data store for the LAN flow map topology.
// The 3D map publishes data here after each fetch; the 2D map subscribes
// so only one set of API calls runs. Pure pub/sub - no fetching.

// Timeline window presets for the shared scrubber. 'max' spans back to the
// earliest stored data point (bounded by the primary bucket's 90-day retention).
export const SCRUBBER_PRESETS = [
    { key: '1h',  ms: 3600000,       label: '1h' },
    { key: '6h',  ms: 6 * 3600000,   label: '6h' },
    { key: '24h', ms: 24 * 3600000,  label: '24h' },
    { key: '3d',  ms: 3 * 86400000,  label: '3d' },
    { key: '7d',  ms: 7 * 86400000,  label: '7d' },
    { key: '30d', ms: 30 * 86400000, label: '30d' },
    { key: 'max', ms: null,          label: 'Max' },
];

let _snapshot = null;
let _liveRates = {};
let _cloudStats = {};
let _nodeBadges = {};
let _clientStats = {};
let _presentClients = null;
let _measuredClients = null;
let _paused = false;
let _mode = 'live';
let _scrubberValue = 10000;
let _scrubberRight = 'Live';
let _playbackSpeed = 1;
// Timeline window the slider spans: { startMs, endMs, presetKey, leftLabel,
// disabledKeys }. The window always trails now, so the right edge is Live.
let _scrubberWindow = null;
let _listeners = new Set();

export function getSnapshot()  { return _snapshot; }
export function getLiveRates()  { return _liveRates; }
export function getCloudStats() { return _cloudStats; }
export function getNodeBadges() { return _nodeBadges; }
export function getClientStats() { return _clientStats; }
// Client node ids connected at the scrub instant, or null in live mode (no filtering).
export function getPresentClients() { return _presentClients; }
// Client node ids playback can say anything about. A client outside this set writes no telemetry
// (one behind a device bridge has no switch port to be tagged with), so its absence is not evidence.
export function getMeasuredClients() { return _measuredClients; }
export function isPaused()       { return _paused; }
export function getMode()        { return _mode; }
export function getScrubber()    { return { value: _scrubberValue, right: _scrubberRight, speed: _playbackSpeed }; }
export function getScrubberWindow() { return _scrubberWindow; }

export function subscribe(fn) {
    _listeners.add(fn);
    return () => _listeners.delete(fn);
}

function _notify(event) {
    for (const fn of _listeners) {
        try { fn(event); } catch { /* swallow */ }
    }
}

export function publishSnapshot(snap) {
    const firstLoad = !_serverSnapshot;
    // Kept pristine. The 3D map keeps the object it passed in as the baseline it diffs the next
    // poll against, so merging into it would write our overlay into its baseline.
    _serverSnapshot = snap;
    _rebuildMerged();
    // Only seed rates on first load. Subsequent refreshes must not clobber
    // the fresh 1s-polled rates with stale snapshot-time values.
    if (firstLoad) _liveRates = snap.liveRates || {};
    _notify('snapshot');
}

// Client leaves that are not in the server snapshot: rebuilt by the historic pass for a scrub
// instant, and emitted on a live tick for a client an AP Agent knows about before the console
// does. Held across snapshot publishes rather than cleared by them - clearing meant the leaf
// vanished until the next tick re-added it, so every snapshot poll blinked the client, and the
// blink swapped its name between the console's and the MAC-only one the agent can offer.
let _serverSnapshot = null;
let _addedNodes = [];
let _addedLinks = [];
let _addedClientKey = '';
// Clients an access point reports gone while the console still lists them. Held with the overlay
// and applied the same way, so a departure shows at the agent's speed rather than the console's.
let _removedIds = new Set();

// Rebuilds the published snapshot from the pristine server one plus the overlay. Always from
// scratch, so an overlay entry that goes away actually goes away rather than accumulating.
function _rebuildMerged() {
    if (!_serverSnapshot) { _snapshot = null; return; }

    const nodes = (_serverSnapshot.nodes || []).filter(n => !_removedIds.has(n.id));
    const links = (_serverSnapshot.links || []).filter(
        l => !_removedIds.has(l.toNodeId) && !_removedIds.has(l.fromNodeId));
    // The server's own node wins for an id it already carries: it has the console's identity for
    // the client, where an added leaf may only have the MAC.
    const haveNodes = new Set(nodes.map(n => n.id));
    const haveLinks = new Set(links.map(l => l.id));

    _snapshot = {
        ..._serverSnapshot,
        nodes: nodes.concat(_addedNodes.filter(n => !haveNodes.has(n.id))),
        links: links.concat(_addedLinks.filter(l => !haveLinks.has(l.id))),
    };
}

function _applyAddedClients(update) {
    const nodes = update.addedClientNodes || [];
    const links = update.addedClientLinks || [];
    const removed = update.removedClientIds || [];
    const key = nodes.map(n => n.id).sort().join(',') + '|' + [...removed].sort().join(',');
    // Steady state over a stable set costs nothing; only a change reshapes the graph.
    if (key === _addedClientKey) return false;

    _addedNodes = nodes;
    _addedLinks = links;
    _removedIds = new Set(removed);
    _addedClientKey = key;
    _rebuildMerged();
    return true;
}

export function publishLive(update) {
    // Historic ticks carry only the links that were moving, and the store MERGES - so a link
    // idle at this instant would keep whatever live value seeded it and read as still busy.
    if (_mode === 'historic' && update.linkRates) {
        for (const key in _liveRates) _liveRates[key] = { downstreamBps: 0, upstreamBps: 0 };
    }
    if (update.linkRates)   Object.assign(_liveRates, update.linkRates);
    if (update.cloudStats)  _cloudStats = update.cloudStats;
    if (update.nodeBadges)  _nodeBadges = update.nodeBadges;
    // Wholesale replace (not merge): historic ticks carry client stats, live ticks
    // don't, so this clears them when returning to live - renderers then fall back to
    // the snapshot values, which live snapshot rebuilds keep current.
    _clientStats = update.clientStats || {};
    // Who was actually connected at this instant. Null in live mode, where the snapshot is the
    // truth and every client in it is by definition connected.
    _presentClients = _mode === 'historic' && update.presentClientIds
        ? new Set(update.presentClientIds)
        : null;
    _measuredClients = _mode === 'historic' && update.measuredClientIds
        ? new Set(update.measuredClientIds)
        : null;
    // Rebuild first when the client set changed, so the rates below land on a graph that
    // already contains the leaves they belong to.
    if (_applyAddedClients(update)) _notify('snapshot');
    _notify('live');
}

export function publishPlayState(paused, mode) {
    _paused = paused;
    _mode = mode;
    _notify('playstate');
}

export function publishScrubber(value, rightLabel, speed) {
    _scrubberValue = value;
    _scrubberRight = rightLabel;
    _playbackSpeed = speed;
    _notify('scrubber');
}

export function publishScrubberWindow(win) {
    _scrubberWindow = win;
    _notify('scrubber-window');
}

// Drop every cached rate. A page that seeds the store with keys of its own derivation - the
// Firmware Rollout preview maps id-keyed historic rates onto portKey, which the maps look up
// first - must call this on teardown, or the next page reads those keys as its own.
export function clearLiveRates() {
    _liveRates = {};
}

// Restore live-mode playback defaults. Called by the 3D map (the playback
// authority) at the start of each of its mounts, so state left behind by a
// previous Live View session - historic mode, a paused flag, a parked
// scrubber - can't leak into the new one. This module is a singleton that
// outlives SPA navigations while the map instance itself is rebuilt fresh,
// so without this every remount inherits whatever the last session left.
// Notifies so any still-mounted consumer UI syncs to the clean state.
export function resetPlayback() {
    _paused = false;
    _mode = 'live';
    _presentClients = null;
    _measuredClients = null;
    _scrubberValue = 10000;
    _scrubberRight = 'Live';
    _playbackSpeed = 1;
    _notify('playstate');
    _notify('scrubber');
}

// Render local-midnight tick marks onto a scrubber track overlay so multi-day
// windows have day-boundary orientation. Shared by the 3D scrubber and its 2D
// mirror. Windows under two days get no ticks; wide windows thin to ~12 ticks.
export function renderScrubberTicks(el, startMs, endMs) {
    if (!el) return;
    el.innerHTML = '';
    const span = endMs - startMs;
    if (span < 48 * 3600000) return;
    const stepDays = Math.max(1, Math.round(span / (12 * 86400000)));
    const first = new Date(startMs);
    first.setHours(24, 0, 0, 0);
    let day = 0;
    for (let t = first.getTime(); t < endMs; day++) {
        if (day % stepDays === 0) {
            const tick = document.createElement('span');
            tick.className = 'lan-flow-map-scrubber-tick';
            tick.style.left = `${((t - startMs) / span * 100).toFixed(2)}%`;
            el.appendChild(tick);
        }
        const next = new Date(t);
        next.setHours(24, 0, 0, 0);
        t = next.getTime();
    }
}

// Standalone data poller for contexts without the 3D map (e.g. dashboard).
let _pollTimer = null;
let _pollAbort = null;
const API_BASE = '/api/monitoring/lan-flow-map';

async function _fetchSnapshot(signal) {
    const res = await fetch(`${API_BASE}/snapshot`, { credentials: 'same-origin', signal });
    if (!res.ok) return;
    const snap = await res.json();
    publishSnapshot(snap);
}

async function _fetchLive(signal) {
    const res = await fetch(`${API_BASE}/live`, { credentials: 'same-origin', signal });
    if (!res.ok) return;
    const update = await res.json();
    publishLive(update);
}

export function startPolling(intervalMs = 3000) {
    if (_pollTimer) return;
    _pollAbort = new AbortController();
    const signal = _pollAbort.signal;
    _fetchSnapshot(signal).catch(() => {});
    _pollTimer = setInterval(() => {
        // The topology snapshot is fetched once at start. If it wasn't ready then (e.g. the
        // site's console connection came up after the map mounted), keep retrying it until it
        // has nodes - otherwise the map has no topology to draw and stays blank indefinitely.
        if (!_snapshot || !_snapshot.nodes || _snapshot.nodes.length === 0)
            _fetchSnapshot(signal).catch(() => {});
        _fetchLive(signal).catch(() => {});
    }, intervalMs);
}

export function stopPolling() {
    if (_pollTimer) { clearInterval(_pollTimer); _pollTimer = null; }
    if (_pollAbort) { _pollAbort.abort(); _pollAbort = null; }
}
