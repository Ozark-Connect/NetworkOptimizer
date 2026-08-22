// TODO: Extract time-range controls (presets, shift arrows, custom range popover,
// filter badges, poll interval scaling) into a shared module so latency-charts,
// device-health-charts, and future chart sets share one implementation.
import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { computeStats, renderStatsTable as renderTable } from './chart-stats.js?v=7';
import { valueSortedTooltip, tooltipHeld, alignedPoints } from './chart-tooltip.js?v=15';
import { renderFilterReset, isFiltered } from './chart-filter.js?v=6';
import { createMarkLayer } from './chart-event-marks.js?v=4';
import { createAxisDateCaption } from './chart-axis-date.js?v=3';
import { syncIdentity, extentsOf, spanTo } from './chart-sync.js?v=7';
import { awaitContainer } from './chart-mount.js?v=1';
import { loadWindowHours, saveWindowHours, markActiveRange } from './chart-window.js?v=2';

// Storage scope for this tab's remembered time window.
const WINDOW_TAB = 'device-health';

// A device answers SNMP but can still miss a single field on a poll - a temperature or
// memory OID that times out is written as no value rather than a zero, so the row arrives
// with that one field absent. Measured over 24h these holes are 30-210s (98% of intervals
// are the normal 30s cadence, nothing beyond 310s), so 5 minutes spans every one of them
// with headroom while leaving a genuine outage broken.
const GAP_BRIDGE_MS = 5 * 60 * 1000;

const PALETTE = window.Apex?.colors || ['#7EB26D', '#EAB839', '#6ED0E0', '#EF843C', '#E24D42', '#1F78C1'];
const _colorCache = {};
function hashColor(id) {
    if (_colorCache[id]) return _colorCache[id];
    let h = 0;
    for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) >>> 0;
    const used = new Set(Object.values(_colorCache));
    let idx = h % PALETTE.length;
    const start = idx;
    while (used.has(PALETTE[idx])) {
        idx = (idx + 1) % PALETTE.length;
        if (idx === start) break;
    }
    _colorCache[id] = PALETTE[idx];
    return PALETTE[idx];
}
const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }

const POLL_INTERVALS = { 0: 5000, 1: 5000, 6: 10000, 24: 15000, 168: 30000, 720: 30000 };
const RANGE_MS = { 0: 15*60000, 1: 3600000, 6: 6*3600000, 24: 86400000, 168: 7*86400000, 720: 30*86400000 };

let tempChart = null;
let cpuChart = null;
let memChart = null;
let fanChart = null;
let customCharts = {};
let customFieldDefs = [];
let pollTimer = null;
let currentRangeHours = 1;
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
let lastEvents = [];
let chartEls = {};
let markResizeTimer = null;

// Every chart this tab stacks shares one group - see chart-sync.js.
const SYNC_GROUP = 'device-health';
let groupExtents = null;
// The group's extents on every chart, so ApexCharts passes the hover between them - see spanTo.
function padFirst(series) {
    return spanTo(series, groupExtents);
}


function baseOpts(height, yTitle, yFormatter, extra, group = SYNC_GROUP) {
    return {
        chart: {
            type: 'line', height,
            ...syncIdentity(group),
            background: 'transparent',
            toolbar: { show: false },
            zoom: { enabled: !matchMedia('(pointer:coarse)').matches, type: 'x', allowMouseWheelZoom: false },
            events: { beforeZoom: (ctx, opts) => applyDragZoom(opts?.xaxis) },
            animations: { enabled: false },
        },
        stroke: { curve: 'smooth', width: 2 },
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
            min: 0,
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
        const resp = await fetch(`/api/monitoring/device-health-chart?${buildQueryParams()}`,
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
            <span>${escapeHtml(d.name)}</span>
        </button>`;
    }).join('');
    if (!el._delegated) {
        el._delegated = true;
        el.addEventListener('click', (e) => {
            const btn = e.target.closest('button[data-mac]');
            if (!btn) return;
            const mac = btn.dataset.mac;

            if (e.ctrlKey || e.metaKey) {
                visibility[mac] = visibility[mac] === false ? undefined : false;
            } else {
                const allVis = deviceMeta.every(d => visibility[d.mac] !== false);
                const onlyThis = visibility[mac] !== false
                    && deviceMeta.filter(d => d.mac !== mac).every(d => visibility[d.mac] === false);
                if (onlyThis) { visibility = {}; }
                else if (allVis) { deviceMeta.forEach(d => visibility[d.mac] = d.mac === mac); }
                else { visibility[mac] = visibility[mac] === false; }
            }
            updateVisibility();
            renderBadges(container);
            renderStatsTable(container, false);
        });
    }

    // Last: the chip rebuild above wipes the row, so the reset is re-added after it.
    renderFilterReset(el, isFiltered(visibility), () => { visibility = {}; updateVisibility(); renderBadges(container); });
}

// Draws exactly the devices that should be on screen, in one update per chart.
//
// This used to call showSeries/hideSeries per device on every chart, and each of those is a full
// redraw - so one chip click cost a redraw per device it changed, on each of temp, CPU, memory and
// every custom chart. Colors are hashed off the device name, so filtering never re-colors a line.
function drawSeries() {
    const devices = (lastData?.devices || []).filter(d => visibility[d.mac] !== false);
    const makeSeries = (field) => devices.map(d => ({
        name: d.name,
        color: hashColor(d.name),
        data: alignedPoints(d.data || [], p => p[field], 'time', GAP_BRIDGE_MS),
    }));
    if (tempChart) tempChart.updateSeries(padFirst(makeSeries('temp')), false);
    if (cpuChart) cpuChart.updateSeries(padFirst(makeSeries('cpu')), false);
    if (memChart) memChart.updateSeries(padFirst(makeSeries('mem')), false);
    if (fanChart) fanChart.updateSeries(padFirst(makeSeries('fan')), false);
    for (const [field, chart] of Object.entries(customCharts)) {
        if (!chart) continue;
        chart.updateSeries(padFirst(devices.map(d => ({
            name: d.name,
            color: hashColor(d.name),
            data: customPoints(d, field),
        }))), false);
    }
}

function customPoints(d, field) {
    return (d.custom?.[field] || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value }));
}

function updateVisibility() {
    drawSeries();
    applyAnnotations();
}

// Every chart on the tab paired with the element it rendered into, which is what the mark
// layer needs to reach the annotation labels it draws.
function chartEntries() {
    return [
        [tempChart, chartEls.temp],
        [cpuChart, chartEls.cpu],
        [memChart, chartEls.mem],
        [fanChart, chartEls.fan],
        ...Object.keys(customCharts).map(k => [customCharts[k], chartEls[`custom:${k}`]]),
    ].filter(([c]) => c);
}

const markLayer = createMarkLayer({ charts: chartEntries });
const axisDate = createAxisDateCaption({ charts: chartEntries, window: effectiveWindow });

function applyAnnotations() {
    markLayer.apply(lastEvents, visibility);
}


// A narrower plot fits fewer marks before they collide, so the folds have to be recomputed.
// Debounced because ApexCharts is redrawing on the same events, and left to settle after it.
function onMarkResize() {
    clearTimeout(markResizeTimer);
    markResizeTimer = setTimeout(applyAnnotations, 200);
}

const fmtCustom = v => v != null ? (Number.isInteger(v) ? v.toLocaleString() : v.toFixed(2)) : '-';

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data?.devices) return;
    deviceMeta = data.devices.map(d => ({
        name: d.name, mac: d.mac, color: hashColor(d.name),
    }));
    groupExtents = extentsOf((data.devices || []).map(d => d.data || []));
    // Set before updateVisibility below, which is what draws the marks.
    lastEvents = data.events || [];
    const newDefs = data.customFields || [];
    const container = document.getElementById(containerId);
    // Before updateVisibility, which draws from it.
    lastData = data;
    const hasFan = data.devices.some(d => (d.data || []).some(p => p.fan != null));
    const fanCard = container?.querySelector('.health-fan-card');
    if (fanCard) fanCard.style.display = hasFan ? '' : 'none';
    if (container) await syncCustomCharts(container, data.devices, newDefs);

    // Ahead of the redraw below - see apply().
    axisDate.apply();
    updateVisibility();
    if (container) {
        renderBadges(container);
        renderStatsTable(container);
    }
}

async function syncCustomCharts(container, devices, defs) {
    const customContainer = container.querySelector('.health-custom-charts');
    if (!customContainer) return;

    const currentKeys = new Set(Object.keys(customCharts));
    const newKeys = new Set(defs.map(d => d.fieldName));

    for (const key of currentKeys) {
        if (!newKeys.has(key)) {
            customCharts[key].destroy();
            delete customCharts[key];
            delete chartEls[`custom:${key}`];
        }
    }

    for (const def of defs) {
        const series = devices.map(d => ({
            name: d.name,
            color: hashColor(d.name),
            data: customPoints(d, def.fieldName),
        }));
        const padded = padFirst(series);

        if (customCharts[def.fieldName]) {
            customCharts[def.fieldName].updateSeries(padded, false);
        } else {
            let chartDiv = customContainer.querySelector(`[data-custom-field="${def.fieldName}"]`);
            if (!chartDiv) {
                const card = document.createElement('div');
                card.className = 'chart-card';
                card.innerHTML = `<div class="chart-header"><h3 class="chart-title">${escapeHtml(def.description)}</h3></div><div data-custom-field="${escapeHtml(def.fieldName)}"></div>`;
                customContainer.appendChild(card);
                chartDiv = card.querySelector(`[data-custom-field]`);
            }
            const chart = new ApexCharts(chartDiv, {
                ...baseOpts(200, def.description, fmtCustom),
                series: padded, colors: PALETTE,
            });
            await chart.render();
            customCharts[def.fieldName] = chart;
            chartEls[`custom:${def.fieldName}`] = chartDiv;
        }
    }

    customFieldDefs = defs;
}

const fmtTemp = v => v != null ? v.toFixed(1) : '-';
const fmtPct = v => v != null ? v.toFixed(1) + '%' : '-';
const fmtRpm = v => v != null ? Math.round(v).toLocaleString() : '-';

function renderStatsTable(container, showAll) {
    const el = container.querySelector('.health-stats-table');
    if (!el || !lastData?.devices?.length) { if (el) el.innerHTML = ''; return; }

    const customCols = [];
    for (const def of customFieldDefs) {
        customCols.push(
            { header: `${def.description} Mean`, format: fmtCustom },
            { header: `${def.description} Min`, format: fmtCustom },
            { header: `${def.description} Max`, format: fmtCustom },
        );
    }

    const hasFanData = lastData.devices.some(d => (d.data || []).some(p => p.fan != null));
    const fanCols = hasFanData ? [
        { header: 'Fan Mean', format: fmtRpm }, { header: 'Fan Min', format: fmtRpm }, { header: 'Fan Max', format: fmtRpm },
    ] : [];

    const rows = lastData.devices.map(d => {
        const pts = d.data || [];
        const temp = computeStats(pts.map(p => p.temp).filter(v => v != null));
        const cpu = computeStats(pts.map(p => p.cpu).filter(v => v != null));
        const mem = computeStats(pts.map(p => p.mem).filter(v => v != null));
        const baseValues = [temp?.mean, temp?.min, temp?.max, cpu?.mean, cpu?.min, cpu?.max, mem?.mean, mem?.min, mem?.max];

        if (hasFanData) {
            const fan = computeStats(pts.map(p => p.fan).filter(v => v != null));
            baseValues.push(fan?.mean, fan?.min, fan?.max);
        }

        for (const def of customFieldDefs) {
            const vals = (d.custom?.[def.fieldName] || []).map(p => p.value).filter(v => v != null);
            const stats = computeStats(vals);
            baseValues.push(stats?.mean, stats?.min, stats?.max);
        }

        return { id: d.mac, label: d.name, color: hashColor(d.name),
            visible: deviceMeta.some(dm => dm.mac === d.mac) && visibility[d.mac] !== false,
            values: baseValues };
    });

    renderTable(el, container, {
        nameHeader: 'Device', rows, showAllRows: showAll,
        columns: [
            { header: 'Temp Mean', format: fmtTemp }, { header: 'Temp Min', format: fmtTemp }, { header: 'Temp Max', format: fmtTemp },
            { header: 'CPU Mean', format: fmtPct }, { header: 'CPU Min', format: fmtPct }, { header: 'CPU Max', format: fmtPct },
            { header: 'Mem Mean', format: fmtPct }, { header: 'Mem Min', format: fmtPct }, { header: 'Mem Max', format: fmtPct },
            ...fanCols,
            ...customCols,
        ],
        filter: { meta: () => deviceMeta, key: 'mac', visibility: () => visibility,
            resetVisibility: () => { visibility = {}; },
            onChanged: (c) => { updateVisibility(); renderBadges(c); renderStatsTable(c, true); } },
    });
}

function isVisible() { return isInViewport; }

function startPoll() {
    stopPoll();
    if (windowOffset !== 0 || isCustomRange) return;
    if (!isVisible()) return;
    pollTimer = setInterval(() => { if (!tooltipHeld(document.getElementById(containerId))) loadAndUpdate(); }, POLL_INTERVALS[currentRangeHours] || 30000);
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
        const rangeMs = RANGE_MS[hours] || 3600000;
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

    const tempEl = container.querySelector('.health-temp-chart');
    const cpuEl = container.querySelector('.health-cpu-chart');
    const memEl = container.querySelector('.health-mem-chart');
    const fanEl = container.querySelector('.health-fan-chart');
    if (!tempEl || !cpuEl || !memEl) return;

    if (tempChart) { tempChart.destroy(); tempChart = null; }
    if (cpuChart) { cpuChart.destroy(); cpuChart = null; }
    if (memChart) { memChart.destroy(); memChart = null; }
    if (fanChart) { fanChart.destroy(); fanChart = null; }

    chartEls = { temp: tempEl, cpu: cpuEl, mem: memEl };

    tempChart = new ApexCharts(tempEl, { ...baseOpts(200, '°C', v => v != null ? v.toFixed(0) + ' °C' : ''), series: [], colors: PALETTE });
    cpuChart = new ApexCharts(cpuEl, {
        ...baseOpts(200, 'CPU %', v => v != null ? v.toFixed(0) + '%' : ''),
        yaxis: { min: 0, max: v => Math.max(v * 1.1, 30), title: { text: 'CPU %', style: { color: '#9ca3af' } }, labels: { style: { colors: '#9ca3af' }, formatter: v => v != null ? v.toFixed(0) + '%' : '' } },
        series: [], colors: PALETTE,
    });
    memChart = new ApexCharts(memEl, {
        ...baseOpts(200, 'Memory %', v => v != null ? v.toFixed(0) + '%' : ''),
        yaxis: { min: 0, max: v => Math.max(v * 1.1, 50), title: { text: 'Memory %', style: { color: '#9ca3af' } }, labels: { style: { colors: '#9ca3af' }, formatter: v => v != null ? v.toFixed(0) + '%' : '' } },
        series: [], colors: PALETTE,
    });

    if (fanEl) {
        chartEls.fan = fanEl;
        fanChart = new ApexCharts(fanEl, {
            ...baseOpts(200, 'RPM', v => v != null ? Math.round(v).toLocaleString() + ' RPM' : ''),
            yaxis: { min: 0, title: { text: 'RPM', style: { color: '#9ca3af' } }, labels: { style: { colors: '#9ca3af' }, formatter: v => v != null ? Math.round(v).toLocaleString() : '' } },
            series: [], colors: PALETTE,
        });
        await fanChart.render();
    }

    await tempChart.render();
    await cpuChart.render();
    await memChart.render();

    // Preset range buttons
    container.querySelectorAll('[data-range]').forEach(btn => {
        btn.addEventListener('click', () => {
            const hours = parseInt(btn.dataset.range);
            // Saved HERE rather than in selectPresetRange: a deep link's framing calls that
            // too, and a window the link chose must not become a remembered preference.
            saveWindowHours(WINDOW_TAB, hours);
            selectPresetRange(container, hours);
        });
    });

    // Shift arrows
    container.querySelectorAll('[data-shift]').forEach(btn => {
        btn.addEventListener('click', () => shiftWindow(container, btn.dataset.shift));
    });

    // Custom range popover
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

function frameCustomWindow(ts, halfWindowMs) {
    customFrom = new Date(ts - halfWindowMs);
    customTo = new Date(ts + halfWindowMs);
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
    startPoll();
}

/**
 * Frames the window on a moment carried in from a live tile that was PARKED on that instant:
 * 30 minutes either side, so the jump arrives at this tab's own 1h default rather than the 15m
 * the latency charts land on. Temperature, CPU and memory drift over a shift; an hour is what
 * makes a climb read as a climb, where 15 minutes of it reads as a flat line.
 */
export function frameMoment(isoTimestamp) {
    frameCustomWindow(new Date(isoTimestamp).getTime(), 30 * 60000);
}

/**
 * Frames a trailing 1-hour window for a jump made while the tile was live. Trailing rather than
 * centered on now: half the window would be in the future, and a chart frozen at the instant of
 * the click looks exactly like a device that stopped reporting.
 */
export function frameTrailing() {
    const container = document.getElementById(containerId);
    if (!container) return;
    // A fresh mount already opens on a trailing hour, and re-selecting it would only buy a second
    // fetch of the same window.
    if (currentRangeHours === 1 && !isCustomRange && windowOffset === 0) return;
    selectPresetRange(container, 1);
}

export function soloDevice(mac) {
    if (!deviceMeta.length) return;
    const match = deviceMeta.find(d => d.mac === mac);
    if (!match) return;
    deviceMeta.forEach(d => { visibility[d.mac] = d.mac === mac; });
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
    if (tempChart) { tempChart.destroy(); tempChart = null; }
    if (cpuChart) { cpuChart.destroy(); cpuChart = null; }
    if (memChart) { memChart.destroy(); memChart = null; }
    if (fanChart) { fanChart.destroy(); fanChart = null; }
    for (const chart of Object.values(customCharts)) chart.destroy();
    customCharts = {};
    customFieldDefs = [];
    chartEls = {};
    containerId = null;
    deviceMeta = [];
    visibility = {};
    lastData = null;
    lastEvents = [];
    markLayer.reset();
    currentRangeHours = 1;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    isInViewport = true;
    axisDate.reset();
}
