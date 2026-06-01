// WAN live chart — real-time area+line chart fed by lan-flow-data.js pub/sub.
// Shows download, upload, packet loss, and mean ISP/transit RTT as a rolling
// ~5-minute window. Mounted from Blazor in LiveViewPanel and Monitoring Live tab.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import * as flowData from './lan-flow-data.js?v=1';

const MAX_POINTS = 100;
const COLOR_DL    = '#3b82f6';
const COLOR_UL    = '#10b981';
const COLOR_LOSS  = '#ef4444';
const COLOR_RTT   = '#d946ef';

let chart = null;
let unsub = null;
let wanLinkIds = [];
let ispCloudIds = [];
let transitCloudIds = [];
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
            sparkline: { enabled: false },
        },
        series: [
            { name: 'Download',  type: 'area', data: [] },
            { name: 'Upload',    type: 'area', data: [] },
            { name: 'Loss',      type: 'area', data: [] },
            { name: 'RTT',       type: 'line', data: [] },
        ],
        colors: [COLOR_DL, COLOR_UL, COLOR_LOSS, COLOR_RTT],
        stroke: {
            curve: 'smooth',
            width: [2, 2, 1.5, 2],
            dashArray: [0, 0, 0, 6],
        },
        fill: {
            type: ['gradient', 'gradient', 'gradient', 'none'],
            gradient: {
                shadeIntensity: 0.4,
                opacityFrom: [0.45, 0.35, 0.5, 0],
                opacityTo:   [0.05, 0.05, 0.05, 0],
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
                title: { text: undefined },
            },
            {
                seriesName: 'Upload',
                show: false,
                min: 0,
            },
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
                title: { text: undefined },
            },
        ],
        grid: {
            borderColor: '#374151',
            strokeDashArray: 3,
            padding: { left: 4, right: 4, top: -8, bottom: 0 },
            xaxis: { lines: { show: false } },
        },
        legend: {
            show: true,
            position: 'top',
            horizontalAlign: 'center',
            floating: true,
            offsetY: -4,
            fontSize: '11px',
            labels: { colors: '#9ca3af' },
            markers: { size: 4, offsetX: -2 },
            itemMargin: { horizontal: 8 },
            customLegendItems: ['Download', 'Upload', 'Loss', 'RTT'],
        },
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
        noData: { text: 'Waiting for data...', style: { color: '#64748b', fontSize: '13px' } },
    };
}

function indexTopology() {
    const snap = flowData.getSnapshot();
    if (!snap) return;
    wanLinkIds = (snap.links || []).filter(l => l.kind === 3).map(l => l.id);
    ispCloudIds = (snap.clouds || []).filter(c => c.kind === 0).map(c => c.id);
    transitCloudIds = (snap.clouds || []).filter(c => c.kind === 1).map(c => c.id);
}

function sampleNow() {
    const rates = flowData.getLiveRates();
    const clouds = flowData.getCloudStats();

    let dlBps = 0, ulBps = 0;
    for (const id of wanLinkIds) {
        const r = rates[id];
        if (!r) continue;
        dlBps += r.downstreamBps || 0;
        ulBps += r.upstreamBps || 0;
    }

    const rttVals = [];
    const lossVals = [];
    for (const id of [...ispCloudIds, ...transitCloudIds]) {
        const c = clouds[id];
        if (!c) continue;
        if (c.rttAvgMs != null) rttVals.push(c.rttAvgMs);
        if (c.lossPercent != null) lossVals.push(c.lossPercent);
    }

    const meanRtt = rttVals.length > 0 ? rttVals.reduce((a, b) => a + b, 0) / rttVals.length : null;
    const meanLoss = lossVals.length > 0 ? lossVals.reduce((a, b) => a + b, 0) / lossVals.length : null;

    return {
        time: Date.now(),
        download: dlBps,
        upload: ulBps,
        loss: meanLoss,
        rtt: meanRtt,
    };
}

function pushAndUpdate() {
    if (!wanLinkIds.length && !ispCloudIds.length && !transitCloudIds.length) return;

    const pt = sampleNow();
    buffer.push(pt);
    if (buffer.length > MAX_POINTS) buffer = buffer.slice(-MAX_POINTS);

    if (!chart) return;

    chart.updateSeries([
        { name: 'Download', data: buffer.map(p => ({ x: p.time, y: p.download })) },
        { name: 'Upload',   data: buffer.map(p => ({ x: p.time, y: p.upload })) },
        { name: 'Loss',     data: buffer.map(p => ({ x: p.time, y: p.loss })) },
        { name: 'RTT',      data: buffer.map(p => ({ x: p.time, y: p.rtt })) },
    ], false);
}

function onFlowEvent(event) {
    if (event === 'snapshot') {
        indexTopology();
        pushAndUpdate();
    } else if (event === 'live') {
        pushAndUpdate();
    }
}

export async function mount(containerId) {
    elId = containerId;
    const el = document.getElementById(containerId);
    if (!el) return;

    buffer = [];
    wanLinkIds = [];
    ispCloudIds = [];
    transitCloudIds = [];

    if (chart) { chart.destroy(); chart = null; }

    chart = new ApexCharts(el, buildOpts());
    await chart.render();

    indexTopology();
    if (wanLinkIds.length || ispCloudIds.length || transitCloudIds.length) {
        pushAndUpdate();
    }

    unsub = flowData.subscribe(onFlowEvent);
}

export function unmount() {
    if (unsub) { unsub(); unsub = null; }
    if (chart) { chart.destroy(); chart = null; }
    buffer = [];
    wanLinkIds = [];
    ispCloudIds = [];
    transitCloudIds = [];
    elId = null;
}
