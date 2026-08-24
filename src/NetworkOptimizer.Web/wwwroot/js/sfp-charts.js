// SFP DDM time-series charts: RX/TX power, temperature, voltage.
// Same control pattern as latency-charts.js and device-health-charts.js.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { computeStats, renderStatsTable as renderTable } from './chart-stats.js?v=8';
import { valueSortedTooltip, tooltipHeld, alignedPoints } from './chart-tooltip.js?v=15';
import { renderFilterReset, isFiltered } from './chart-filter.js?v=6';
import { createMarkLayer } from './chart-event-marks.js?v=5';
import { createAxisDateCaption } from './chart-axis-date.js?v=3';
import { syncIdentity, extentsOf, spanTo } from './chart-sync.js?v=7';
import { ponSeriesFor, ponDetailsHtml, updatePonCard } from './pon-section.js?v=3';
import { awaitContainer } from './chart-mount.js?v=1';
import { loadWindowHours, saveWindowHours, markActiveRange, notifyWindowMoved } from './chart-window.js?v=2';

// Storage scope for this tab's remembered time window.
const WINDOW_TAB = 'sfp';

const PALETTE = window.Apex?.colors || ['#7EB26D', '#EAB839', '#6ED0E0', '#EF843C', '#E24D42', '#1F78C1'];
const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }

const POLL_INTERVALS = { 0: 10000, 1: 10000, 6: 15000, 24: 30000, 168: 60000, 720: 60000 };
const RANGE_MS = { 0: 15*60000, 1: 3600000, 6: 6*3600000, 24: 86400000, 168: 7*86400000, 720: 30*86400000 };

let powerChart = null;
let tempChart = null;
// Supplemental PON-layer charts (attached Network Optimizer Custom ONT configs);
// the whole section stays hidden unless some module has PON data.
let ponErrChart = null;
let ponGemChart = null;
let ponHostChart = null;
// Modules that currently have PON data, whether visible or not. The PON section and
// its detail table are shown only for the ones selected in the filter.
let ponCapableModules = [];
let pollTimer = null;
let currentRangeHours = 24;
let windowOffset = 0;
let isCustomRange = false;
let customFrom = null;
let customTo = null;
let containerId = null;
let fetchController = null;
let moduleMeta = [];
let visibility = {};
let visibilityObserver = null;
let isInViewport = true;
let lastData = null;
let lastEvents = [];
let chartEls = {};
let markResizeTimer = null;

const axisDate = createAxisDateCaption({ charts: () => [...opticsChartEntries(), ...ponChartEntries()], window: effectiveWindow });

// Every chart this tab stacks shares one group - see chart-sync.js. The PON charts come from each
// module's `pon` array rather than its optics rows, so they are trimmed and padded to the group's
// extents like everything else, which is what the sync actually turns on.
const SYNC_GROUP = 'sfp';
let groupExtents = null;
// The group's extents on every chart, so ApexCharts passes the hover between them - see spanTo.
function padFirst(series) {
    return spanTo(series, groupExtents);
}

// The group a chart belongs to is fixed at construction, and ApexCharts decides membership from
// its own registry entry plus the hovering chart's config - so moving a chart between groups means
// setting both. Rebuilding the charts instead would cost a full remount on every chip click.
const PON_ONLY_GROUP = 'sfp-pon';

function setPonSyncGroup(group) {
    for (const chart of [ponErrChart, ponGemChart, ponHostChart]) {
        if (!chart?.w) continue;
        chart.w.config.chart.group = group;
        const entry = (window.Apex?._chartInstances || []).find(i => i.chart === chart);
        if (entry) entry.group = group;
    }
}


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
        const resp = await fetch(`/api/monitoring/sfp-chart?${buildQueryParams()}`,
            { signal: fetchController.signal });
        if (!resp.ok) return null;
        return await resp.json();
    } catch (e) {
        if (e.name === 'AbortError') return null;
        return null;
    }
}

function renderBadges(container) {
    const el = container.querySelector('.sfp-filter-badges');
    if (!el) return;
    if (moduleMeta.length <= 1) { el.innerHTML = ''; return; }
    el.innerHTML = moduleMeta.map(m => {
        const vis = visibility[m.id] !== false;
        return `<button class="wan-filter-badge ${vis ? 'active' : 'inactive'}" data-sfp="${m.id}">
            <span class="wan-badge-dot" style="background-color: ${m.color}"></span>
            <span>${escapeHtml(m.label)}</span>
        </button>`;
    }).join('');
    if (!el._delegated) {
        el._delegated = true;
        el.addEventListener('click', (e) => {
            const btn = e.target.closest('button[data-sfp]');
            if (!btn) return;
            const id = btn.dataset.sfp;

            if (e.ctrlKey || e.metaKey) {
                visibility[id] = visibility[id] === false ? undefined : false;
            } else {
                const allVis = moduleMeta.every(m => visibility[m.id] !== false);
                const onlyThis = visibility[id] !== false
                    && moduleMeta.filter(m => m.id !== id).every(m => visibility[m.id] === false);
                if (onlyThis) { visibility = {}; }
                else if (allVis) { moduleMeta.forEach(m => visibility[m.id] = m.id === id); }
                else { visibility[id] = visibility[id] === false; }
            }
            updateVisibility();
            renderBadges(container);
            renderStatsTable(container, false);
        });
    }

    // Last: the chip rebuild above wipes the row, so the reset is re-added after it.
    renderFilterReset(el, isFiltered(visibility), () => { visibility = {}; updateVisibility(); renderBadges(container); renderStatsTable(container, false); });
}

// Every chart on the tab paired with the element it rendered into, which is what the mark
// layer needs to reach the annotation labels it draws. The PON charts are created lazily, so
// the pairs are built on demand rather than captured once.
function opticsChartEntries() {
    return [
        [powerChart, chartEls.power],
        [tempChart, chartEls.temp],
    ].filter(([chart]) => chart);
}

// GEM Frames is deliberately absent. It plots per-interval frame counts whose shape is the
// traffic itself, so a mark on it reads as a comment on throughput rather than on the link.
function ponChartEntries() {
    return [
        [ponErrChart, chartEls.ponErr],
        [ponHostChart, chartEls.ponHost],
    ].filter(([chart]) => chart);
}

// Two layers rather than one, because the two chart groups do not mark the same events. The
// server says which is which: an event scoped 'pon' describes the link riding over the module
// and stays on the PON charts, while everything scoped 'all' - the SFP alerts, and a PON link
// going down - is worth seeing while reading the optics too. The PON charts carry both, since an
// RX power dip is often the explanation for the BIP errors beside it.
const opticsMarkLayer = createMarkLayer({ charts: opticsChartEntries });
const ponMarkLayer = createMarkLayer({ charts: ponChartEntries });

function applyAnnotations() {
    opticsMarkLayer.apply(lastEvents.filter(e => e.scope !== 'pon'), visibility);
    ponMarkLayer.apply(lastEvents, visibility);
}

// A narrower plot fits fewer marks before they collide, so the folds have to be recomputed.
// Debounced because ApexCharts is redrawing on the same events, and left to settle after it.
function onMarkResize() {
    clearTimeout(markResizeTimer);
    markResizeTimer = setTimeout(applyAnnotations, 200);
}

// Draws exactly the modules that should be on screen, in one update per chart.
//
// This used to call showSeries/hideSeries per module, and each of those is a full redraw - so a
// chip click cost one redraw per module it changed, and going from everything to one cost as many
// as there are modules. Colors come from moduleMeta, which holds each module's palette slot from
// the full list, so filtering never re-colors what stays on screen.
function drawSeries() {
    const modules = (lastData?.modules || []).filter(m => visibility[m.id] !== false);
    const powerSeries = [];
    const tSeries = [];
    // Dash patterns are positional, so they are rebuilt against the series actually drawn: RX
    // solid, TX dashed, per module.
    const powerDash = [];
    modules.forEach(m => {
        const color = moduleMeta.find(x => x.id === m.id)?.color || PALETTE[0];
        const pts = m.data || [];
        powerSeries.push({ name: `${m.label} RX`, color: color, data: alignedPoints(pts, p => p.rx) });
        powerSeries.push({ name: `${m.label} TX`, color: color, data: alignedPoints(pts, p => p.tx) });
        powerDash.push(0);
        powerDash.push(5);
        tSeries.push({ name: m.label, color: color, data: alignedPoints(pts, p => p.temp) });
    });
    if (powerChart) {
        // Fourth argument false: RX solid / TX dashed is positional to this chart's series, and
        // sharing it with the group is what dashed TX Frames on GEM Frames.
        powerChart.updateOptions({ stroke: { curve: 'smooth', width: 2, dashArray: powerDash } }, false, false, false);
        powerChart.updateSeries(padFirst(powerSeries), false);
    }
    if (tempChart) tempChart.updateSeries(padFirst(tSeries), false);
}

function updateVisibility() {
    drawSeries();
    applyAnnotations();
    // Fire-and-forget: rebuilds the PON charts/section for the selected modules. Kept
    // off the synchronous path so a chart error can never break chip re-rendering.
    refreshPonSection();
}

// Rebuild the PON section (charts + detail table) for the currently-selected PON
// modules, or hide it when none are selected. The section is shown BEFORE the charts
// are mounted/updated so ApexCharts sizes them correctly - a chart first rendered in a
// display:none container stays zero-size until its next update.
async function refreshPonSection() {
    const container = document.getElementById(containerId);
    const section = container?.querySelector('.sfp-pon-section');
    if (!section) return;
    const visiblePon = ponCapableModules.filter(m => visibility[m.id] !== false);
    if (!visiblePon.length) { section.style.display = 'none'; return; }
    section.style.display = '';
    await ensurePonChartsMounted();

    // The PON charts follow the optics only while the ONT modules are the ONLY ones on screen.
    // With another module shown beside them the optics charts are drawing something the PON
    // charts have no line for, and a crosshair tracking across the two would claim a
    // correspondence that is not there. They still follow each other either way.
    const visibleModules = (lastData?.modules || []).filter(m => visibility[m.id] !== false);
    setPonSyncGroup(visibleModules.length === visiblePon.length ? SYNC_GROUP : PON_ONLY_GROUP);
    if (!ponErrChart) return;
    try {
        const multi = visiblePon.length > 1;
        const errSeries = [], gemSeries = [], hostSeries = [];
        visiblePon.forEach(m => {
            const prefix = multi ? `${m.label} ` : '';
            const slot = Math.max(0, ponCapableModules.findIndex(x => x.id === m.id));
            const series = ponSeriesFor(m, prefix, slot, PALETTE);
            errSeries.push(...series.errSeries);
            gemSeries.push(...series.gemSeries);
            hostSeries.push(...series.hostSeries);
        });
        // Every section of the contract is optional, so an implementation serving the GTC
        // counters and no host-link ones leaves that card empty for good. Hide what nothing fills.
        updatePonCard(container, '.sfp-pon-errors-card', ponErrChart, errSeries, padFirst);
        updatePonCard(container, '.sfp-pon-gem-card', ponGemChart, gemSeries, padFirst);
        updatePonCard(container, '.sfp-pon-host-card', ponHostChart, hostSeries, padFirst);
        renderPonDetails(container, visiblePon);
    } catch (e) { /* leave the previous render if a chart update fails */ }
}

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data?.modules) return;
    moduleMeta = data.modules.map((m, i) => ({
        id: m.id, label: m.label, color: PALETTE[i % PALETTE.length],
    }));
    groupExtents = extentsOf((data.modules || []).map(m => m.data || []));
    // Set before updateVisibility below, which is what draws the marks.
    lastEvents = data.events || [];

    // Never let a PON chart failure abort the rest of the refresh (badges, stats table).
    try { await updatePonCharts(data); } catch (_) {}

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


// Create the three PON charts on first use. Kept out of mount() so setups without
// supplemental PON polling never pay for instances they'd never see.
async function ensurePonChartsMounted() {
    if (ponErrChart) return;
    const container = document.getElementById(containerId);
    const ponErrEl = container?.querySelector('.sfp-pon-errors-chart');
    const ponGemEl = container?.querySelector('.sfp-pon-gem-chart');
    const ponHostEl = container?.querySelector('.sfp-pon-host-chart');
    if (!ponErrEl || !ponGemEl || !ponHostEl) return;
    ponErrChart = new ApexCharts(ponErrEl, { ...baseOpts(160, 'errors', fmtCount), series: [], colors: PALETTE });
    ponGemChart = new ApexCharts(ponGemEl, { ...baseOpts(160, 'frames', fmtCount), series: [], colors: PALETTE });
    ponHostChart = new ApexCharts(ponHostEl, { ...baseOpts(160, 'errors', fmtCount), series: [], colors: PALETTE });
    // No ponGem: GEM Frames takes no marks, so the layer never needs to reach it.
    chartEls.ponErr = ponErrEl;
    chartEls.ponHost = ponHostEl;
    await Promise.all([ponErrChart.render(), ponGemChart.render(), ponHostChart.render()]);
    // These arrive after the first draw, so they start with no marks on them.
    ponMarkLayer.reset();
    applyAnnotations();
}

async function updatePonCharts(data) {
    ponCapableModules = (data.modules || []).filter(m => m.pon?.length);
    await refreshPonSection();
}

function renderPonDetails(container, withPon) {
    const el = container.querySelector('.sfp-pon-details');
    if (!el) return;
    el.innerHTML = ponDetailsHtml(withPon, 'Module');
}

function renderStatsTable(container, showAll) {
    const el = container.querySelector('.sfp-stats-table');
    if (!el || !lastData?.modules?.length) { if (el) el.innerHTML = ''; return; }

    const rows = lastData.modules.map(m => {
        const pts = m.data || [];
        const rx = computeStats(pts.map(p => p.rx).filter(v => v != null));
        const tx = computeStats(pts.map(p => p.tx).filter(v => v != null));
        const temp = computeStats(pts.map(p => p.temp).filter(v => v != null));
        const meta = moduleMeta.find(mm => mm.id === m.id);
        return { id: m.id, label: m.label, color: meta?.color || '#9ca3af',
            visible: meta && visibility[meta.id] !== false,
            values: [rx?.latest, rx?.mean, rx?.min, rx?.max, tx?.latest, tx?.mean, tx?.min, tx?.max,
                     temp?.latest, temp?.mean, temp?.min, temp?.max] };
    });

    renderTable(el, container, {
        nameHeader: 'Module', rows, showAllRows: showAll,
        columns: [
            { header: 'RX Latest', format: fmtDbm , cls: 'stats-lead' }, { header: 'RX Mean', format: fmtDbm }, { header: 'RX Min', format: fmtDbm }, { header: 'RX Max', format: fmtDbm },
            { header: 'TX Latest', format: fmtDbm , cls: 'stats-lead' }, { header: 'TX Mean', format: fmtDbm }, { header: 'TX Min', format: fmtDbm }, { header: 'TX Max', format: fmtDbm },
            { header: 'Temp Latest', format: fmtTemp , cls: 'stats-lead' }, { header: 'Temp Mean', format: fmtTemp }, { header: 'Temp Min', format: fmtTemp }, { header: 'Temp Max', format: fmtTemp },
        ],
        filter: { meta: () => moduleMeta, key: 'id', visibility: () => visibility,
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
    notifyWindowMoved();
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

/**
 * Frames an hour around a moment an alert link carried in. A custom range rather than a preset,
 * so a linked window never becomes a remembered one, and an hour rather than the 15 minutes the
 * latency charts use, because these counters move over a shift.
 */
export function frameMoment(isoTimestamp) {
    const ts = new Date(isoTimestamp).getTime();
    if (!Number.isFinite(ts)) return;
    customFrom = new Date(ts - 30 * 60000);
    customTo = new Date(ts + 30 * 60000);
    isCustomRange = true;
    windowOffset = 0;

    const container = document.getElementById(containerId);
    if (container) {
        container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        container.querySelector('.custom-range-btn')?.classList.add('active');
        const fromInput = container.querySelector('[data-input="from"]');
        const toInput = container.querySelector('[data-input="to"]');
        if (fromInput) fromInput.value = toLocalDatetimeString(customFrom);
        if (toInput) toInput.value = toLocalDatetimeString(customTo);
        updateCustomLabel(container);
    }
    loadAndUpdate();
}

export async function mount(elId) {
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

    const powerEl = container.querySelector('.sfp-power-chart');
    const tempEl = container.querySelector('.sfp-temp-chart');
    if (!powerEl || !tempEl) return;

    chartEls = { power: powerEl, temp: tempEl };

    if (powerChart) { powerChart.destroy(); powerChart = null; }
    if (tempChart) { tempChart.destroy(); tempChart = null; }
    if (ponErrChart) { ponErrChart.destroy(); ponErrChart = null; }
    if (ponGemChart) { ponGemChart.destroy(); ponGemChart = null; }
    if (ponHostChart) { ponHostChart.destroy(); ponHostChart = null; }

    powerChart = new ApexCharts(powerEl, {
        ...baseOpts(200, 'dBm', v => v != null ? v.toFixed(1) + ' dBm' : '', {
            yaxis: { min: v => Math.floor(v - 2), max: v => Math.ceil(v + 2) } }),
        series: [], colors: PALETTE,
    });
    tempChart = new ApexCharts(tempEl, {
        ...baseOpts(160, '°C', v => v != null ? v.toFixed(0) + ' °C' : ''),
        series: [], colors: PALETTE,
    });

    await powerChart.render();
    await tempChart.render();

    // PON charts are mounted lazily (ensurePonChartsMounted) only once a module with
    // supplemental PON polling actually reports data - the ~99% of SFP setups without
    // it never create these instances.

    container.querySelectorAll('[data-range]').forEach(btn => {
        btn.addEventListener('click', () => {
            const hours = parseInt(btn.dataset.range);
            // Saved HERE rather than in selectPresetRange: a deep link's framing calls that
            // too, and a window the link chose must not become a remembered preference.
            saveWindowHours(WINDOW_TAB, hours);
            notifyWindowMoved();
            selectPresetRange(container, hours);
        });
    });

    container.querySelectorAll('[data-shift]').forEach(btn => {
        btn.addEventListener('click', () => { notifyWindowMoved(); shiftWindow(container, btn.dataset.shift); });
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
        notifyWindowMoved();
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

export function navigateToTime(isoTimestamp) {
    const ts = new Date(isoTimestamp).getTime();
    const windowMs = 10 * 60000; // 10 min window centered on event
    customFrom = new Date(ts - windowMs);
    customTo = new Date(ts + windowMs);
    isCustomRange = true;
    windowOffset = 0;

    const container = document.getElementById(containerId);
    if (container) {
        container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        container.querySelector('.custom-range-btn')?.classList.add('active');
        updateCustomLabel(container);
    }
    loadAndUpdate();
    startPoll();
}

export function soloModule(id) {
    if (!moduleMeta.length) return;
    const match = moduleMeta.find(m => m.id === id);
    if (!match) return;
    moduleMeta.forEach(m => { visibility[m.id] = m.id === id; });
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
    if (ponErrChart) { ponErrChart.destroy(); ponErrChart = null; }
    if (ponGemChart) { ponGemChart.destroy(); ponGemChart = null; }
    if (ponHostChart) { ponHostChart.destroy(); ponHostChart = null; }
    containerId = null;
    moduleMeta = [];
    visibility = {};
    ponCapableModules = [];
    chartEls = {};
    lastData = null;
    lastEvents = [];
    opticsMarkLayer.reset();
    ponMarkLayer.reset();
    currentRangeHours = 24;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    isInViewport = true;
    axisDate.reset();
}
