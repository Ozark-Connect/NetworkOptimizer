// Shared data store for the LAN flow map topology.
// The 3D map publishes data here after each fetch; the 2D map subscribes
// so only one set of API calls runs. Pure pub/sub - no fetching.

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
let _listeners = new Set();

export function getSnapshot()  { return _snapshot; }
export function getLiveRates()  { return _liveRates; }
export function getCloudStats() { return _cloudStats; }
export function getNodeBadges() { return _nodeBadges; }
export function getClientStats() { return _clientStats; }
export function isPaused()       { return _paused; }
export function getMode()        { return _mode; }
export function getScrubber()    { return { value: _scrubberValue, right: _scrubberRight, speed: _playbackSpeed }; }

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
