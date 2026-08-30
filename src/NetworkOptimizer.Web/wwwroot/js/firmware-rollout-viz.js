// Firmware Rollout topology visualization. Drives the 2D map's node-overlay API from
// rollout state and renders the timeline scrubber under it. One component, two modes:
// planned (preview: scrub or play the sequence over estimated times) and live
// (actual step states with a now marker). No chart library - DOM + the map canvas.

import * as map2d from './lan-flow-map-2d.js?v=37'; // bump v= when lan-flow-map-2d.js changes
// KEEP IN SYNC with lan-flow-map-2d.js: the same specifier, so both share one store.
import * as flowData from './lan-flow-data.js?v=16';

function cssVar(name, fallback) {
    const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return v || fallback;
}

// State colors resolve from the app palette once per mount (canvas cannot resolve var()).
let COLORS = null;
function resolveColors() {
    COLORS = {
        pending: cssVar('--text-muted', '#5c5c66'),
        queued: cssVar('--info-color', '#4797ff'),
        upgrading: cssVar('--warning-color', '#e79613'),
        cooldown: cssVar('--primary-color', '#0550B5'),
        done: cssVar('--success-color', '#24bc70'),
        failed: cssVar('--danger-color', '#ee6368'),
        held: '#a78bfa',
        playhead: cssVar('--primary-color', '#0550B5'),
    };
}

// DB step states (FirmwareRolloutStepState ints) to visual buckets for live mode.
const LIVE_STATE = {
    0: 'pending',    // Pending
    1: 'held',       // Held
    2: 'upgrading',  // Commanded
    3: 'upgrading',  // Down
    4: 'cooldown',   // BackOnline
    5: 'cooldown',   // CoolDown
    6: 'done',       // LitmusPassed
    7: 'done',       // RegressionFlagged (came back; flagged separately via badge)
    8: 'failed',     // Failed
    9: 'excluded',   // SkippedExcluded
    10: 'failed',    // AbortedSku
};

let _mode = 'planned';
let _plan = null;              // RolloutPlanDocument (camelCase)
let _excluded = [];            // MACs rendered dimmed
let _liveSteps = null;         // [{deviceMac, state, wave}] for live mode
let _timelineEl = null;
let _stageEl = null;
let _playheadSec = 0;
let _playing = false;
let _playTimer = 0;
let _lastTick = 0;
let _liveStartMs = null;
let _apiBase = '/api/monitoring/lan-flow-map';
let _windowStartMs = null;   // wall clock the preview plays back from
let _plannedStartMs = null;  // when the rollout would actually run
let _timeZoneId = null;      // the server's zone: one clock across every site, as everywhere else
let _historicGen = 0;
let _lastHistoricMs = 0;

export async function mount(stageId, timelineId, opts) {
    resolveColors();
    if (opts?.apiBase) _apiBase = opts.apiBase;
    _stageEl = document.getElementById(stageId);
    // This map is a rollout picture, not the live explorer: no overlay toggles, no client
    // filter, and no synthetic multi-MAC hub nodes, none of which a rollout acts on.
    // A preview is not a live view: the map opens on the traffic of the window the rollout
    // would run in, paused, and moves only when the timeline is played or scrubbed.
    flowData.publishPlayState(true, 'historic');
    await map2d.mount(stageId, {
        ...(opts || {}),
        hideOverlayControls: true,
        hideFilter: true,
        hideVirtualHubs: true,
        hideClouds: true,
        hideWiredClients: true,
        hideWifiClients: true,
        hideHelp: true,
        hideRates: true,
        hideScrubber: true,
    });
    await loadTopologyAsync();
    _timelineEl = document.getElementById(timelineId);
    _playheadSec = 0;
    _playing = false;
    _mode = 'planned';
}

export function dispose() {
    pause();
    _historicGen++;
    map2d.stopDataPolling();
    flowData.clearLiveRates();
    flowData.resetPlayback();
    flowData.publishPlayState(false, 'live');
    map2d.clearNodeOverlays();
    map2d.unmount();
    if (_timelineEl) _timelineEl.replaceChildren();
    _stageEl?.querySelector('.firmware-rollout-legend')?.remove();
    _timelineEl = null;
    _stageEl = null;
    _plan = null; _liveSteps = null; _excluded = [];
}

/// Planned mode: the preview drives everything from ETAs.
/// startUtcMs is when the rollout would run; traffic is played back from the most recent
/// occurrence of that weekday and hour, since the window itself has not happened yet.
export function setPlan(planDoc, excludedMacs, startUtcMs, timeZoneId) {
    _plan = planDoc || null;
    _excluded = excludedMacs || [];
    _mode = 'planned';
    _playheadSec = 0;
    _plannedStartMs = startUtcMs || null;
    _timeZoneId = timeZoneId || null;
    _windowStartMs = lastOccurrenceOf(startUtcMs);
    renderTimeline();
    applyOverlays();
    loadHistoricAt(_windowStartMs, true);
}

/// The same weekday and hour, most recently past. A window in the future has no traffic yet.
function lastOccurrenceOf(startUtcMs) {
    if (!startUtcMs) return Date.now() - 3600000;
    const week = 7 * 24 * 3600000;
    let at = startUtcMs;
    while (at > Date.now() - 60000) at -= week;
    return at;
}

async function loadTopologyAsync() {
    try {
        const res = await fetch(`${_apiBase}/snapshot`, { credentials: 'same-origin' });
        if (!res.ok) { console.warn('[firmware-rollout] topology fetch failed', res.status); return; }
        flowData.publishSnapshot(await res.json());
    } catch (err) {
        console.warn('[firmware-rollout] topology fetch threw', err);
    }
}

/// Traffic as it was at that instant. Throttled: scrubbing must not open a request per frame.
async function loadHistoricAt(atMs, force) {
    if (!atMs) return;
    const now = performance.now();
    if (!force && now - _lastHistoricMs < 700) return;
    _lastHistoricMs = now;

    const gen = ++_historicGen;
    const url = `${_apiBase}/history?at=${encodeURIComponent(new Date(atMs).toISOString())}`;
    try {
        const res = await fetch(url, { credentials: 'same-origin' });
        if (!res.ok) { console.warn('[firmware-rollout] history fetch failed', res.status, url); return; }
        if (gen !== _historicGen) return;
        const update = await res.json();
        // The store MERGES link rates, so a link that was idle at this instant is simply absent
        // from the update and would keep whatever live value seeded it. Zero every known link
        // first, then let the update fill in the ones that were moving.
        const snap = flowData.getSnapshot();
        const rates = {};
        for (const link of snap?.links || []) {
            if (link.portKey) rates[link.portKey] = { downstreamBps: 0, upstreamBps: 0 };
            if (link.id) rates[link.id] = { downstreamBps: 0, upstreamBps: 0 };
        }
        Object.assign(rates, update.linkRates || {});

        // The historic update keys rates by link id, but the map looks up portKey first, so a
        // zeroed portKey would hide the value that arrived under the id.
        for (const link of snap?.links || []) {
            if (link.id && link.portKey && rates[link.id]) rates[link.portKey] = rates[link.id];
        }

        flowData.publishLive({ ...update, linkRates: rates });
        map2d.refreshRates();
    } catch (err) {
        console.warn('[firmware-rollout] history fetch threw', err, url);
    }
}

/// Live mode: actual step states; startedAtMs positions the now marker.
export function setLiveSteps(planDoc, steps, startedAtMs) {
    _plan = planDoc || _plan;
    _liveSteps = steps || [];
    _liveStartMs = startedAtMs || null;
    const wasPlanned = _mode !== 'live';
    _mode = 'live';

    // A running rollout wants the traffic it is actually causing, so this is the one mode that
    // polls. The preview's pause belongs to its playhead, and live has no playhead to hold still.
    if (wasPlanned) {
        _playing = false;
        if (_playTimer) cancelAnimationFrame(_playTimer);
        _playTimer = 0;
        _historicGen++;
        flowData.publishPlayState(false, 'live');
        map2d.startDataPolling();
    }

    renderTimeline();
    applyOverlays();
}

export function setTimelinePosition(seconds) {
    _playheadSec = Math.max(0, Math.min(seconds, totalSeconds()));
    positionPlayhead();
    applyOverlays();
    if (_mode === 'planned' && _windowStartMs) loadHistoricAt(_windowStartMs + _playheadSec * 1000);
}

export function play() {
    if (_playing || _mode !== 'planned') return;
    if (_playheadSec >= totalSeconds()) _playheadSec = 0;
    _playing = true;
    // The map only advances its particles while the store says it is not paused, so our
    // Play is what sets the traffic moving.
    flowData.publishPlayState(false, 'historic');
    _lastTick = performance.now();
    const speedup = 120; // 1 s wall clock = 2 min of plan
    const tick = () => {
        if (!_playing) return;
        const now = performance.now();
        _playheadSec += ((now - _lastTick) / 1000) * speedup;
        _lastTick = now;
        // Running off the end is a pause like any other: the traffic has to settle with the
        // playhead, since the map keeps its particles moving until the store says paused.
        if (_playheadSec >= totalSeconds()) {
            _playheadSec = totalSeconds();
            _playing = false;
            flowData.publishPlayState(true, 'historic');
        }
        positionPlayhead();
        applyOverlays();
        if (_windowStartMs) loadHistoricAt(_windowStartMs + _playheadSec * 1000);
        if (_playing) _playTimer = requestAnimationFrame(tick);
        updatePlayButton();
    };
    _playTimer = requestAnimationFrame(tick);
    updatePlayButton();
}

export function pause() {
    _playing = false;
    flowData.publishPlayState(true, 'historic');
    if (_playTimer) cancelAnimationFrame(_playTimer);
    _playTimer = 0;
    updatePlayButton();
}

function totalSeconds() { return Math.max(1, _plan?.totalEstimatedSeconds || 1); }

function fmtDuration(s) {
    // Round to whole minutes first so 59m 55s reads 1h 0m, not 1h 60m.
    const totalMin = Math.round(s / 60);
    const h = Math.floor(totalMin / 60), m = totalMin % 60;
    return h > 0 ? `${h}h ${m}m` : `${m}m`;
}

// ---- Overlay computation ----

function plannedStepState(step, waveStart) {
    const eta = step.etaOffsetSeconds ?? waveStart;
    const end = eta + (step.estimatedDowntimeSeconds || 0);
    if (_playheadSec >= end && _playheadSec > 0) return 'done';
    if (_playheadSec >= eta && _playheadSec > 0) return 'upgrading';
    return step.heldForCanary ? 'held' : 'queued';
}

function applyOverlays() {
    if (!_plan) { map2d.clearNodeOverlays(); return; }
    const overlays = {};

    if (_mode === 'planned') {
        for (const wave of _plan.waves || []) {
            for (const s of wave.steps || []) {
                const state = plannedStepState(s, wave.startOffsetSeconds || 0);
                overlays[s.mac] = overlayFor(state, s, wave);
            }
        }
    } else {
        const byMac = {};
        for (const wave of _plan.waves || []) for (const s of wave.steps || []) byMac[s.mac.toLowerCase()] = { s, wave };
        for (const step of _liveSteps || []) {
            const mac = (step.deviceMac || step.mac || '').toLowerCase();
            const state = LIVE_STATE[step.state] ?? 'pending';
            const doc = byMac[mac];
            overlays[mac] = state === 'excluded'
                ? { dim: true }
                : overlayFor(state, doc?.s || {}, doc?.wave || {}, step);
        }
    }

    applyConsoleOverlay(overlays);

    // Planned mode only. The live run carries its own exclusions in the step states, and dimming
    // from the preview's list here painted a device grey over whatever it was really doing.
    if (_mode === 'planned') {
        for (const mac of _excluded) {
            overlays[mac.toLowerCase()] = { dim: true };
        }
    }
    map2d.setNodeOverlays(overlays);
}

/// The UniFi Network and UniFi OS steps have no device of their own, so they mark the console.
/// Only a Cloud Gateway is its own console - elsewhere consoleMac is absent and neither step
/// touches the map. The console is also a device in the last wave, so the console step wins the
/// node only while it is the one actually running.
function applyConsoleOverlay(overlays) {
    const mac = (_plan.consoleMac || '').toLowerCase();
    if (!mac) return;
    const con = _mode === 'planned' ? plannedConsoleState() : liveConsoleState();
    if (!con) return;
    if (con.state !== 'upgrading' && overlays[mac]) return;

    const ov = { color: COLORS[con.state] || COLORS.pending, tip: con.tip };
    if (con.state === 'upgrading') ov.pulse = true;
    if (con.state === 'failed') ov.badge = '!';
    overlays[mac] = ov;
}

/// Which console step the playhead is inside, if any. UniFi Network runs ahead of wave 1 and
/// UniFi OS after every device step, so at most one is ever live.
function plannedConsoleState() {
    const sec = _playheadSec;
    if (_plan.includesUniFiNetworkUpdate) {
        const end = _plan.uniFiNetworkUpdateSeconds || 0;
        if (sec < end) return { state: 'upgrading', tip: 'Updating the UniFi Network application' };
    }
    if (_plan.includesUniFiOsUpdate) {
        const start = _plan.uniFiOsStartOffsetSeconds || 0;
        const end = start + (_plan.uniFiOsUpdateSeconds || 0);
        if (sec >= start && sec < end) return { state: 'upgrading', tip: 'Updating UniFi OS' };
        if (sec >= end) return { state: 'done', tip: 'UniFi OS updated' };
    }
    return null;
}

function liveConsoleState() {
    const os = _plan.uniFiOsUpdate, app = _plan.networkAppUpdate;
    for (const [step, label] of [[os, 'UniFi OS'], [app, 'the UniFi Network application']]) {
        if (!step?.triggered) continue;
        if (!step.settled) return { state: 'upgrading', tip: `Updating ${label}` };
        if (step.outcome === 'refused' || step.outcome === 'stuck') {
            return { state: 'failed', tip: `${label} did not update (${step.outcome})` };
        }
    }
    return null;
}

const STATE_WORDS = {
    queued: 'Queued',
    pending: 'Queued',
    upgrading: 'Upgrading now',
    cooldown: 'Cooling down',
    done: 'Upgraded',
    failed: 'Failed',
    held: 'Held until its model’s canary passes',
};

function overlayFor(state, step, wave, liveStep) {
    const ov = { color: COLORS[state] || COLORS.pending };
    if (state === 'upgrading') ov.pulse = true;
    if (state === 'queued' || state === 'pending') ov.badge = String(wave.number ?? '');
    if (step.isCanary) ov.badge = 'C';
    if (state === 'held') ov.badge = 'H';
    if (state === 'failed') ov.badge = '!';
    if (liveStep && liveStep.state === 7) { ov.badge = '!'; ov.color = COLORS.upgrading; } // RegressionFlagged

    // The badge is shorthand; the tooltip is the sentence behind it.
    const parts = [];
    if (wave.number) parts.push(`Wave ${wave.number}`);
    parts.push(liveStep && liveStep.state === 7 ? 'Upgraded, resources worth a look' : (STATE_WORDS[state] || state));
    if (step.isCanary) parts.push(`canary for ${step.displayModel || step.model || 'this model'}`);
    if (step.toVersion) parts.push(`to ${step.toVersion}`);
    ov.tip = parts.join(' · ');
    return ov;
}

// ---- Timeline scrubber (DOM) ----

function renderTimeline() {
    if (!_timelineEl || !_plan) return;
    const total = totalSeconds();
    const el = _timelineEl;
    el.replaceChildren();
    el.classList.add('firmware-rollout-timeline');

    if (_mode === 'planned') {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-secondary btn-sm firmware-rollout-play';
        btn.addEventListener('click', () => _playing ? pause() : play());
        el.appendChild(btn);
    }

    _stageEl?.querySelector('.firmware-rollout-legend')?.remove();
    const legend = document.createElement('div');
    legend.className = 'firmware-rollout-legend';
    // Two deliberate rows: what the colours mean, then what the marks mean. Wrapping by
    // width alone lands on three rows at some sizes.
    const stateRow = document.createElement('div');
    stateRow.className = 'firmware-rollout-legend-row';
    const legendItems = _mode === 'planned'
        ? [['queued', 'Queued'], ['upgrading', 'Upgrading'], ['done', 'Done'], ['held', 'Held for canary']]
        : [['queued', 'Queued'], ['upgrading', 'Upgrading'], ['cooldown', 'Cooling down'], ['done', 'Upgraded'], ['failed', 'Failed'], ['held', 'Held for canary']];
    for (const [state, label] of legendItems) {
        const item = document.createElement('span');
        item.className = 'firmware-rollout-legend-item';
        const dot = document.createElement('span');
        dot.className = 'firmware-rollout-legend-dot';
        dot.style.background = COLORS[state];
        item.append(dot, document.createTextNode(label));
        stateRow.appendChild(item);
    }
    legend.appendChild(stateRow);

    const markRow = document.createElement('div');
    markRow.className = 'firmware-rollout-legend-row';
    for (const [mark, label] of [['1', 'wave number'], ['C', 'canary'], ['H', 'held'], ['!', 'needs a look']]) {
        const item = document.createElement('span');
        item.className = 'firmware-rollout-legend-item';
        const badge = document.createElement('span');
        badge.className = 'firmware-rollout-legend-badge';
        badge.textContent = mark;
        item.append(badge, document.createTextNode(label));
        markRow.appendChild(item);
    }
    legend.appendChild(markRow);

    (_stageEl || el).appendChild(legend);

    const track = document.createElement('div');
    track.className = 'firmware-rollout-track';

    // The console's own updates bracket the device waves: the application first, the OS last.
    if (_plan.includesUniFiNetworkUpdate && _plan.uniFiNetworkUpdateSeconds > 0) {
        const seg = document.createElement('div');
        seg.className = 'firmware-rollout-wave-seg firmware-rollout-console-seg';
        seg.style.left = '0%';
        seg.style.width = Math.max(_plan.uniFiNetworkUpdateSeconds / total * 100, 0.75) + '%';
        seg.dataset.tooltip = 'UniFi Network application update';
        track.appendChild(seg);
    }
    if (_plan.includesUniFiOsUpdate && _plan.uniFiOsUpdateSeconds > 0) {
        const seg = document.createElement('div');
        seg.className = 'firmware-rollout-wave-seg firmware-rollout-console-seg';
        seg.style.left = ((_plan.uniFiOsStartOffsetSeconds || 0) / total * 100) + '%';
        seg.style.width = Math.max(_plan.uniFiOsUpdateSeconds / total * 100, 0.75) + '%';
        seg.dataset.tooltip = 'UniFi OS update on the console';
        track.appendChild(seg);
    }

    for (const wave of _plan.waves || []) {
        const seg = document.createElement('div');
        seg.className = 'firmware-rollout-wave-seg';
        const start = (wave.startOffsetSeconds || 0) / total;
        const durSec = Math.max(...(wave.steps || []).map(s => s.estimatedDowntimeSeconds || 0), 60);
        seg.style.left = (start * 100) + '%';
        seg.style.width = Math.max(durSec / total * 100, 0.75) + '%';
        seg.dataset.tooltip = `Wave ${wave.number}: ` +
            (wave.steps || []).map(s => s.name || s.mac).join(', ');
        track.appendChild(seg);
    }

    const playhead = document.createElement('div');
    playhead.className = 'firmware-rollout-playhead';
    track.appendChild(playhead);
    el.appendChild(track);

    const readout = document.createElement('span');
    readout.className = 'firmware-rollout-timeline-readout';
    const clock = document.createElement('span');
    clock.className = 'firmware-rollout-timeline-clock';
    const offsets = document.createElement('span');
    const elapsed = document.createElement('span');
    elapsed.className = 'firmware-rollout-playhead-label';
    const totalLabel = document.createElement('span');
    totalLabel.className = 'firmware-rollout-muted';
    totalLabel.textContent = ` / ${fmtDuration(total)}`;
    offsets.append(elapsed, totalLabel);
    readout.append(clock, offsets);
    el.appendChild(readout);

    // Scrub by pointer (planned mode only; live position is the clock's)
    if (_mode === 'planned') {
        const scrub = (ev) => {
            const r = track.getBoundingClientRect();
            const frac = Math.max(0, Math.min(1, (ev.clientX - r.left) / r.width));
            pause();
            setTimelinePosition(frac * total);
        };
        track.addEventListener('pointerdown', (ev) => {
            track.setPointerCapture(ev.pointerId);
            scrub(ev);
            const move = (e) => scrub(e);
            // pointercancel must clean up too, or the stale move handler keeps scrubbing on hover.
            const up = () => {
                track.removeEventListener('pointermove', move);
                track.removeEventListener('pointerup', up);
                track.removeEventListener('pointercancel', up);
            };
            track.addEventListener('pointermove', move);
            track.addEventListener('pointerup', up);
            track.addEventListener('pointercancel', up);
        });
    }

    positionPlayhead();
    updatePlayButton();
    if (window.tippy && el.querySelectorAll) initTooltips(el);
}

function initTooltips(root) {
    for (const n of root.querySelectorAll('[data-tooltip]')) {
        if (!n._tippy) window.tippy(n, { content: n.dataset.tooltip });
    }
}

function positionPlayhead() {
    if (!_timelineEl) return;
    const total = totalSeconds();
    let sec = _playheadSec;
    if (_mode === 'live' && _liveStartMs) {
        sec = Math.max(0, Math.min((Date.now() - _liveStartMs) / 1000, total));
    }
    const head = _timelineEl.querySelector('.firmware-rollout-playhead');
    if (head) head.style.left = (sec / total * 100) + '%';
    const label = _timelineEl.querySelector('.firmware-rollout-playhead-label');
    if (label) label.textContent = 'T+' + fmtDuration(sec);

    // The clock the plan would actually run on, so scrubbing answers "what time is that".
    const clock = _timelineEl.querySelector('.firmware-rollout-timeline-clock');
    if (clock) clock.textContent = plannedClock(sec);
}

/// Wall-clock time of a point in the plan, in the site's own timezone.
function plannedClock(sec) {
    if (_mode !== 'planned' || !_plannedStartMs) return '';
    const at = new Date(_plannedStartMs + sec * 1000);
    try {
        return new Intl.DateTimeFormat(undefined, {
            weekday: 'short', hour: 'numeric', minute: '2-digit',
            ...(_timeZoneId ? { timeZone: _timeZoneId } : {}),
        }).format(at);
    } catch {
        return new Intl.DateTimeFormat(undefined, { weekday: 'short', hour: 'numeric', minute: '2-digit' }).format(at);
    }
}

function updatePlayButton() {
    const btn = _timelineEl?.querySelector('.firmware-rollout-play');
    if (btn) btn.textContent = _playing ? 'Pause' : 'Play';
}
