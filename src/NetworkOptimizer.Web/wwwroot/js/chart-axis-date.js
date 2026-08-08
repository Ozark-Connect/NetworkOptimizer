// The window's date, named once under the x-axis.
//
// The tick labels are times, and ApexCharts' datetimeFormatter only grows a date where the
// granularity rolls over - so any window that crosses no midnight said nowhere what day it was
// showing. There is no option for stamping the date once, and a caption is the one place that
// holds still: the tick that would carry it moves with the chart's width.

// Above a day the ticks carry dates themselves, so the caption would only repeat them.
const SUPPRESS_AT_MS = 24 * 3600000;

// The tick labels' own style, so the caption reads as one of them rather than as a heading under
// them. ApexCharts renders x-axis labels at 12px/600 in this family; the color is the shared
// axis grey.
const TITLE_STYLE = {
    color: '#9ca3af',
    fontSize: '12px',
    fontFamily: 'Helvetica, Arial, sans-serif',
    fontWeight: 600,
};

/**
 * @param {() => Array} charts Every chart the caption belongs on. Entries may be chart instances
 *   or the [chart, element] pairs the mark layer takes, so a module hands over whichever list it
 *   already keeps rather than maintaining a second one.
 * @param {() => {from: Date, to: Date}} window The window on screen. Modules compute this from
 *   their own range state, which is where the custom ranges and shift offsets live.
 */
export function createAxisDateCaption({ charts, window: getWindow }) {
    let shown = null;

    function text() {
        const { from, to } = getWindow() || {};
        if (!from || !to || to - from >= SUPPRESS_AT_MS) return '';
        // Zero-padded to match a day tick, which datetimeFormatter draws as "MMM dd".
        const fmt = d => d.toLocaleDateString(undefined, { month: 'short', day: '2-digit' });
        const start = fmt(from), end = fmt(to);
        return start === end ? start : `${start} - ${end}`;
    }

    return {
        /**
         * The xaxis.title a chart is created with. Records what it hands out, so the first load
         * after a mount has nothing to correct - a chart is usually built several times per tab.
         */
        option() {
            shown = text();
            return { text: shown, style: TITLE_STYLE };
        },

        /**
         * Repaints the caption if the window now says something different - a range the user
         * moved, or a trailing window crossing midnight. Guarded because updateOptions redraws
         * and the callers poll every few seconds.
         *
         * Call it BEFORE the mark layer draws, never after: this redraw recreates the annotation
         * label elements that the layer's tooltips are bound to.
         */
        apply() {
            const next = text();
            if (next === shown) return;
            shown = next;
            for (const entry of charts()) {
                const chart = Array.isArray(entry) ? entry[0] : entry;
                chart?.updateOptions({ xaxis: { title: { text: next } } }, false, false, false);
            }
        },

        /** Forgets what was drawn, for a module tearing its charts down. */
        reset() { shown = null; },
    };
}
