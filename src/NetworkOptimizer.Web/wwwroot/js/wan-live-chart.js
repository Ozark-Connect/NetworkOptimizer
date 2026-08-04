// WAN live chart — real-time area+line chart showing download, upload, packet
// loss, and mean ISP/transit RTT. Pre-loads history from InfluxDB, then polls
// /api/monitoring/live-stats for real-time updates.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import * as flowData from './lan-flow-data.js?v=7';
import { valueSortedTooltip } from './chart-tooltip.js?v=8';

const HISTORY_MINUTES = 5;
// Poll at twice the site's SNMP sample rate so no sample is missed when the two
// clocks drift out of phase; pollLive dedupes repeat reads via sampleTime. The
// rate is per-site and configurable, so it comes back with the chart data rather
// than being assumed - at the 5s default tier this is the 2500ms it always was.
const POLL_MS = 2500;
const POLL_MS_MIN = 1000;
const POLL_MS_MAX = 5000;
// Window-scroll redraw cadence. At 250ms each step moves well under a pixel
// on typical widths, so the chart slides instead of visibly ticking.
const SCROLL_MS = 250;
// Occasionally re-pull the whole history window and merge it back in, so gaps
// that filled AFTER the live buffer scrolled past them appear without a manual
// refresh: a monitoring outage whose buffered samples replayed into Influx on
// reconnect, or the cold-start gap before data first flowed. Live mode +
// foreground only, and the newest live samples are kept, so the smooth edge
// never regresses.
const BACKFILL_MS = 60000;
const COLOR_DL   = '#3b82f6';
const COLOR_UL   = '#10b981';
const COLOR_LOSS = '#ef4444';
const COLOR_RTT  = '#d946ef';

let chart = null;
let pollTimer = null;
let scrollTimer = null;
let backfillTimer = null;
let buffer = [];
// Comparison mode: [{ key, label }] for the WANs on screen, and one buffer per WAN keyed the same
// way. Empty for the ordinary single-WAN case, which never reads either.
let compareWans = [];
let compareBuffers = new Map();
// Dash patterns by position, so a WAN keeps its pattern for as long as the selection holds.
const WAN_DASH = [0, 6, 2, 10, 4, 8];
let elId = null;
let visHandler = null;
let mountGen = 0;
let lastSampleTime = 0;
// Whether a LIVE poll has landed this mount. Separate from lastSampleTime, which history and
// backfill points also advance - so on any site with stored history it is never 0 by the time the
// first live sample arrives, and keying the re-phase off it did nothing.
let seenLiveSample = false;
// Wall-clock of the last accepted live sample, and how long a stall has to last before data coming
// back counts as "started flowing again". Keyed on a GAP rather than on the first sample of a mount
// because the case that matters is the server restarting under a tab that stays open all the way
// through: the chart never remounts, so any per-mount flag is already set and never re-arms.
// Comfortably longer than the poll interval so ordinary jitter or one failed poll is not a resume.
let lastLiveAt = 0;
const RESUME_AFTER_MS = 20000;
// This site's SNMP sample interval and the poll cadence derived from it. Every
// chart fetch reports the interval, so a settings change is picked up on the next
// backfill without a reload. An explicit pollMs from the caller still wins.
let pollMs = POLL_MS;
let pollMsOverride = null;

// Adopt the interval a chart-data response reported. Restarts the live timer when
// the cadence actually moved, so a site polling faster than the chart does stops
// having samples fall between reads.
function applySampleInterval(data) {
    const seconds = data?.sampleIntervalSeconds;
    if (!seconds || seconds <= 0) return;
    const next = Math.min(POLL_MS_MAX, Math.max(POLL_MS_MIN, Math.round(seconds * 1000 / 2)));
    if (next === pollMs) return;
    dbg('sample interval changed', { seconds, pollMs: next });
    pollMs = next;
    if (pollTimer && !pollMsOverride) {
        clearInterval(pollTimer);
        pollTimer = setInterval(pollLive, pollMs);
    }
}

// Opt-in tracing for the backfill cadence, which is otherwise invisible: it is all client side and
// the interesting part is WHEN each fill runs relative to the first live sample. Enable with
//   localStorage.setItem('no-wan-chart-debug', '1')
// and reload; clear it with removeItem. Off by default so nothing ships noise to users.
const CHART_DEBUG = (() => {
    try { return localStorage.getItem('no-wan-chart-debug') === '1'; } catch { return false; }
})();
const mountedAt = () => (chartMountedAt ? ((Date.now() - chartMountedAt) / 1000).toFixed(1) + 's' : 'n/a');
let chartMountedAt = 0;
function dbg(what, detail) {
    if (CHART_DEBUG) console.log(`[wan-chart +${mountedAt()}] ${what}`, detail ?? '');
}
// Historic playback interpolation state (see seekTime).
let histTimer = null;
let histAt = 0;
let histWall = 0;
let histRate = 0;
// Seek fetch generation: rapid seeks can resolve out of order, and applying a
// stale response after a newer one snaps the playhead backward.
let seekGen = 0;
// Touch-primary devices tap the plot to reveal the value tooltip - that's the
// established paradigm and click-to-seek must not hijack it. Tap-to-seek is
// desktop-only; the play/pause + Historic cluster (separate tap targets off the
// plot) still shows, so mobile keeps playback control without breaking tooltips.
const IS_TOUCH = typeof window !== 'undefined'
    && window.matchMedia && window.matchMedia('(pointer: coarse)').matches;
// Click-to-seek (Live View, desktop mount only): clicking the chart scrubs the
// map timeline to the clicked instant.
let seekOnClick = false;
// Historic mode cluster (upper-left): play/pause button + Historic badge
// (click returns to live). Unlike the map badges it hides in live mode
// entirely - the chart is live by default and only needs the flag while
// parked or playing back. Synced from the shared store's playstate events.
let modeCluster = null;
let histBadge = null;
let playBtn = null;
let unsubFlow = null;
// Renders forced through the tooltip hold-off until this wall time: a click
// on the chart (to seek or play) or a play/pause / historic/live flip must
// draw even though the pointer - and therefore the tooltip - may still be
// over the plot.
let clickRenderUntil = 0;
const CLICK_RENDER_MS = 2000;
// Last known mouse position over the chart, for the tooltip hold-off's
// inside-the-plot check (see tooltipShowing).
let lastMouse = null;
let mouseMoveHandler = null;
let mouseLeaveHandler = null;

function formatBps(v) {
    if (v == null || v < 1) return '0';
    if (v >= 1e9) return (v / 1e9).toFixed(1) + ' Gbps';
    if (v >= 1e6) return (v / 1e6).toFixed(1) + ' Mbps';
    if (v >= 1e3) return (v / 1e3).toFixed(0) + ' Kbps';
    return v.toFixed(0) + ' bps';
}

// The plot (grid) rect: exactly the X-Y area, excluding the axis gutters and
// the label strip. .apexcharts-grid rather than .apexcharts-inner - the inner
// group's bounding box is the union of its children, and the scrolling
// annotation tick labels overflow the plot edge into the gutters. The grid
// group is the same rect ApexCharts' own tooltip bounds-check reads.
function gridRect() {
    const el = elId ? document.getElementById(elId) : null;
    return el?.querySelector('.apexcharts-grid')?.getBoundingClientRect() ?? null;
}

function insideGrid(p) {
    if (!p) return false;
    const r = gridRect();
    return !!r && p.x >= r.left && p.x <= r.right && p.y >= r.top && p.y <= r.bottom;
}

// Map a click inside the plot to the instant under the cursor. Bounded to the
// grid rect on both axes, so gutter and label-strip clicks don't seek - the
// same region the seek crosshair shows over.
function clickToTime(event, ctx) {
    const g = ctx?.w?.globals;
    if (!g || !Number.isFinite(g.minX) || !Number.isFinite(g.maxX)) return null;
    const rect = gridRect();
    if (!rect?.width) return null;
    if (event.clientY < rect.top || event.clientY > rect.bottom) return null;
    const frac = (event.clientX - rect.left) / rect.width;
    if (frac < 0 || frac > 1) return null;
    return g.minX + frac * (g.maxX - g.minX);
}

// True only while the value tooltip is actually on screen. ApexCharts marks
// the whole mount 'apexcharts-tooltip-active' on any mousemove over the svg,
// and its bounds check is vertical-only - so a cursor parked in a y-axis
// gutter held redraws indefinitely with no tooltip showing. With a mouse the
// class is therefore cross-checked against the pointer being inside the plot
// grid (the region where the shared tooltip really appears); on touch the
// class stands alone, since tap-to-inspect has no live pointer to check.
function tooltipShowing() {
    const el = document.getElementById(elId);
    if (!el?.classList.contains('apexcharts-tooltip-active')) return false;
    if (IS_TOUCH) return true;
    // No pointer over the container at all: the class is stale (ApexCharts
    // missed its mouseout, e.g. a fast exit). A mouse tooltip can't be
    // showing with the cursor gone - trusting the class here froze the chart
    // until the cursor happened to come back.
    return insideGrid(lastMouse);
}

function removeMouseTracking() {
    const el = elId ? document.getElementById(elId) : null;
    if (el && mouseMoveHandler) el.removeEventListener('mousemove', mouseMoveHandler);
    if (el && mouseLeaveHandler) el.removeEventListener('mouseleave', mouseLeaveHandler);
    mouseMoveHandler = null;
    mouseLeaveHandler = null;
    lastMouse = null;
}

/** True while several WANs are on screen together. */
function comparing() { return compareWans.length > 1; }

function buildOpts() {
    return {
        chart: {
            type: 'area',
            height: 175,
            background: 'transparent',
            toolbar: { show: false },
            zoom: { enabled: false },
            // Drop the default 15px dead strip below the svg so the time
            // labels sit close to the card bottom.
            parentHeightOffset: 0,
            animations: { enabled: true, easing: 'smooth', dynamicAnimation: { speed: 800 } },
            events: {
                // Click-to-seek: park the map timeline - and this chart's
                // playhead, via the normal seekTime round-trip - at the clicked
                // instant. Live View only - the mount opts in.
                click: (event, ctx) => {
                    // Touch: leave the tap for the native tooltip, don't seek.
                    if (!seekOnClick || IS_TOUCH || !event?.clientX) return;
                    const t = clickToTime(event, ctx);
                    if (t == null) return;
                    const inst = window.__lanFlowMap?.getInstance?.();
                    if (!inst?.seekTo) return;
                    // Instant feedback: freeze live drawing and draw the playhead
                    // at the clicked instant from the buffer already on screen;
                    // the seek round-trip then re-renders with the proper window.
                    pause();
                    histAt = t;
                    histWall = Date.now();
                    clickRenderUntil = Date.now() + CLICK_RENDER_MS;
                    renderHistoric(t);
                    inst.seekTo(t);
                },
            },
        },
        series: comparing()
            ? compareWans.flatMap(w => ([
                { name: `${w.label} down`, type: 'area', data: [] },
                { name: `${w.label} up`,   type: 'area', data: [] },
            ]))
            : [
                { name: 'Download', type: 'area', data: [] },
                { name: 'Upload',   type: 'area', data: [] },
                { name: 'Loss',     type: 'area', data: [] },
                { name: 'RTT',      type: 'line', data: [] },
            ],
        // Comparing: the COLOUR still says which direction a line is, because that is what the eye
        // is sorting for, and the dash pattern says which WAN. Loss and RTT drop out of the chart
        // in this mode - they are per-WAN figures in the stat cards above, and four lines per WAN
        // is not a comparison anyone can read.
        colors: comparing()
            ? compareWans.flatMap(() => [COLOR_DL, COLOR_UL])
            : [COLOR_DL, COLOR_UL, COLOR_LOSS, COLOR_RTT],
        stroke: {
            curve: 'smooth',
            width: comparing() ? compareWans.flatMap(() => [2, 2]) : [2, 2, 1, 1],
            dashArray: comparing()
                ? compareWans.flatMap((_, i) => { const d = WAN_DASH[i % WAN_DASH.length]; return [d, d]; })
                : [0, 0, 0, 6],
        },
        fill: comparing()
            ? {
                // Flat translucent fills: several overlapping gradients turn the plot into mud.
                type: compareWans.flatMap(() => ['solid', 'solid']),
                opacity: compareWans.flatMap(() => [0.12, 0.10]),
            }
            : {
                type: ['gradient', 'gradient', 'gradient', 'solid'],
                opacity: [1, 1, 1, 0],
                gradient: {
                    shadeIntensity: 0.4,
                    opacityFrom: [0.55, 0.45, 0.5, 0],
                    opacityTo:   [0.1,  0.08, 0.05, 0],
                    stops: [0, 95],
                },
            },
        markers: { size: 0 },
        dataLabels: { enabled: false },
        xaxis: {
            type: 'datetime',
            min: Date.now() - HISTORY_MINUTES * 60000,
            max: Date.now(),
            // Built-in labels are hidden: ApexCharts re-picks "nice" ticks as
            // the window slides, so the labels kept swapping between
            // :00 | :20 | :40 and :10 | :30 | :50 alignments. Time labels are
            // drawn as annotations pinned to a fixed 20s grid instead (see
            // buildTimeTicks).
            labels: { show: false },
            axisBorder: { show: false },
            axisTicks: { show: false },
        },
        // ONE axis for every throughput series while comparing - not one per series. A yaxis
        // ARRAY is laid out entry by entry even where show is false, so 2N entries stole the plot
        // width and pushed the chart past its container. A single object is also what lets the
        // WANs be read against the same scale.
        yaxis: comparing()
            ? {
                min: 0,
                max: v => v * 1.1,
                labels: {
                    style: { colors: '#9ca3af', fontSize: '10px' },
                    formatter: v => formatBps(v),
                    offsetX: -10,
                },
                axisBorder: { show: false },
                axisTicks: { show: false },
            }
            : [
            {
                seriesName: 'Download',
                min: 0,
                labels: {
                    style: { colors: '#9ca3af', fontSize: '10px' },
                    formatter: v => formatBps(v),
                    offsetX: -10,
                },
                axisBorder: { show: false },
                axisTicks: { show: false },
            },
            { seriesName: 'Download', show: false, min: 0 },
            {
                seriesName: 'Loss',
                opposite: true,
                show: false,
                min: 0,
                max: v => Math.max(v * 1.2, 10),
            },
            {
                seriesName: 'RTT',
                opposite: true,
                min: 0,
                max: 10,
                labels: {
                    style: { colors: '#9ca3af', fontSize: '10px' },
                    formatter: v => v != null ? v.toFixed(0) : '',
                    maxWidth: 30,
                    offsetX: -3,
                },
                title: { text: 'ms', style: { color: '#64748b', fontSize: '9px' }, offsetX: -4 },
                axisBorder: { show: false },
                axisTicks: { show: false },
            },
        ],
        grid: {
            borderColor: '#374151',
            strokeDashArray: 3,
            // Bottom padding holds the strip below the axis where the
            // annotation time labels render. Comparing has no opposite RTT axis holding the right
            // edge open, so it pads its own or the newest sample sits on the container's edge.
            padding: comparing()
                ? { left: 3, right: 26, top: -8, bottom: 12 }
                : { left: 3, right: 0, top: -8, bottom: 12 },
            xaxis: { lines: { show: false } },
        },
        responsive: [{
            breakpoint: 1024,
            options: {
                // One entry per series in BOTH modes: a mismatched length leaves ApexCharts
                // holding axes for series that do not exist, and the plot escapes its container.
                yaxis: comparing()
                    ? { show: false, min: 0, max: v => v * 1.1 }
                    : [
                        { seriesName: 'Download', show: false, min: 0, max: v => v * 1.1 },
                        { seriesName: 'Download', show: false, min: 0, max: v => v * 1.1 },
                        { seriesName: 'Loss', opposite: true, show: false, min: 0, max: v => Math.max(v * 1.2, 10) },
                        { seriesName: 'RTT', opposite: true, show: false, min: 0 },
                    ],
                grid: { padding: { left: -5, right: -5, top: -8, bottom: 12 } },
            },
        }],
        legend: { show: false },
        tooltip: {
            theme: 'dark',
            // Shared, so every line's value is stacked at the cursor's instant. There is no way to
            // hover one line out of 2N overlapping ones, so a per-series tooltip would be unusable
            // here - and the stack is how a WAN is told from its neighbour, since the chart has no
            // legend and the series names carry the WAN.
            shared: true,
            x: { format: 'HH:mm:ss', formatter: (val) => new Date(val).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) },
            // Comparing uses the same custom tooltip as the rest of the Monitoring charts: it
            // stacks every series at the hovered instant sorted by value, and paints its own
            // hover dots (the library's markers are the flaky ones, and any non-zero size puts a
            // permanent dot on every sample). An explicit formatter because the throughput axis
            // is an object here, not the array the helper reads by default.
            // The same custom tooltip either way - it stacks every series at the hovered instant
            // and paints its own small hover dots. What differs is the ordering: comparing puts
            // the WANs on ONE axis, so their values rank and the biggest belongs on top; the
            // single-WAN chart spreads four series across four axes, where bits per second, a
            // percentage and milliseconds cannot be ranked against each other, so they keep a
            // fixed order instead.
            custom: comparing()
                ? (ctx) => valueSortedTooltip(ctx, { format: v => formatBps(v) })
                : (ctx) => valueSortedTooltip(ctx, {
                    sort: false,
                    // order is how they READ; format is indexed by SERIES position (Loss is
                    // series 2, RTT series 3) - the two lists are deliberately not parallel.
                    order: ['Download', 'Upload', 'RTT', 'Loss'],
                    format: [
                        v => formatBps(v),
                        v => formatBps(v),
                        v => v != null ? v.toFixed(2) + '%' : '-',
                        v => v != null ? v.toFixed(1) + ' ms' : '-',
                    ],
                }),
            // Positional, one per series: a short array leaves the rest of the series with no
            // formatter at all, which renders raw bits per second.
            y: comparing()
                ? compareWans.flatMap(() => [{ formatter: v => formatBps(v) }, { formatter: v => formatBps(v) }])
                : [
                    { formatter: v => formatBps(v) },
                    { formatter: v => formatBps(v) },
                    { formatter: v => v != null ? v.toFixed(2) + '%' : '-' },
                    { formatter: v => v != null ? v.toFixed(1) + ' ms' : '-' },
                ],
        },
        noData: { text: 'Loading...', style: { color: '#64748b', fontSize: '13px' } },
    };
}

// Time labels pinned to a fixed grid, rendered as x-axis annotations so
// they scroll with the data. Full 24-hour time on every tick, placed below
// the axis in the space freed by the hidden built-in labels. The grid
// interval is chosen from the chart's own rendered width (clientWidth), not
// the viewport, so half-view panels and mobile - which constrain the chart
// below full width while the viewport stays wide - step out to a sparser
// grid instead of colliding. Full width keeps the dense 20s grid.
// Comparison series on ONE time grid, the union of every WAN's timestamps, with a null wherever a
// WAN has no reading for an instant.
//
// Each WAN's history is fetched separately and comes back on its own timestamps, so series built
// straight from those buffers share no x values. ApexCharts addresses series by data-point INDEX,
// so a shared tooltip then has nothing to print for the WANs that lack a point at the hovered
// instant - the reading you want appears only when the pointer happens to find that WAN's own line,
// which is hunting rather than reading. A common grid gives every series the same indices.
//
// Nulls rather than dropped points, for the reason alignedPoints exists in chart-tooltip.js: a
// missing point makes its neighbours adjacent and the stroke spans a gap that is really there,
// while a null ends one segment and starts another. valueSortedTooltip skips nulls, so a WAN with
// no reading costs no row.
function compareSeries() {
    const times = [...new Set(compareWans.flatMap(w =>
        (compareBuffers.get(w.key) || []).map(p => p.time)))].sort((a, b) => a - b);
    return compareWans.flatMap(w => {
        const byTime = new Map((compareBuffers.get(w.key) || []).map(p => [p.time, p]));
        const on = key => times.map(t => ({ x: t, y: byTime.get(t)?.[key] ?? null }));
        return [
            { name: `${w.label} down`, data: on('download') },
            { name: `${w.label} up`,   data: on('upload') },
        ];
    });
}

function buildTimeTicks(minMs, maxMs) {
    const width = document.getElementById(elId)?.clientWidth || 800;
    // An HH:mm:ss label is ~46px at 10px; budget 64px per slot for breathing
    // room, then pick the densest grid whose tick count fits that budget.
    const slots = Math.max(2, Math.floor(width / 64));
    const spanMs = maxMs - minMs;
    const GRIDS = [20000, 30000, 60000, 120000];
    let GRID_MS = GRIDS[GRIDS.length - 1];
    for (const g of GRIDS) {
        if (spanMs / g <= slots) { GRID_MS = g; break; }
    }
    const ticks = [];
    for (let t = Math.ceil(minMs / GRID_MS) * GRID_MS; t <= maxMs; t += GRID_MS) {
        const d = new Date(t);
        const text = [d.getHours(), d.getMinutes(), d.getSeconds()]
            .map(v => String(v).padStart(2, '0')).join(':');
        ticks.push({
            x: t,
            borderColor: 'transparent',
            label: {
                text,
                position: 'bottom',
                orientation: 'horizontal',
                offsetY: 19,
                borderColor: 'transparent',
                // The wan-chart-tick-label class opts these out of the
                // app.css rule that forces annotation labels to text-primary,
                // so they fall through to the muted .apexcharts-text fill.
                style: { background: 'transparent', fontSize: '10px', cssClass: 'wan-chart-tick-label' },
            },
        });
    }
    return ticks;
}

function rttYMax() {
    const rtts = buffer.map(p => p.rtt).filter(v => v != null && v > 0).sort((a, b) => a - b);
    if (rtts.length === 0) return 10;
    const p95 = rtts[Math.floor(rtts.length * 0.95)];
    return Math.ceil((p95 * 1.5) / 10) * 10;
}

function buildSeriesData() {
    const now = Date.now();
    const last = buffer[buffer.length - 1];
    const pts = [...buffer];
    if (last && now - last.time > 1000) {
        pts.push({ time: now, download: last.download, upload: last.upload, loss: last.loss, rtt: last.rtt });
    }
    return pts;
}

function updateChart() {
    if (!chart) return;
    if (!comparing() && buffer.length === 0) return;
    if (Date.now() > clickRenderUntil && tooltipShowing()) return;
    const now = Date.now();
    const pts = buildSeriesData();
    // Rescaling the RTT axis is a single-WAN concern: there IS no fourth axis while comparing,
    // and rebuilding the array to a fixed length of four truncated the axes for three or more
    // WANs and stamped an RTT-sized ceiling (~10) onto a throughput axis for two - which is what
    // clipped the taller WAN's line. Comparison axes are set at mount and left alone.
    chart.updateOptions(comparing()
        ? {
            xaxis: { min: now - HISTORY_MINUTES * 60000, max: now },
            annotations: { xaxis: buildTimeTicks(now - HISTORY_MINUTES * 60000, now) },
        }
        : {
            xaxis: { min: now - HISTORY_MINUTES * 60000, max: now },
            yaxis: [chart.opts.yaxis[0], chart.opts.yaxis[1], chart.opts.yaxis[2], { ...chart.opts.yaxis[3], max: rttYMax() }],
            annotations: { xaxis: buildTimeTicks(now - HISTORY_MINUTES * 60000, now) },
        }, false, false, false);
    if (comparing()) {
        chart.updateSeries(compareSeries(), false);
        return;
    }
    chart.updateSeries([
        { name: 'Download', data: pts.map(p => ({ x: p.time, y: p.download })) },
        { name: 'Upload',   data: pts.map(p => ({ x: p.time, y: p.upload })) },
        { name: 'Loss',     data: pts.map(p => ({ x: p.time, y: p.loss })) },
        { name: 'RTT',      data: pts.map(p => ({ x: p.time, y: p.rtt })) },
    ], false);
}

// Which WAN's counters this chart is showing. Null is the primary, which is what every caller
// meant before the chart could be pointed at another WAN - so an absent scope has to keep
// producing the exact request it always did.
let wanScope = null;

function historyUrl(from, to) {
    const base = `/api/monitoring/wan-live-chart-data?from=${from.toISOString()}&to=${to.toISOString()}`;
    return wanScope ? `${base}&wan=${encodeURIComponent(wanScope)}` : base;
}

async function loadHistory() {
    const to = new Date();
    const from = new Date(to.getTime() - HISTORY_MINUTES * 60000);
    try {
        const resp = await fetch(historyUrl(from, to), { credentials: 'same-origin' });
        if (!resp.ok) return 0;
        const data = await resp.json();
        applySampleInterval(data);
        buffer = (data.points || []).map(p => ({
            time: new Date(p.time).getTime(),
            download: p.downloadBps,
            upload: p.uploadBps,
            rtt: p.rttMs,
            loss: p.lossPercent ?? 0,
        }));
        // Advance the live-sample watermark past the reloaded history so the
        // next pollLive can't append a sample older than the last history
        // point (its response may predate the newest cycle history includes -
        // on mount lastSampleTime is 0, and after a background-tab refocus
        // it can be minutes stale).
        for (const p of buffer) {
            if (p.time > lastSampleTime) lastSampleTime = p.time;
        }
    } catch { }
}

/** Pulls each compared WAN's history into its own buffer. */
async function loadCompareHistory() {
    const to = new Date();
    const from = new Date(to.getTime() - HISTORY_MINUTES * 60000);
    for (const w of compareWans) {
        try {
            const resp = await fetch(
                `/api/monitoring/wan-live-chart-data?from=${from.toISOString()}&to=${to.toISOString()}&wan=${encodeURIComponent(w.key)}`,
                { credentials: 'same-origin' });
            if (!resp.ok) { compareBuffers.set(w.key, compareBuffers.get(w.key) || []); continue; }
            const data = await resp.json();
            applySampleInterval(data);
            compareBuffers.set(w.key, (data.points || []).map(p => ({
                time: new Date(p.time).getTime(),
                download: p.downloadBps,
                upload: p.uploadBps,
            })));
        } catch { compareBuffers.set(w.key, compareBuffers.get(w.key) || []); }
    }
}

/** One live tick per compared WAN, appended to that WAN's own buffer. */
async function pollLiveCompare() {
    const cutoff = Date.now() - HISTORY_MINUTES * 60000;

    // Every WAN is read for the same tick, then stamped with ONE timestamp. A shared tooltip
    // stacks values that sit at the same x - and each WAN's own SNMP sample time is a few hundred
    // milliseconds off its neighbour's, so stamping each with its own left every series on its own
    // x and the stack showed one WAN at a time. The reading is still each WAN's own; only the
    // instant they are filed under is common, which is what "at this moment" means on one chart.
    const results = await Promise.all(compareWans.map(async w => {
        try {
            const resp = await fetch(`/api/monitoring/live-stats?wan=${encodeURIComponent(w.key)}`,
                { credentials: 'same-origin' });
            if (!resp.ok) return null;
            const d = await resp.json();
            return { key: w.key, d, sampled: d.sampleTime ? new Date(d.sampleTime).getTime() : 0 };
        } catch { return null; }
    }));

    const live = results.filter(Boolean);
    if (live.length === 0) return;
    // The newest real sample time across the WANs, so the x still tracks the data rather than the
    // browser's clock; falls back to now when no WAN reported one.
    const tick = Math.max(...live.map(r => r.sampled), 0) || Date.now();
    if (tick <= lastSampleTime) return;   // same dedupe as the single-WAN path
    lastSampleTime = tick;

    for (const r of live) {
        const b = compareBuffers.get(r.key) || [];
        b.push({ time: tick, download: r.d.downloadBps, upload: r.d.uploadBps });
        compareBuffers.set(r.key, b.filter(p => p.time >= cutoff));
    }
    updateChart();
}

async function pollLive() {
    if (comparing()) return await pollLiveCompare();
    try {
        const resp = await fetch(
            wanScope ? `/api/monitoring/live-stats?wan=${encodeURIComponent(wanScope)}` : '/api/monitoring/live-stats',
            { credentials: 'same-origin' });
        if (!resp.ok) return;
        const d = await resp.json();
        // Stamp the point with the server-side SNMP sample time and skip polls
        // that return the sample we already plotted. Without this, two
        // unsynchronized ~5s clocks (SNMP tier vs setInterval) alias: some
        // samples get plotted twice and others never appear. Falls back to
        // client time when no SNMP rate data exists (rtt-only sites).
        // Strictly newer, not just different: overlapping fetches can resolve
        // out of order, and pushing an older sample after a newer one makes
        // the line double back on itself.
        const sampleTime = d.sampleTime ? new Date(d.sampleTime).getTime() : Date.now();
        if (sampleTime <= lastSampleTime) return;
        // First sample of this mount: data has just started flowing (an agent coming up, a console
        // reconnecting). The backfill cadence was started at mount against a site that had nothing
        // to backfill, so its next tick sits at an arbitrary point in the 60s window. Restart it
        // from here so the first fill happens now rather than up to a minute into live data.
        const now = Date.now();
        const stalledFor = lastLiveAt ? now - lastLiveAt : Infinity;
        const dataJustStarted = !seenLiveSample || stalledFor > RESUME_AFTER_MS;
        seenLiveSample = true;
        lastLiveAt = now;
        lastSampleTime = sampleTime;
        if (dataJustStarted) {
            dbg('live data started flowing', {
                stalledForSec: stalledFor === Infinity ? 'first' : (stalledFor / 1000).toFixed(1),
                sampleTime: new Date(sampleTime).toISOString(),
            });
            restartBackfill();
        }
        const cutoff = Date.now() - HISTORY_MINUTES * 60000;
        buffer.push({
            time: sampleTime,
            download: d.downloadBps,
            upload: d.uploadBps,
            loss: d.lossPercent ?? 0,
            rtt: d.rttMs,
        });
        buffer = buffer.filter(p => p.time >= cutoff);
        updateChart();
    } catch { }
}

// Re-pull the full history window and merge it back over the buffer so gaps that
// filled in after the buffer scrolled past them (outage backlog replay, cold
// start) show up. Live mode + foreground only. The newest live samples - which
// can be ahead of Influx ingestion - are kept ahead of the re-pulled history so
// the live edge never flickers back.
// Runs a fill now and re-phases the 60s cadence to this moment. Only meaningful while mounted;
// pause() clears the timer and a later mount starts its own.
// Data starting to flow does not mean the history is queryable yet: the latency line appears first,
// SNMP rates a couple of seconds later, and the server needs a moment more before a fill returns
// anything. Firing one fill at that instant returns nothing and leaves the next attempt a full
// BACKFILL_MS away, which is why this felt unchanged. Run a short burst of closely spaced fills
// instead, then settle back to the normal cadence - whenever the data lands, a fill is at most
// CATCHUP_MS behind it.
const CATCHUP_MS = 10000;
const CATCHUP_TRIES = 6;
let catchUpLeft = 0;

async function catchUpTick() {
    // Stop as soon as a fill actually returns something: the burst exists only to bridge the wait
    // until the server has the window queryable, and once it does there is nothing left to catch up
    // on. Normally that is a single extra fill, not the whole burst - each one re-pulls the entire
    // history window, so running them when they are not needed is pure waste.
    const got = await backfillHistory();
    if (!backfillTimer) return;
    if (got > 0 || --catchUpLeft <= 0) {
        dbg(got > 0 ? 'caught up - back to the normal cadence' : 'catch-up exhausted - normal cadence',
            { fillsLeftUnused: Math.max(catchUpLeft, 0) });
        catchUpLeft = 0;
        clearInterval(backfillTimer);
        backfillTimer = setInterval(backfillHistory, BACKFILL_MS);
    }
}

// Arms the fills in catch-up mode. Called wherever polling starts, so it cannot depend on the
// first live sample arriving after the timer exists - it did not, and the trigger was wasted on a
// stale sample served before the agent had even connected.
function startBackfillCatchUp() {
    if (backfillTimer) clearInterval(backfillTimer);
    dbg(`backfill armed - ${CATCHUP_TRIES} fills every ${CATCHUP_MS / 1000}s, then ${BACKFILL_MS / 1000}s`);
    catchUpLeft = CATCHUP_TRIES;
    backfillTimer = setInterval(catchUpTick, CATCHUP_MS);
}

// Live data resuming after a stall re-arms the same burst: the history for the gap only becomes
// queryable once the server catches up, which is after the samples themselves start arriving.
function restartBackfill() {
    if (!backfillTimer) {
        dbg('restartBackfill skipped - not polling');
        return;
    }
    startBackfillCatchUp();
}

async function backfillHistory() {
    if (!chart || !pollTimer || document.hidden) {
        dbg('backfill skipped', { chart: !!chart, polling: !!pollTimer, hidden: document.hidden });
        return 0;
    }
    const startedAt = Date.now();
    const gen = mountGen;
    const to = new Date();
    const from = new Date(to.getTime() - HISTORY_MINUTES * 60000);
    let points;
    try {
        const resp = await fetch(historyUrl(from, to), { credentials: 'same-origin' });
        if (!resp.ok) return;
        const data = await resp.json();
        applySampleInterval(data);
        points = (data.points || []).map(p => ({
            time: new Date(p.time).getTime(),
            download: p.downloadBps,
            upload: p.uploadBps,
            rtt: p.rttMs,
            loss: p.lossPercent ?? 0,
        }));
    } catch { return 0; }
    // Bail if the mount changed or we left live mode while fetching.
    if (gen !== mountGen || !pollTimer) return 0;
    const newestHist = points.length ? points[points.length - 1].time : 0;
    // The number that matters when the fill looks like it did nothing: zero points means the fill
    // ran BEFORE the server had that window queryable, so the data arrives on the next tick.
    dbg('backfill returned', {
        points: points.length,
        newestHist: newestHist ? new Date(newestHist).toISOString() : null,
        tookMs: Date.now() - startedAt,
    });
    const liveTail = buffer.filter(p => p.time > newestHist);
    const cutoff = Date.now() - HISTORY_MINUTES * 60000;
    buffer = points.concat(liveTail).filter(p => p.time >= cutoff).sort((a, b) => a.time - b.time);
    for (const p of buffer) {
        if (p.time > lastSampleTime) lastSampleTime = p.time;
    }
    updateChart();
    return points.length;
}

// Upper-left mode cluster: play/pause + Historic badge, shown only while the
// timeline is off the live edge. State comes from the shared store's playstate
// events (published by the 3D map instance, which owns the timeline).
function ensureModeUi(el) {
    if (!seekOnClick) return;
    if (!modeCluster) {
        modeCluster = document.createElement('div');
        modeCluster.className = 'wan-chart-mode-cluster';

        playBtn = document.createElement('button');
        playBtn.type = 'button';
        playBtn.className = 'lan-flow-map-scrubber-playpause';
        playBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            clickRenderUntil = Date.now() + CLICK_RENDER_MS;
            window.__lanFlowMap?.getInstance?.()?._togglePlayPause?.();
        });
        modeCluster.appendChild(playBtn);

        histBadge = document.createElement('div');
        histBadge.className = 'lan-flow-map-mode is-historic';
        histBadge.textContent = 'Historic';
        histBadge.setAttribute('data-tooltip', 'Click to return to live');
        histBadge.setAttribute('data-tooltip-hover-only', '');
        histBadge.addEventListener('click', (e) => {
            e.stopPropagation();
            clickRenderUntil = Date.now() + CLICK_RENDER_MS;
            window.__lanFlowMap?.getInstance?.()?._returnToLive?.();
        });
        modeCluster.appendChild(histBadge);
    }
    // Parent OUTSIDE the chart's mount div: ApexCharts rebuilds that div's
    // content on render (and can again on option updates), silently removing
    // any foreign children. The card body wraps the chart tightly, so the
    // cluster anchors to the same visual spot.
    (el.closest('.card-body') ?? el).appendChild(modeCluster);
    if (!unsubFlow) {
        unsubFlow = flowData.subscribe((ev) => {
            if (ev !== 'playstate') return;
            // A play/pause or historic/live flip must draw immediately, even
            // with the cursor parked over the plot: let renders through the
            // tooltip hold briefly. Fires only on state flips (and the mount
            // reset), never on playback ticks, so steady playback keeps the
            // hold for tooltip inspection.
            clickRenderUntil = Date.now() + CLICK_RENDER_MS;
            syncModeUi();
        });
    }
    syncModeUi();
}

function syncModeUi() {
    if (!modeCluster) return;
    const historic = flowData.getMode() === 'historic';
    modeCluster.style.display = historic ? '' : 'none';
    if (!playBtn) return;
    const paused = flowData.isPaused();
    playBtn.textContent = paused ? '▶' : '⏸';
    playBtn.setAttribute('aria-label', paused ? 'Play' : 'Pause');
}

export async function mount(containerId, opts) {
    // Ride in with the mount rather than in a call behind it: this module is imported
    // asynchronously, so a scope pushed separately can land before the import resolves.
    if (opts && 'wan' in opts) wanScope = opts.wan || null;
    // A selection of several arrives as wans: [{key,label}] and starts the chart in comparison
    // mode, so the first paint is already right rather than flipping a moment later.
    if (opts && Array.isArray(opts.wans)) {
        const list = opts.wans.filter(w => w && w.key);
        compareWans = list.length > 1 ? list : [];
        if (list.length >= 1) wanScope = list[0].key;
    }
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    if (scrollTimer) { clearInterval(scrollTimer); scrollTimer = null; }
    if (chart) { chart.destroy(); chart = null; }
    removeMouseTracking();
    buffer = [];
    compareBuffers.clear();
    lastSampleTime = 0;
    seenLiveSample = false;
    lastLiveAt = 0;
    chartMountedAt = Date.now();
    dbg('mount', { backfillEverySec: BACKFILL_MS / 1000 });
    const gen = ++mountGen;
    elId = containerId;
    const el = document.getElementById(containerId);
    if (!el) return;
    // On the container div (Blazor-owned, survives ApexCharts' re-renders of
    // its content), so the listeners outlive option updates.
    // The seek crosshair only over the plot itself, not the axis gutters -
    // matching where a click actually seeks (clickToTime's grid bound).
    mouseMoveHandler = (e) => {
        lastMouse = { x: e.clientX, y: e.clientY };
        if (seekOnClick && !IS_TOUCH) el.classList.toggle('wan-chart-seek-hot', insideGrid(lastMouse));
    };
    mouseLeaveHandler = () => { lastMouse = null; el.classList.remove('wan-chart-seek-hot'); };
    el.addEventListener('mousemove', mouseMoveHandler);
    el.addEventListener('mouseleave', mouseLeaveHandler);
    seekOnClick = !!opts?.seekOnClick;
    // Crosshair only where tap-to-seek is actually live (desktop).
    el.classList.toggle('wan-chart-seekable', seekOnClick && !IS_TOUCH);

    chart = new ApexCharts(el, buildOpts());
    await chart.render();
    if (gen !== mountGen) return;
    // After render - ApexCharts rebuilds the container's content, so anything
    // appended before it is wiped.
    ensureModeUi(el);

    if (comparing()) await loadCompareHistory();
    else await loadHistory();
    if (gen !== mountGen) return;
    await pollLive();
    if (gen !== mountGen) return;
    updateChart();
    pollMsOverride = opts?.pollMs || null;
    const interval = pollMsOverride || pollMs;

    pollTimer = setInterval(pollLive, interval);
    scrollTimer = setInterval(updateChart, SCROLL_MS);
    startBackfillCatchUp();

    if (visHandler) document.removeEventListener('visibilitychange', visHandler);
    visHandler = async () => {
        if (!document.hidden && chart && pollTimer) {
            await loadHistory();
            await pollLive();
        }
    };
    document.addEventListener('visibilitychange', visHandler);

    // Converge to the 3D map's (the playback authority's) current state. This
    // mount runs concurrently with the map's deep-link/scrub seeks, so the
    // seekTime push from Blazor's OnMapTimeChanged can land before the chart is
    // ready and be lost (or be wiped by the live polling started above). Pulling
    // once at mount completion closes that race: pushes that already fired are
    // recovered here, and pushes still pending find a fully mounted chart.
    const mapInst = window.__lanFlowMap?.getInstance?.();
    if (mapInst && mapInst._mode === 'historic') {
        const at = mapInst._pendingScrubAt ?? mapInst._historicAt;
        if (at) await seekTime(at.toISOString());
    } else if (mapInst && mapInst._paused) {
        pause();
    }
}

/**
 * Points the chart at another WAN and reloads its history. Deliberately does NOT touch the
 * paused/scrubbed state: changing which WAN you are looking at should not drag you back to live,
 * and a scrubbed position is still a valid position on the new WAN's series. The seek path shares
 * historyUrl(), so scrubbing after a WAN change reads that WAN too.
 */
/**
 * Shows several WANs together, or falls back to the single-WAN path for one. Rebuilds the chart:
 * the series count, their axes and their fills all differ between the two modes, so updating in
 * place would leave ApexCharts holding a config for the shape it no longer has.
 */
export async function setWans(wans) {
    const list = (Array.isArray(wans) ? wans : []).filter(w => w && w.key);
    if (list.length <= 1) {
        const wasComparing = comparing();
        compareWans = [];
        compareBuffers.clear();
        if (wasComparing) await remountChart();
        await setWan(list[0]?.key ?? null);
        // A swap made while parked has to be drawn at the parked instant too.
        if (histAt > 0) await seekTime(new Date(histAt).toISOString());
        return;
    }
    if (list.map(w => w.key).join(",") === compareWans.map(w => w.key).join(",")) return;
    compareWans = list;
    compareBuffers.clear();
    wanScope = list[0].key;
    await remountChart();
    await loadCompareHistory();
    await redrawForCurrentTime();
}

/**
 * Draws the newly loaded scope at whatever instant the chart is showing.
 *
 * Live, that is now, and updateChart is the whole job. Parked or playing back it is the instant on
 * the playhead, and updateChart draws the live edge instead - which is why changing WANs during
 * playback appeared to do nothing: the series were replaced correctly, then painted for a time the
 * user was not looking at. A full seek rather than a redraw, because the new scope holds no data
 * for that instant until it is fetched, which is what seekTime does.
 */
async function redrawForCurrentTime() {
    if (histAt > 0) {
        await seekTime(new Date(histAt).toISOString());
        return;
    }
    updateChart();
}

/** Rebuilds the chart in place with the current mode's options, keeping the mount and listeners. */
async function remountChart() {
    if (!chart || !elId) return;
    const el = document.getElementById(elId);
    if (!el) return;
    chart.destroy();
    chart = new ApexCharts(el, buildOpts());
    await chart.render();
}

export async function setWan(wanKey) {
    const next = wanKey || null;
    if (next === wanScope) return;
    wanScope = next;
    await loadHistory();
    render();
}

export function pause() {
    stopHistInterpolation();
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    if (scrollTimer) { clearInterval(scrollTimer); scrollTimer = null; }
    if (backfillTimer) { clearInterval(backfillTimer); backfillTimer = null; }
}

export function resume() {
    if (!chart || pollTimer) return;
    pollTimer = setInterval(pollLive, pollMsOverride || pollMs);
    scrollTimer = setInterval(updateChart, SCROLL_MS);
    startBackfillCatchUp();
}

// Render the historic view at a given playhead time from the current buffer.
// No fetching - callers load data; the interpolation timer reuses it.
// Hovering holds redraws (same as live updateChart) so values can be inspected
// without the chart shifting under the cursor - EXCEPT briefly after a click on
// the chart, which must draw its playhead even though the pointer (and so the
// tooltip) is still over the plot. `force` bypasses the hover-hold for a discrete
// paused seek (a deep-link or manual scrub) - its single draw must land even under
// the cursor, or it's swallowed and never retried while paused. Callers must NOT
// force during active playback: the per-tick seeks would then redraw through a
// hover, kicking the user out of tooltip inspection while the background timeline
// advances - the exact behavior the hover-hold exists to preserve.
function renderHistoric(at, force = false) {
    if (!chart || buffer.length === 0) return;
    if (!force && Date.now() > clickRenderUntil && tooltipShowing()) return;
    const halfWindow = HISTORY_MINUTES * 60000 / 2;
    const maxTime = Math.min(at + halfWindow, Date.now());
    const playhead = {
        x: at,
        borderColor: '#f1f5f9',
        strokeDashArray: 3,
        opacity: 0.5,
        label: {
            text: new Date(at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }),
            borderColor: 'transparent',
            style: { background: 'transparent', color: '#f1f5f9', fontSize: '9px' },
            position: 'front',
            orientation: 'horizontal',
            offsetY: -5,
        }
    };
    // Same window and playhead either way; only the RTT axis rescale is single-WAN, since there is
    // no RTT axis to rescale while comparing.
    const histWindow = {
        xaxis: { min: maxTime - HISTORY_MINUTES * 60000, max: maxTime },
        annotations: { xaxis: [...buildTimeTicks(maxTime - HISTORY_MINUTES * 60000, maxTime), playhead] },
    };
    chart.updateOptions(comparing()
        ? histWindow
        : {
            ...histWindow,
            yaxis: [chart.opts.yaxis[0], chart.opts.yaxis[1], chart.opts.yaxis[2], { ...chart.opts.yaxis[3], max: rttYMax() }],
        }, false, false, false);

    // Scrubbing while comparing draws every WAN at the parked instant - the point of comparing is
    // to read them against each other, and freezing the time only makes that easier.
    if (comparing()) {
        chart.updateSeries(compareSeries(), false);
        return;
    }
    chart.updateSeries([
        { name: 'Download', data: buffer.map(p => ({ x: p.time, y: p.download })) },
        { name: 'Upload',   data: buffer.map(p => ({ x: p.time, y: p.upload })) },
        { name: 'Loss',     data: buffer.map(p => ({ x: p.time, y: p.loss })) },
        { name: 'RTT',      data: buffer.map(p => ({ x: p.time, y: p.rtt })) },
    ], false);
}

// Historic playback only seeks once per second (the map's playback tick), so
// the chart would step instead of slide. Between real seeks, interpolate the
// playhead and redraw from the existing buffer at the live-mode cadence.
// Draw-only: no extra fetches or polling. The rate comes from the map
// instance's actual playback state - inferring it from seek timing also
// matched mouse drags and keyboard scrubbing (steady ~200ms steps), which
// sent the chart flying after the scrub ended.
function mapPlaybackRate() {
    const inst = window.__lanFlowMap?.getInstance?.();
    if (!inst || inst._paused || inst._mode !== 'historic' || !inst._historicPlaybackTimer) return 0;
    return inst._playbackSpeed > 0 ? inst._playbackSpeed : 0;
}

function stopHistInterpolation() {
    if (histTimer) { clearInterval(histTimer); histTimer = null; }
    histRate = 0;
    histWall = 0;
}

export async function seekTime(isoTimestamp) {
    if (!chart) return;
    seekGen++;
    if (!isoTimestamp) {
        // Return to live mode - updateChart replaces the annotations with the
        // plain time grid, dropping the playhead. (Mode cluster visibility is
        // driven by the store's playstate events, not by seeks.)
        stopHistInterpolation();
        if (pollTimer) return; // already live
        const liveGen = seekGen;
        buffer = [];
        await loadHistory();
        if (liveGen !== seekGen) return; // seeked again while loading
        updateChart();
        pollTimer = setInterval(pollLive, pollMsOverride || pollMs);
        scrollTimer = setInterval(updateChart, SCROLL_MS);
        startBackfillCatchUp();
        return;
    }
    // Historic mode: stop polling, fetch window centered on timestamp
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    if (scrollTimer) { clearInterval(scrollTimer); scrollTimer = null; }
    if (backfillTimer) { clearInterval(backfillTimer); backfillTimer = null; }
    const gen = seekGen;
    const at = new Date(isoTimestamp).getTime();
    histAt = at;
    histWall = Date.now();
    const halfWindow = HISTORY_MINUTES * 60000 / 2;
    // Fetch the window the axis will actually show. The axis is a trailing
    // 5min window ending at min(at + half, now), so a fetch centered on the
    // seek time would leave the left of the chart empty when seeking close
    // to live.
    const maxTime = Math.min(at + halfWindow, Date.now());
    const from = new Date(maxTime - HISTORY_MINUTES * 60000);
    const to = new Date(maxTime);
    try {
        const resp = await fetch(historyUrl(from, to), { credentials: 'same-origin' });
        if (!resp.ok) return;
        const data = await resp.json();
        if (gen !== seekGen) return; // a newer seek (or return to live) superseded this one
        applySampleInterval(data);
        buffer = (data.points || []).map(p => ({
            time: new Date(p.time).getTime(),
            download: p.downloadBps,
            upload: p.uploadBps,
            rtt: p.rttMs,
            loss: p.lossPercent,
        }));
    } catch { return; }
    if (buffer.length === 0) return;
    // Force the reposition draw only for a discrete/paused seek (deep-link, manual
    // scrub): it must land even under the cursor or it's never retried while paused.
    // During active playback leave it unforced so a hover still holds the redraw for
    // inspection while the background timeline (2D/3D maps) keeps advancing.
    renderHistoric(at, mapPlaybackRate() <= 0);
    if (!histTimer) {
        histTimer = setInterval(() => {
            const rate = mapPlaybackRate();
            if (rate <= 0) {
                // Not playing (paused, scrubbing, or no map): settle back on
                // the last confirmed seek position if we were extrapolating.
                if (histRate > 0) { histRate = 0; renderHistoric(histAt); }
                return;
            }
            // Playback just started: the last seek (histWall) was the final scrub
            // position, stamped BEFORE play was pressed. Re-baseline so the idle
            // gap between scrubbing and pressing play isn't counted as playback
            // time - extrapolating it made the cursor run ahead and then snap
            // back when the first real playback seek arrived.
            if (histRate === 0) histWall = Date.now();
            histRate = rate;
            const elapsed = Date.now() - histWall;
            if (elapsed > 2500) return; // seeks stalled (hidden tab etc) - hold
            renderHistoric(histAt + elapsed * rate);
        }, SCROLL_MS);
    }
}

export function unmount() {
    compareWans = [];
    compareBuffers.clear();
    mountGen++;
    stopHistInterpolation();
    if (unsubFlow) { unsubFlow(); unsubFlow = null; }
    if (modeCluster) { modeCluster.remove(); modeCluster = null; histBadge = null; playBtn = null; }
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    if (scrollTimer) { clearInterval(scrollTimer); scrollTimer = null; }
    if (backfillTimer) { clearInterval(backfillTimer); backfillTimer = null; }
    if (visHandler) { document.removeEventListener('visibilitychange', visHandler); visHandler = null; }
    if (chart) { chart.destroy(); chart = null; }
    removeMouseTracking();
    buffer = [];
    elId = null;
}
