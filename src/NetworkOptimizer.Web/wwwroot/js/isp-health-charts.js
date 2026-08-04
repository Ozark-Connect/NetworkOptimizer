// ISP Health per-ASN RTT chart - pure JS ApexCharts fed by
// /api/monitoring/isp-health/asn-series. Fixed 24 h window; congestion events
// render as shaded x-axis ranges, path shifts as annotation lines.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { valueSortedTooltip, tooltipHeld, alignedPoints } from './chart-tooltip.js?v=8';
import { renderFilterReset, isFiltered } from './chart-filter.js?v=4';

const PALETTE = ['#2ba89a', '#3b82f6', '#a78bfa', '#ef5858', '#f59e0b', '#10b981'];
const POLL_MS = 60000;

let chart = null;
let pollTimer = null;
let fetchController = null;
let resetBtn = null;
// Floor for the gap budget; past ~17 h the server's buckets are wider than this and alignedPoints
// scales off their own cadence instead. Below it, a single missed minute is noise rather than an
// outage and should not chop the line.
const GAP_BRIDGE_MS = 5 * 60 * 1000;
let isZoomed = false;
// The drag-zoom window, kept so a series toggle can put it back. Toggling visibility redraws from
// the chart's default axis, which is not a zoom event and so leaves nothing behind to restore from.
let zoomWindow = null;
// Set while WE re-apply that window, so the resulting event is not mistaken for a user gesture and
// echoed back to the Blazor panel as a fresh zoom.
let restoringZoom = false;
let dotNetRef = null;
// null = default 48 h cached view; { from, to } ISO strings = a filter-selected window.
let win = null;
// Event annotation types hidden by the panel's category filter pills; display-only.
let hiddenTypes = new Set();

// One entry per ASN series, in chart order, for the filter chips below the plot.
let asnMeta = [];
// name -> false when hidden. Absent means visible, so a new ASN appears without asking.
let seriesVisibility = {};
let badgesEl = null;

const _escSpan = document.createElement('span');
function escapeHtml(v) { _escSpan.textContent = v ?? ''; return _escSpan.innerHTML; }
let lastEvents = [];

function buildOpts() {
    return {
        chart: {
            type: 'line',
            height: 280,
            background: 'transparent',
            toolbar: { show: false },
            zoom: { enabled: !matchMedia('(pointer:coarse)').matches, type: 'x', autoScaleYaxis: true, allowMouseWheelZoom: false },
            parentHeightOffset: 0,
            animations: { enabled: false },
            events: {
                zoomed: (ctx, opts) => {
                    const min = opts?.xaxis?.min, max = opts?.xaxis?.max;
                    zoomWindow = min != null ? { min, max } : null;
                    if (restoringZoom) return;
                    setZoomed(min != null);
                    notifyZoom(min, max);
                },
            },
        },
        series: [],
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
            title: { text: 'ms', style: { color: '#9ca3af', fontSize: '9px' } },
            labels: {
                style: { colors: '#9ca3af', fontSize: '10px' },
                formatter: v => v != null ? v.toFixed(1) : '',
            },
            axisBorder: { show: false },
            axisTicks: { show: false },
        },
        grid: {
            borderColor: '#374151',
            strokeDashArray: 3,
            padding: { right: 6, top: -8, bottom: 0 },
        },
        responsive: [{
            breakpoint: 768,
            options: {
                yaxis: {
                    min: 0,
                    show: false,
                },
                grid: { padding: { left: -5, right: -5, top: -8, bottom: 0 } },
            },
        }],
        // Our own chips below the plot instead: same look and click behaviour as every other
        // chart tab, where the built-in legend gave a different affordance for the same job.
        legend: { show: false },
        tooltip: {
            theme: 'dark',
            shared: true,
            x: { format: 'MMM dd, HH:mm' },
            // The shared renderer: rows ordered by value so they match the vertical order of the
            // lines, plus a hover dot on each. Its own format because this axis prints a bare
            // number under an "ms" title while the tooltip has always carried the unit.
            custom: (ctx) => valueSortedTooltip(ctx, { format: v => v.toFixed(1) + ' ms', omitSeconds: true }),
        },
        noData: { text: 'No path data in the last 24 hours', style: { color: '#64748b' } },
    };
}

function buildAnnotations(events) {
    const xaxis = [];
    for (const e of events) {
        if (hiddenTypes.has(e.type)) continue;
        if (e.type === 'congestion') {
            xaxis.push({
                x: new Date(e.start).getTime(),
                x2: new Date(e.end).getTime(),
                fillColor: e.shared ? '#ef5858' : '#f59e0b',
                opacity: 0.12,
                label: {
                    text: e.label,
                    style: { color: '#ededef', background: e.shared ? '#7f1d1d' : '#78350f', fontSize: '10px' },
                },
            });
        } else if (e.type === 'unreachable') {
            xaxis.push({
                x: new Date(e.start).getTime(),
                x2: e.end ? new Date(e.end).getTime() : undefined,
                fillColor: '#a78bfa',
                opacity: 0.12,
                label: {
                    text: e.label,
                    style: { color: '#ededef', background: '#581c87', fontSize: '10px' },
                },
            });
        } else {
            xaxis.push({
                x: new Date(e.start).getTime(),
                borderColor: '#4797ff',
                strokeDashArray: 4,
                label: {
                    text: e.label,
                    style: { color: '#ededef', background: '#1e3a5f', fontSize: '10px' },
                    orientation: 'horizontal',
                },
            });
        }
    }
    return { xaxis };
}

function setZoomed(zoomed) {
    isZoomed = zoomed;
    if (resetBtn) resetBtn.style.display = zoomed ? 'inline-flex' : 'none';
}

// Tell the Blazor panel the current zoom range (epoch ms), or null/null when cleared, so it can
// filter the Path & Congestion Events list to the visible window. scrollToChart is set only on the
// Reset zoom button, so the panel can keep the chart in view when the list expands above it.
function notifyZoom(min, max, scrollToChart = false) {
    try { dotNetRef?.invokeMethodAsync('OnChartZoom', min ?? null, max ?? null, scrollToChart); }
    catch { /* ref disposed / not set */ }
}

function resetZoom() {
    if (!chart) return;
    chart.updateOptions({ xaxis: { min: undefined, max: undefined } }, false, false);
    zoomWindow = null;
    setZoomed(false);
    notifyZoom(null, null, true);
    loadAndUpdate();
}

async function loadAndUpdate() {
    if (!chart) return;
    fetchController?.abort();
    fetchController = new AbortController();
    try {
        let url = '/api/monitoring/isp-health/asn-series';
        const params = [];
        if (win) params.push(`from=${encodeURIComponent(win.from)}`, `to=${encodeURIComponent(win.to)}`);
        // Selected WAN (null = primary): the panel's WAN selector routes the chart to the
        // matching per-WAN report so lines and event annotations always agree with the score.
        if (wanKey) params.push(`wan=${encodeURIComponent(wanKey)}`);
        if (params.length) url += `?${params.join('&')}`;
        const resp = await fetch(url, { credentials: 'same-origin', signal: fetchController.signal });
        if (!resp.ok) return;
        const json = await resp.json();

        const series = (json.asns || []).map((a, i) => ({
            name: a.name,
            color: PALETTE[i % PALETTE.length],
            // A total outage leaves NO row for that stretch - the endpoint's merged axis is built
            // from the buckets that exist - so the readings either side were adjacent and the line
            // carried straight over it. A null is inserted where the cadence breaks, which draws
            // the gap the other tabs draw. Interpolation stays off: where the endpoint already
            // nulled a series because that ASN alone went quiet, that break is the truth and is
            // drawn as-is.
            data: alignedPoints(a.points || [], p => p.value, 'time', GAP_BRIDGE_MS, false),
        }));

        asnMeta = series.map(x => ({ name: x.name, color: x.color }));
        // Drop hidden entries for ASNs that are no longer on the path, or a vanished name would
        // keep a series hidden forever with no chip left to turn it back on.
        const live = new Set(asnMeta.map(m => m.name));
        Object.keys(seriesVisibility).forEach(k => { if (!live.has(k)) delete seriesVisibility[k]; });
        renderBadges();

        lastEvents = json.events || [];
        chart.updateOptions({ annotations: buildAnnotations(lastEvents) }, false, false);
        // Preserve the user's drag-zoom; a series refresh while zoomed would snap back
        // updateSeries resets per-series visibility, so re-apply after every refresh.
        if (!isZoomed) { chart.updateSeries(series, false); applySeriesVisibility(); }
    } catch (e) {
        if (e.name !== 'AbortError') console.warn('isp-health chart load failed', e);
    }
}

function applySeriesVisibility() {
    if (!chart) return;
    asnMeta.forEach(m => {
        if (seriesVisibility[m.name] === false) chart.hideSeries(m.name);
        else chart.showSeries(m.name);
    });

    // hideSeries/showSeries redraw against the default axis range, so a chip click threw away a
    // drag-zoom - you would zoom into an event, isolate the provider you wanted to look at, and
    // land back on the whole window. Filtering and zooming are answers to the same question here,
    // so they have to survive each other. Re-applied rather than prevented, because the library
    // gives no way to toggle a series without that redraw.
    if (zoomWindow) {
        restoringZoom = true;
        chart.zoomX(zoomWindow.min, zoomWindow.max);
        // Cleared on a later tick: zoomX redraws asynchronously, so the event it raises has not
        // arrived yet and clearing inline would let it through as a user gesture.
        setTimeout(() => { restoringZoom = false; }, 0);
    }
}

/// Chips below the plot, matching the other chart tabs: a plain click isolates one series (and
/// clicking the isolated one restores all), ctrl or cmd toggles a single series. Same paradigm on
/// purpose - this chart used the built-in ApexCharts legend, which is a different gesture for the
/// same job and does not survive the updateSeries that a poll performs.
function renderBadges() {
    if (!badgesEl) return;
    if (asnMeta.length <= 1) { badgesEl.innerHTML = ''; return; }

    badgesEl.innerHTML = asnMeta.map(m => {
        const vis = seriesVisibility[m.name] !== false;
        return `<button type="button" class="wan-filter-badge ${vis ? 'active' : 'inactive'}" data-asn="${escapeHtml(m.name)}">
            <span class="wan-badge-dot" style="background-color: ${m.color}"></span>
            <span>${escapeHtml(m.name)}</span>
        </button>`;
    }).join('');

    // Last: the chip rebuild above wipes the row, so the reset is re-added after it.
    renderFilterReset(badgesEl, isFiltered(seriesVisibility), () => {
        seriesVisibility = {};
        applySeriesVisibility();
        renderBadges();
    });

    if (!badgesEl._delegated) {
        badgesEl._delegated = true;
        badgesEl.addEventListener('click', (e) => {
            const btn = e.target.closest('button[data-asn]');
            if (!btn) return;
            const name = btn.dataset.asn;

            if (e.ctrlKey || e.metaKey) {
                seriesVisibility[name] = seriesVisibility[name] === false ? undefined : false;
            } else {
                const allVis = asnMeta.every(m => seriesVisibility[m.name] !== false);
                const onlyThis = seriesVisibility[name] !== false
                    && asnMeta.filter(m => m.name !== name).every(m => seriesVisibility[m.name] === false);
                if (onlyThis) seriesVisibility = {};
                else if (allVis) asnMeta.forEach(m => seriesVisibility[m.name] = m.name === name);
                else seriesVisibility[name] = seriesVisibility[name] === false;
            }
            applySeriesVisibility();
            renderBadges();
        });
    }
}

export async function mount(elId, fromISO = null, toISO = null, hidden = null) {
    const el = document.getElementById(elId);
    if (!el) return;
    win = (fromISO && toISO) ? { from: fromISO, to: toISO } : null;
    hiddenTypes = new Set(hidden || []);

    resetBtn = document.createElement('button');
    resetBtn.type = 'button';
    resetBtn.className = 'btn btn-sm btn-secondary isp-chart-reset-btn';
    resetBtn.textContent = 'Reset zoom';
    resetBtn.style.display = 'none';
    resetBtn.addEventListener('click', resetZoom);
    el.parentElement.classList.add('isp-chart-wrap');
    el.parentElement.appendChild(resetBtn);

    // Immediately after the plot, where the legend it replaces used to sit. NOT appended to the
    // parent: that wrapper holds more than the chart (the Dig deeper block among it), so appending
    // put the chips at the bottom of the card instead of under the lines they filter.
    badgesEl = document.createElement('div');
    badgesEl.className = 'wan-filter-badges isp-chart-badges';
    el.parentElement.insertBefore(badgesEl, el.nextSibling);

    chart = new ApexCharts(el, buildOpts());
    await chart.render();
    await loadAndUpdate();
    // Guarded at the tick, not inside loadAndUpdate, so an explicit reload is never suppressed.
    pollTimer = setInterval(() => { if (!tooltipHeld(el)) loadAndUpdate(); }, POLL_MS);
}

export async function reload() {
    await loadAndUpdate();
}

// Follow a filter-selected window (or null, null for the default 48 h view). Clears any
// drag-zoom and reloads, so the chart resets and refetches on every filter change.
export async function setWindow(fromISO, toISO) {
    win = (fromISO && toISO) ? { from: fromISO, to: toISO } : null;
    if (chart) chart.updateOptions({ xaxis: { min: undefined, max: undefined } }, false, false);
    setZoomed(false);
    notifyZoom(null, null);
    await loadAndUpdate();
}

let wanKey = null;

export function setWan(w) {
    wanKey = w || null;
    loadAndUpdate();
}

export function setDotNetRef(ref) {
    dotNetRef = ref;
}

// Re-render event annotations with the given types hidden (display-only category filter);
// no refetch, the last-loaded events are re-applied.
export function setHiddenTypes(types) {
    hiddenTypes = new Set(types || []);
    if (chart) chart.updateOptions({ annotations: buildAnnotations(lastEvents) }, false, false);
}

export function scrollChartIntoView() {
    document.getElementById('isp-health-asn-chart')?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

export function unmount() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    fetchController?.abort();
    if (resetBtn) { resetBtn.remove(); resetBtn = null; }
    isZoomed = false;
    zoomWindow = null;
    restoringZoom = false;
    dotNetRef = null;
    win = null;
    hiddenTypes = new Set();
    lastEvents = [];
    if (chart) { chart.destroy(); chart = null; }
}
