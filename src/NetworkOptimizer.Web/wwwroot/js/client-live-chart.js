// Client live throughput chart: a five-minute sliding window of one client's From Device and To
// Device rates, in the WAN live chart's shape. A renderer only - the Client Performance page owns
// the client and already polls it, so history and every live sample arrive by push.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { valueSortedTooltip } from './chart-tooltip.js?v=15';
import { downloadColor, uploadColor } from './chart-colors.js?v=2';

const HISTORY_MINUTES = 5;
// Window-scroll redraw cadence: each step moves well under a pixel, so the chart slides.
const SCROLL_MS = 250;
// The page's convention, as its speed test hero reads: From Device is what the client sends and
// takes the download color, To Device is what it receives and takes the upload color.
const COLOR_FROM = downloadColor();
const COLOR_TO = uploadColor();

let chart = null;
let scrollTimer = null;
let buffer = [];
let elId = null;
let mountGen = 0;
let lastSampleTime = 0;
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
        series: [
            { name: 'From Device', type: 'area', data: [] },
            { name: 'To Device',   type: 'area', data: [] },
        ],
        colors: [COLOR_FROM, COLOR_TO],
        // Stepped: a reading holds until the next one, which is 5 to 30 s away depending on the
        // source, and a smooth curve between two readings would draw rates nobody measured.
        stroke: { curve: 'stepline', width: 2 },
        fill: {
            type: 'gradient',
            gradient: { shadeIntensity: 0.4, opacityFrom: 0.5, opacityTo: 0.08, stops: [0, 95] },
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
        legend: { show: false },
        tooltip: {
            theme: 'dark',
            shared: true,
            x: { formatter: (val) => new Date(val).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) },
            custom: (ctx) => valueSortedTooltip(ctx, { format: v => formatBps(v) }),
            y: [{ formatter: v => formatBps(v) }, { formatter: v => formatBps(v) }],
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

// Client terms in, chart terms out: From Device is the client's upload.
function toPoint(p) {
    return { time: new Date(p.time).getTime(), from: p.uploadBps ?? null, to: p.downloadBps ?? null };
}

function updateChart() {
    if (!chart || buffer.length === 0) return;
    if (tooltipShowing()) return;
    const now = Date.now();
    const pts = [...buffer];
    const last = pts[pts.length - 1];
    // Hold the last reading out to the live edge, so the line reaches the right of the plot.
    if (last && now - last.time > 1000) pts.push({ time: now, from: last.from, to: last.to });
    chart.updateOptions({
        xaxis: { min: now - HISTORY_MINUTES * 60000, max: now },
        annotations: { xaxis: buildTimeTicks(now - HISTORY_MINUTES * 60000, now) },
    }, false, false, false);
    chart.updateSeries([
        { name: 'From Device', data: pts.map(p => ({ x: p.time, y: p.from })) },
        { name: 'To Device',   data: pts.map(p => ({ x: p.time, y: p.to })) },
    ], false);
}

function trim() {
    const cutoff = Date.now() - HISTORY_MINUTES * 60000;
    buffer = buffer.filter(p => p.time >= cutoff);
    for (const p of buffer) if (p.time > lastSampleTime) lastSampleTime = p.time;
}

/** Mounts into a container with the window's history: `{ points: [{time, downloadBps, uploadBps}] }`. */
export async function mount(containerId, opts) {
    dispose();
    const gen = ++mountGen;
    elId = containerId;
    const el = document.getElementById(containerId);
    if (!el) return;
    buffer = (opts?.points || []).map(toPoint).sort((a, b) => a.time - b.time);
    lastSampleTime = 0;
    trim();
    mouseMoveHandler = (e) => { lastMouse = { x: e.clientX, y: e.clientY }; };
    mouseLeaveHandler = () => { lastMouse = null; };
    el.addEventListener('mousemove', mouseMoveHandler);
    el.addEventListener('mouseleave', mouseLeaveHandler);
    chart = new ApexCharts(el, buildOpts());
    await chart.render();
    if (gen !== mountGen) return;
    updateChart();
    scrollTimer = setInterval(updateChart, SCROLL_MS);
}

/** One live reading. Strictly newer than the last, so a repeated poll of the same sample is a no-op. */
export function push(sample) {
    if (!chart) return;
    const p = toPoint(sample);
    if (!(p.time > lastSampleTime)) return;
    buffer.push(p);
    trim();
    updateChart();
}

/** Re-pulled history merged under the live tail, so a gap that filled after the buffer scrolled past
 *  it appears; the newest live samples stay ahead of what the store has caught up to. */
export function setHistory(points) {
    if (!chart) return;
    const hist = (points || []).map(toPoint).sort((a, b) => a.time - b.time);
    const newest = hist.length ? hist[hist.length - 1].time : 0;
    buffer = hist.concat(buffer.filter(p => p.time > newest));
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
    lastSampleTime = 0;
}
