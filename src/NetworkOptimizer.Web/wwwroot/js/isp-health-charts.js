// ISP Health per-ASN RTT chart - pure JS ApexCharts fed by
// /api/monitoring/isp-health/asn-series. Fixed 24 h window; congestion events
// render as shaded x-axis ranges, path shifts as annotation lines.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';

const PALETTE = ['#2ba89a', '#3b82f6', '#a78bfa', '#ef5858', '#f59e0b', '#10b981'];
const POLL_MS = 60000;

let chart = null;
let pollTimer = null;
let fetchController = null;
let resetBtn = null;
let isZoomed = false;
// null = default 48 h cached view; { from, to } ISO strings = a filter-selected window.
let win = null;

function buildOpts() {
    return {
        chart: {
            type: 'line',
            height: 280,
            background: 'transparent',
            toolbar: { show: false },
            zoom: { enabled: !matchMedia('(pointer:coarse)').matches, type: 'x', autoScaleYaxis: true, allowMouseWheelZoom: false },
            parentHeightOffset: 0,
            animations: { enabled: false },
            events: {
                zoomed: (ctx, opts) => setZoomed(opts?.xaxis?.min != null),
            },
        },
        series: [],
        stroke: { curve: 'smooth', width: 2 },
        markers: { size: 0 },
        dataLabels: { enabled: false },
        xaxis: {
            type: 'datetime',
            labels: {
                style: { colors: '#9ca3af' },
                datetimeUTC: false,
                datetimeFormatter: { hour: 'HH:mm', day: 'MMM dd' },
            },
        },
        yaxis: {
            min: 0,
            title: { text: 'ms', style: { color: '#9ca3af', fontSize: '9px' } },
            labels: {
                style: { colors: '#9ca3af', fontSize: '10px' },
                formatter: v => v != null ? v.toFixed(1) : '',
            },
            axisBorder: { show: false },
            axisTicks: { show: false },
        },
        grid: {
            borderColor: '#374151',
            strokeDashArray: 3,
            padding: { right: 6, top: -8, bottom: 0 },
        },
        responsive: [{
            breakpoint: 768,
            options: {
                yaxis: {
                    min: 0,
                    show: false,
                },
                grid: { padding: { left: -5, right: -5, top: -8, bottom: 0 } },
            },
        }],
        legend: { show: true, labels: { colors: '#9ca3af' } },
        tooltip: {
            theme: 'dark',
            shared: true,
            x: { format: 'MMM dd, HH:mm' },
        },
        noData: { text: 'No path data in the last 24 hours', style: { color: '#64748b' } },
    };
}

function buildAnnotations(events) {
    const xaxis = [];
    for (const e of events) {
        if (e.type === 'congestion') {
            xaxis.push({
                x: new Date(e.start).getTime(),
                x2: new Date(e.end).getTime(),
                fillColor: e.shared ? '#ef5858' : '#f59e0b',
                opacity: 0.12,
                label: {
                    text: e.label,
                    style: { color: '#ededef', background: e.shared ? '#7f1d1d' : '#78350f', fontSize: '10px' },
                },
            });
        } else {
            xaxis.push({
                x: new Date(e.start).getTime(),
                borderColor: '#4797ff',
                strokeDashArray: 4,
                label: {
                    text: e.label,
                    style: { color: '#ededef', background: '#1e3a5f', fontSize: '10px' },
                    orientation: 'horizontal',
                },
            });
        }
    }
    return { xaxis };
}

function setZoomed(zoomed) {
    isZoomed = zoomed;
    if (resetBtn) resetBtn.style.display = zoomed ? 'inline-flex' : 'none';
}

function resetZoom() {
    if (!chart) return;
    chart.updateOptions({ xaxis: { min: undefined, max: undefined } }, false, false);
    setZoomed(false);
    loadAndUpdate();
}

async function loadAndUpdate() {
    if (!chart) return;
    fetchController?.abort();
    fetchController = new AbortController();
    try {
        let url = '/api/monitoring/isp-health/asn-series';
        if (win) url += `?from=${encodeURIComponent(win.from)}&to=${encodeURIComponent(win.to)}`;
        const resp = await fetch(url, { credentials: 'same-origin', signal: fetchController.signal });
        if (!resp.ok) return;
        const json = await resp.json();

        const series = (json.asns || []).map((a, i) => ({
            name: a.name,
            color: PALETTE[i % PALETTE.length],
            data: (a.points || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value })),
        }));

        chart.updateOptions({ annotations: buildAnnotations(json.events || []) }, false, false);
        // Preserve the user's drag-zoom; a series refresh while zoomed would snap back
        if (!isZoomed) chart.updateSeries(series, false);
    } catch (e) {
        if (e.name !== 'AbortError') console.warn('isp-health chart load failed', e);
    }
}

export async function mount(elId, fromISO = null, toISO = null) {
    const el = document.getElementById(elId);
    if (!el) return;
    win = (fromISO && toISO) ? { from: fromISO, to: toISO } : null;

    resetBtn = document.createElement('button');
    resetBtn.type = 'button';
    resetBtn.className = 'btn btn-sm btn-secondary isp-chart-reset-btn';
    resetBtn.textContent = 'Reset zoom';
    resetBtn.style.display = 'none';
    resetBtn.addEventListener('click', resetZoom);
    el.parentElement.classList.add('isp-chart-wrap');
    el.parentElement.appendChild(resetBtn);

    chart = new ApexCharts(el, buildOpts());
    await chart.render();
    await loadAndUpdate();
    pollTimer = setInterval(loadAndUpdate, POLL_MS);
}

export async function reload() {
    await loadAndUpdate();
}

// Follow a filter-selected window (or null, null for the default 48 h view). Clears any
// drag-zoom and reloads, so the chart resets and refetches on every filter change.
export async function setWindow(fromISO, toISO) {
    win = (fromISO && toISO) ? { from: fromISO, to: toISO } : null;
    if (chart) chart.updateOptions({ xaxis: { min: undefined, max: undefined } }, false, false);
    setZoomed(false);
    await loadAndUpdate();
}

export function unmount() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    fetchController?.abort();
    if (resetBtn) { resetBtn.remove(); resetBtn = null; }
    isZoomed = false;
    win = null;
    if (chart) { chart.destroy(); chart = null; }
}
