// Firmware Rollout topology visualization. Drives the 2D map's node-overlay API from
// rollout state and renders the timeline scrubber under it. One component, two modes:
// planned (preview: scrub or play the sequence over estimated times) and live
// (actual step states with a now marker). No chart library - DOM + the map canvas.

import * as map2d from './lan-flow-map-2d.js?v=1'; // bump v= when lan-flow-map-2d.js changes
// KEEP IN SYNC with lan-flow-map-2d.js: the same specifier, so both share one store.
import * as flowData from './lan-flow-data.js?v=7';

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
    4: 'upgrading',  // BackOnline
    5: 'upgrading',  // CoolDown
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
let _playheadSec = 0;
let _playing = false;
let _playTimer = 0;
let _lastTick = 0;
let _liveStartMs = null;
let _apiBase = '/api/monitoring/lan-flow-map';
let _windowStartMs = null;   // wall clock the preview plays back from
let _historicGen = 0;
let _lastHistoricMs = 0;

export async function mount(stageId, timelineId, opts) {
    resolveColors();
    if (opts?.apiBase) _apiBase = opts.apiBase;
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
    flowData.publishPlayState(false, 'live');
    map2d.clearNodeOverlays();
    map2d.unmount();
    if (_timelineEl) _timelineEl.replaceChildren();
    _timelineEl = null;
    _plan = null; _liveSteps = null; _excluded = [];
}

/// Planned mode: the preview drives everything from ETAs.
/// startUtcMs is when the rollout would run; traffic is played back from the most recent
/// occurrence of that weekday and hour, since the window itself has not happened yet.
export function setPlan(planDoc, excludedMacs, startUtcMs) {
    _plan = planDoc || null;
    _excluded = excludedMacs || [];
    _mode = 'planned';
    _playheadSec = 0;
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
        if (res.ok) flowData.publishSnapshot(await res.json());
    } catch { /* the map draws what it has */ }
}

/// Traffic as it was at that instant. Throttled: scrubbing must not open a request per frame.
async function loadHistoricAt(atMs, force) {
    if (!atMs) return;
    const now = performance.now();
    if (!force && now - _lastHistoricMs < 700) return;
    _lastHistoricMs = now;

    const gen = ++_historicGen;
    try {
        const url = `${_apiBase}/history?at=${encodeURIComponent(new Date(atMs).toISOString())}`;
        const res = await fetch(url, { credentials: 'same-origin' });
        if (!res.ok || gen !== _historicGen) return;
        flowData.publishLive(await res.json());
    } catch { /* keep the last frame rather than blanking the map */ }
}

/// Live mode: actual step states; startedAtMs positions the now marker.
export function setLiveSteps(planDoc, steps, startedAtMs) {
    _plan = planDoc || _plan;
    _liveSteps = steps || [];
    _liveStartMs = startedAtMs || null;
    _mode = 'live';
    pause();
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
    _lastTick = performance.now();
    const speedup = 60; // 1 s wall clock = 1 min of plan
    const tick = () => {
        if (!_playing) return;
        const now = performance.now();
        _playheadSec += ((now - _lastTick) / 1000) * speedup;
        _lastTick = now;
        if (_playheadSec >= totalSeconds()) { _playheadSec = totalSeconds(); _playing = false; }
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
    if (_playTimer) cancelAnimationFrame(_playTimer);
    _playTimer = 0;
    updatePlayButton();
}

function totalSeconds() { return Math.max(1, _plan?.totalEstimatedSeconds || 1); }

function fmtDuration(s) {
    s = Math.round(s);
    const h = Math.floor(s / 3600), m = Math.round((s % 3600) / 60);
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
            if (state === 'excluded') continue; // dimmed below with _excluded
            const doc = byMac[mac];
            overlays[mac] = overlayFor(state, doc?.s || {}, doc?.wave || {}, step);
        }
    }

    for (const mac of _excluded) {
        overlays[mac.toLowerCase()] = { dim: true };
    }
    map2d.setNodeOverlays(overlays);
}

const STATE_WORDS = {
    queued: 'Queued',
    pending: 'Queued',
    upgrading: 'Upgrading now',
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

    const legend = document.createElement('div');
    legend.className = 'firmware-rollout-legend';
    const legendItems = _mode === 'planned'
        ? [['queued', 'Queued'], ['upgrading', 'Upgrading'], ['done', 'Done'], ['held', 'Held for canary']]
        : [['queued', 'Queued'], ['upgrading', 'Upgrading'], ['done', 'Upgraded'], ['failed', 'Failed'], ['held', 'Held for canary']];
    for (const [state, label] of legendItems) {
        const item = document.createElement('span');
        item.className = 'firmware-rollout-legend-item';
        const dot = document.createElement('span');
        dot.className = 'firmware-rollout-legend-dot';
        dot.style.background = COLORS[state];
        item.append(dot, document.createTextNode(label));
        legend.appendChild(item);
    }
    for (const [mark, label] of [['1', 'wave number'], ['C', 'canary'], ['H', 'held'], ['!', 'needs a look']]) {
        const item = document.createElement('span');
        item.className = 'firmware-rollout-legend-item';
        const badge = document.createElement('span');
        badge.className = 'firmware-rollout-legend-badge';
        badge.textContent = mark;
        item.append(badge, document.createTextNode(label));
        legend.appendChild(item);
    }
    el.appendChild(legend);

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

    const labels = document.createElement('div');
    labels.className = 'firmware-rollout-timeline-labels';
    const l0 = document.createElement('span'); l0.textContent = 'start';
    const l1 = document.createElement('span'); l1.className = 'firmware-rollout-playhead-label';
    const l2 = document.createElement('span'); l2.textContent = fmtDuration(total);
    labels.append(l0, l1, l2);
    el.appendChild(labels);

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
            const up = () => {
                track.removeEventListener('pointermove', move);
                track.removeEventListener('pointerup', up);
            };
            track.addEventListener('pointermove', move);
            track.addEventListener('pointerup', up);
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
}

function updatePlayButton() {
    const btn = _timelineEl?.querySelector('.firmware-rollout-play');
    if (btn) btn.textContent = _playing ? 'Pause' : 'Play';
}
