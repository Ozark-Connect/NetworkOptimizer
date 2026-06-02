// WAN live chart — real-time area+line chart showing download, upload, packet
// loss, and mean ISP/transit RTT. Pre-loads history from InfluxDB, then polls
// /api/monitoring/live-stats for real-time updates.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';

const HISTORY_MINUTES = 5;
const POLL_MS = 3000;
const COLOR_DL   = '#3b82f6';
const COLOR_UL   = '#10b981';
const COLOR_LOSS = '#ef4444';
const COLOR_RTT  = '#d946ef';

let chart = null;
let pollTimer = null;
let buffer = [];
let elId = null;
let mountGen = 0;

function formatBps(v) {
    if (v == null || v < 1) return '0';
    if (v >= 1e9) return (v / 1e9).toFixed(1) + ' Gbps';
    if (v >= 1e6) return (v / 1e6).toFixed(1) + ' Mbps';
    if (v >= 1e3) return (v / 1e3).toFixed(0) + ' Kbps';
    return v.toFixed(0) + ' bps';
}

function buildOpts() {
    return {
        chart: {
            type: 'area',
            height: 175,
            background: 'transparent',
            toolbar: { show: false },
            zoom: { enabled: false },
            animations: { enabled: true, easing: 'smooth', dynamicAnimation: { speed: 800 } },
        },
        series: [
            { name: 'Download', type: 'area', data: [] },
            { name: 'Upload',   type: 'area', data: [] },
            { name: 'Loss',     type: 'area', data: [] },
            { name: 'RTT',      type: 'line', data: [] },
        ],
        colors: [COLOR_DL, COLOR_UL, COLOR_LOSS, COLOR_RTT],
        stroke: {
            curve: 'smooth',
            width: [2, 2, 1, 1],
            dashArray: [0, 0, 0, 6],
        },
        fill: {
            type: ['gradient', 'gradient', 'gradient', 'solid'],
            opacity: [1, 1, 1, 0],
            gradient: {
                shadeIntensity: 0.4,
                opacityFrom: [0.55, 0.45, 0.5, 0],
                opacityTo:   [0.1,  0.08, 0.05, 0],
                stops: [0, 95],
            },
        },
        markers: { size: 0 },
        dataLabels: { enabled: false },
        xaxis: {
            type: 'datetime',
            min: Date.now() - HISTORY_MINUTES * 60000,
            max: Date.now(),
            labels: {
                show: true,
                style: { colors: '#64748b', fontSize: '10px' },
                datetimeUTC: false,
                datetimeFormatter: { hour: 'HH:mm', minute: 'HH:mm:ss' },
            },
            axisBorder: { show: false },
            axisTicks: { show: false },
        },
        yaxis: [
            {
                seriesName: 'Download',
                min: 0,
                labels: {
                    style: { colors: '#9ca3af', fontSize: '10px' },
                    formatter: v => formatBps(v),
                    offsetX: -10,
                },
                axisBorder: { show: false },
                axisTicks: { show: false },
            },
            { seriesName: 'Download', show: false, min: 0 },
            {
                seriesName: 'Loss',
                opposite: true,
                show: false,
                min: 0,
                max: v => Math.max(v * 1.2, 2),
            },
            {
                seriesName: 'RTT',
                opposite: true,
                min: 0,
                max: 10,
                labels: {
                    style: { colors: '#9ca3af', fontSize: '10px' },
                    formatter: v => v != null ? v.toFixed(0) : '',
                    maxWidth: 30,
                },
                title: { text: 'ms', style: { color: '#64748b', fontSize: '9px' }, offsetX: -4 },
                axisBorder: { show: false },
                axisTicks: { show: false },
            },
        ],
        grid: {
            borderColor: '#374151',
            strokeDashArray: 3,
            padding: { left: 3, right: 0, top: -8, bottom: 0 },
            xaxis: { lines: { show: false } },
        },
        legend: { show: false },
        tooltip: {
            theme: 'dark',
            shared: true,
            x: { format: 'HH:mm:ss' },
            y: [
                { formatter: v => formatBps(v) },
                { formatter: v => formatBps(v) },
                { formatter: v => v != null ? v.toFixed(2) + '%' : '-' },
                { formatter: v => v != null ? v.toFixed(1) + ' ms' : '-' },
            ],
        },
        noData: { text: 'Loading...', style: { color: '#64748b', fontSize: '13px' } },
    };
}

function rttYMax() {
    const rtts = buffer.map(p => p.rtt).filter(v => v != null && v > 0).sort((a, b) => a - b);
    if (rtts.length === 0) return 10;
    const p95 = rtts[Math.floor(rtts.length * 0.95)];
    return Math.ceil((p95 * 1.5) / 10) * 10;
}

function updateChart() {
    if (!chart || buffer.length === 0) return;
    const now = Date.now();
    chart.updateOptions({
        xaxis: { min: now - HISTORY_MINUTES * 60000, max: now },
        yaxis: [chart.opts.yaxis[0], chart.opts.yaxis[1], chart.opts.yaxis[2], { ...chart.opts.yaxis[3], max: rttYMax() }],
    }, false, false, false);
    chart.updateSeries([
        { name: 'Download', data: buffer.map(p => ({ x: p.time, y: p.download })) },
        { name: 'Upload',   data: buffer.map(p => ({ x: p.time, y: p.upload })) },
        { name: 'Loss',     data: buffer.map(p => ({ x: p.time, y: p.loss })) },
        { name: 'RTT',      data: buffer.map(p => ({ x: p.time, y: p.rtt })) },
    ], false);
}

async function loadHistory() {
    const to = new Date();
    const from = new Date(to.getTime() - HISTORY_MINUTES * 60000);
    try {
        const resp = await fetch(
            `/api/monitoring/wan-live-chart-data?from=${from.toISOString()}&to=${to.toISOString()}`,
            { credentials: 'same-origin' });
        if (!resp.ok) return;
        const data = await resp.json();
        buffer = (data.points || []).map(p => ({
            time: new Date(p.time).getTime(),
            download: p.downloadBps,
            upload: p.uploadBps,
            rtt: p.rttMs,
            loss: p.lossPercent,
        }));
    } catch { }
}

async function pollLive() {
    try {
        const resp = await fetch('/api/monitoring/live-stats', { credentials: 'same-origin' });
        if (!resp.ok) return;
        const d = await resp.json();
        const cutoff = Date.now() - HISTORY_MINUTES * 60000;
        buffer.push({
            time: Date.now(),
            download: d.downloadBps,
            upload: d.uploadBps,
            loss: d.lossPercent,
            rtt: d.rttMs,
        });
        buffer = buffer.filter(p => p.time >= cutoff);
        updateChart();
    } catch { }
}

export async function mount(containerId, opts) {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    if (chart) { chart.destroy(); chart = null; }
    buffer = [];
    const gen = ++mountGen;
    elId = containerId;
    const el = document.getElementById(containerId);
    if (!el) return;

    chart = new ApexCharts(el, buildOpts());
    await chart.render();
    if (gen !== mountGen) return;

    await loadHistory();
    if (gen !== mountGen) return;
    await pollLive();
    if (gen !== mountGen) return;
    updateChart();
    const interval = opts?.pollMs || POLL_MS;

    pollTimer = setInterval(pollLive, interval);
}

export function pause() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
}

export function resume() {
    if (!chart || pollTimer) return;
    pollTimer = setInterval(pollLive, POLL_MS);
}

export async function seekTime(isoTimestamp) {
    if (!chart) return;
    if (!isoTimestamp) {
        // Return to live mode - clear playhead annotation
        chart.clearAnnotations();
        if (pollTimer) return; // already live
        buffer = [];
        await loadHistory();
        updateChart();
        pollTimer = setInterval(pollLive, POLL_MS);
        return;
    }
    // Historic mode: stop polling, fetch window centered on timestamp
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    const at = new Date(isoTimestamp).getTime();
    const halfWindow = HISTORY_MINUTES * 60000 / 2;
    const from = new Date(at - halfWindow);
    const to = new Date(at + halfWindow);
    try {
        const resp = await fetch(
            `/api/monitoring/wan-live-chart-data?from=${from.toISOString()}&to=${to.toISOString()}`,
            { credentials: 'same-origin' });
        if (!resp.ok) return;
        const data = await resp.json();
        buffer = (data.points || []).map(p => ({
            time: new Date(p.time).getTime(),
            download: p.downloadBps,
            upload: p.uploadBps,
            rtt: p.rttMs,
            loss: p.lossPercent,
        }));
    } catch { return; }
    if (buffer.length === 0) return;
    const maxTime = Math.min(at + halfWindow, Date.now());
    chart.updateOptions({
        xaxis: { min: maxTime - HISTORY_MINUTES * 60000, max: maxTime },
        yaxis: [chart.opts.yaxis[0], chart.opts.yaxis[1], chart.opts.yaxis[2], { ...chart.opts.yaxis[3], max: rttYMax() }],
    }, false, false, false);
    chart.updateSeries([
        { name: 'Download', data: buffer.map(p => ({ x: p.time, y: p.download })) },
        { name: 'Upload',   data: buffer.map(p => ({ x: p.time, y: p.upload })) },
        { name: 'Loss',     data: buffer.map(p => ({ x: p.time, y: p.loss })) },
        { name: 'RTT',      data: buffer.map(p => ({ x: p.time, y: p.rtt })) },
    ], false);

    chart.clearAnnotations();
    chart.addXaxisAnnotation({
        x: at,
        borderColor: '#f1f5f9',
        strokeDashArray: 3,
        opacity: 0.5,
        label: {
            text: new Date(at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }),
            borderColor: 'transparent',
            style: { background: 'transparent', color: '#f1f5f9', fontSize: '9px' },
            position: 'front',
            orientation: 'horizontal',
            offsetY: -5,
        }
    });
}

export function unmount() {
    mountGen++;
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    if (chart) { chart.destroy(); chart = null; }
    buffer = [];
    elId = null;
}
