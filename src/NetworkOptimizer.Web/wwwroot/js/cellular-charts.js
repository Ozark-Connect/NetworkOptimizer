// Cellular modem signal time-series charts: RSRP, SNR, Signal Quality.
// Same control pattern as sfp-charts.js and device-health-charts.js.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';

const PALETTE = ['#2ba89a', '#3b82f6', '#a78bfa', '#ef5858', '#f59e0b', '#10b981'];
const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }
const POLL_INTERVALS = { 0: 10000, 1: 10000, 6: 15000, 24: 30000, 168: 60000, 720: 60000 };
const RANGE_MS = { 0: 15*60000, 1: 3600000, 6: 6*3600000, 24: 86400000, 168: 7*86400000, 720: 30*86400000 };

let rsrpChart = null;
let rsrqChart = null;
let snrChart = null;
let qualityChart = null;
let pollTimer = null;
let currentRangeHours = 24;
let windowOffset = 0;
let isCustomRange = false;
let customFrom = null;
let customTo = null;
let containerId = null;
let fetchController = null;
let modemMeta = [];
let visibility = {};
let visibilityObserver = null;
let isInViewport = true;

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
            title: { text: yTitle, style: { color: '#9ca3af' } },
            labels: { style: { colors: '#9ca3af' }, formatter: yFormatter },
        },
        grid: { borderColor: '#374151', strokeDashArray: 3 },
        legend: { show: false },
        tooltip: { theme: 'dark', shared: true, x: { format: 'MMM dd, HH:mm:ss' } },
        noData: { text: 'No data in this time range', style: { color: '#64748b' } },
        ...extra,
    };
}

function buildQueryParams() {
    let params = '';
    if (isCustomRange && customFrom && customTo) {
        params = `from=${customFrom.toISOString()}&to=${customTo.toISOString()}`;
    } else if (windowOffset !== 0) {
        const now = Date.now();
        const rangeMs = RANGE_MS[currentRangeHours] || 3600000;
        const to = new Date(now + windowOffset);
        const from = new Date(to.getTime() - rangeMs);
        params = `from=${from.toISOString()}&to=${to.toISOString()}`;
    } else {
        params = `rangeHours=${currentRangeHours}`;
    }
    return params;
}

async function fetchData() {
    if (fetchController) fetchController.abort();
    fetchController = new AbortController();
    try {
        const resp = await fetch(`/api/monitoring/cellular-chart?${buildQueryParams()}`,
            { signal: fetchController.signal });
        if (!resp.ok) return null;
        return await resp.json();
    } catch (e) {
        if (e.name === 'AbortError') return null;
        return null;
    }
}

function renderBadges(container) {
    const el = container.querySelector('.cellular-filter-badges');
    if (!el) return;
    if (modemMeta.length <= 1) { el.innerHTML = ''; return; }
    el.innerHTML = modemMeta.map(m => {
        const vis = visibility[m.id] !== false;
        return `<button class="wan-filter-badge ${vis ? 'active' : 'inactive'}" data-modem="${m.id}">
            <span class="wan-badge-dot" style="background-color: ${m.color}"></span>
            <span>${escapeHtml(m.label)}</span>
        </button>`;
    }).join('');
    if (!el._delegated) {
        el._delegated = true;
        el.addEventListener('click', (e) => {
            const btn = e.target.closest('button[data-modem]');
            if (!btn) return;
            const id = btn.dataset.modem;
            if (e.ctrlKey || e.metaKey) {
                visibility[id] = visibility[id] === false ? undefined : false;
            } else {
                const allVis = modemMeta.every(m => visibility[m.id] !== false);
                const onlyThis = visibility[id] !== false
                    && modemMeta.filter(m => m.id !== id).every(m => visibility[m.id] === false);
                if (onlyThis) { visibility = {}; }
                else if (allVis) { modemMeta.forEach(m => visibility[m.id] = m.id === id); }
                else { visibility[id] = visibility[id] === false; }
            }
            updateVisibility();
            renderBadges(container);
        });
    }
}

function updateVisibility() {
    modemMeta.forEach(m => {
        const vis = visibility[m.id] !== false;
        [rsrpChart, rsrqChart, snrChart, qualityChart].forEach(chart => {
            if (!chart) return;
            if (vis) chart.showSeries(m.label);
            else chart.hideSeries(m.label);
        });
    });
}

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data?.modems) return;
    modemMeta = data.modems.map((m, i) => ({
        id: m.id, label: m.label, color: PALETTE[i % PALETTE.length],
    }));

    const rsrpSeries = [];
    const rsrqSeries = [];
    const snrSeries = [];
    const qualitySeries = [];
    data.modems.forEach((m, i) => {
        const color = PALETTE[i % PALETTE.length];
        const pts = m.data || [];
        rsrpSeries.push({
            name: m.label,
            color,
            data: pts.filter(p => p.rsrp != null).map(p => ({ x: new Date(p.time).getTime(), y: p.rsrp })),
        });
        rsrqSeries.push({
            name: m.label,
            color,
            data: pts.filter(p => p.rsrq != null).map(p => ({ x: new Date(p.time).getTime(), y: p.rsrq })),
        });
        snrSeries.push({
            name: m.label,
            color,
            data: pts.filter(p => p.snr != null).map(p => ({ x: new Date(p.time).getTime(), y: p.snr })),
        });
        qualitySeries.push({
            name: m.label,
            color,
            data: pts.filter(p => p.quality != null).map(p => ({ x: new Date(p.time).getTime(), y: p.quality })),
        });
    });

    if (rsrpChart) rsrpChart.updateSeries(rsrpSeries, false);
    if (rsrqChart) rsrqChart.updateSeries(rsrqSeries, false);
    if (snrChart) snrChart.updateSeries(snrSeries, false);
    if (qualityChart) qualityChart.updateSeries(qualitySeries, false);

    updateVisibility();
    const container = document.getElementById(containerId);
    if (container) renderBadges(container);
}

function isVisible() { return isInViewport; }

function startPoll() {
    stopPoll();
    if (windowOffset !== 0 || isCustomRange) return;
    if (!isVisible()) return;
    pollTimer = setInterval(loadAndUpdate, POLL_INTERVALS[currentRangeHours] || 60000);
}
function stopPoll() { if (pollTimer) { clearInterval(pollTimer); pollTimer = null; } }

function toLocalDatetimeString(d) {
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function getEffectiveFrom() {
    if (isCustomRange && customFrom) return customFrom;
    if (windowOffset !== 0) return new Date(Date.now() + windowOffset - (RANGE_MS[currentRangeHours] || 3600000));
    return null;
}
function getEffectiveTo() {
    if (isCustomRange && customTo) return customTo;
    if (windowOffset !== 0) return new Date(Date.now() + windowOffset);
    return null;
}

function updateCustomLabel(container) {
    const btn = container.querySelector('.custom-range-btn');
    if (!btn) return;
    let clearBtn = btn.querySelector('.custom-range-clear');
    const label = btn.querySelector('.custom-range-label');
    if (label) label.remove();
    const active = isCustomRange || windowOffset !== 0;
    if (active) {
        btn.classList.add('active');
        const from = getEffectiveFrom(), to = getEffectiveTo();
        if (from && to) {
            const fmt = d => d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
            btn.setAttribute('data-tooltip', `${fmt(from)} - ${fmt(to)}`);
        }
        if (!clearBtn) {
            clearBtn = document.createElement('span');
            clearBtn.className = 'custom-range-clear';
            clearBtn.textContent = '×';
            clearBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                selectPresetRange(container, currentRangeHours);
            });
            btn.appendChild(clearBtn);
        }
    } else {
        btn.classList.remove('active');
        btn.setAttribute('data-tooltip', 'Custom date range');
        if (clearBtn) clearBtn.remove();
    }
}

function selectPresetRange(container, hours) {
    currentRangeHours = hours;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
    const btn = container.querySelector(`[data-range="${hours}"]`);
    if (btn) btn.classList.add('active');
    const fromInput = container.querySelector('[data-input="from"]');
    const toInput = container.querySelector('[data-input="to"]');
    if (fromInput && toInput) {
        const now = Date.now();
        const rangeMs = RANGE_MS[hours] || 86400000;
        fromInput.value = toLocalDatetimeString(new Date(now - rangeMs));
        toInput.value = toLocalDatetimeString(new Date(now));
    }
    container.querySelector('[data-popover="custom-range"]')?.classList.remove('open');
    updateCustomLabel(container);
    loadAndUpdate();
    startPoll();
}

function shiftWindow(container, direction) {
    const rangeMs = isCustomRange && customFrom && customTo
        ? customTo.getTime() - customFrom.getTime()
        : RANGE_MS[currentRangeHours] || 3600000;
    const shiftMs = rangeMs * 0.5;
    if (isCustomRange && customFrom && customTo) {
        const delta = direction === 'back' ? -shiftMs : shiftMs;
        customFrom = new Date(customFrom.getTime() + delta);
        customTo = new Date(customTo.getTime() + delta);
    } else {
        windowOffset += direction === 'back' ? -shiftMs : shiftMs;
        if (windowOffset > 0) windowOffset = 0;
    }
    const fromInput = container.querySelector('[data-input="from"]');
    const toInput = container.querySelector('[data-input="to"]');
    const ef = getEffectiveFrom(), et = getEffectiveTo();
    if (fromInput && ef) fromInput.value = toLocalDatetimeString(ef);
    if (toInput && et) toInput.value = toLocalDatetimeString(et);
    updateCustomLabel(container);
    loadAndUpdate();
    startPoll();
}

export async function mount(elId) {
    // Reset all state in case unmount didn't complete (Blazor Dispose race)
    stopPoll();
    currentRangeHours = 24;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    modemMeta = [];
    visibility = {};
    containerId = elId;
    const container = document.getElementById(elId);
    if (!container) return;

    const rsrpEl = container.querySelector('.cellular-rsrp-chart');
    const rsrqEl = container.querySelector('.cellular-rsrq-chart');
    const snrEl = container.querySelector('.cellular-snr-chart');
    const qualityEl = container.querySelector('.cellular-quality-chart');
    if (!rsrpEl || !rsrqEl || !snrEl || !qualityEl) return;

    if (rsrpChart) { rsrpChart.destroy(); rsrpChart = null; }
    if (rsrqChart) { rsrqChart.destroy(); rsrqChart = null; }
    if (snrChart) { snrChart.destroy(); snrChart = null; }
    if (qualityChart) { qualityChart.destroy(); qualityChart = null; }

    rsrpChart = new ApexCharts(rsrpEl, {
        ...baseOpts(200, 'dBm', v => v != null ? v.toFixed(0) + ' dBm' : ''),
        series: [], colors: PALETTE,
    });
    rsrqChart = new ApexCharts(rsrqEl, {
        ...baseOpts(160, 'dB', v => v != null ? v.toFixed(1) + ' dB' : ''),
        series: [], colors: PALETTE,
    });
    snrChart = new ApexCharts(snrEl, {
        ...baseOpts(160, 'dB', v => v != null ? v.toFixed(1) + ' dB' : ''),
        series: [], colors: PALETTE,
    });
    qualityChart = new ApexCharts(qualityEl, {
        ...baseOpts(160, '%', v => v != null ? v.toFixed(0) + '%' : '', { yaxis: {
            title: { text: '%', style: { color: '#9ca3af' } },
            labels: { style: { colors: '#9ca3af' }, formatter: v => v != null ? v.toFixed(0) + '%' : '' },
            min: 0, max: 100,
        }}),
        series: [], colors: PALETTE,
    });

    await rsrpChart.render();
    await rsrqChart.render();
    await snrChart.render();
    await qualityChart.render();

    container.querySelectorAll('[data-range]').forEach(btn => {
        btn.addEventListener('click', () => selectPresetRange(container, parseInt(btn.dataset.range)));
    });

    container.querySelectorAll('[data-shift]').forEach(btn => {
        btn.addEventListener('click', () => shiftWindow(container, btn.dataset.shift));
    });

    const popover = container.querySelector('[data-popover="custom-range"]');
    const fromInput = container.querySelector('[data-input="from"]');
    const toInput = container.querySelector('[data-input="to"]');

    container.querySelector('[data-action="custom-range"]')?.addEventListener('click', () => {
        const now = new Date();
        const rangeMs = RANGE_MS[currentRangeHours] || 3600000;
        if (!fromInput.value) fromInput.value = toLocalDatetimeString(new Date(now.getTime() - rangeMs));
        if (!toInput.value) toInput.value = toLocalDatetimeString(now);
        popover?.classList.toggle('open');
    });

    container.querySelector('[data-action="cancel-custom"]')?.addEventListener('click', () => {
        popover?.classList.remove('open');
    });

    container.querySelector('[data-action="apply-custom"]')?.addEventListener('click', () => {
        const from = fromInput?.value ? new Date(fromInput.value) : null;
        const to = toInput?.value ? new Date(toInput.value) : null;
        if (!from || !to || isNaN(from) || isNaN(to) || from >= to) return;
        customFrom = from;
        customTo = to;
        isCustomRange = true;
        windowOffset = 0;
        container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        popover?.classList.remove('open');
        updateCustomLabel(container);
        loadAndUpdate();
        startPoll();
    });

    document.addEventListener('click', (e) => {
        if (!popover?.classList.contains('open')) return;
        const customBtn = container.querySelector('[data-action="custom-range"]');
        if (popover.contains(e.target) || customBtn?.contains(e.target)) return;
        popover.classList.remove('open');
    });

    visibilityObserver = new IntersectionObserver(([entry]) => {
        const was = isVisible();
        isInViewport = entry.isIntersecting;
        if (isVisible() && !was) { loadAndUpdate(); startPoll(); }
        else if (!isVisible() && was) { stopPoll(); }
    }, { threshold: 0 });
    visibilityObserver.observe(container);

    await loadAndUpdate();
    startPoll();
}

export function soloModem(modemId) {
    if (!modemMeta.length) return;
    // Show only series whose id starts with this modem ID (covers "3:LTE", "3:5G NSA", etc.)
    modemMeta.forEach(m => { visibility[m.id] = m.id === modemId || m.id.startsWith(modemId + ':'); });
    updateVisibility();
    const container = document.getElementById(containerId);
    if (container) renderBadges(container);
}

export function unmount() {
    stopPoll();
    if (visibilityObserver) { visibilityObserver.disconnect(); visibilityObserver = null; }
    if (fetchController) { fetchController.abort(); fetchController = null; }
    if (rsrpChart) { rsrpChart.destroy(); rsrpChart = null; }
    if (rsrqChart) { rsrqChart.destroy(); rsrqChart = null; }
    if (snrChart) { snrChart.destroy(); snrChart = null; }
    if (qualityChart) { qualityChart.destroy(); qualityChart = null; }
    containerId = null;
    modemMeta = [];
    visibility = {};
    currentRangeHours = 24;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    isInViewport = true;
}
