// Cellular modem signal time-series charts: RSRP, SNR, Signal Quality.
// Same control pattern as sfp-charts.js and device-health-charts.js.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { computeStats, renderStatsTable as renderTable } from './chart-stats.js?v=7';
import { valueSortedTooltip, tooltipHeld, alignedPoints } from './chart-tooltip.js?v=15';
import { renderFilterReset, isFiltered } from './chart-filter.js?v=6';
import { createAxisDateCaption } from './chart-axis-date.js?v=3';
import { syncIdentity } from './chart-sync.js?v=7';
import { awaitContainer } from './chart-mount.js?v=1';
import { loadWindowHours, saveWindowHours, markActiveRange } from './chart-window.js?v=1';

// Storage scope for this tab's remembered time window.
const WINDOW_TAB = 'cellular';

const PALETTE = window.Apex?.colors || ['#4269d0', '#efb118', '#ff725c', '#6cc5b0', '#3ca951', '#ff8ab7'];
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
let lastData = null;

const axisDate = createAxisDateCaption({ charts: () => [rsrpChart, rsrqChart, snrChart, qualityChart], window: effectiveWindow });

// Every chart this tab stacks shares one group - see chart-sync.js.
const SYNC_GROUP = 'cellular';

function baseOpts(height, yTitle, yFormatter, extra, group = SYNC_GROUP) {
    return {
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
            renderStatsTable(container, false);
        });
    }

    // Last: the chip rebuild above wipes the row, so the reset is re-added after it.
    renderFilterReset(el, isFiltered(visibility), () => { visibility = {}; updateVisibility(); renderBadges(container); });
}

// Draws exactly the modems that should be on screen, in one update per chart.
//
// This used to call showSeries/hideSeries per modem on each of the four charts, and every one of
// those is a full redraw. Colors come from modemMeta, which holds each modem's palette slot from
// the full list, so filtering never re-colors what stays on screen.
function updateVisibility() {
    const modems = (lastData?.modems || []).filter(m => visibility[m.id] !== false);
    const seriesOf = (pick) => modems.map(m => ({
        name: m.label,
        color: modemMeta.find(x => x.id === m.id)?.color || PALETTE[0],
        data: alignedPoints(m.data || [], pick),
    }));
    if (rsrpChart) rsrpChart.updateSeries(seriesOf(p => p.rsrp), false);
    if (rsrqChart) rsrqChart.updateSeries(seriesOf(p => p.rsrq), false);
    if (snrChart) snrChart.updateSeries(seriesOf(p => p.snr), false);
    if (qualityChart) qualityChart.updateSeries(seriesOf(p => p.quality), false);
}

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data?.modems) return;
    modemMeta = data.modems.map((m, i) => ({
        id: m.id, label: m.label, color: PALETTE[i % PALETTE.length],
    }));

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
const fmtDb = v => v != null ? v.toFixed(2) : '-';
const fmtPct = v => v != null ? v.toFixed(0) + '%' : '-';

function renderStatsTable(container, showAll) {
    const el = container.querySelector('.cellular-stats-table');
    if (!el || !lastData?.modems?.length) { if (el) el.innerHTML = ''; return; }

    const rows = lastData.modems.map(m => {
        const pts = m.data || [];
        const rsrp = computeStats(pts.map(p => p.rsrp).filter(v => v != null));
        const rsrq = computeStats(pts.map(p => p.rsrq).filter(v => v != null));
        const snr = computeStats(pts.map(p => p.snr).filter(v => v != null));
        const quality = computeStats(pts.map(p => p.quality).filter(v => v != null));
        const meta = modemMeta.find(mm => mm.id === m.id);
        return { id: m.id, label: m.label, color: meta?.color || '#9ca3af',
            visible: meta && visibility[meta.id] !== false,
            values: [rsrp?.mean, rsrp?.min, rsrp?.max, rsrq?.mean, rsrq?.min, rsrq?.max,
                snr?.mean, snr?.min, snr?.max, quality?.mean, quality?.min, quality?.max] };
    });

    renderTable(el, container, {
        nameHeader: 'Modem', rows, showAllRows: showAll,
        columns: [
            { header: 'RSRP Mean', format: fmtDbm }, { header: 'RSRP Min', format: fmtDbm }, { header: 'RSRP Max', format: fmtDbm },
            { header: 'RSRQ Mean', format: fmtDb }, { header: 'RSRQ Min', format: fmtDb }, { header: 'RSRQ Max', format: fmtDb },
            { header: 'SNR Mean', format: fmtDb }, { header: 'SNR Min', format: fmtDb }, { header: 'SNR Max', format: fmtDb },
            { header: 'Qual Mean', format: fmtPct }, { header: 'Qual Min', format: fmtPct }, { header: 'Qual Max', format: fmtPct },
        ],
        filter: { meta: () => modemMeta, key: 'id', visibility: () => visibility,
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
    modemMeta = [];
    visibility = {};
    containerId = elId;
    // Read before the await, so the decision is made against the URL this mount was called
    // for. Null leaves this tab's own default standing.
    const restoredHours = loadWindowHours(WINDOW_TAB);
    if (restoredHours !== null) currentRangeHours = restoredHours;

    // Awaited, not read once: Blazor can call mount before it has rendered this tab.
    const container = await awaitContainer(elId);
    if (!container) return;
    // A second mount while this one waited owns the tab now.
    if (containerId !== elId) return;

    if (restoredHours !== null) markActiveRange(container, restoredHours);

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
        btn.addEventListener('click', () => {
            const hours = parseInt(btn.dataset.range);
            // Saved HERE rather than in selectPresetRange: a deep link's framing calls that
            // too, and a window the link chose must not become a remembered preference.
            saveWindowHours(WINDOW_TAB, hours);
            selectPresetRange(container, hours);
        });
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
    if (container) { renderBadges(container); renderStatsTable(container, false); }
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
    lastData = null;
    currentRangeHours = 24;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    isInViewport = true;
    axisDate.reset();
}
