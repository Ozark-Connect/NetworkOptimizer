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
