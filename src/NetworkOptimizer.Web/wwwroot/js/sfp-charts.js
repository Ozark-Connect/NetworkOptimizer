// SFP DDM time-series charts: RX/TX power, temperature, voltage.
// Same control pattern as latency-charts.js and device-health-charts.js.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { computeStats, renderStatsTable as renderTable } from './chart-stats.js?v=7';
import { valueSortedTooltip, tooltipHeld, alignedPoints } from './chart-tooltip.js?v=10';
import { renderFilterReset, isFiltered } from './chart-filter.js?v=5';
import { createMarkLayer } from './chart-event-marks.js?v=1';
import { createAxisDateCaption } from './chart-axis-date.js?v=2';

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

function baseOpts(height, yTitle, yFormatter, extra) {
    const base = {
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
        powerChart.updateOptions({ stroke: { curve: 'smooth', width: 2, dashArray: powerDash } }, false, false);
        powerChart.updateSeries(powerSeries, false);
    }
    if (tempChart) tempChart.updateSeries(tSeries, false);
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
    if (!ponErrChart) return;
    try {
        const multi = visiblePon.length > 1;
        const errSeries = [], gemSeries = [], hostSeries = [];
        visiblePon.forEach(m => {
            const pts = m.pon;
            const prefix = multi ? `${m.label} ` : '';
            errSeries.push(
                { name: `${prefix}BIP`, data: ponPoints(pts, 'bip') },
                { name: `${prefix}FEC`, data: ponPoints(pts, 'fec') },
                { name: `${prefix}FEC corrected`, data: ponPoints(pts, 'fecCorr') },
                { name: `${prefix}HEC`, data: ponPoints(pts, 'hec') },
                { name: `${prefix}GEM drops`, data: ponPoints(pts, 'gemDrop') },
                { name: `${prefix}Allocs lost`, data: ponPoints(pts, 'allocLost') },
            );
            gemSeries.push(
                { name: `${prefix}RX frames`, data: ponPoints(pts, 'gemRx') },
                { name: `${prefix}TX frames`, data: ponPoints(pts, 'gemTx') },
            );
            hostSeries.push(
                { name: `${prefix}FCS errors`, data: ponPoints(pts, 'lanFcs') },
                { name: `${prefix}TX drops`, data: ponPoints(pts, 'lanDrop') },
                { name: `${prefix}Buffer overflows`, data: ponPoints(pts, 'lanOvfl') },
            );
        });
        ponErrChart.updateSeries(errSeries.filter(s => s.data.length), false);
        ponGemChart.updateSeries(gemSeries.filter(s => s.data.length), false);
        ponHostChart.updateSeries(hostSeries.filter(s => s.data.length), false);
        renderPonDetails(container, visiblePon);
    } catch (e) { /* leave the previous render if a chart update fails */ }
}

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data?.modules) return;
    moduleMeta = data.modules.map((m, i) => ({
        id: m.id, label: m.label, color: PALETTE[i % PALETTE.length],
    }));
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

// Same encoding PonLinkStateExtensions.ToInfluxValue uses for pon_link_status.
const PLOAM_LABELS = {
    initial: 'Initializing (O1)', standby: 'Standby (O2)', serial_number: 'Authenticating (O3)',
    ranging: 'Ranging (O4)', operation: 'Connected (O5)', popup: 'Signal Lost (O6)',
    emergency_stop: 'Disabled (O7)',
};

// Every series keeps the same x values, with gaps as null rather than dropped.
//
// Filtering each series down to its own non-null points gave the series different x
// arrays, and a shared ApexCharts tooltip resolves the other series by data-point INDEX
// rather than by timestamp - so once the arrays diverged, only the first series lined up
// and PON Errors showed "BIP: 0" while every non-zero counter beside it went unlisted.
// A null y still breaks the line where there is no reading, which is what the filter was
// really for; valueSortedTooltip skips nulls, so the gaps cost no tooltip rows either.
//
// A counter the module never reports at all returns nothing, so it is dropped as a
// series rather than drawn as an empty one.
function ponPoints(pts, key) {
    return alignedPoints(pts, p => p[key]);
}

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
    const fmtUp = s => s == null ? '-'
        : `${Math.floor(s / 86400)}d ${Math.floor(s % 86400 / 3600)}h ${Math.floor(s % 3600 / 60)}m`;
    const rows = withPon.map(m => {
        const last = [...m.pon].reverse().find(p => p.state != null) || m.pon[m.pon.length - 1];
        const state = PLOAM_LABELS[last.state] || last.state || '-';
        const fec = last.dsFec == null && last.usFec == null ? '-'
            : `${last.dsFec ? 'on' : 'off'} / ${last.usFec ? 'on' : 'off'}`;
        return `<tr>
            <td>${escapeHtml(m.label)}</td>
            <td>${escapeHtml(state)}</td>
            <td>${last.onuId ?? '-'}</td>
            <td>${fec}</td>
            <td>${last.respTime ?? '-'}</td>
            <td>${fmtUp(last.uptime)}</td>
        </tr>`;
    }).join('');
    el.innerHTML = `<div class="table-responsive"><table class="data-table">
        <thead><tr><th>Module</th><th>PLOAM State</th><th>ONU ID</th><th>FEC DS / US</th><th>Response Time</th><th>ONT Uptime</th></tr></thead>
        <tbody>${rows}</tbody></table></div>`;
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
            values: [rx?.mean, rx?.min, rx?.max, tx?.mean, tx?.min, tx?.max, temp?.mean, temp?.min, temp?.max] };
    });

    renderTable(el, container, {
        nameHeader: 'Module', rows, showAllRows: showAll,
        columns: [
            { header: 'RX Mean', format: fmtDbm }, { header: 'RX Min', format: fmtDbm }, { header: 'RX Max', format: fmtDbm },
            { header: 'TX Mean', format: fmtDbm }, { header: 'TX Min', format: fmtDbm }, { header: 'TX Max', format: fmtDbm },
            { header: 'Temp Mean', format: fmtTemp }, { header: 'Temp Min', format: fmtTemp }, { header: 'Temp Max', format: fmtTemp },
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
    containerId = elId;
    const container = document.getElementById(elId);
    if (!container) return;

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
