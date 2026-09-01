// Client live throughput chart: a five-minute sliding window of one client's From Device and To
// Device rates, in the WAN live chart's shape. A renderer only - the Client Performance page owns
// the client and already polls it, so history and every live sample arrive by push.
//
// When the gateway conntrack feed covers the site, a dashed pair in the same colors overlays the
// measured WAN share of each rate, filled darker so it reads as the inner component of the
// LAN + WAN total. The WAN series carry their own buffer: their samples land on their own
// timestamps, and folding them into the totals' points would punch null gaps into those lines.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { valueSortedTooltip } from './chart-tooltip.js?v=16';
import { downloadColor, uploadColor } from './chart-colors.js?v=2';

const HISTORY_MINUTES = 5;
// Window-scroll redraw cadence: each step moves well under a pixel, so the chart slides.
const SCROLL_MS = 250;
// How long the WAN edge may stand in at the live edge; past it coverage has lapsed and the
// dashed lines end rather than flat-lining a stale measurement.
const WAN_EDGE_MAX_MS = 10000;
// The device's frame: Download is what it receives, Upload what it sends.
const COLOR_DOWN = downloadColor();
const COLOR_UP = uploadColor();

// Opt-in trace of every push and history merge, for the cases only a live capture can explain:
//   localStorage.setItem('no-client-chart-debug', '1')
const CHART_DEBUG = (() => {
    try { return localStorage.getItem('no-client-chart-debug') === '1'; } catch { return false; }
})();
function dbg(what, detail) {
    if (CHART_DEBUG) console.log(`[client-chart ${new Date().toLocaleTimeString()}] ${what}`, detail ?? '');
}

let chart = null;
let scrollTimer = null;
let buffer = [];
let wanBuffer = [];
let elId = null;
let mountGen = 0;
let lastSampleTime = 0;
let lastWanSampleTime = 0;
// The newest reading as it stands, ahead of the folded points: the line's right end follows it,
// so the chart moves with the identity row instead of a fold behind it.
let liveEdge = null;
let wanEdge = null;
let lastMouse = null;
let mouseMoveHandler = null;
let mouseLeaveHandler = null;

function formatBps(v) {
    if (v == null || v < 1) return '0';
    if (v >= 1e9) return (v / 1e9).toFixed(1) + ' Gbps';
    if (v >= 1e6) return (v / 1e6).toFixed(1) + ' Mbps';
    if (v >= 1e3) return (v / 1e3).toFixed(0) + ' Kbps';
    return v.toFixed(0) + ' bps';
}

// True only while the value tooltip is on screen. ApexCharts marks the mount on any mousemove and
// misses fast exits, so with a mouse the class is cross-checked against the pointer being over the
// plot grid; on touch the class stands alone.
function tooltipShowing() {
    const el = document.getElementById(elId);
    if (!el?.classList.contains('apexcharts-tooltip-active')) return false;
    if (window.matchMedia?.('(pointer: coarse)').matches) return true;
    const r = el.querySelector('.apexcharts-grid')?.getBoundingClientRect();
    return !!lastMouse && !!r && lastMouse.x >= r.left && lastMouse.x <= r.right
        && lastMouse.y >= r.top && lastMouse.y <= r.bottom;
}

function buildOpts() {
    const bpsFormatter = { formatter: v => formatBps(v) };
    return {
        chart: {
            type: 'area',
            height: 175,
            background: 'transparent',
            toolbar: { show: false },
            zoom: { enabled: false },
            parentHeightOffset: 0,
            animations: { enabled: true, easing: 'smooth', dynamicAnimation: { speed: 800 } },
        },
        // The WAN pair renders after the totals so its darker fill sits inside them.
        series: [
            { name: 'Download',     type: 'area', data: [] },
            { name: 'Upload',       type: 'area', data: [] },
            { name: 'WAN Download', type: 'area', data: [] },
            { name: 'WAN Upload',   type: 'area', data: [] },
        ],
        colors: [COLOR_DOWN, COLOR_UP, COLOR_DOWN, COLOR_UP],
        stroke: { curve: 'smooth', width: [2, 2, 2, 2], dashArray: [0, 0, 5, 5] },
        fill: {
            type: 'gradient',
            gradient: {
                shadeIntensity: 0.4,
                opacityFrom: [0.5, 0.5, 0.8, 0.8],
                opacityTo: [0.08, 0.08, 0.3, 0.3],
                stops: [0, 95],
            },
        },
        markers: { size: 0 },
        dataLabels: { enabled: false },
        xaxis: {
            type: 'datetime',
            min: Date.now() - HISTORY_MINUTES * 60000,
            max: Date.now(),
            // Built-in labels re-pick their ticks as the window slides; time labels are drawn as
            // annotations on a fixed grid instead (buildTimeTicks).
            labels: { show: false },
            axisBorder: { show: false },
            axisTicks: { show: false },
        },
        yaxis: {
            min: 0,
            max: v => Math.max(v * 1.1, 1000),
            labels: {
                style: { colors: '#9ca3af', fontSize: '10px' },
                formatter: v => formatBps(v),
                offsetX: -10,
            },
            axisBorder: { show: false },
            axisTicks: { show: false },
        },
        grid: {
            borderColor: '#374151',
            strokeDashArray: 3,
            padding: { left: 3, right: 26, top: -8, bottom: 12 },
            xaxis: { lines: { show: false } },
        },
        // On a phone the plot runs to the card's right edge (the container bleeds into the card
        // padding there); the value axis stays, so the left keeps its room.
        responsive: [{
            breakpoint: 1024,
            options: {
                grid: { padding: { left: -8, right: -5, top: -8, bottom: 12 } },
            },
        }],
        legend: { show: false },
        tooltip: {
            theme: 'dark',
            shared: true,
            x: { formatter: (val) => new Date(val).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) },
            custom: (ctx) => valueSortedTooltip(ctx, { format: v => formatBps(v) }),
            y: [bpsFormatter, bpsFormatter, bpsFormatter, bpsFormatter],
        },
        noData: { text: 'Waiting for a reading...', style: { color: '#64748b', fontSize: '13px' } },
    };
}

// Time labels pinned to a fixed grid, as annotations so they scroll with the data. The grid
// interval comes from the rendered width, so a narrow panel steps out to a sparser grid.
function buildTimeTicks(minMs, maxMs) {
    const width = document.getElementById(elId)?.clientWidth || 800;
    const slots = Math.max(2, Math.floor(width / 64));
    const spanMs = maxMs - minMs;
    const GRIDS = [20000, 30000, 60000, 120000];
    let gridMs = GRIDS[GRIDS.length - 1];
    for (const g of GRIDS) {
        if (spanMs / g <= slots) { gridMs = g; break; }
    }
    const ticks = [];
    for (let t = Math.ceil(minMs / gridMs) * gridMs; t <= maxMs; t += gridMs) {
        const d = new Date(t);
        const text = [d.getHours(), d.getMinutes(), d.getSeconds()]
            .map(v => String(v).padStart(2, '0')).join(':');
        ticks.push({
            x: t,
            borderColor: 'transparent',
            label: {
                text,
                position: 'bottom',
                orientation: 'horizontal',
                offsetY: 19,
                borderColor: 'transparent',
                style: { background: 'transparent', fontSize: '10px', cssClass: 'wan-chart-tick-label' },
            },
        });
    }
    return ticks;
}

function toPoint(p) {
    return {
        time: new Date(p.time).getTime(),
        down: p.downloadBps ?? null,
        up: p.uploadBps ?? null,
        wanDown: p.wanDownloadBps ?? null,
        wanUp: p.wanUploadBps ?? null,
    };
}

function hasWan(p) { return p.wanDown != null || p.wanUp != null; }

// A WAN sample gap past this is a coverage break: the dashed line ends instead of bridging it.
const MAX_WAN_GAP_MS = 15000;

// One total series' value at an arbitrary instant, interpolated between its surrounding points.
function seriesAt(pts, t, key) {
    let before = null, after = null;
    for (const p of pts) {
        if (p[key] == null) continue;
        if (p.time <= t) before = p;
        else { after = p; break; }
    }
    if (before && after && after.time > before.time) {
        const f = (t - before.time) / (after.time - before.time);
        return before[key] + (after[key] - before[key]) * f;
    }
    return (before ?? after)?.[key] ?? null;
}

// The WAN pair is drawn area-conserved under the total, not as its own polyline: within each WAN
// window the measured bytes are redistributed along the total curve's shape (wan = total x scale,
// scale = window's WAN bytes / area under the total, capped at 1). The raw windows are coarser
// than the totals, so drawing them directly puts flat blocks through the totals' spikes - crossing
// a line WAN physically cannot cross (it rides the same port/radio). Render-time only: the
// buffers keep the raw measurements, and the area under the dashes stays the measured bytes.
function reshapeWanSeries(wanPts, pts, wanKey, totKey) {
    const src = wanPts.filter(p => p[wanKey] != null);
    const out = [];
    const pushGap = () => { if (out.length && out[out.length - 1].y != null) out.push({ x: out[out.length - 1].x + 1, y: null }); };
    for (let i = 0; i < src.length; i++) {
        const a = src[i], b = src[i + 1];
        if (!b || b.time - a.time <= 0 || b.time - a.time > MAX_WAN_GAP_MS) {
            // No window ahead: a lone sample still shows, clamped pointwise, then the line breaks.
            if (!out.length || out[out.length - 1].x < a.time) {
                const tot = seriesAt(pts, a.time, totKey);
                out.push({ x: a.time, y: tot != null ? Math.min(a[wanKey], tot) : a[wanKey] });
            }
            pushGap();
            continue;
        }
        const times = [a.time];
        for (const p of pts) if (p.time > a.time && p.time < b.time) times.push(p.time);
        times.push(b.time);
        const tot = times.map(t => seriesAt(pts, t, totKey) ?? 0);
        let area = 0;
        for (let k = 1; k < times.length; k++)
            area += (tot[k - 1] + tot[k]) / 2 * (times[k] - times[k - 1]);
        const avgTot = area / (b.time - a.time);
        const avgWan = (a[wanKey] + b[wanKey]) / 2;
        const scale = avgTot > 0 ? Math.min(1, avgWan / avgTot) : 0;
        for (let k = 0; k < times.length; k++) {
            // The segment's first point duplicates the previous segment's end; keep that one.
            if (k === 0 && out.length && out[out.length - 1].x >= times[0]) continue;
            // No totals under this window: nothing to conserve against, so draw the measured
            // rate flat (as the lone-sample branch does) rather than scaling real WAN to zero.
            out.push({ x: times[k], y: avgTot > 0 ? tot[k] * scale : avgWan });
        }
    }
    return out;
}

function updateChart() {
    if (!chart || (buffer.length === 0 && wanBuffer.length === 0)) return;
    if (tooltipShowing()) return;
    const now = Date.now();
    const pts = [...buffer];
    const last = pts[pts.length - 1];
    // Carry the newest reading out to the live edge, so the line reaches the right of the plot
    // and its end is what the device is doing now, not the last fold.
    const edge = liveEdge && (!last || liveEdge.time > last.time) ? liveEdge : last;
    if (edge && (!last || now - last.time > 1000)) pts.push({ time: now, down: edge.down, up: edge.up });
    const wanPts = [...wanBuffer];
    const wanLast = wanPts[wanPts.length - 1];
    // The WAN edge only stands in while fresh: coverage lapsing ends the dashed lines instead
    // of flat-lining a stale measurement.
    const wEdge = wanEdge && (!wanLast || wanEdge.time > wanLast.time) ? wanEdge : wanLast;
    if (wEdge && now - wEdge.time <= WAN_EDGE_MAX_MS && (!wanLast || now - wanLast.time > 1000))
        wanPts.push({ time: now, wanDown: wEdge.wanDown, wanUp: wEdge.wanUp });
    chart.updateOptions({
        xaxis: { min: now - HISTORY_MINUTES * 60000, max: now },
        annotations: { xaxis: buildTimeTicks(now - HISTORY_MINUTES * 60000, now) },
    }, false, false, false);
    chart.updateSeries([
        { name: 'Download',     data: pts.map(p => ({ x: p.time, y: p.down })) },
        { name: 'Upload',       data: pts.map(p => ({ x: p.time, y: p.up })) },
        { name: 'WAN Download', data: reshapeWanSeries(wanPts, pts, 'wanDown', 'down') },
        { name: 'WAN Upload',   data: reshapeWanSeries(wanPts, pts, 'wanUp', 'up') },
    ], false);
}

function trim() {
    const cutoff = Date.now() - HISTORY_MINUTES * 60000;
    buffer = buffer.filter(p => p.time >= cutoff);
    wanBuffer = wanBuffer.filter(p => p.time >= cutoff);
    for (const p of buffer) if (p.time > lastSampleTime) lastSampleTime = p.time;
    for (const p of wanBuffer) if (p.time > lastWanSampleTime) lastWanSampleTime = p.time;
}

/** Mounts into a container with the window's history:
 *  `{ points: [{time, downloadBps, uploadBps}], wanPoints: [{time, wanDownloadBps, wanUploadBps}] }`.
 *  False when the container is not in the DOM, so the caller can try again on its next render. */
export async function mount(containerId, opts) {
    dispose();
    const gen = ++mountGen;
    elId = containerId;
    const el = document.getElementById(containerId);
    if (!el) return false;
    buffer = (opts?.points || []).map(toPoint).sort((a, b) => a.time - b.time);
    wanBuffer = (opts?.wanPoints || []).map(toPoint).filter(hasWan).sort((a, b) => a.time - b.time);
    lastSampleTime = 0;
    lastWanSampleTime = 0;
    liveEdge = null;
    wanEdge = null;
    trim();
    mouseMoveHandler = (e) => { lastMouse = { x: e.clientX, y: e.clientY }; };
    mouseLeaveHandler = () => { lastMouse = null; };
    el.addEventListener('mousemove', mouseMoveHandler);
    el.addEventListener('mouseleave', mouseLeaveHandler);
    chart = new ApexCharts(el, buildOpts());
    await chart.render();
    if (gen !== mountGen) return true;
    updateChart();
    scrollTimer = setInterval(updateChart, SCROLL_MS);
    return true;
}

/** The newest reading, shown at the live edge at once; the folded point for it arrives via push. */
export function setLive(sample) {
    if (!chart) return;
    const p = toPoint(sample);
    if (liveEdge && !(p.time > liveEdge.time)) return;
    liveEdge = p;
    if (hasWan(p)) wanEdge = p;
    updateChart();
}

/** One live reading. Strictly newer than the last, so a repeated poll of the same sample is a no-op. */
export function push(sample) {
    if (!chart) return;
    const p = toPoint(sample);
    dbg('push', { at: new Date(p.time).toLocaleTimeString(), down: p.down, up: p.up, wanDown: p.wanDown, accepted: p.time > lastSampleTime });
    if (p.time > lastSampleTime) {
        buffer.push(p);
    }
    if (hasWan(p) && p.time > lastWanSampleTime) {
        wanBuffer.push(p);
    }
    trim();
    updateChart();
}

/** Re-pulled history merged under the live tail, so a gap that filled after the buffer scrolled past
 *  it appears; the newest live samples stay ahead of what the store has caught up to. */
export function setHistory(points, wanPoints) {
    if (!chart) return;
    const hist = (points || []).map(toPoint).sort((a, b) => a.time - b.time);
    const newest = hist.length ? hist[hist.length - 1].time : 0;
    dbg('history', { points: hist.length, wanPoints: (wanPoints || []).length, newest: newest ? new Date(newest).toLocaleTimeString() : null,
        liveTailKept: buffer.filter(p => p.time > newest).length, last3: hist.slice(-3) });
    buffer = hist.concat(buffer.filter(p => p.time > newest));
    const wanHist = (wanPoints || []).map(toPoint).filter(hasWan).sort((a, b) => a.time - b.time);
    const wanNewest = wanHist.length ? wanHist[wanHist.length - 1].time : 0;
    wanBuffer = wanHist.concat(wanBuffer.filter(p => p.time > wanNewest));
    trim();
    updateChart();
}

export function dispose() {
    if (scrollTimer) { clearInterval(scrollTimer); scrollTimer = null; }
    const el = elId ? document.getElementById(elId) : null;
    if (el && mouseMoveHandler) el.removeEventListener('mousemove', mouseMoveHandler);
    if (el && mouseLeaveHandler) el.removeEventListener('mouseleave', mouseLeaveHandler);
    mouseMoveHandler = null;
    mouseLeaveHandler = null;
    lastMouse = null;
    if (chart) { chart.destroy(); chart = null; }
    buffer = [];
    wanBuffer = [];
    lastSampleTime = 0;
    lastWanSampleTime = 0;
    liveEdge = null;
    wanEdge = null;
}
