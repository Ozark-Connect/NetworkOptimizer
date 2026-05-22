import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';

const PALETTE = ['#2ba89a', '#3b82f6', '#a78bfa', '#ef5858', '#f59e0b', '#10b981'];
const POLL_INTERVALS = { 0: 5000, 1: 5000, 6: 10000, 24: 15000, 168: 30000, 720: 30000 };

let tempChart = null;
let cpuChart = null;
let memChart = null;
let pollTimer = null;
let currentRangeHours = 1;
let containerId = null;
let fetchController = null;
let deviceMeta = [];
let visibility = {};

function baseOpts(height, yTitle, yFormatter, extra) {
    return {
        chart: {
            type: 'line', height,
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
        tooltip: { theme: 'dark', shared: true, x: { format: 'MMM dd, HH:mm:ss' } },
        noData: { text: 'No data in this time range', style: { color: '#64748b' } },
        ...extra,
    };
}

function pctOpts(height, yTitle) {
    return baseOpts(height, yTitle, v => v != null ? v.toFixed(0) + '%' : '', {
        yaxis: {
            min: 0, max: 100,
            title: { text: yTitle, style: { color: '#9ca3af' } },
            labels: { style: { colors: '#9ca3af' }, formatter: v => v != null ? v.toFixed(0) + '%' : '' },
        },
    });
}

async function fetchData() {
    if (fetchController) fetchController.abort();
    fetchController = new AbortController();
    try {
        const resp = await fetch(
            `/api/monitoring/device-health-chart?rangeHours=${currentRangeHours}`,
            { signal: fetchController.signal });
        if (!resp.ok) return null;
        return await resp.json();
    } catch (e) {
        if (e.name === 'AbortError') return null;
        return null;
    }
}

function renderBadges(container) {
    const el = container.querySelector('.health-filter-badges');
    if (!el) return;
    if (deviceMeta.length <= 1) { el.innerHTML = ''; return; }
    el.innerHTML = deviceMeta.map(d => {
        const vis = visibility[d.mac] !== false;
        return `<button class="wan-filter-badge ${vis ? 'active' : 'inactive'}" data-mac="${d.mac}">
            <span class="wan-badge-dot" style="background-color: ${d.color}"></span>
            <span>${d.name}</span>
        </button>`;
    }).join('');
    el.querySelectorAll('button').forEach(btn => {
        btn.addEventListener('click', () => {
            const mac = btn.dataset.mac;
            const allVis = deviceMeta.every(d => visibility[d.mac] !== false);
            const onlyThis = visibility[mac] !== false
                && deviceMeta.filter(d => d.mac !== mac).every(d => visibility[d.mac] === false);
            if (onlyThis) { visibility = {}; }
            else if (allVis) { deviceMeta.forEach(d => visibility[d.mac] = d.mac === mac); }
            else { visibility[mac] = visibility[mac] === false; }
            updateVisibility();
            renderBadges(container);
        });
    });
}

function updateVisibility() {
    deviceMeta.forEach(d => {
        const vis = visibility[d.mac] !== false;
        for (const chart of [tempChart, cpuChart, memChart]) {
            if (!chart) continue;
            if (vis) chart.showSeries(d.name);
            else chart.hideSeries(d.name);
        }
    });
}

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data?.devices) return;

    deviceMeta = data.devices.map((d, i) => ({
        name: d.name, mac: d.mac, color: PALETTE[i % PALETTE.length],
    }));

    const makeSeries = (field) => data.devices.map((d, i) => ({
        name: d.name,
        color: PALETTE[i % PALETTE.length],
        data: (d.data || []).filter(p => p[field] != null).map(p => ({
            x: new Date(p.time).getTime(), y: p[field]
        })),
    }));

    if (tempChart) tempChart.updateSeries(makeSeries('temp'), false);
    if (cpuChart) cpuChart.updateSeries(makeSeries('cpu'), false);
    if (memChart) memChart.updateSeries(makeSeries('mem'), false);

    updateVisibility();
    const container = document.getElementById(containerId);
    if (container) renderBadges(container);
}

function startPoll() {
    stopPoll();
    pollTimer = setInterval(loadAndUpdate, POLL_INTERVALS[currentRangeHours] || 30000);
}

function stopPoll() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
}

export async function mount(elId) {
    containerId = elId;
    const container = document.getElementById(elId);
    if (!container) return;

    const tempEl = container.querySelector('.health-temp-chart');
    const cpuEl = container.querySelector('.health-cpu-chart');
    const memEl = container.querySelector('.health-mem-chart');
    if (!tempEl || !cpuEl || !memEl) return;

    if (tempChart) { tempChart.destroy(); tempChart = null; }
    if (cpuChart) { cpuChart.destroy(); cpuChart = null; }
    if (memChart) { memChart.destroy(); memChart = null; }

    tempChart = new ApexCharts(tempEl, { ...baseOpts(200, '°C', v => v != null ? v.toFixed(0) + ' °C' : ''), series: [], colors: PALETTE });
    cpuChart = new ApexCharts(cpuEl, { ...pctOpts(200, 'CPU %'), series: [], colors: PALETTE });
    memChart = new ApexCharts(memEl, { ...pctOpts(200, 'Memory %'), series: [], colors: PALETTE });

    await tempChart.render();
    await cpuChart.render();
    await memChart.render();

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
    if (tempChart) { tempChart.destroy(); tempChart = null; }
    if (cpuChart) { cpuChart.destroy(); cpuChart = null; }
    if (memChart) { memChart.destroy(); memChart = null; }
    containerId = null;
    deviceMeta = [];
    visibility = {};
}
