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
function paintHoverDots(w, dataPointIndex) {
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
        const p = points[i]?.[dataPointIndex];
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

export function valueSortedTooltip({ series, dataPointIndex, w }) {
    paintHoverDots(w, dataPointIndex);
    const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const fmt = w.config.yaxis?.[0]?.labels?.formatter ?? (v => v);
    const rows = [];
    let ts = null;
    for (let i = 0; i < series.length; i++) {
        const v = series[i]?.[dataPointIndex];
        if (v == null) continue;
        ts ??= w.globals.seriesX[i]?.[dataPointIndex];
        rows.push({ name: w.globals.seriesNames[i], color: w.globals.colors[i % w.globals.colors.length], v });
    }
    rows.sort((a, b) => b.v - a.v);
    const when = ts ? new Date(ts).toLocaleString(undefined, { month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }) : '';
    return (when ? '<div class="apexcharts-tooltip-title" style="font-family:Helvetica, Arial, sans-serif;font-size:12px">' + esc(when) + '</div>' : '')
        + rows.map(r =>
            '<div class="apexcharts-tooltip-series-group apexcharts-active" style="display:flex">'
            + '<span class="apexcharts-tooltip-marker" style="background-color:' + r.color + ';border-radius:50%;width:12px;height:12px"></span>'
            + '<div class="apexcharts-tooltip-text" style="font-family:Helvetica, Arial, sans-serif;font-size:12px"><div class="apexcharts-tooltip-y-group">'
            + '<span class="apexcharts-tooltip-text-y-label">' + esc(r.name) + ': </span>'
            + '<span class="apexcharts-tooltip-text-y-value">' + esc(fmt(r.v)) + '</span>'
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
export function alignedPoints(pts, sel, timeKey = 'time', gapBridgeMs = 0) {
    if (!pts?.some(p => sel(p) != null)) return [];
    const out = pts.map(p => ({ x: new Date(p[timeKey]).getTime(), y: sel(p) ?? null }));
    if (gapBridgeMs <= 0) return out;

    for (let i = 0; i < out.length; i++) {
        if (out[i].y != null) continue;
        let end = i;
        while (end < out.length && out[end].y == null) end++;
        // A run at either edge has nothing to interpolate between, so it stays null.
        const prev = i > 0 ? out[i - 1] : null;
        const next = end < out.length ? out[end] : null;
        if (prev && next && next.x - prev.x <= gapBridgeMs) {
            const span = next.x - prev.x;
            for (let j = i; j < end; j++)
                out[j].y = prev.y + (next.y - prev.y) * ((out[j].x - prev.x) / span);
        }
        i = end - 1;
    }

    const withBreaks = [];
    for (let i = 0; i < out.length; i++) {
        if (i > 0 && out[i].x - out[i - 1].x > gapBridgeMs)
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
