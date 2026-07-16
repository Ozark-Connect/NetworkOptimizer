// Starlink terminal time-series charts: power draw, ping drop rate, obstruction,
// outage seconds, GPS satellites, alignment offset.
// Same control pattern as cellular-charts.js and cm-charts.js.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { computeStats, renderStatsTable as renderTable } from './chart-stats.js?v=4';

const PALETTE = window.Apex?.colors || ['#2ba89a', '#3b82f6', '#a78bfa', '#ef5858', '#f59e0b', '#10b981'];
const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }

const POLL_INTERVALS = { 0: 10000, 1: 10000, 6: 15000, 24: 30000, 168: 60000, 720: 60000 };
const RANGE_MS = { 0: 15*60000, 1: 3600000, 6: 6*3600000, 24: 86400000, 168: 7*86400000, 720: 30*86400000 };

let powerChart = null;
let dropChart = null;
let obstructionChart = null;
let outageChart = null;
let gpsChart = null;
let alignmentChart = null;
let pollTimer = null;
let currentRangeHours = 24;
let windowOffset = 0;
let isCustomRange = false;
let customFrom = null;
let customTo = null;
let containerId = null;
let fetchController = null;
let deviceMeta = [];
let visibility = {};
let visibilityObserver = null;
let isInViewport = true;
let lastData = null;

// Paired charts carry an avg (solid) and max (dashed) series per terminal;
// the suffixes let visibility toggling reach both.
function chartsWithSuffixes() {
    return [
        { chart: powerChart, suffixes: [' (avg)', ' (max)'] },
        { chart: dropChart, suffixes: [' (avg)', ' (max)'] },
        { chart: obstructionChart, suffixes: [''] },
        { chart: outageChart, suffixes: [''] },
        { chart: gpsChart, suffixes: [''] },
        { chart: alignmentChart, suffixes: [''] },
    ];
}

function baseOpts(height, yTitle, yFormatter, extra) {
    return {
        chart: {
            type: 'area', height,
            background: 'transparent',
            toolbar: { show: false },
            zoom: { enabled: !matchMedia('(pointer:coarse)').matches, type: 'x', allowMouseWheelZoom: false },
            events: { beforeZoom: (ctx, opts) => applyDragZoom(opts?.xaxis) },
            animations: { enabled: false },
        },
        stroke: { curve: 'smooth', width: 2 },
        fill: {
            type: 'gradient',
            gradient: { shadeIntensity: 0.3, opacityFrom: 0.4, opacityTo: 0.05 },
        },
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
        const resp = await fetch(`/api/monitoring/starlink-chart?${buildQueryParams()}`,
            { signal: fetchController.signal });
        if (!resp.ok) return null;
        return await resp.json();
    } catch (e) {
        if (e.name === 'AbortError') return null;
        return null;
    }
}

function renderBadges(container) {
    const el = container.querySelector('.starlink-filter-badges');
    if (!el) return;
    if (deviceMeta.length <= 1) { el.innerHTML = ''; return; }
    el.innerHTML = deviceMeta.map(m => {
        const vis = visibility[m.id] !== false;
        return `<button class="wan-filter-badge ${vis ? 'active' : 'inactive'}" data-device="${m.id}">
            <span class="wan-badge-dot" style="background-color: ${m.color}"></span>
            <span>${escapeHtml(m.label)}</span>
        </button>`;
    }).join('');
    if (!el._delegated) {
        el._delegated = true;
        el.addEventListener('click', (e) => {
            const btn = e.target.closest('button[data-device]');
            if (!btn) return;
            const id = btn.dataset.device;
            if (e.ctrlKey || e.metaKey) {
                visibility[id] = visibility[id] === false ? undefined : false;
            } else {
                const allVis = deviceMeta.every(m => visibility[m.id] !== false);
                const onlyThis = visibility[id] !== false
                    && deviceMeta.filter(m => m.id !== id).every(m => visibility[m.id] === false);
                if (onlyThis) { visibility = {}; }
                else if (allVis) { deviceMeta.forEach(m => visibility[m.id] = m.id === id); }
                else { visibility[id] = visibility[id] === false; }
            }
            updateVisibility();
            renderBadges(container);
            renderStatsTable(container, false);
        });
    }
}

function updateVisibility() {
    deviceMeta.forEach(m => {
        const vis = visibility[m.id] !== false;
        chartsWithSuffixes().forEach(({ chart, suffixes }) => {
            if (!chart) return;
            suffixes.forEach(suffix => {
                if (vis) chart.showSeries(m.label + suffix);
                else chart.hideSeries(m.label + suffix);
            });
        });
    });
}

const pct = v => v != null ? v * 100 : null;

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data?.devices) return;
    deviceMeta = data.devices.map((d, i) => ({
        id: d.id, label: d.label, color: PALETTE[i % PALETTE.length],
    }));

    const powerSeries = [];
    const dropSeries = [];
    const obstructionSeries = [];
    const outageSeries = [];
    const gpsSeries = [];
    const alignmentSeries = [];
    data.devices.forEach((d, i) => {
        const color = PALETTE[i % PALETTE.length];
        const pts = d.data || [];
        const map = (sel) => pts.filter(p => sel(p) != null).map(p => ({ x: new Date(p.time).getTime(), y: sel(p) }));
        powerSeries.push(
            { name: `${d.label} (avg)`, color, data: map(p => p.powerAvg) },
            { name: `${d.label} (max)`, color, data: map(p => p.powerMax) });
        dropSeries.push(
            { name: `${d.label} (avg)`, color, data: map(p => pct(p.dropAvg)) },
            { name: `${d.label} (max)`, color, data: map(p => pct(p.dropMax)) });
        obstructionSeries.push({ name: d.label, color, data: map(p => pct(p.obstructed)) });
        outageSeries.push({ name: d.label, color, data: map(p => p.outageSeconds) });
        gpsSeries.push({ name: d.label, color, data: map(p => p.gpsSats) });
        alignmentSeries.push({ name: d.label, color, data: map(p => p.alignment) });
    });

    // Solid avg / dashed max per terminal on the paired charts
    const pairedDash = data.devices.flatMap(() => [0, 5]);
    if (powerChart) {
        powerChart.updateOptions({ stroke: { curve: 'smooth', width: 2, dashArray: pairedDash } }, false, false);
        powerChart.updateSeries(powerSeries, false);
    }
    if (dropChart) {
        dropChart.updateOptions({ stroke: { curve: 'smooth', width: 2, dashArray: pairedDash } }, false, false);
        dropChart.updateSeries(dropSeries, false);
    }
    if (obstructionChart) obstructionChart.updateSeries(obstructionSeries, false);
    if (outageChart) outageChart.updateSeries(outageSeries, false);
    if (gpsChart) gpsChart.updateSeries(gpsSeries, false);
    if (alignmentChart) alignmentChart.updateSeries(alignmentSeries, false);

    updateVisibility();
    lastData = data;
    const container = document.getElementById(containerId);
    if (container) {
        renderBadges(container);
        renderStatsTable(container);
    }
}

const fmtW = v => v != null ? v.toFixed(1) + ' W' : '-';
const fmtPct = v => v != null ? v.toFixed(2) + '%' : '-';
const fmtSec = v => v != null ? v.toFixed(0) + ' s' : '-';
const fmtSats = v => v != null ? v.toFixed(1) : '-';
const fmtDeg = v => v != null ? v.toFixed(2) + '°' : '-';

function renderStatsTable(container, showAll) {
    const el = container.querySelector('.starlink-stats-table');
    if (!el || !lastData?.devices?.length) { if (el) el.innerHTML = ''; return; }

    const rows = lastData.devices.map(d => {
        const pts = d.data || [];
        const power = computeStats(pts.map(p => p.powerAvg).filter(v => v != null));
        const powerMax = computeStats(pts.map(p => p.powerMax).filter(v => v != null));
        const drop = computeStats(pts.map(p => pct(p.dropAvg)).filter(v => v != null));
        const dropMax = computeStats(pts.map(p => pct(p.dropMax)).filter(v => v != null));
        const obstr = computeStats(pts.map(p => pct(p.obstructed)).filter(v => v != null));
        const gps = computeStats(pts.map(p => p.gpsSats).filter(v => v != null));
        const align = computeStats(pts.map(p => p.alignment).filter(v => v != null));
        const outageSum = pts.reduce((s, p) => s + (p.outageSeconds ?? 0), 0);
        const meta = deviceMeta.find(mm => mm.id === d.id);
        return { id: d.id, label: d.label, color: meta?.color || '#9ca3af',
            visible: meta && visibility[meta.id] !== false,
            values: [power?.mean, powerMax?.max, drop?.mean, dropMax?.max,
                obstr?.mean, obstr?.max, outageSum, gps?.mean, align?.mean, align?.max] };
    });

    renderTable(el, container, {
        nameHeader: 'Terminal', rows, showAllRows: showAll,
        columns: [
            { header: 'Power Mean', format: fmtW }, { header: 'Power Max', format: fmtW },
            { header: 'Drop Mean', format: fmtPct }, { header: 'Drop Max', format: fmtPct },
            { header: 'Obstr Mean', format: fmtPct }, { header: 'Obstr Max', format: fmtPct },
            { header: 'Outage Total', format: fmtSec },
            { header: 'GPS Mean', format: fmtSats },
            { header: 'Align Mean', format: fmtDeg }, { header: 'Align Max', format: fmtDeg },
        ],
        filter: { meta: () => deviceMeta, key: 'id', visibility: () => visibility,
            resetVisibility: () => { visibility = {}; },
            onChanged: (c) => { updateVisibility(); renderBadges(c); renderStatsTable(c, true); } },
    });
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

// Grafana-style drag-select on a chart becomes a custom time window,
// synced to the range selector (custom-range button + popover inputs).
function applyDragZoom(xaxis) {
    const container = document.getElementById(containerId);
    if (container && xaxis && Number.isFinite(xaxis.min) && Number.isFinite(xaxis.max) && xaxis.min < xaxis.max) {
        customFrom = new Date(xaxis.min);
        customTo = new Date(xaxis.max);
        isCustomRange = true;
        windowOffset = 0;
        container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        const fromInput = container.querySelector('[data-input="from"]');
        const toInput = container.querySelector('[data-input="to"]');
        if (fromInput) fromInput.value = toLocalDatetimeString(customFrom);
        if (toInput) toInput.value = toLocalDatetimeString(customTo);
        updateCustomLabel(container);
        loadAndUpdate();
        startPoll();
    }
    // Cancel ApexCharts' client-side zoom; the refetch repaints the selected window
    return { xaxis: { min: undefined, max: undefined } };
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
    deviceMeta = [];
    visibility = {};
    containerId = elId;
    const container = document.getElementById(elId);
    if (!container) return;

    const powerEl = container.querySelector('.starlink-power-chart');
    const dropEl = container.querySelector('.starlink-drop-chart');
    const obstructionEl = container.querySelector('.starlink-obstruction-chart');
    const outageEl = container.querySelector('.starlink-outage-chart');
    const gpsEl = container.querySelector('.starlink-gps-chart');
    const alignmentEl = container.querySelector('.starlink-alignment-chart');
    if (!powerEl || !dropEl || !obstructionEl || !outageEl || !gpsEl || !alignmentEl) return;

    if (powerChart) { powerChart.destroy(); powerChart = null; }
    if (dropChart) { dropChart.destroy(); dropChart = null; }
    if (obstructionChart) { obstructionChart.destroy(); obstructionChart = null; }
    if (outageChart) { outageChart.destroy(); outageChart = null; }
    if (gpsChart) { gpsChart.destroy(); gpsChart = null; }
    if (alignmentChart) { alignmentChart.destroy(); alignmentChart = null; }

    powerChart = new ApexCharts(powerEl, {
        ...baseOpts(200, 'W', v => v != null ? v.toFixed(0) + ' W' : ''),
        series: [], colors: PALETTE,
    });
    dropChart = new ApexCharts(dropEl, {
        ...baseOpts(160, '%', v => v != null ? v.toFixed(1) + '%' : ''),
        series: [], colors: PALETTE,
    });
    obstructionChart = new ApexCharts(obstructionEl, {
        ...baseOpts(160, '%', v => v != null ? v.toFixed(2) + '%' : ''),
        series: [], colors: PALETTE,
    });
    outageChart = new ApexCharts(outageEl, {
        ...baseOpts(160, 's', v => v != null ? v.toFixed(0) + ' s' : ''),
        series: [], colors: PALETTE,
    });
    gpsChart = new ApexCharts(gpsEl, {
        ...baseOpts(160, 'sats', v => v != null ? v.toFixed(0) : ''),
        series: [], colors: PALETTE,
    });
    alignmentChart = new ApexCharts(alignmentEl, {
        ...baseOpts(160, 'deg', v => v != null ? v.toFixed(1) + '°' : ''),
        series: [], colors: PALETTE,
    });

    await powerChart.render();
    await dropChart.render();
    await obstructionChart.render();
    await outageChart.render();
    await gpsChart.render();
    await alignmentChart.render();

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

export function soloDevice(deviceId) {
    if (!deviceMeta.length) return;
    deviceMeta.forEach(m => { visibility[m.id] = m.id === deviceId; });
    updateVisibility();
    const container = document.getElementById(containerId);
    if (container) { renderBadges(container); renderStatsTable(container, false); }
}

export function unmount() {
    stopPoll();
    if (visibilityObserver) { visibilityObserver.disconnect(); visibilityObserver = null; }
    if (fetchController) { fetchController.abort(); fetchController = null; }
    if (powerChart) { powerChart.destroy(); powerChart = null; }
    if (dropChart) { dropChart.destroy(); dropChart = null; }
    if (obstructionChart) { obstructionChart.destroy(); obstructionChart = null; }
    if (outageChart) { outageChart.destroy(); outageChart = null; }
    if (gpsChart) { gpsChart.destroy(); gpsChart = null; }
    if (alignmentChart) { alignmentChart.destroy(); alignmentChart = null; }
    containerId = null;
    deviceMeta = [];
    visibility = {};
    lastData = null;
    currentRangeHours = 24;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    isInViewport = true;
}
