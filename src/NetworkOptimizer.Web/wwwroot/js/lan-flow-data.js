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
    const firstLoad = !_snapshot;
    _snapshot = snap;
    // Only seed rates on first load. Subsequent refreshes must not clobber
    // the fresh 1s-polled rates with stale snapshot-time values.
    if (firstLoad) _liveRates = snap.liveRates || {};
    _notify('snapshot');
}

export function publishLive(update) {
    if (update.linkRates)   Object.assign(_liveRates, update.linkRates);
    if (update.cloudStats)  _cloudStats = update.cloudStats;
    if (update.nodeBadges)  _nodeBadges = update.nodeBadges;
    // Wholesale replace (not merge): historic ticks carry client stats, live ticks
    // don't, so this clears them when returning to live - renderers then fall back to
    // the snapshot values, which live snapshot rebuilds keep current.
    _clientStats = update.clientStats || {};
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
        _fetchLive(signal).catch(() => {});
    }, intervalMs);
}

export function stopPolling() {
    if (_pollTimer) { clearInterval(_pollTimer); _pollTimer = null; }
    if (_pollAbort) { _pollAbort.abort(); _pollAbort = null; }
}
