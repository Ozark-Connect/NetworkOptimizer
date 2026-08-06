// TODO: Extract time-range controls (presets, shift arrows, custom range popover,
// filter badges, poll interval scaling) into a shared module so latency-charts,
// device-health-charts, and future chart sets share one implementation.
import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { computeStats, renderStatsTable as renderTable } from './chart-stats.js?v=4';
import { valueSortedTooltip, tooltipHeld, alignedPoints } from './chart-tooltip.js?v=8';
import { renderFilterReset, isFiltered } from './chart-filter.js?v=4';
import { eventColor, chartSurfaceColor } from './chart-colors.js?v=2';

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
let lastMarkSignature = null;

function baseOpts(height, yTitle, yFormatter, extra) {
    return {
        chart: {
            type: 'line', height,
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

function updateVisibility() {
    deviceMeta.forEach(d => {
        const vis = visibility[d.mac] !== false;
        const allCharts = [tempChart, cpuChart, memChart, ...Object.values(customCharts)];
        for (const chart of allCharts) {
            if (!chart) continue;
            if (vis) chart.showSeries(d.name);
            else chart.hideSeries(d.name);
        }
    });
    applyAnnotations();
}

// Marks on the time axis for things that happened to a charted device: restarts, and the
// device's own alerts. The glyph says which kind it is and the colour says how bad, so the
// two read independently - a planned firmware restart and a panic are both ↻, in different
// colours, which is the distinction the operator is actually scanning for.
const EVENT_GLYPH = { reboot: '↻', alert: '⚠' };

// Every chart on the tab paired with the element it rendered into. ApexCharts draws annotation
// labels as SVG text, and the tooltip is attached by walking that SVG afterwards, so the
// element is needed as well as the instance.
function chartEntries() {
    return [
        [tempChart, chartEls.temp],
        [cpuChart, chartEls.cpu],
        [memChart, chartEls.mem],
        ...Object.keys(customCharts).map(k => [customCharts[k], chartEls[`custom:${k}`]]),
    ];
}

const SEVERITY_RANK = { info: 0, warning: 1, critical: 2 };

// Two marks closer together than this overlap into an unreadable smear - the label box is
// about 20px wide once padded. Folding at that distance is what keeps a flapping device from
// laying a solid row of glyphs across the 30d view, and it bounds the mark count at
// plotWidth/24 however many events land in the window. Nothing is dropped: a folded event is
// still listed in its cluster's tooltip.
const MARK_COLLISION_PX = 24;

// Milliseconds per pixel of plot, taken from the chart's own geometry rather than from the
// requested range: gridWidth excludes the y-axis gutter, and minX/maxX are the extents
// ApexCharts actually drew, including its own padding. Every chart on the tab shares a width
// and an x-window, so the first one that can answer speaks for all of them - which also
// guarantees the folds line up vertically instead of each chart clustering to its own
// slightly different answer.
//
// Asking every chart rather than only the temperature one matters: alignedPoints returns an
// empty series when a field is null throughout, so a site whose switch temperatures never
// arrive has an empty temperature chart - and reading geometry from that alone would silently
// disable folding on the CPU and Memory charts, which are exactly the ones carrying marks.
function markMsPerPx() {
    for (const [chart] of chartEntries()) {
        const g = chart?.w?.globals;
        if (!g || !(g.gridWidth > 0) || !(g.maxX > g.minX)) continue;
        return (g.maxX - g.minX) / g.gridWidth;
    }
    return null;
}

// Colour and glyph both come from the worst thing in the cluster so they can never disagree:
// a fold containing one kernel panic reads as a panic, not as the routine restart next to it.
function dominantMark(marks) {
    return marks.reduce((worst, m) =>
        (SEVERITY_RANK[m.severity] ?? 0) > (SEVERITY_RANK[worst.severity] ?? 0) ? m : worst);
}

// Events arrive time-ordered from the endpoint, so one pass does it. The gap is measured from
// the cluster's FIRST member, not its last, so the mark stays anchored where the run started
// rather than drifting rightward as the cluster absorbs more.
function clusterMarks(marks) {
    const msPerPx = markMsPerPx();
    // No geometry yet (first paint, or a range with no series to scale) means no clustering,
    // which is the pre-fold behaviour rather than a guess.
    if (msPerPx == null) return marks.map(m => ({ at: new Date(m.time).getTime(), marks: [m] }));

    const gapMs = MARK_COLLISION_PX * msPerPx;
    const clusters = [];
    for (const mark of marks) {
        const at = new Date(mark.time).getTime();
        const open = clusters[clusters.length - 1];
        if (open && at - open.at <= gapMs) open.marks.push(mark);
        else clusters.push({ at, marks: [mark] });
    }
    return clusters;
}

function markTime(time) {
    return new Date(time).toLocaleString(undefined,
        { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

function deviceNameFor(mac) {
    return deviceMeta.find(d => d.mac === mac)?.name;
}

function annotationTooltip(cluster) {
    if (cluster.marks.length === 1) {
        const mark = cluster.marks[0];
        const device = deviceNameFor(mark.mac);
        const lines = [`<strong>${escapeHtml(mark.title)}</strong>`];
        if (device) lines.push(escapeHtml(device));
        lines.push(escapeHtml(markTime(mark.time)));
        if (mark.detail) lines.push(escapeHtml(mark.detail));
        return lines.join('<br>');
    }

    // Every member is listed. The list scrolls rather than truncating, because a folded event
    // that is neither on the chart nor in the tooltip is simply lost.
    const rows = cluster.marks.map(mark => {
        const device = deviceNameFor(mark.mac);
        return `<div><span class="chart-annotation-time">${escapeHtml(markTime(mark.time))}</span> `
            + `${escapeHtml(mark.title)}${device ? ` - ${escapeHtml(device)}` : ''}</div>`;
    }).join('');

    return `<strong>${cluster.marks.length} events</strong>`
        + `<div class="chart-annotation-list">${rows}</div>`;
}

// ApexCharts stamps each label with rel="<index into the annotation array>", so each cluster is
// matched to its own label by that rather than by trusting NodeList order.
//
// Setting the attribute is all that is needed to get a tooltip: App.razor rescans for
// [data-tooltip] and initializes Tippy on anything new, which is how the other dynamically
// drawn badges work.
function tagAnnotationTooltips(el, clusters) {
    if (!el) return;
    clusters.forEach((cluster, i) => {
        const label = el.querySelector(`.apexcharts-xaxis-annotation-label[rel='${i}']`);
        if (!label) return;
        label.setAttribute('data-tooltip', annotationTooltip(cluster));
        label.setAttribute('data-tooltip-interactive', '');
        label.style.cursor = 'help';
    });
}

// Each redraw replaces the label elements, so the Tippy instances bound to the outgoing ones
// have to go with them - otherwise a 5s poll leaves an orphan per mark per tick.
function destroyAnnotationTooltips(el) {
    if (!el) return;
    el.querySelectorAll('.apexcharts-xaxis-annotation-label').forEach(label => label._tippy?.destroy());
}

function applyAnnotations() {
    // Filtering here rather than server-side keeps the badge toggles instant: hiding a device
    // drops its marks without a refetch, the same way it drops its line.
    const visible = lastEvents.filter(e => visibility[e.mac] !== false);
    const clusters = clusterMarks(visible);

    // A poll usually returns the same events over the same geometry, and redrawing then costs a
    // full annotation teardown - and a Tippy rebuild - for no visible change. The signature
    // covers what the marks are drawn FROM, so a new event, a badge toggle or a resize still
    // gets through.
    const signature = JSON.stringify([markMsPerPx(), clusters.map(c => [c.at, c.marks.length])]);
    if (signature === lastMarkSignature) return;
    lastMarkSignature = signature;

    const surface = chartSurfaceColor();
    const xaxis = clusters.map(cluster => {
        const lead = dominantMark(cluster.marks);
        const color = eventColor(lead.severity);
        const glyph = EVENT_GLYPH[lead.kind] || EVENT_GLYPH.alert;
        return {
            x: cluster.at,
            borderColor: color,
            strokeDashArray: 4,
            label: {
                text: cluster.marks.length > 1 ? `${glyph}${cluster.marks.length}` : glyph,
                borderColor: color,
                // ApexCharts' own stylesheet puts pointer-events: none on every annotation
                // label, which would leave these unhoverable and the tooltips dead. The class
                // is concatenated onto theirs, not swapped for it, so app.css can win on
                // specificity without !important.
                style: {
                    cssClass: 'chart-event-mark',
                    color,
                    background: surface,
                    fontSize: '11px',
                    padding: { left: 4, right: 4, top: 2, bottom: 2 },
                },
            },
        };
    });

    for (const [chart, el] of chartEntries()) {
        if (!chart) continue;
        destroyAnnotationTooltips(el);
        // Wrapped rather than chained directly: the labels only exist once the update has
        // rendered, and updateOptions is not promise-returning in every ApexCharts build.
        Promise.resolve(chart.updateOptions({ annotations: { xaxis } }, false, false))
            .then(() => tagAnnotationTooltips(el, clusters))
            .catch(e => console.warn('Device health annotations failed to draw', e));
    }
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
    // Set before updateVisibility below, which is what draws the marks.
    lastEvents = data.events || [];
    const makeSeries = (field) => data.devices.map(d => ({
        name: d.name,
        color: hashColor(d.name),
        data: alignedPoints(d.data || [], p => p[field], 'time', GAP_BRIDGE_MS),
    }));
    if (tempChart) tempChart.updateSeries(makeSeries('temp'), false);
    if (cpuChart) cpuChart.updateSeries(makeSeries('cpu'), false);
    if (memChart) memChart.updateSeries(makeSeries('mem'), false);

    const newDefs = data.customFields || [];
    const container = document.getElementById(containerId);
    if (container) await syncCustomCharts(container, data.devices, newDefs);

    updateVisibility();
    lastData = data;
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
            data: (d.custom?.[def.fieldName] || []).map(p => ({
                x: new Date(p.time).getTime(), y: p.value
            })),
        }));

        if (customCharts[def.fieldName]) {
            customCharts[def.fieldName].updateSeries(series, false);
        } else {
            let chartDiv = customContainer.querySelector(`[data-custom-field="${def.fieldName}"]`);
            if (!chartDiv) {
                const card = document.createElement('div');
                card.className = 'chart-card';
                card.style.marginTop = '1rem';
                card.innerHTML = `<div class="chart-header"><h3 class="chart-title">${escapeHtml(def.description)}</h3></div><div data-custom-field="${escapeHtml(def.fieldName)}"></div>`;
                customContainer.appendChild(card);
                chartDiv = card.querySelector(`[data-custom-field]`);
            }
            const chart = new ApexCharts(chartDiv, {
                ...baseOpts(200, def.description, fmtCustom),
                series, colors: PALETTE,
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

    const rows = lastData.devices.map(d => {
        const pts = d.data || [];
        const temp = computeStats(pts.map(p => p.temp).filter(v => v != null));
        const cpu = computeStats(pts.map(p => p.cpu).filter(v => v != null));
        const mem = computeStats(pts.map(p => p.mem).filter(v => v != null));
        const baseValues = [temp?.mean, temp?.min, temp?.max, cpu?.mean, cpu?.min, cpu?.max, mem?.mean, mem?.min, mem?.max];

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
    const container = document.getElementById(elId);
    if (!container) return;

    const tempEl = container.querySelector('.health-temp-chart');
    const cpuEl = container.querySelector('.health-cpu-chart');
    const memEl = container.querySelector('.health-mem-chart');
    if (!tempEl || !cpuEl || !memEl) return;

    if (tempChart) { tempChart.destroy(); tempChart = null; }
    if (cpuChart) { cpuChart.destroy(); cpuChart = null; }
    if (memChart) { memChart.destroy(); memChart = null; }

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

    await tempChart.render();
    await cpuChart.render();
    await memChart.render();

    // Preset range buttons
    container.querySelectorAll('[data-range]').forEach(btn => {
        btn.addEventListener('click', () => selectPresetRange(container, parseInt(btn.dataset.range)));
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
    for (const chart of Object.values(customCharts)) chart.destroy();
    customCharts = {};
    customFieldDefs = [];
    chartEls = {};
    containerId = null;
    deviceMeta = [];
    visibility = {};
    lastData = null;
    lastEvents = [];
    lastMarkSignature = null;
    currentRangeHours = 1;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    isInViewport = true;
}
