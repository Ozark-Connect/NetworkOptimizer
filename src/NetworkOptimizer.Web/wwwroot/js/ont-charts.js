// External ONT signal time-series charts: RX/TX Power, Temperature, OLT RX Power.
// Same control pattern as cellular-charts.js.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { computeStats, renderStatsTable as renderTable } from './chart-stats.js?v=7';
import { valueSortedTooltip, tooltipHeld, alignedPoints } from './chart-tooltip.js?v=15';
import { renderFilterReset, isFiltered } from './chart-filter.js?v=6';
import { createMarkLayer } from './chart-event-marks.js?v=2';
import { createAxisDateCaption } from './chart-axis-date.js?v=3';
import { ponSeriesFor, ponDetailsHtml } from './pon-section.js?v=1';
import { syncIdentity } from './chart-sync.js?v=7';

const PALETTE = window.Apex?.colors || ['#4269d0', '#efb118', '#ff725c', '#6cc5b0', '#3ca951', '#ff8ab7'];
const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }

const POLL_INTERVALS = { 0: 10000, 1: 10000, 6: 15000, 24: 30000, 168: 60000, 720: 60000 };
const RANGE_MS = { 0: 15*60000, 1: 3600000, 6: 6*3600000, 24: 86400000, 168: 7*86400000, 720: 30*86400000 };

let powerChart = null;
let tempChart = null;
// FEC/BIP error-delta chart; its section stays hidden unless some ONT reports the counters.
let errorsChart = null;
let ponGemChart = null;
let ponHostChart = null;
let pollTimer = null;
let currentRangeHours = 24;
let windowOffset = 0;
let isCustomRange = false;
let customFrom = null;
let customTo = null;
let containerId = null;
let fetchController = null;
let deviceMeta = [];
let lastEvents = [];
let chartEls = {};
let markResizeTimer = null;
let visibility = {};
let visibilityObserver = null;
let isInViewport = true;
let lastData = null;

const axisDate = createAxisDateCaption({ charts: chartEntries, window: effectiveWindow });

// Every chart this tab stacks shares one group - see chart-sync.js.
const SYNC_GROUP = 'ont';

function baseOpts(height, yTitle, yFormatter, extra, group = SYNC_GROUP) {
    const base = {
        chart: {
            type: 'area', height,
            ...syncIdentity(group),
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
        // Stays 0: the library's hover markers are the flaky ones, and any non-zero size puts
        // a permanent dot on every sample. valueSortedTooltip draws the hover dots instead.
        markers: { size: 0 },
        dataLabels: { enabled: false },
        xaxis: {
            type: 'datetime',
            labels: {
                style: { colors: '#9ca3af' },
                datetimeUTC: false,
                datetimeFormatter: { hour: 'HH:mm', day: 'MMM dd' },
            },
            title: axisDate.option(),
        },
        yaxis: {
            title: { text: yTitle, style: { color: '#9ca3af' } },
            labels: { style: { colors: '#9ca3af' }, formatter: yFormatter },
        },
        grid: { borderColor: '#374151', strokeDashArray: 3 },
        legend: { show: false },
        tooltip: { theme: 'dark', shared: true, x: { format: 'MMM dd, HH:mm:ss' }, custom: valueSortedTooltip },
        noData: { text: 'No data in this time range', style: { color: '#64748b' } },
    };
    if (extra?.yaxis) {
        base.yaxis = { ...base.yaxis, ...extra.yaxis };
        const { yaxis, ...rest } = extra;
        Object.assign(base, rest);
    } else if (extra) {
        Object.assign(base, extra);
    }
    return base;
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
        const resp = await fetch(`/api/monitoring/ont-chart?${buildQueryParams()}`,
            { signal: fetchController.signal });
        if (!resp.ok) return null;
        return await resp.json();
    } catch (e) {
        if (e.name === 'AbortError') return null;
        return null;
    }
}

function renderBadges(container) {
    const el = container.querySelector('.ont-filter-badges');
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

    // Last: the chip rebuild above wipes the row, so the reset is re-added after it.
    renderFilterReset(el, isFiltered(visibility), () => { visibility = {}; updateVisibility(); renderBadges(container); });
}

// Every chart on the tab paired with the element it rendered into, which is what the mark layer
// needs to reach the annotation labels it draws. The errors chart is created lazily, so the
// pairs are built on demand rather than captured once.
function chartEntries() {
    return [
        [powerChart, chartEls.power],
        [tempChart, chartEls.temp],
        [errorsChart, chartEls.errors],
        [ponHostChart, chartEls.ponHost],
    ].filter(([chart]) => chart);
}

const markLayer = createMarkLayer({ charts: chartEntries });

function applyAnnotations() {
    markLayer.apply(lastEvents, visibility);
}

// A narrower plot fits fewer marks before they collide, so the folds have to be recomputed.
// Debounced because ApexCharts is redrawing on the same events, and left to settle after it.
function onMarkResize() {
    clearTimeout(markResizeTimer);
    markResizeTimer = setTimeout(applyAnnotations, 200);
}

// RX, TX and temperature each get their own color per ONT, so the three charts stay readable
// side by side. Indexed by the ONT's place in the full list, so filtering never re-colors a line.
const COLOR_SETS = [
    { rx: PALETTE[0], tx: PALETTE[4], temp: PALETTE[1] },
    { rx: PALETTE[6], tx: PALETTE[3], temp: PALETTE[11] },
    { rx: PALETTE[10], tx: PALETTE[18], temp: PALETTE[13] },
];

// Draws exactly the ONTs that should be on screen, in one update per chart.
//
// This used to call showSeries/hideSeries per ONT per series, and each of those is a full redraw.
function drawSeries() {
    const all = lastData?.devices || [];
    const devices = all.filter(d => visibility[d.id] !== false);
    const powerSeries = [];
    const tempSeries = [];
    // Positional, so rebuilt against the ONTs actually drawn: RX solid, TX dashed.
    const powerDash = [];
    devices.forEach(d => {
        const c = COLOR_SETS[all.indexOf(d) % COLOR_SETS.length];
        const pts = d.data || [];
        powerSeries.push({ name: d.label + ' RX', color: c.rx, data: alignedPoints(pts, p => p.rx) });
        powerSeries.push({ name: d.label + ' TX', color: c.tx, data: alignedPoints(pts, p => p.tx) });
        powerDash.push(0);
        powerDash.push(5);
        tempSeries.push({ name: d.label, color: c.temp, data: alignedPoints(pts, p => p.temp) });
    });
    if (powerChart) {
        powerChart.updateOptions({ stroke: { curve: 'smooth', width: 2, dashArray: powerDash } }, false, false, false);
        powerChart.updateSeries(powerSeries, false);
    }
    if (tempChart) tempChart.updateSeries(tempSeries, false);
}

function updateVisibility() {
    applyAnnotations();
    // No single-ONT short-circuit: this is the draw path now, not just the toggle path, so the one
    // ONT on a single-ONT site would never be plotted at all.
    drawSeries();
    // Fire-and-forget: it mounts the chart on first use. Kept off the synchronous path so a chart
    // error can never break chip re-rendering.
    updateErrorsChart().catch(() => {});
}

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data?.devices) return;
    deviceMeta = data.devices.map((d, i) => ({
        id: d.id, label: d.label, color: PALETTE[i % PALETTE.length],
    }));
    // Set before updateVisibility below, which is what draws the marks.
    lastEvents = data.events || [];

    // Before updateVisibility, which draws from it.
    lastData = data;
    // Ahead of the redraw below - see apply().
    axisDate.apply();
    updateVisibility();
    const container = document.getElementById(containerId);
    if (container) {
        renderBadges(container);
        renderStatsTable(container);
    }
}

const fmtDbm = v => v != null ? v.toFixed(2) : '-';
const fmtTemp = v => v != null ? v.toFixed(1) : '-';
const fmtCount = v => v == null ? '' : v >= 1e6 ? (v / 1e6).toFixed(1) + 'M' : v >= 1e3 ? (v / 1e3).toFixed(1) + 'k' : String(Math.round(v));

// Create the errors chart on first use, so ONTs that never report FEC/BIP don't
// pay for an instance they'd never see.
async function ensureErrorsChartMounted() {
    if (errorsChart) return;
    const container = document.getElementById(containerId);
    const errorsEl = container?.querySelector('.ont-errors-chart');
    if (!errorsEl) return;
    errorsChart = new ApexCharts(errorsEl, { ...baseOpts(160, 'errors', fmtCount), series: [], colors: PALETTE });
    chartEls.errors = errorsEl;
    const hostEl = container.querySelector('.ont-pon-host-chart');
    const gemEl = container.querySelector('.ont-pon-gem-chart');
    if (hostEl) { ponHostChart = new ApexCharts(hostEl, { ...baseOpts(160, 'errors', fmtCount), series: [], colors: PALETTE }); chartEls.ponHost = hostEl; }
    if (gemEl) ponGemChart = new ApexCharts(gemEl, { ...baseOpts(160, 'frames', fmtCount), series: [], colors: PALETTE });
    await Promise.all([errorsChart.render(), ponHostChart?.render(), ponGemChart?.render()].filter(Boolean));
    // It arrives after the first draw, so it starts with no marks on it.
    markLayer.reset();
    applyAnnotations();
}

async function updateErrorsChart() {
    const container = document.getElementById(containerId);
    const section = container?.querySelector('.ont-errors-section');
    if (!section) return;
    const reporting = (lastData?.devices || []).filter(d => d.pon?.length);
    // The section appears when ANY ONT reports errors, filtered or not - it is a property of the
    // hardware, so it must not come and go as the chips are clicked. Only its series are filtered.
    if (!reporting.length) {
        section.style.display = 'none';
        return;
    }
    await ensureErrorsChartMounted();
    if (!errorsChart) return;
    section.style.display = '';

    const withErrors = reporting.filter(d => visibility[d.id] !== false);
    const multi = withErrors.length > 1;
    const errSeries = [], gemSeries = [], hostSeries = [];
    withErrors.forEach(d => {
        const slot = Math.max(0, reporting.findIndex(x => x.id === d.id));
        const built = ponSeriesFor(d, multi ? `${d.label} ` : '', slot, PALETTE);
        errSeries.push(...built.errSeries);
        gemSeries.push(...built.gemSeries);
        hostSeries.push(...built.hostSeries);
    });
    errorsChart.updateSeries(errSeries.filter(x => x.data.length), false);

    // Unlike SFP Stats, this tab mixes ONTs that serve the whole PON set with ones reporting a
    // couple of counters, so each of these hides itself rather than drawing an empty frame.
    updatePonCard(container, '.ont-pon-host-card', ponHostChart, hostSeries);
    updatePonCard(container, '.ont-pon-gem-card', ponGemChart, gemSeries);

    const el = container.querySelector('.ont-pon-details');
    if (el) el.innerHTML = ponDetailsHtml(withErrors, 'ONT', DETAIL_EXTRAS);
}

// Columns this tab adds to the shared table. Ones no ONT fills are dropped by the renderer.
const DETAIL_EXTRAS = [
    { header: 'PON Type', cell: d => d.ponType ? escapeHtml(d.ponType) : null },
    { header: 'OLT', cell: d => d.olt ? escapeHtml(d.olt) : null },
];

function updatePonCard(container, cardSelector, chart, series) {
    const card = container.querySelector(cardSelector);
    if (!card) return;
    const filled = series.filter(x => x.data.length);
    card.style.display = filled.length ? '' : 'none';
    if (filled.length && chart) chart.updateSeries(filled, false);
}

function renderStatsTable(container, showAll) {
    const el = container.querySelector('.ont-stats-table');
    if (!el || !lastData?.devices?.length) { if (el) el.innerHTML = ''; return; }

    const rows = lastData.devices.map(d => {
        const pts = d.data || [];
        const rx = computeStats(pts.map(p => p.rx).filter(v => v != null));
        const tx = computeStats(pts.map(p => p.tx).filter(v => v != null));
        const temp = computeStats(pts.map(p => p.temp).filter(v => v != null));
        const meta = deviceMeta.find(dm => dm.id === d.id);
        return { id: d.id, label: d.label, color: meta?.color || '#9ca3af',
            visible: meta && visibility[meta.id] !== false,
            values: [rx?.mean, rx?.min, rx?.max, tx?.mean, tx?.min, tx?.max, temp?.mean, temp?.min, temp?.max] };
    });

    renderTable(el, container, {
        nameHeader: 'Device', rows, showAllRows: showAll,
        columns: [
            { header: 'RX Mean', format: fmtDbm }, { header: 'RX Min', format: fmtDbm }, { header: 'RX Max', format: fmtDbm },
            { header: 'TX Mean', format: fmtDbm }, { header: 'TX Min', format: fmtDbm }, { header: 'TX Max', format: fmtDbm },
            { header: 'Temp Mean', format: fmtTemp }, { header: 'Temp Min', format: fmtTemp }, { header: 'Temp Max', format: fmtTemp },
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
    pollTimer = setInterval(() => { if (!tooltipHeld(document.getElementById(containerId))) loadAndUpdate(); }, POLL_INTERVALS[currentRangeHours] || 60000);
}
function stopPoll() { if (pollTimer) { clearInterval(pollTimer); pollTimer = null; } }

function toLocalDatetimeString(d) {
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

// A plain preset keeps no explicit bounds - getEffectiveFrom/To answer null for it - so its window
// is the range, trailing from now.
function effectiveWindow() {
    const to = getEffectiveTo() || new Date();
    return {
        from: getEffectiveFrom() || new Date(to.getTime() - (RANGE_MS[currentRangeHours] || 3600000)),
        to,
    };
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

    const powerEl = container.querySelector('.ont-power-chart');
    const tempEl = container.querySelector('.ont-temp-chart');
    if (!powerEl || !tempEl) return;

    chartEls = { power: powerEl, temp: tempEl };

    if (powerChart) { powerChart.destroy(); powerChart = null; }
    if (tempChart) { tempChart.destroy(); tempChart = null; }
    if (errorsChart) { errorsChart.destroy(); errorsChart = null; }
    if (ponHostChart) { ponHostChart.destroy(); ponHostChart = null; }
    if (ponGemChart) { ponGemChart.destroy(); ponGemChart = null; }

    powerChart = new ApexCharts(powerEl, {
        ...baseOpts(220, 'dBm', v => v != null ? v.toFixed(1) + ' dBm' : '', {
            yaxis: { min: v => Math.floor(v - 2), max: v => Math.ceil(v + 2) } }),
        series: [], colors: PALETTE,
    });
    tempChart = new ApexCharts(tempEl, {
        ...baseOpts(160, '°C', v => v != null ? v.toFixed(1) + ' °C' : ''),
        series: [], colors: PALETTE,
    });

    await powerChart.render();
    await tempChart.render();

    // The FEC/BIP errors chart is mounted lazily (ensureErrorsChartMounted) only when
    // an ONT actually reports those counters - ONTs without them never create it.

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

    window.addEventListener('resize', onMarkResize);

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
    window.removeEventListener('resize', onMarkResize);
    clearTimeout(markResizeTimer);
    markResizeTimer = null;
    if (visibilityObserver) { visibilityObserver.disconnect(); visibilityObserver = null; }
    if (fetchController) { fetchController.abort(); fetchController = null; }
    if (powerChart) { powerChart.destroy(); powerChart = null; }
    if (tempChart) { tempChart.destroy(); tempChart = null; }
    if (errorsChart) { errorsChart.destroy(); errorsChart = null; }
    if (ponHostChart) { ponHostChart.destroy(); ponHostChart = null; }
    if (ponGemChart) { ponGemChart.destroy(); ponGemChart = null; }
    containerId = null;
    deviceMeta = [];
    visibility = {};
    chartEls = {};
    lastData = null;
    lastEvents = [];
    markLayer.reset();
    currentRangeHours = 24;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    isInViewport = true;
    axisDate.reset();
}
