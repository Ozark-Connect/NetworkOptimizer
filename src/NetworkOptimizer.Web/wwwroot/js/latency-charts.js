// Latency & Packet Loss charts — pure JS ApexCharts, fed by /api/monitoring/chart-data.
// Mounted from Blazor the same way as lan-flow-map.js.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';

const PALETTE = ['#2ba89a', '#3b82f6', '#a78bfa', '#ef5858', '#f59e0b', '#10b981'];
const POLL_INTERVALS = { 0: 5000, 1: 5000, 6: 10000, 24: 15000, 168: 30000, 720: 30000 };

let rttChart = null;
let lossChart = null;
let pollTimer = null;
let currentCategory = 'Fabric';
let currentRangeHours = 1;
let visibility = {};
let targetMeta = [];
let containerId = null;
let fetchController = null;

function baseChartOpts(type, yTitle, yFormatter, extraOpts) {
    return {
        chart: {
            type: type,
            height: type === 'area' ? 200 : 260,
            background: 'transparent',
            toolbar: { show: false },
            zoom: { enabled: false },
            animations: { enabled: false },
        },
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
            title: { text: yTitle, style: { color: '#9ca3af' } },
            labels: {
                style: { colors: '#9ca3af' },
                formatter: yFormatter,
            },
        },
        grid: { borderColor: '#374151', strokeDashArray: 3 },
        legend: { show: false },
        tooltip: {
            theme: 'dark',
            shared: true,
            x: { format: 'MMM dd, HH:mm:ss' },
        },
        noData: { text: 'No data in this time range', style: { color: '#64748b' } },
        markers: { size: 0 },
        dataLabels: { enabled: false },
        ...extraOpts,
    };
}

function buildRttOpts() {
    return baseChartOpts('line', 'ms',
        v => v != null ? v.toFixed(1) : '');
}

function buildLossOpts() {
    return baseChartOpts('area', '% loss',
        v => v != null ? v.toFixed(1) + '%' : '',
        {
            yaxis: {
                min: 0, max: 100,
                title: { text: '% loss', style: { color: '#9ca3af' } },
                labels: {
                    style: { colors: '#9ca3af' },
                    formatter: v => v != null ? v.toFixed(1) + '%' : '',
                },
            },
            fill: {
                type: 'gradient',
                gradient: { shadeIntensity: 0.3, opacityFrom: 0.4, opacityTo: 0.05 },
            },
        });
}

async function fetchData() {
    if (fetchController) fetchController.abort();
    fetchController = new AbortController();
    try {
        const resp = await fetch(
            `/api/monitoring/chart-data?category=${currentCategory}&rangeHours=${currentRangeHours}`,
            { signal: fetchController.signal });
        if (!resp.ok) return null;
        return await resp.json();
    } catch (e) {
        if (e.name === 'AbortError') return null;
        return null;
    }
}

function renderBadges(container) {
    const el = container.querySelector('.latency-filter-badges');
    if (!el) return;
    if (targetMeta.length <= 1) { el.innerHTML = ''; return; }

    el.innerHTML = targetMeta.map(t => {
        const vis = visibility[t.id] !== false;
        return `<button class="wan-filter-badge ${vis ? 'active' : 'inactive'}" data-target="${t.id}">
            <span class="wan-badge-dot" style="background-color: ${t.color}"></span>
            <span>${t.name}</span>
        </button>`;
    }).join('');

    el.querySelectorAll('button').forEach(btn => {
        btn.addEventListener('click', () => {
            const tid = btn.dataset.target;
            const allVis = targetMeta.every(t => visibility[t.id] !== false);
            const onlyThis = visibility[tid] !== false
                && targetMeta.filter(t => t.id !== tid).every(t => visibility[t.id] === false);

            if (onlyThis) {
                visibility = {};
            } else if (allVis) {
                targetMeta.forEach(t => visibility[t.id] = t.id === tid);
            } else {
                visibility[tid] = visibility[tid] === false;
            }
            updateChartVisibility();
            renderBadges(container);
        });
    });
}

function updateChartVisibility() {
    if (!rttChart || !lossChart) return;
    targetMeta.forEach((t, i) => {
        const vis = visibility[t.id] !== false;
        if (vis) {
            rttChart.showSeries(t.name);
            lossChart.showSeries(t.name);
        } else {
            rttChart.hideSeries(t.name);
            lossChart.hideSeries(t.name);
        }
    });
}

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data || !data.targets) return;

    targetMeta = data.targets.map((t, i) => ({
        id: t.targetId,
        name: t.name,
        color: PALETTE[i % PALETTE.length],
    }));

    const rttSeries = data.targets.map((t, i) => ({
        name: t.name,
        color: PALETTE[i % PALETTE.length],
        data: (t.rtt || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value })),
    }));

    const lossSeries = data.targets.map((t, i) => ({
        name: t.name,
        color: PALETTE[i % PALETTE.length],
        data: (t.loss || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value })),
    }));

    if (rttChart) {
        rttChart.updateSeries(rttSeries, false);
    }
    if (lossChart) {
        lossChart.updateSeries(lossSeries, false);
    }

    updateChartVisibility();

    const container = document.getElementById(containerId);
    if (container) renderBadges(container);
}

function startPoll() {
    stopPoll();
    const interval = POLL_INTERVALS[currentRangeHours] || 30000;
    pollTimer = setInterval(loadAndUpdate, interval);
}

function stopPoll() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
}

export async function mount(elId) {
    containerId = elId;
    const container = document.getElementById(elId);
    if (!container) return;

    const rttEl = container.querySelector('.latency-rtt-chart');
    const lossEl = container.querySelector('.latency-loss-chart');
    if (!rttEl || !lossEl) return;

    if (rttChart) { rttChart.destroy(); rttChart = null; }
    if (lossChart) { lossChart.destroy(); lossChart = null; }

    rttChart = new ApexCharts(rttEl, {
        ...buildRttOpts(),
        series: [],
        colors: PALETTE,
    });
    lossChart = new ApexCharts(lossEl, {
        ...buildLossOpts(),
        series: [],
        colors: PALETTE,
    });

    await rttChart.render();
    await lossChart.render();

    container.querySelectorAll('[data-category]').forEach(btn => {
        btn.addEventListener('click', () => {
            currentCategory = btn.dataset.category;
            container.querySelectorAll('[data-category]').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            visibility = {};
            loadAndUpdate();
            startPoll();
        });
    });

    container.querySelectorAll('[data-range]').forEach(btn => {
        btn.addEventListener('click', () => {
            currentRangeHours = parseInt(btn.dataset.range);
            container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            loadAndUpdate();
            startPoll();
        });
    });

    await loadAndUpdate();
    startPoll();
}

export function unmount() {
    stopPoll();
    if (fetchController) { fetchController.abort(); fetchController = null; }
    if (rttChart) { rttChart.destroy(); rttChart = null; }
    if (lossChart) { lossChart.destroy(); lossChart = null; }
    containerId = null;
    targetMeta = [];
    visibility = {};
}
