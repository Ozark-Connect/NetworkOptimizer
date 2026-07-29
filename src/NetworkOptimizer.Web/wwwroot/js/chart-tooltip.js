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
export function valueSortedTooltip({ series, dataPointIndex, w }) {
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
 * A null y breaks the line exactly where the filter used to remove the point, so the plot is
 * unchanged, and valueSortedTooltip skips nulls so the gaps cost no tooltip rows.
 *
 * A series with no readings at all comes back empty and should be dropped by the caller: the
 * dot pass skips empty series safely, and an all-null line draws nothing anyway.
 *
 * NOTE: this aligns series that share one source array. Series built from DIFFERENT sources
 * (several devices or targets on one chart) can still hold genuinely different timestamps,
 * which no per-series transform can reconcile.
 */
export function alignedPoints(pts, sel, timeKey = 'time') {
    if (!pts?.some(p => sel(p) != null)) return [];
    return pts.map(p => ({ x: new Date(p[timeKey]).getTime(), y: sel(p) ?? null }));
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
