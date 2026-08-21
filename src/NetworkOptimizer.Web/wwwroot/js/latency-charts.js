// Latency & Packet Loss charts — pure JS ApexCharts, fed by /api/monitoring/chart-data.
// Mounted from Blazor the same way as lan-flow-map.js.
// TODO: Extract time-range controls (presets, shift arrows, custom range popover,
// filter badges, poll interval scaling) into a shared module so latency-charts,
// device-health-charts, and future chart sets share one implementation.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { computeStats, renderStatsTable as renderTable } from './chart-stats.js?v=7';
import { valueSortedTooltip, tooltipHeld } from './chart-tooltip.js?v=15';
import { renderFilterReset, renderInactiveToggle, isFiltered } from './chart-filter.js?v=6';
import { downloadColor, uploadColor } from './chart-colors.js?v=2';
import { createAxisDateCaption } from './chart-axis-date.js?v=3';
import { syncIdentity, extentsOf, spanTo } from './chart-sync.js?v=7';
import { awaitContainer } from './chart-mount.js?v=1';
import { loadWindowHours, saveWindowHours, markActiveRange } from './chart-window.js?v=2';

// Storage scope for this tab's remembered time window.
const WINDOW_TAB = 'latency';

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
/**
 * The same colour, faded, for a series nothing is probing any more. Keeping the hue is the point:
 * a host's retired line and its live replacement have to read as the same host, so only the
 * strength changes. Palette entries are #rrggbb, and anything else is returned untouched.
 */
function dimmed(hex) {
    const m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex ?? '');
    if (!m) return hex;
    const [r, g, b] = m.slice(1).map(h => parseInt(h, 16));
    return `rgba(${r}, ${g}, ${b}, 0.4)`;
}

const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }
const POLL_INTERVALS = { 0: 5000, 1: 5000, 6: 10000, 24: 15000, 168: 30000, 720: 30000 };
const RANGE_MS = { 0: 15 * 60000, 1: 3600000, 6: 6 * 3600000, 24: 86400000, 168: 7 * 86400000, 720: 30 * 86400000 };

let rttChart = null;
let lossChart = null;
let wanRateChart = null;
let pollTimer = null;
let currentCategory = 'Fabric';
let currentRangeHours = 1;
let customFrom = null;  // Date or null
let customTo = null;    // Date or null
let isCustomRange = false;
let windowOffset = 0;   // ms offset from "now" for shift arrows
let visibility = {};
let targetMeta = [];
let containerId = null;
let fetchController = null;
let visibilityObserver = null;
let isInViewport = true;
let lastFetchData = null;
let savedState = null;
// Whether the fetch asks for the series nothing is probing any more, and how many of them this
// category has. The count comes back on every response, including one that excluded them, because
// it is what decides whether the control to ask for them is offered at all.
let showInactive = false;
let inactiveCount = 0;
// Per-WAN scope, set by Blazor (which owns the WAN pill bar and its visibility gate).
// null = no scoping at all: single-WAN sites never reach this code path and render
// exactly as before. Shape: { primaryKey, selected: [wanKey...], tokens: {key: 'WAN1'} };
// selecting every key is comparison mode (per-host color kept, per-WAN dash pattern).
let wanScope = null;
// The instants every chart on this tab must report as its own first and last for the hover sync to
// fire - see spanTo. Recomputed each load, since each category brings its own targets.
let groupExtents = null;
// The throughput chart's dash pattern as last applied, so an unchanged one costs no redraw.
let lastWanRateDash = null;
// Dash patterns by WAN order: primary solid, then visibly distinct patterns per extra WAN.
const WAN_DASH_PATTERNS = [0, 6, 2, 9];

// The server's marker for a target whose probe is pinned to no WAN. Null means the same thing on
// rows written before the marker existed, so both are treated as unpinned here.
const UNPINNED_WAN = 'unpinned';

function effectiveWanKey(t) {
    // Unpinned targets are primary-path measurements (same rule as the server side). Checked
    // rather than relying on a falsy value: the marker is a real string, so `||` alone would take
    // it for a WAN key, match no selected pill, and drop every LAN target off the chart.
    const wan = t.wanInterface && t.wanInterface.toLowerCase() !== UNPINNED_WAN ? t.wanInterface : null;
    return (wan || wanScope?.primaryKey || 'wan').toLowerCase();
}

function wanComparisonActive() {
    return !!wanScope && wanScope.selected.length > 1;
}

// Which categories each scope can show anything in. LAN holds the fabric devices and the
// hand-added targets that sit on this network; a single WAN holds neither, because neither leaves
// by it. All is the union, so it offers everything.
const CATEGORIES_BY_SCOPE = {
    lan: ['Fabric', 'Custom'],
    wan: ['AccessIsp', 'Transit', 'InternetService', 'Custom'],
    all: ['Fabric', 'AccessIsp', 'Transit', 'InternetService', 'Custom'],
};

function scopeName() {
    if (!wanScope) return 'all';
    if (wanScope.lan) return 'lan';
    return wanScope.all ? 'all' : 'wan';
}

function filterTargetsToWanScope(targets) {
    if (!wanScope) return targets;
    // LAN is its own scope, not a WAN: it shows what is on this network and nothing else, and a
    // single WAN shows the opposite. Only All carries both.
    if (wanScope.lan) return targets.filter(t => t.isLan);
    if (wanScope.all) return targets;
    const sel = new Set(wanScope.selected.map(k => k.toLowerCase()));
    return targets.filter(t => !t.isLan && sel.has(effectiveWanKey(t)));
}

// Shows only the categories the current scope can fill, and moves off one it cannot: the current
// choice is kept whenever it survives the move - switching WAN while on Custom should stay on
// Custom - and otherwise falls to the first that does.
function applyCategoryAvailability() {
    const container = document.getElementById(containerId);
    if (!container) return currentCategory;
    const allowed = CATEGORIES_BY_SCOPE[scopeName()] || CATEGORIES_BY_SCOPE.all;
    container.querySelectorAll('[data-category]').forEach(b => {
        b.style.display = allowed.includes(b.dataset.category) ? '' : 'none';
    });
    if (!allowed.includes(currentCategory)) currentCategory = allowed[0];
    container.querySelectorAll('[data-category]').forEach(b => {
        b.classList.toggle('active', b.dataset.category === currentCategory);
    });
    return currentCategory;
}

function wanDisplayName(t) {
    if (!wanComparisonActive()) return t.name;
    const key = effectiveWanKey(t);
    const token = wanScope.tokens?.[key] || key.toUpperCase();
    return `${t.name} (${token})`;
}

function wanDashFor(t) {
    if (!wanComparisonActive()) return 0;
    const idx = wanScope.selected.map(k => k.toLowerCase()).indexOf(effectiveWanKey(t));
    return WAN_DASH_PATTERNS[Math.max(0, idx) % WAN_DASH_PATTERNS.length];
}
let investigateMarker = null;  // { startMs, endMs, label, loaded } while investigating a loss event

// Highlight the investigated loss event on the RTT and loss charts, mirroring the
// shaded event annotations on the ISP Health chart. The band spans the actual coalesced
// event so it stays tight around the loss instead of trailing past it. Loaded-loss events
// are amber (the SQM/bufferbloat signal); plain packet-loss events are info blue.
function buildInvestigateAnnotations() {
    if (!investigateMarker) return { xaxis: [] };
    const { startMs, endMs, label, loaded } = investigateMarker;
    // The 1-min loss buckets are stamped at their stop edge, so the first bucket (startMs)
    // covers data from the minute before it; back the band start up one minute to match.
    const bucketMs = 60000;
    const color = loaded ? '#f59e0b' : '#4797ff';
    const labelBg = loaded ? '#78350f' : '#1e3a5f';
    return {
        xaxis: [{
            x: startMs - bucketMs,
            x2: endMs,
            fillColor: color,
            opacity: 0.15,
            borderColor: color,
            label: {
                text: label,
                style: { color: '#ededef', background: labelBg, fontSize: '10px' },
            },
        }],
    };
}

const axisDate = createAxisDateCaption({ charts: () => [rttChart, lossChart, wanRateChart], window: effectiveWindow });

// Every chart this tab stacks shares one group - see chart-sync.js. WAN Throughput is in it too:
// reading a latency spike against what the link was carrying at that moment is most of why it sits
// under them. It comes from its own query, so its points can be spaced differently - the tooltip
// resolves by the hovered instant rather than by index, which is what keeps its rows honest.
const SYNC_GROUP = 'latency';

function baseChartOpts(type, yTitle, yFormatter, extraOpts, group = SYNC_GROUP) {
    return {
        chart: {
            type: type,
            height: type === 'area' ? 200 : 260,
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
            labels: {
                style: { colors: '#9ca3af' },
                formatter: yFormatter,
            },
        },
        grid: { borderColor: '#374151', strokeDashArray: 3 },
        legend: { show: false },
        tooltip: {
            theme: 'dark',
            shared: true,
            x: { format: 'MMM dd, HH:mm:ss' },
            custom: valueSortedTooltip,
        },
        noData: { text: 'No data in this time range', style: { color: '#64748b' } },
        ...extraOpts,
    };
}


function buildRttOpts() {
    return baseChartOpts('line', 'ms',
        v => v != null ? v.toFixed(1) : '');
}

function buildLossOpts() {
    return baseChartOpts('area', '% loss',
        v => v != null ? v.toFixed(1) + '%' : '',
        {
            yaxis: {
                min: 0, max: v => Math.max(v * 1.1, 5),
                title: { text: '% loss', style: { color: '#9ca3af' } },
                labels: {
                    style: { colors: '#9ca3af' },
                    formatter: v => v != null ? v.toFixed(1) + '%' : '',
                },
            },
            fill: {
                type: 'gradient',
                gradient: { shadeIntensity: 0.3, opacityFrom: 0.4, opacityTo: 0.05 },
            },
        });
}

function formatBps(v) {
    if (v == null || v < 1) return '0';
    if (v >= 1e9) return (v / 1e9).toFixed(1) + ' Gbps';
    if (v >= 1e6) return (v / 1e6).toFixed(1) + ' Mbps';
    if (v >= 1e3) return (v / 1e3).toFixed(0) + ' Kbps';
    return v.toFixed(0) + ' bps';
}

function buildWanRateOpts() {
    return baseChartOpts('area', 'Throughput',
        v => v != null ? formatBps(v) : '',
        {
            colors: [downloadColor(), uploadColor()],
            fill: {
                type: 'gradient',
                gradient: { shadeIntensity: 0.3, opacityFrom: 0.3, opacityTo: 0.05 },
            },
        });
}

function buildQueryParams() {
    let params = `category=${currentCategory}`;
    if (isCustomRange && customFrom && customTo) {
        params += `&from=${customFrom.toISOString()}&to=${customTo.toISOString()}`;
    } else {
        params += `&rangeHours=${currentRangeHours}`;
        if (windowOffset !== 0) {
            const now = Date.now();
            const rangeMs = RANGE_MS[currentRangeHours] || 3600000;
            const to = new Date(now + windowOffset);
            const from = new Date(to.getTime() - rangeMs);
            params = `category=${currentCategory}&from=${from.toISOString()}&to=${to.toISOString()}`;
        }
    }
    if (showInactive) params += '&includeInactive=true';
    return params;
}

async function fetchData() {
    if (fetchController) fetchController.abort();
    fetchController = new AbortController();
    try {
        const resp = await fetch(
            `/api/monitoring/chart-data?${buildQueryParams()}`,
            { signal: fetchController.signal });
        if (!resp.ok) return null;
        return await resp.json();
    } catch (e) {
        if (e.name === 'AbortError') return null;
        return null;
    }
}

function renderBadges(container) {
    const el = container.querySelector('.latency-filter-badges');
    if (el) el.dataset.tour = 'chart-series-filter';
    if (!el) return;
    if (badgeGroups().length <= 1) {
        // Still offer the toggle: a category whose every target has been retired draws no chips at
        // all, and without this there would be no way to ask for the ones that are missing.
        el.innerHTML = '';
        renderInactiveToggle(el, inactiveCount > 0, showInactive, toggleInactive);
        return;
    }

    el.innerHTML = badgeGroups().map(g => {
        const vis = g.ids.some(id => visibility[id] !== false);
        // The chip's own `inactive` class already means "filtered off by this row", so the
        // not-probed state needs its own name here even though the control calls it inactive.
        const unprobed = g.unprobed ? ' wan-badge-unprobed' : '';
        return `<button class="wan-filter-badge ${vis ? 'active' : 'inactive'}${unprobed}" data-target="${escapeHtml(g.key)}">
            <span class="wan-badge-dot" style="background-color: ${g.color}"></span>
            <span>${escapeHtml(g.key)}</span>
        </button>`;
    }).join('');

    if (!el._delegated) {
        el._delegated = true;
        el.addEventListener('click', (e) => {
            const btn = e.target.closest('button[data-target]');
            if (!btn) return;
            const key = btn.dataset.target;
            const groups = badgeGroups();
            const group = groups.find(g => g.key === key);
            if (!group) return;
            const inGroup = new Set(group.ids);
            const groupVisible = group.ids.some(id => visibility[id] !== false);

            if (e.ctrlKey || e.metaKey) {
                group.ids.forEach(id => { visibility[id] = groupVisible ? false : undefined; });
            } else {
                const allVis = targetMeta.every(t => visibility[t.id] !== false);
                const onlyThis = groupVisible
                    && targetMeta.filter(t => !inGroup.has(t.id)).every(t => visibility[t.id] === false);

                if (onlyThis) {
                    visibility = {};
                } else if (allVis) {
                    targetMeta.forEach(t => visibility[t.id] = inGroup.has(t.id));
                } else {
                    // Flip it: assigning the state back to itself leaves a hidden host hidden, so
                    // the click after a solo did nothing at all.
                    group.ids.forEach(id => { visibility[id] = !groupVisible; });
                }
            }

            updateChartVisibility();
            renderBadges(container);
            if (lastFetchData) renderStatsTable(container, false);
        });
    }

    // Last: the chip rebuild above wipes the row, so both controls are re-added after it.
    renderInactiveToggle(el, inactiveCount > 0, showInactive, toggleInactive);
    renderFilterReset(el, isFiltered(visibility), () => {
        visibility = {};
        updateChartVisibility();
        renderBadges(container);
        if (lastFetchData) renderStatsTable(container, false);
    });
}

/**
 * Ask the server for the series nothing is probing, or stop asking. Unlike the reset beside it
 * this changes what comes back rather than what is drawn, so it reloads instead of redrawing.
 */
function toggleInactive() {
    showInactive = !showInactive;
    // Visibility is keyed by target id, and the ids on the way in are ones it has never seen.
    // Left alone they would arrive hidden if the user had soloed something earlier.
    if (showInactive) visibility = {};
    loadAndUpdate();
}

// One entry per host, in the order its series first appear, carrying every series id that host
// owns. Keyed on the raw name because that is what decides the colour - two rows sharing a colour
// are the same host over different WANs, and the badge speaks for both.
function badgeGroups() {
    const byHost = new Map();
    targetMeta.forEach(t => {
        const key = t.hostName ?? t.name;
        // A host is shown as not-probed only when every series it owns is: a host with a retired
        // target and a live replacement is still being probed, and saying otherwise would be wrong.
        if (!byHost.has(key)) byHost.set(key, { key, color: t.color, ids: [], unprobed: true });
        if (!t.inactive) byHost.get(key).unprobed = false;
        byHost.get(key).ids.push(t.id);
    });
    return [...byHost.values()];
}

// Draws exactly the series that should be on screen, in one update per chart.
//
// This used to call showSeries/hideSeries once per series per chart, and each of those is a full
// redraw - so going from everything to one host, or clearing back again, cost a redraw for every
// series that changed while the clicks in between cost two. That is why the first selection and
// the clear were the slow ones. Nothing here talks to the server; it is all redraw.
function updateChartVisibility() {
    if (!rttChart || !lossChart) return;
    const shown = (lastFetchData?.targets || []).filter(t => visibility[t.targetId] !== false);

    const seriesOf = key => shown.map(t => ({
        name: wanDisplayName(t),
        // Faded rather than dashed for the ones nothing is probing: the dash pattern already says
        // which WAN a series belongs to in comparison mode, and one pattern cannot mean two things.
        color: t.inactive ? dimmed(hashColor(t.name)) : hashColor(t.name),
        data: (t[key] || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value })),
    }));

    rttChart.updateSeries(spanTo(seriesOf('rtt'), groupExtents), false);
    lossChart.updateSeries(spanTo(seriesOf('loss'), groupExtents), false);

    // Dashes are positional, so they have to be rebuilt against the series actually drawn. Twins
    // of one host share its colour, so the pattern is the only thing telling their WANs apart.
    const annotations = buildInvestigateAnnotations();
    const opts = { annotations, stroke: { curve: 'smooth', width: 2, dashArray: shown.map(wanDashFor) } };
    // Fourth argument false: this dash pattern is positional to THESE series, and a grouped
    // chart would otherwise be handed it - which is how WAN Throughput's upload went dashed.
    rttChart.updateOptions(opts, false, false, false);
    lossChart.updateOptions(opts, false, false, false);
}

// Mean loss (%) over the visible window at or above which a LAN fabric target is treated as
// flaky enough to advise pausing. Deliberately low: any sustained loss to a LAN device is
// abnormal, so even a fraction of a percent is worth flagging as a poor measurement target.
const LAN_FLAKY_LOSS_PCT = 0.5;

// A loss sample at or above this marks a target as "down" in that timestamp. Used for both outage
// masks below. High bar so only true unreachability counts, never the low-level flaky loss
// (>= LAN_FLAKY_LOSS_PCT) we actually want to surface.
const OUTAGE_LOSS_PCT = 50;
// Fraction of the reporting fabric pool that must be down in a timestamp for it to read as a
// systemic (gateway/shared-switch) outage rather than one target's own trouble.
const SHARED_OUTAGE_POOL_FRACTION = 0.5;
// The shared-outage test needs a real pool to infer "systemic": with only a target or two, one
// down device is already "half the pool," so we lean on the gateway signal alone and never mask
// a genuinely-down single target as shared loss.
const SHARED_OUTAGE_MIN_TARGETS = 3;

// Report the current LAN (Fabric) category's flaky targets to Blazor so it can render the
// "flaky LAN target" advisory. Detection only; the role/dismissed gating and the notice itself
// live in Blazor (Monitoring.razor), which has the target metadata. Entirely best-effort:
// wrapped so a failure here can never disturb chart rendering, and a no-op until Blazor has
// handed us its DotNet reference via window.__netoptLatencyRef.
// A ?at= in the URL says where a link wanted this window. The moment the user moves it themselves
// that stops being true, so Blazor is told to drop the parameter - otherwise a reload or the back
// button drags them back to the linked instant. Called from the user's own handlers only, never
// from frameMoment/frameTrailing, which ARE the link landing. Best-effort, like the hints below.
function notifyTimelineMoved() {
    try { window.__netoptLatencyRef?.invokeMethodAsync('OnTimelineMovedByUser'); }
    catch { /* no ref yet, or the circuit is gone - the window still moved */ }
}

function notifyLanFlakyHints(data) {
    try {
        const ref = window.__netoptLatencyRef;
        if (!ref) return;
        let ids = [];
        if (scopeName() === 'lan' && currentCategory === 'Fabric' && data && Array.isArray(data.targets)) {
            // Mask out timestamps whose loss rode an outage rather than one target's own flakiness,
            // so a switch isn't flagged for loss the gateway (or a shared upstream) caused. A
            // timestamp is an outage when EITHER holds:
            //   - the gateway fabric target itself was down (>= OUTAGE_LOSS_PCT) - catches a gateway
            //     outage even when it only downs some LAN targets, and
            //   - most of the reporting fabric pool was down at once - catches a shared-switch
            //     outage and covers sites with no monitored gateway target.
            // Neither test trips on a single flaky target's solo loss, so it still surfaces.
            const outageTimes = new Set();
            const perTime = new Map(); // time -> { down, reporting }
            data.targets.forEach(t => {
                const isGateway = t.autoLabel === 'gateway';
                (t.loss || []).forEach(p => {
                    if (p.value == null) return;
                    const down = p.value >= OUTAGE_LOSS_PCT;
                    if (isGateway && down) outageTimes.add(p.time);
                    const agg = perTime.get(p.time) || { down: 0, reporting: 0 };
                    agg.reporting++;
                    if (down) agg.down++;
                    perTime.set(p.time, agg);
                });
            });
            perTime.forEach((agg, time) => {
                if (agg.reporting >= SHARED_OUTAGE_MIN_TARGETS
                    && agg.down / agg.reporting >= SHARED_OUTAGE_POOL_FRACTION) {
                    outageTimes.add(time);
                }
            });
            ids = data.targets.filter(t => {
                if (t.autoLabel === 'gateway') return false;
                const vals = (t.loss || [])
                    .filter(p => p.value != null && !outageTimes.has(p.time))
                    .map(p => p.value);
                if (!vals.length) return false;
                const mean = vals.reduce((a, b) => a + b, 0) / vals.length;
                return mean >= LAN_FLAKY_LOSS_PCT;
            }).map(t => t.targetId);
        }
        // Fire-and-forget; swallow rejection if the Blazor circuit is already gone.
        Promise.resolve(ref.invokeMethodAsync('SetLanFlakyHints', currentCategory, ids)).catch(() => { });
    } catch { }
}

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data || !data.targets) return;

    // WAN scoping is client-side over the full per-type payload: the fetch stays shared
    // across WAN selections, and comparison mode simply keeps every WAN's rows. Twin rows
    // of one host share a name (and therefore a color); the WAN suffix + dash pattern
    // are what tells them apart in comparison mode.
    const scopedTargets = filterTargetsToWanScope(data.targets);

    inactiveCount = data.inactiveCount ?? 0;

    targetMeta = scopedTargets.map(t => ({
        id: t.targetId,
        name: wanDisplayName(t),
        hostName: t.name,
        color: hashColor(t.name),
        inactive: t.inactive === true,
    }));

    lastFetchData = { ...data, targets: scopedTargets };
    groupExtents = extentsOf(scopedTargets.flatMap(t => [t.rtt || [], t.loss || []]));

    // Ahead of the redraw below - see apply().
    axisDate.apply();
    updateChartVisibility();

    const container = document.getElementById(containerId);
    if (container) {
        renderBadges(container);
        renderStatsTable(container);
    }

    notifyLanFlakyHints(data);

    // WAN rate chart - show for non-Fabric categories
    const showWanRate = currentCategory !== 'Fabric';
    const wanCard = container?.querySelector('.latency-wan-rate-card');
    if (wanCard) wanCard.style.display = showWanRate ? '' : 'none';

    if (showWanRate && wanRateChart) {
        const timeParams = buildQueryParams().replace(/category=[^&]*&?/, '');
        // The throughput follows the WAN filter: the WANs being compared, each drawn in full.
        // Summing them would answer a question nobody asked - the point of comparing two WANs is
        // to see them apart - so they arrive as their own pair of lines, told apart the way the
        // RTT chart tells its WANs apart.
        const keys = wanScope?.selected?.length ? wanScope.selected : [null];
        const many = keys.length > 1;

        // Its own samples, at its own cadence - only the extents are trimmed and padded to the
        // group's, which is all the hover sync asks for.
        const points = (pts) => (pts || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value }));

        try {
            const fetched = await Promise.all(keys.map(async (key) => {
                const params = key ? `${timeParams}${timeParams ? '&' : ''}wan=${encodeURIComponent(key)}` : timeParams;
                const resp = await fetch(`/api/monitoring/wan-rate-chart?${params}`, { credentials: 'same-origin' });
                return resp.ok ? { key, wan: await resp.json() } : null;
            }));

            const series = [];
            const dashes = [];
            fetched.filter(Boolean).forEach(({ key, wan }) => {
                // The WAN's own token, as on the pills: the color says download or upload, so the
                // name and the dash are what say which connection.
                const token = many ? ` (${wanScope.tokens?.[key] || String(key).toUpperCase()})` : '';
                // Keyed off the WAN's place in the filter, exactly as wanDashFor does it, so a WAN
                // wears the same pattern here as on the RTT chart above. Its place among the
                // series drawn here would not survive one WAN's request failing.
                const dash = many ? WAN_DASH_PATTERNS[Math.max(0, keys.indexOf(key)) % WAN_DASH_PATTERNS.length] : 0;
                series.push(
                    { name: `Download${token}`, color: downloadColor(), data: points(wan.download) },
                    { name: `Upload${token}`, color: uploadColor(), data: points(wan.upload) });
                dashes.push(dash, dash);
            });
            if (!series.length) return;

            wanRateChart.updateSeries(spanTo(series, groupExtents), false);
            // Positional, so rebuilt against the WANs actually drawn - and only when it changes,
            // since this redraws and the poll comes round every few seconds. Fourth argument
            // false, or the group takes this pattern for its own series.
            const pattern = dashes.join(',');
            if (pattern !== lastWanRateDash) {
                lastWanRateDash = pattern;
                wanRateChart.updateOptions(
                    { stroke: { curve: 'smooth', width: 2, dashArray: dashes } }, false, false, false);
            }
        } catch { }
    }
}


function fmtRtt(v) { return v != null ? v.toFixed(3) : '-'; }
function fmtLossColored(v, redAt, orangeAt, yellowAt, lightAt, subtleAt, decimals) {
    if (v == null) return '-';
    const s = v.toFixed(decimals) + '%';
    if (v >= redAt) return `<span style="color:var(--danger-color)">${s}</span>`;
    if (v >= orangeAt) return `<span style="color:var(--accent-color)">${s}</span>`;
    if (v >= yellowAt) return `<span style="color:var(--warning-color)">${s}</span>`;
    if (v >= lightAt) return `<span style="color:#d4c06a">${s}</span>`;
    if (v > subtleAt) return `<span style="color:#c8c4a8">${s}</span>`;
    return s;
}
function fmtLossMean(v) { return fmtLossColored(v, 1, 0.2, 0.05, 0.005, 0.0005, 3); }
function fmtLossMax(v) { return fmtLossColored(v, 5, 2, 0.5, 0.005, 0.005, 2); }

function renderStatsTable(container, showAll) {
    const el = container.querySelector('.latency-stats-table');
    const data = lastFetchData;
    if (!el || !data?.targets?.length) { if (el) el.innerHTML = ''; return; }

    const rows = data.targets.map(t => {
        const rttVals = (t.rtt || []).map(p => p.value).filter(v => v != null && v > 0);
        const lossVals = (t.loss || []).map(p => p.value).filter(v => v != null);
        const rtt = computeStats(rttVals);
        const loss = computeStats(lossVals);
        const meta = targetMeta.find(m => m.id === t.targetId);
        return { id: t.targetId, label: meta?.name || t.name, color: meta?.color || '#9ca3af',
            visible: meta && visibility[meta.id] !== false,
            values: [rtt?.mean, rtt?.min, rtt?.max, rtt?.p95, rtt?.p99, loss?.mean, loss?.max] };
    });

    renderTable(el, container, {
        nameHeader: 'Target', rows, showAllRows: showAll,
        columns: [
            { header: 'RTT Mean', format: fmtRtt }, { header: 'Min', format: fmtRtt }, { header: 'Max', format: fmtRtt },
            { header: 'P95', format: fmtRtt }, { header: 'P99', format: fmtRtt },
            { header: 'Loss Mean', format: fmtLossMean }, { header: 'Loss Max', format: fmtLossMax },
        ],
        filter: { meta: () => targetMeta, key: 'id', visibility: () => visibility,
            resetVisibility: () => { visibility = {}; },
            // The same host over two WANs is one series to the user - they are comparing the WANs,
            // not choosing between them - so its rows toggle together, exactly as the chip above
            // does. Outside a comparison every group holds one id and this changes nothing.
            groupOf: (id) => badgeGroups().find(g => g.ids.some(i => String(i) === String(id)))?.ids ?? [id],
            onChanged: (c) => { updateChartVisibility(); renderBadges(c); renderStatsTable(c, true); } },
    });
}

function isVisible() { return isInViewport; }

function startPoll() {
    stopPoll();
    if (windowOffset !== 0 || isCustomRange) return;
    if (!isVisible()) return;
    const interval = POLL_INTERVALS[currentRangeHours] || 30000;
    pollTimer = setInterval(() => { if (!tooltipHeld(document.getElementById(containerId))) loadAndUpdate(); }, interval);
}

function stopPoll() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
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
    container.querySelector('.custom-range-btn')?.classList.remove('active');
    syncPopoverInputs(container);
    updateCustomLabel(container);
    loadAndUpdate();
    startPoll();
}

function shiftWindow(direction) {
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

    const container = document.getElementById(containerId);
    if (container) {
        syncPopoverInputs(container);
        updateCustomLabel(container);
    }

    loadAndUpdate();
    startPoll();
}

function syncPopoverInputs(container) {
    const fromInput = container.querySelector('[data-input="from"]');
    const toInput = container.querySelector('[data-input="to"]');
    if (!fromInput || !toInput) return;

    if (isCustomRange && customFrom && customTo) {
        fromInput.value = toLocalDatetimeString(customFrom);
        toInput.value = toLocalDatetimeString(customTo);
    } else {
        const now = Date.now();
        const rangeMs = RANGE_MS[currentRangeHours] || 3600000;
        const to = new Date(now + windowOffset);
        const from = new Date(to.getTime() - rangeMs);
        fromInput.value = toLocalDatetimeString(from);
        toInput.value = toLocalDatetimeString(to);
    }
}

function toLocalDatetimeString(d) {
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function updateCustomLabel(container) {
    const btn = container.querySelector('.custom-range-btn');
    if (!btn) return;
    const label = btn.querySelector('.custom-range-label');
    if (label) label.remove();

    const active = isCustomRange || windowOffset !== 0;
    let clearBtn = btn.querySelector('.custom-range-clear');
    if (active) {
        btn.classList.add('active');
        const from = getEffectiveFrom();
        const to = getEffectiveTo();
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
                const ctr = document.getElementById(containerId);
                if (ctr) selectPresetRange(ctr, currentRangeHours);
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
        notifyTimelineMoved();
        customFrom = new Date(xaxis.min);
        customTo = new Date(xaxis.max);
        isCustomRange = true;
        windowOffset = 0;
        container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        syncPopoverInputs(container);
        updateCustomLabel(container);
        loadAndUpdate();
        startPoll();
    }
    // Cancel ApexCharts' client-side zoom; the refetch repaints the selected window
    return { xaxis: { min: undefined, max: undefined } };
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
    if (windowOffset !== 0) {
        const rangeMs = RANGE_MS[currentRangeHours] || 3600000;
        return new Date(Date.now() + windowOffset - rangeMs);
    }
    return null;
}

function getEffectiveTo() {
    if (isCustomRange && customTo) return customTo;
    if (windowOffset !== 0) return new Date(Date.now() + windowOffset);
    return null;
}

// initialWanScope arrives with the mount rather than in a call behind it: this module is imported
// asynchronously, so a separate push can land before the import resolves and be dropped silently.
// Taking it here also survives the unmount/remount of leaving the tab and returning.
export async function mount(elId, initialWanScope, initialCategory) {
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

    setWanScope(initialWanScope);

    // The opening category comes from the server, which knows whether the WANs on screen have any
    // LAN targets. From here the module owns it: the buttons carry no server-rendered active class,
    // so a re-render of the header cannot put a stale one back while this still holds another.
    if (initialCategory) currentCategory = initialCategory;
    applyCategoryAvailability();

    const rttEl = container.querySelector('.latency-rtt-chart');
    const lossEl = container.querySelector('.latency-loss-chart');
    if (!rttEl || !lossEl) return;

    const wanRateEl = container.querySelector('.latency-wan-rate-chart');

    if (rttChart) { rttChart.destroy(); rttChart = null; }
    if (lossChart) { lossChart.destroy(); lossChart = null; }
    if (wanRateChart) { wanRateChart.destroy(); wanRateChart = null; }

    rttChart = new ApexCharts(rttEl, { ...buildRttOpts(), series: [], colors: PALETTE });
    lossChart = new ApexCharts(lossEl, { ...buildLossOpts(), series: [], colors: PALETTE });

    await rttChart.render();
    await lossChart.render();

    if (wanRateEl) {
        wanRateChart = new ApexCharts(wanRateEl, { ...buildWanRateOpts(), series: [] });
        await wanRateChart.render();
    }

    // Category buttons - preserve current time window when switching
    container.querySelectorAll('[data-category]').forEach(btn => {
        btn.addEventListener('click', () => {
            currentCategory = btn.dataset.category;
            container.querySelectorAll('[data-category]').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            visibility = {};
            loadAndUpdate();
            startPoll();
        });
    });

    // Preset range buttons
    container.querySelectorAll('[data-range]').forEach(btn => {
        btn.addEventListener('click', () => {
            notifyTimelineMoved();
            const hours = parseInt(btn.dataset.range);
            // Saved HERE rather than in selectPresetRange: a deep link's framing calls that
            // too, and a window the link chose must not become a remembered preference.
            saveWindowHours(WINDOW_TAB, hours);
            selectPresetRange(container, hours);
        });
    });

    // Shift arrows
    container.querySelectorAll('[data-shift]').forEach(btn => {
        btn.addEventListener('click', () => { notifyTimelineMoved(); shiftWindow(btn.dataset.shift); });
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

    document.addEventListener('click', (e) => {
        if (!popover?.classList.contains('open')) return;
        const customBtn = container.querySelector('[data-action="custom-range"]');
        if (popover.contains(e.target) || customBtn?.contains(e.target)) return;
        popover.classList.remove('open');
    });

    container.querySelector('[data-action="apply-custom"]')?.addEventListener('click', () => {
        const from = fromInput?.value ? new Date(fromInput.value) : null;
        const to = toInput?.value ? new Date(toInput.value) : null;
        if (!from || !to || isNaN(from) || isNaN(to) || from >= to) return;
        notifyTimelineMoved();
        customFrom = from;
        customTo = to;
        isCustomRange = true;
        windowOffset = 0;
        container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        container.querySelector('.custom-range-btn')?.classList.add('active');
        popover?.classList.remove('open');
        updateCustomLabel(container);
        loadAndUpdate();
        startPoll();
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

// Frames a custom window centered on one instant and switches category, stashing the view it
// replaced so leaving can put the user's own filter back. Shared by the two ways in - the
// Investigate flow below and the jump from the Live tab - because centering, the range-button
// bookkeeping and the save-once rule are the same job for both; only the marker differs.
function stashView() {
    if (savedState) return;
    savedState = { category: currentCategory, rangeHours: currentRangeHours,
        customFrom, customTo, isCustomRange, windowOffset, visibility: { ...visibility } };
}

function frameCustomWindow(ts, category, halfWindowMs) {
    customFrom = new Date(ts - halfWindowMs);
    customTo = new Date(ts + halfWindowMs);
    isCustomRange = true;
    windowOffset = 0;
    if (category) currentCategory = category;

    const container = document.getElementById(containerId);
    if (container) {
        container.querySelectorAll('[data-category]').forEach(b => {
            b.classList.toggle('active', b.dataset.category === currentCategory);
        });
        container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        container.querySelector('.custom-range-btn')?.classList.add('active');
        syncPopoverInputs(container);
        updateCustomLabel(container);
    }
    loadAndUpdate();
    startPoll();
}

export function navigateToTime(isoTimestamp, category, label, loaded, eventStartIso, eventEndIso) {
    stashView();
    const ts = new Date(isoTimestamp).getTime();
    investigateMarker = label
        ? {
            startMs: eventStartIso ? new Date(eventStartIso).getTime() : ts,
            endMs: eventEndIso ? new Date(eventEndIso).getTime() : ts,
            label,
            loaded: !!loaded,
        }
        : null;
    frameCustomWindow(ts, category, 10 * 60000); // 10 min either side of the event
}

/**
 * Frames the window on a moment carried in from the Live tab while it was PARKED on that instant:
 * 7.5 minutes either side, the same 15 minutes wide as the live jump below, so the two arrive at
 * the same zoom and an event looks like itself whichever way you came in.
 * Deliberately NOT navigateToTime - that is the Investigate flow, and it carries an event marker
 * and label this has no business drawing. Same window machinery, no marker.
 */
/**
 * Frames a window of a stated width on a moment, for a link that carries its own span - a jump
 * from a view that is itself a window rather than an instant. Used only when the link says so;
 * without a span, frameMoment's 15 minutes still applies.
 */
export function frameWindow(isoTimestamp, category, spanMs) {
    investigateMarker = null;
    frameCustomWindow(new Date(isoTimestamp).getTime(), category, Math.max(60000, spanMs) / 2);
}

export function frameMoment(isoTimestamp, category) {
    investigateMarker = null;
    frameCustomWindow(new Date(isoTimestamp).getTime(), category, 7.5 * 60000);
}

/**
 * Frames a trailing 15-minute window for a jump made while the Live tab was LIVE rather than
 * parked. Centering on "now" would leave half the window in the future and freeze the chart at
 * the instant of the click - and a frozen chart and a quiet network look identical, so someone
 * who was watching would end up reading a still frame as the present. Someone who was watching
 * carries on watching. 15m is also the shortest preset that keeps polling: startPoll stands down
 * on custom ranges, so a trailing custom window would be the frozen chart this avoids.
 */
export function frameTrailing(category) {
    investigateMarker = null;
    if (category) currentCategory = category;
    const container = document.getElementById(containerId);
    if (!container) return;
    container.querySelectorAll('[data-category]').forEach(b => {
        b.classList.toggle('active', b.dataset.category === currentCategory);
    });
    selectPresetRange(container, 0);
}

/**
 * The view the Live tab needs to reproduce this one: the instant at the CENTER of the window on
 * screen, plus the category being charted. Center rather than either edge because the spike
 * someone wants to watch play back is the thing they framed the window around, and a playback
 * position at the edge puts it half a window away. A plain trailing range keeps no explicit
 * bounds - getEffectiveFrom/To answer null for it - so its window is derived from the range.
 */
export function currentView() {
    const from = getEffectiveFrom();
    const to = getEffectiveTo();
    const endMs = to ? to.getTime() : Date.now();
    const startMs = from ? from.getTime() : endMs - (RANGE_MS[currentRangeHours] || 3600000);
    return {
        atIso: new Date((startMs + endMs) / 2).toISOString(),
        category: currentCategory,
    };
}

export function restoreState() {
    if (!savedState) return;
    investigateMarker = null;
    currentCategory = savedState.category;
    currentRangeHours = savedState.rangeHours;
    customFrom = savedState.customFrom;
    customTo = savedState.customTo;
    isCustomRange = savedState.isCustomRange;
    windowOffset = savedState.windowOffset;
    visibility = savedState.visibility;
    savedState = null;

    const container = document.getElementById(containerId);
    if (container) {
        container.querySelectorAll('[data-category]').forEach(b => {
            b.classList.toggle('active', b.dataset.category === currentCategory);
        });
        if (isCustomRange) {
            container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
            container.querySelector('.custom-range-btn')?.classList.add('active');
        } else {
            container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
            const btn = container.querySelector(`[data-range="${currentRangeHours}"]`);
            if (btn) btn.classList.add('active');
            container.querySelector('.custom-range-btn')?.classList.remove('active');
        }
        syncPopoverInputs(container);
        updateCustomLabel(container);
    }
    loadAndUpdate();
    startPoll();
}

// Blazor pushes the WAN pill bar's state here. Passing null clears scoping entirely
// (the gate is closed - single WAN, no contexts).
export function setWanScope(scope) {
    // The LAN scope carries no WAN keys, so it must survive the empty-selected check that used to
    // mean "no scoping at all".
    wanScope = scope && (scope.lan || (Array.isArray(scope.selected) && scope.selected.length > 0))
        ? scope : null;
    visibility = {};
    applyCategoryAvailability();
    loadAndUpdate();
}

export function setCategory(cat) {
    currentCategory = cat;
    const container = document.getElementById(containerId);
    if (container) {
        container.querySelectorAll('[data-category]').forEach(b => {
            b.classList.toggle('active', b.dataset.category === cat);
        });
    }
    visibility = {};
    // Each category answers "are there any inactive series" for itself, so asking for them does
    // not follow the reader from one tab to the next.
    showInactive = false;
    inactiveCount = 0;
    loadAndUpdate();
    startPoll();
}

export function soloTarget(targetId) {
    if (!targetMeta.length) return;
    const match = targetMeta.find(t => t.id === targetId);
    if (!match) return;
    targetMeta.forEach(t => { visibility[t.id] = t.id === targetId; });
    updateChartVisibility();
    const container = document.getElementById(containerId);
    if (container) {
        renderBadges(container);
        if (lastFetchData) renderStatsTable(container, false);
    }
}

export function unmount() {
    stopPoll();
    if (visibilityObserver) { visibilityObserver.disconnect(); visibilityObserver = null; }
    if (fetchController) { fetchController.abort(); fetchController = null; }
    if (rttChart) { rttChart.destroy(); rttChart = null; }
    if (lossChart) { lossChart.destroy(); lossChart = null; }
    if (wanRateChart) { wanRateChart.destroy(); wanRateChart = null; }
    containerId = null;
    targetMeta = [];
    visibility = {};
    currentCategory = 'Fabric';
    currentRangeHours = 1;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    lastFetchData = null;
    savedState = null;
    showInactive = false;
    inactiveCount = 0;
    investigateMarker = null;
    isInViewport = true;
    wanScope = null;
    lastWanRateDash = null;
    axisDate.reset();
}
