// Shared tooltip behaviour for the Monitoring time-series charts.
//
// Lived in device-health-charts.js and latency-charts.js as two copies of the same
// function; the other five chart sets used the stock ApexCharts tooltip, which lists
// series in declaration order however the lines actually sit on the plot.

/**
 * Rows ordered by value descending, so the tooltip reads in the same vertical order as
 * the lines at the hovered instant. Values format through the chart's own y-axis
 * formatter, so units stay correct per chart.
 *
 * Skips null points rather than printing them: a series with no reading at this instant
 * has nothing to say, and a row of blanks pushes the values that matter off the bottom.
 */
const SVG_NS = 'http://www.w3.org/2000/svg';
const DOT_LAYER = 'netopt-hover-dots';

/**
 * A dot on EVERY line at the hovered instant, drawn by us.
 *
 * ApexCharts will not do this for these charts. Its hover-marker pass only runs when the
 * hovered series already has marker elements (`hasMarkers`), and with `markers.size: 0`
 * there are none - so usually nothing was drawn at all. When a chart happened to contain
 * nulls the library created virtual markers, which flipped it onto an index-based path
 * that reads pointsArray[series][hoveredIndex] and throws on any series that is shorter,
 * keeping whatever it had drawn so far. That is the none/one/several behaviour: it was
 * data-dependent, not random. Giving markers a real size would fix the path and put a
 * visible dot on every point of every series, which on a month of samples is a smear -
 * and `newPointSize` refuses to enlarge a marker whose default size is 0, so there is no
 * invisible-at-rest option to reach for.
 *
 * Drawing them here costs one <circle> per visible series per hover and needs no marker
 * elements at all. Nulls are skipped, so a line with no reading at this instant has no dot
 * rather than one parked on the axis.
 */
// The x the pointer is actually on. Taken from the hovered series when there is one, and from
// the first series holding a point at that index otherwise, which is what a shared tooltip lands
// on when it cannot name one.
function hoveredXValue(w, dataPointIndex, seriesIndex) {
    const direct = w.globals.seriesX[seriesIndex]?.[dataPointIndex];
    if (direct != null && !isNaN(direct)) return direct;
    for (const xs of w.globals.seriesX || []) {
        const x = xs?.[dataPointIndex];
        if (x != null && !isNaN(x)) return x;
    }
    return null;
}

// Where a series holds its point for a given x, or -1 when it has none near enough to be the
// same moment. Series on this chart are separate targets polled at their own intervals, so the
// same index means a different time in each of them - matching on the value is what keeps the
// dots on one vertical line instead of scattering them along the axis.
function indexAtX(xs, x) {
    if (!xs || xs.length === 0 || x == null) return -1;
    // Binary search: x is ascending, and this now runs for the rows as well as the dots, on every
    // mousemove, over ranges that reach thousands of points per series.
    let lo = 0, hi = xs.length - 1;
    while (lo < hi) {
        const mid = (lo + hi) >> 1;
        if (xs[mid] < x) lo = mid + 1; else hi = mid;
    }
    let best = lo;
    if (lo > 0 && Math.abs(xs[lo - 1] - x) <= Math.abs(xs[lo] - x)) best = lo - 1;
    const bestGap = Math.abs(xs[best] - x);
    // Half a step of this series' own spacing: close enough to be the same bucket, far enough
    // out and the series simply has nothing here, which is worth showing as a missing dot
    // rather than as a dot in the wrong place.
    const span = xs.length > 1 ? Math.abs(xs[xs.length - 1] - xs[0]) / (xs.length - 1) : 0;
    return bestGap <= Math.max(span / 2, 1) ? best : -1;
}

/**
 * Which point each series holds at the hovered moment - ONE resolution, used for the dot and for
 * the row alike.
 *
 * They were resolved separately, and disagreed the moment two series did not share timestamps: the
 * dot was placed by matching the x value while the row read the array position, so a series polled
 * on its own cadence drew its dot on a spike and printed the value it held at some other moment.
 * The reader then had to hunt along the line for the x where the two happened to coincide.
 */
function hoveredIndices(w, dataPointIndex, seriesIndex) {
    const hoveredX = hoveredXValue(w, dataPointIndex, seriesIndex);
    const at = (w.globals.seriesX || []).map(xs =>
        hoveredX == null ? dataPointIndex : indexAtX(xs, hoveredX));
    return { hoveredX, at };
}

function paintHoverDots(w, at) {
    const inner = w.globals.dom.baseEl.querySelector('.apexcharts-inner');
    if (!inner) return;

    let layer = inner.querySelector('.' + DOT_LAYER);
    if (!layer) {
        // Stand ApexCharts' own hover markers down. It creates virtual ones whenever the
        // chart contains a null - which alignedPoints makes routine - and then moves them
        // under the pointer, so its dots appeared on top of ours. Hidden in CSS rather than
        // per paint because the library re-shows them on its own schedule, not ours.
        w.globals.dom.baseEl.classList.add('netopt-own-hover-dots');

        layer = document.createElementNS(SVG_NS, 'g');
        layer.setAttribute('class', DOT_LAYER);
        // Never swallow a pointer event: these sit over the plot, and the chart needs the
        // mouse to keep reaching it or the tooltip flickers as the pointer crosses a dot.
        layer.setAttribute('pointer-events', 'none');
        inner.appendChild(layer);

        // The dots outlive the tooltip otherwise - ApexCharts hides its own tooltip on the
        // way out and knows nothing about this layer.
        const host = w.globals.dom.baseEl;
        host.addEventListener('mouseleave', () => { layer.innerHTML = ''; });
    }

    while (layer.firstChild) layer.removeChild(layer.firstChild);

    const points = w.globals.pointsArray;
    for (let i = 0; i < points.length; i++) {
        if (w.globals.collapsedSeriesIndices?.indexOf(i) >= 0) continue;
        if (at[i] == null || at[i] < 0) continue;
        const p = points[i]?.[at[i]];
        if (!p) continue;
        const [cx, cy] = p;
        if (cx == null || cy == null || isNaN(cx) || isNaN(cy)) continue;

        const dot = document.createElementNS(SVG_NS, 'circle');
        dot.setAttribute('cx', cx);
        dot.setAttribute('cy', cy);
        dot.setAttribute('r', 4);
        dot.setAttribute('fill', w.globals.colors[i % w.globals.colors.length]);
        dot.setAttribute('stroke', '#0f0f11');
        dot.setAttribute('stroke-width', 2);
        layer.appendChild(dot);
    }
}

export function valueSortedTooltip({ series, seriesIndex, dataPointIndex, w }, options = {}) {
    const { hoveredX, at } = hoveredIndices(w, dataPointIndex, seriesIndex);
    paintHoverDots(w, at);
    const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    // The axis formatter by default, since it is already right for the chart. An explicit one
    // is for charts whose axis deliberately omits a unit that the tooltip should still carry -
    // ISP Health's axis reads "12.4" under an "ms" title, but its tooltip says "12.4 ms".
    // An ARRAY of formatters addresses series by index, for a chart whose series do not share a
    // unit - throughput beside loss beside latency, where one formatter cannot be right for all.
    const fmtOpt = options.format ?? w.config.yaxis?.[0]?.labels?.formatter ?? (v => v);
    const fmtFor = i => Array.isArray(fmtOpt) ? (fmtOpt[i] ?? (v => v)) : fmtOpt;
    const rows = [];
    let ts = hoveredX;
    for (let i = 0; i < series.length; i++) {
        // The same index the dot uses. A series whose nearest reading is further off than half its
        // own spacing has nothing to say at this moment, so it gets no row - the same silence it
        // gets no dot for, rather than a number belonging to some other instant.
        const j = at[i] ?? dataPointIndex;
        if (j < 0) continue;
        const v = series[i]?.[j];
        if (v == null) continue;
        ts ??= w.globals.seriesX[i]?.[j];
        rows.push({ name: w.globals.seriesNames[i], color: w.globals.colors[i % w.globals.colors.length], v, i });
    }
    // Sorted by value only where the series share a SCALE - several WANs' throughput on one axis,
    // several targets' latency on one axis - because then the number and the height on the chart
    // rank the same way. Series on different axes must not be sorted: bits per second, a
    // percentage and milliseconds have no common order, so ranking them by raw magnitude puts
    // throughput on top forever and tells the reader nothing. Those pass sort: false and keep
    // their fixed places, which is also where the eye expects to find them.
    if (options.sort !== false) rows.sort((a, b) => b.v - a.v);
    else if (Array.isArray(options.order)) {
        // Reading order, not series order: the chart draws throughput first because it is the
        // backdrop, but the pair a reader compares is RTT and loss, so those sit together.
        const rank = new Map(options.order.map((n, i) => [n, i]));
        rows.sort((a, b) => (rank.get(a.name) ?? 99) - (rank.get(b.name) ?? 99));
    }
    // Seconds by default, because the Monitoring charts poll fast enough for them to mean
    // something. A chart on a slower cadence can drop them - ISP Health polls once a minute over a
    // 24 hour window, where a seconds field is noise and was never shown before this was shared.
    const timeParts = options.omitSeconds
        ? { month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false }
        : { month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false };
    const when = ts ? new Date(ts).toLocaleString(undefined, timeParts) : '';
    return (when ? '<div class="apexcharts-tooltip-title" style="font-family:Helvetica, Arial, sans-serif;font-size:12px">' + esc(when) + '</div>' : '')
        + rows.map(r =>
            '<div class="apexcharts-tooltip-series-group apexcharts-active" style="display:flex">'
            + '<span class="apexcharts-tooltip-marker" style="background-color:' + r.color + ';border-radius:50%;width:12px;height:12px"></span>'
            + '<div class="apexcharts-tooltip-text" style="font-family:Helvetica, Arial, sans-serif;font-size:12px"><div class="apexcharts-tooltip-y-group">'
            + '<span class="apexcharts-tooltip-text-y-label">' + esc(r.name) + ': </span>'
            + '<span class="apexcharts-tooltip-text-y-value">' + esc(fmtFor(r.i)(r.v)) + '</span>'
            + '</div></div></div>').join('');
}

/**
 * Points for one series, keeping every timestamp in `pts` and marking gaps as null.
 *
 * Filtering a series down to its own non-null readings looks harmless and breaks two things
 * at once, because ApexCharts addresses the other series by data-point INDEX rather than by
 * timestamp. A shared tooltip then lines up only the first series; worse, the hover-dot pass
 * (moveDynamicPointsOnHover) reads pointsArray[series][hoveredIndex][1] for every series, so
 * a series shorter than the hovered index throws and the loop abandons every dot after it.
 * That is the "sometimes several dots, usually just one" behaviour.
 *
 * A null y CUTS the line where the filter used to span it: dropping a point makes its
 * neighbours adjacent, so the plot joined across the hole, while a null ends one segment and
 * starts another. valueSortedTooltip skips nulls either way, so the gaps still cost no
 * tooltip rows - only the stroke differs. Pass `gapBridgeMs` where that visible cut is wrong.
 *
 * `interpolateNulls` turns the bridging half off while keeping the break half. A caller whose rows
 * already carry explicit nulls may want those drawn as the breaks they are, and still needs a break
 * inserted where there is no row at all - the two halves are independent, and ISP Health wants only
 * the second.
 *
 * `gapBridgeMs` restores the spanning stroke without losing the index alignment above:
 *   - a null run whose bracketing readings sit within the budget is interpolated, so the
 *     line carries and the array keeps its length and indices;
 *   - a longer run stays null and breaks the line, which is what a real outage should look
 *     like;
 *   - a stretch with no ROW at all - the query uses createEmpty:false, so an outage yields
 *     no row rather than a null one - gets a null inserted, because otherwise the two
 *     readings either side are adjacent and the line spans the outage silently.
 * The insert keys off row timestamps, which every field of one source array shares, so all
 * of that array's series receive the same insertions and stay aligned with each other.
 *
 * A series with no readings at all comes back empty and should be dropped by the caller: the
 * dot pass skips empty series safely, and an all-null line draws nothing anyway.
 *
 * NOTE: this aligns series that share one source array. Series built from DIFFERENT sources
 * (several devices or targets on one chart) can still hold genuinely different timestamps,
 * which no per-series transform can reconcile.
 */
/**
 * Typical spacing between consecutive rows, used to scale the gap budget to whatever aggregate
 * window the server chose. Median rather than mean so one long outage in the range cannot inflate
 * it. Needs at least three points to mean anything; below that the caller's own value stands.
 */
function medianDelta(pts) {
    if (pts.length < 3) return 0;
    const deltas = [];
    for (let i = 1; i < pts.length; i++) deltas.push(pts[i].x - pts[i - 1].x);
    deltas.sort((a, b) => a - b);
    return deltas[deltas.length >> 1];
}

export function alignedPoints(pts, sel, timeKey = 'time', gapBridgeMs = 0, interpolateNulls = true) {
    if (!pts?.some(p => sel(p) != null)) return [];
    const out = pts.map(p => ({ x: new Date(p[timeKey]).getTime(), y: sel(p) ?? null }));
    if (gapBridgeMs <= 0) return out;

    // The budget must follow the data's own cadence, not a wall-clock constant. The server picks an
    // aggregate window from the range asked for, so at 24h / 7d / 30d the rows are minutes or hours
    // apart - and a fixed budget smaller than the bucket makes EVERY pair of points too far apart,
    // inserting a null between all of them. Every segment then breaks, and with markers.size 0 an
    // isolated point draws nothing at all: the chart went blank except for the newest stretch,
    // where the buckets were still tighter than the constant. The tooltip kept reporting values,
    // because the data was always there - only the strokes were gone.
    //
    // Scaling off the median row spacing keeps the caller's value as a FLOOR for dense ranges while
    // letting a coarse one breathe. A gap of up to a couple of buckets bridges, which at these
    // zooms is under a pixel of chart; anything longer still breaks the line, which is what a real
    // outage should look like.
    const budget = Math.max(gapBridgeMs, 2.5 * medianDelta(out));

    for (let i = 0; interpolateNulls && i < out.length; i++) {
        if (out[i].y != null) continue;
        let end = i;
        while (end < out.length && out[end].y == null) end++;
        // A run at either edge has nothing to interpolate between, so it stays null.
        const prev = i > 0 ? out[i - 1] : null;
        const next = end < out.length ? out[end] : null;
        if (prev && next && next.x - prev.x <= budget) {
            const span = next.x - prev.x;
            for (let j = i; j < end; j++)
                out[j].y = prev.y + (next.y - prev.y) * ((out[j].x - prev.x) / span);
        }
        i = end - 1;
    }

    const withBreaks = [];
    for (let i = 0; i < out.length; i++) {
        if (i > 0 && out[i].x - out[i - 1].x > budget)
            withBreaks.push({ x: out[i - 1].x + 1, y: null });
        withBreaks.push(out[i]);
    }
    return withBreaks;
}

/**
 * True while a tooltip is open anywhere under <paramref name="root"/>. A poll tick that
 * redraws under the pointer tears the tooltip away mid-read, so the tick is skipped and
 * the next one picks it up - the same hold-off the WAN Live chart already uses.
 *
 * Deliberately checks the element AND its descendants: WAN Live passes the chart element
 * itself, while the Monitoring tabs pass a container holding several charts.
 */
export function tooltipHeld(root) {
    if (!root) return false;
    return root.classList?.contains('apexcharts-tooltip-active')
        || !!root.querySelector?.('.apexcharts-tooltip-active');
}
