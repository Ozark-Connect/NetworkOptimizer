// A Live View tile's sparkline you can read and click (StatTileSparkline, Traceable): a dot follows
// the pointer along the line with that moment's time and value, and a click moves Live View there,
// the same seek the WAN chart's click makes. The series rides on the tile's strip as data attributes
// (data-trace-*), read on every move because Blazor redraws it. The dot and label live on <body>:
// Blazor owns the tile's children and would drop anything added among them.

import { formatBps } from './chart-format.js?v=2';

const bound = new WeakSet();
let dot = null;
let label = null;

function ensureMarks() {
    if (dot?.isConnected) return;
    dot = document.createElement('div');
    dot.className = 'spark-trace-dot';
    label = document.createElement('div');
    label.className = 'spark-trace-label';
    document.body.append(dot, label);
}

let tracingHost = null;

function hide() {
    if (dot) dot.style.display = 'none';
    if (label) label.style.display = 'none';
    tracingHost?.classList.remove('spark-tracing');
    tracingHost = null;
}

const parsed = new WeakMap();
function series(area) {
    const raw = area.dataset.tracePoints;
    const cached = parsed.get(area);
    if (cached?.raw === raw) return cached.points;
    let points = [];
    try { points = JSON.parse(raw || '[]'); } catch { /* a half-written attribute reads as no line */ }
    parsed.set(area, { raw, points });
    return points;
}

// The value at `t`, read off the line the chart draws: straight between the two samples around it.
function valueAt(points, t) {
    if (!points.length || t < points[0][0]) return null;
    for (let i = 1; i < points.length; i++) {
        const [t1, v1] = points[i];
        if (t <= t1) {
            const [t0, v0] = points[i - 1];
            return t1 === t0 ? v1 : v0 + (v1 - v0) * (t - t0) / (t1 - t0);
        }
    }
    return points[points.length - 1][1];
}

// Where the pointer sits on the line, or null when it is off the chart.
function readAt(host, clientX) {
    const area = host.querySelector('[data-trace-points]');
    const chart = host.querySelector('.signal-spark');
    if (!area || !chart) return null;
    const box = chart.getBoundingClientRect();
    if (box.width <= 0 || clientX < box.left || clientX > box.right) return null;
    const points = series(area);
    if (points.length < 2) return null;
    const windowMs = +area.dataset.traceWindow;
    const end = points[points.length - 1][0];
    const t = end - windowMs + (clientX - box.left) / box.width * windowMs;
    const v = valueAt(points, t);
    if (v == null) return null;
    const lo = +area.dataset.traceLo, hi = +area.dataset.traceHi;
    const frac = hi > lo ? Math.min(1, Math.max(0, (v - lo) / (hi - lo))) : 0;
    return {
        t, v, x: clientX, y: box.top + (1 - frac) * box.height,
        kind: area.dataset.traceKind, href: area.dataset.traceHref, color: getComputedStyle(chart).color,
    };
}

function show(host, r) {
    ensureMarks();
    // The line's live end dot steps aside: the tracer is that dot, moved to the pointer.
    if (tracingHost !== host) { tracingHost?.classList.remove('spark-tracing'); tracingHost = host; host.classList.add('spark-tracing'); }
    dot.style.display = label.style.display = 'block';
    dot.style.left = `${r.x}px`;
    dot.style.top = `${r.y}px`;
    dot.style.color = r.color;
    const time = window.noTime ? window.noTime.timeLabel(r.t, false) : new Date(r.t).toLocaleTimeString();
    label.textContent = `${time} · ${r.kind === 'bps' ? formatBps(r.v) : r.v.toFixed(1)}`;
    // Above the dot, kept on screen.
    const w = label.offsetWidth;
    label.style.left = `${Math.min(window.innerWidth - w - 4, Math.max(4, r.x - w / 2))}px`;
    label.style.top = `${r.y - 30}px`;
}

/** Binds a tile around its strip. Safe to call on every render: a bound tile is left as it is. */
export function bind(area) {
    const host = area?.closest('.signal-spark-host');
    if (!host || bound.has(host)) return;
    bound.add(host);
    host.classList.add('spark-traceable');
    host.addEventListener('pointermove', (e) => {
        const r = readAt(host, e.clientX);
        if (r) show(host, r); else hide();
    });
    host.addEventListener('pointerleave', hide);
    host.addEventListener('click', (e) => {
        const r = readAt(host, e.clientX);
        if (!r) return;
        hide();
        const at = Math.round(r.t);
        // A page without a Live View map of its own opens Live View at the moment instead.
        if (r.href) {
            const url = r.href + at;
            if (window.__navigate) window.__navigate(url); else location.href = url;
            return;
        }
        window.__lanFlowMap?.getInstance?.()?.seekTo?.(at);
    });
}
