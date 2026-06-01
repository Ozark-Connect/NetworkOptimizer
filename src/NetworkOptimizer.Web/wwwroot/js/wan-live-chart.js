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
            width: [2.5, 2.5, 1.5, 1.5],
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
                    offsetX: -4,
                },
                axisBorder: { show: false },
                axisTicks: { show: false },
            },
            { seriesName: 'Upload', show: false, min: 0 },
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
                labels: {
                    style: { colors: '#9ca3af', fontSize: '10px' },
                    formatter: v => v != null ? v.toFixed(0) + ' ms' : '',
                    offsetX: 4,
                },
                axisBorder: { show: false },
                axisTicks: { show: false },
            },
        ],
        grid: {
            borderColor: '#374151',
            strokeDashArray: 3,
            padding: { left: 4, right: 4, top: -8, bottom: 0 },
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

function updateChart() {
    if (!chart || buffer.length === 0) return;
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
    const qFrom = from.toISOString();
    const qTo = to.toISOString();

    const [wanResp, ispResp, transitResp] = await Promise.all([
        fetch(`/api/monitoring/wan-rate-chart?from=${qFrom}&to=${qTo}`, { credentials: 'same-origin' }).catch(() => null),
        fetch(`/api/monitoring/chart-data?category=AccessIsp&from=${qFrom}&to=${qTo}`, { credentials: 'same-origin' }).catch(() => null),
        fetch(`/api/monitoring/chart-data?category=Transit&from=${qFrom}&to=${qTo}`, { credentials: 'same-origin' }).catch(() => null),
    ]);

    const wan = wanResp?.ok ? await wanResp.json() : null;
    const isp = ispResp?.ok ? await ispResp.json() : null;
    const transit = transitResp?.ok ? await transitResp.json() : null;

    // Build time-indexed maps from WAN rate data
    const dlMap = new Map();
    const ulMap = new Map();
    if (wan) {
        for (const p of (wan.download || [])) {
            const t = new Date(p.time).getTime();
            dlMap.set(t, p.value || 0);
            if (!ulMap.has(t)) ulMap.set(t, 0);
        }
        for (const p of (wan.upload || [])) {
            const t = new Date(p.time).getTime();
            ulMap.set(t, p.value || 0);
            if (!dlMap.has(t)) dlMap.set(t, 0);
        }
    }

    // Compute ISP and Transit RTT/loss separately to avoid sawtooth from
    // interleaved timestamps. Then merge with LOCF (last observation carried
    // forward) so each output point has the combined mean.
    function meanTimeSeries(data) {
        if (!data?.targets?.length) return new Map();
        const byTime = new Map();
        for (const target of data.targets) {
            for (const p of (target.rtt || [])) {
                const t = new Date(p.time).getTime();
                if (p.value == null) continue;
                if (!byTime.has(t)) byTime.set(t, { rtts: [], losses: [] });
                byTime.get(t).rtts.push(p.value);
            }
            for (const p of (target.loss || [])) {
                const t = new Date(p.time).getTime();
                if (p.value == null) continue;
                if (!byTime.has(t)) byTime.set(t, { rtts: [], losses: [] });
                byTime.get(t).losses.push(p.value);
            }
        }
        const result = new Map();
        for (const [t, v] of byTime) {
            result.set(t, {
                rtt: v.rtts.length > 0 ? v.rtts.reduce((a, b) => a + b, 0) / v.rtts.length : null,
                loss: v.losses.length > 0 ? v.losses.reduce((a, b) => a + b, 0) / v.losses.length : null,
            });
        }
        return result;
    }

    const ispSeries = meanTimeSeries(isp);
    const transitSeries = meanTimeSeries(transit);

    // Use only WAN rate timestamps for the buffer (throughput is the primary
    // series); interpolate RTT/loss at each point using LOCF.
    const wanTimes = [...dlMap.keys()].sort((a, b) => a - b);

    const ispTimes = [...ispSeries.keys()].sort((a, b) => a - b);
    const transitTimes = [...transitSeries.keys()].sort((a, b) => a - b);

    function locfAt(sortedTimes, seriesMap, t) {
        let best = null;
        for (const st of sortedTimes) {
            if (st > t) break;
            best = seriesMap.get(st);
        }
        return best;
    }

    buffer = wanTimes.map(t => {
        const ispVal = locfAt(ispTimes, ispSeries, t);
        const transitVal = locfAt(transitTimes, transitSeries, t);

        let rtt = null;
        if (ispVal?.rtt != null && transitVal?.rtt != null) rtt = (ispVal.rtt + transitVal.rtt) / 2;
        else if (ispVal?.rtt != null) rtt = ispVal.rtt;
        else if (transitVal?.rtt != null) rtt = transitVal.rtt;

        let loss = null;
        if (ispVal?.loss != null && transitVal?.loss != null) loss = (ispVal.loss + transitVal.loss) / 2;
        else if (ispVal?.loss != null) loss = ispVal.loss;
        else if (transitVal?.loss != null) loss = transitVal.loss;

        return { time: t, download: dlMap.get(t) ?? null, upload: ulMap.get(t) ?? null, loss, rtt };
    });
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

export async function mount(containerId) {
    elId = containerId;
    const el = document.getElementById(containerId);
    if (!el) return;

    buffer = [];
    if (chart) { chart.destroy(); chart = null; }

    chart = new ApexCharts(el, buildOpts());
    await chart.render();

    await loadHistory();
    updateChart();

    pollTimer = setInterval(pollLive, POLL_MS);
}

export function unmount() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    if (chart) { chart.destroy(); chart = null; }
    buffer = [];
    elId = null;
}
