// Event marks for the Monitoring time-series charts: a mark on the time axis for something that
// happened to a charted series, hoverable for what it was.
//
// Shared because the chart sets that want them are the same shape - Device Stats and SFP Stats
// differ only in what a series IS. A series is identified here by an opaque key the server
// supplies (a device MAC for Device Health, device + port for SFP), and every display value the
// tooltip needs comes down with the event, so this module knows nothing about devices or ports.
//
// Event shape from the server:
//   { key, time, kind: 'reboot'|'alert', severity: 'info'|'warning'|'critical',
//     title, detail, device, port? }

import { eventColor, chartSurfaceColor } from './chart-colors.js?v=2';

// The glyph says which kind it is and the colour says how bad, so the two read independently -
// a planned firmware restart and a panic are both ↻, in different colours, which is the
// distinction the operator is actually scanning for.
const EVENT_GLYPH = { reboot: '↻', alert: '⚠' };

const SEVERITY_RANK = { info: 0, warning: 1, critical: 2 };

// Two marks closer together than this overlap into an unreadable smear - the label box is about
// 20px wide once padded. Folding at that distance is what keeps a flapping series from laying a
// solid row of glyphs across the 30d view, and it bounds the mark count at plotWidth/24 however
// many events land in the window. Nothing is dropped: a folded event is still listed in its
// cluster's tooltip.
const MARK_COLLISION_PX = 24;

// Set on the label once its tooltip is bound, so a redraw that replaced the nodes is visible by
// counting rather than by tracking who redrew what. The label carries it whichever element
// hitTarget bound the tooltip to.
const TAGGED = 'data-mark-bound';

const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s ?? ''; return _esc.innerHTML; }

const DATE_TIME = { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' };
const TIME_ONLY = { hour: '2-digit', minute: '2-digit' };

function formatTime(time, opts) {
    return new Date(time).toLocaleString(undefined, opts);
}

function row(label, value) {
    return `<div class="device-tooltip-row chart-mark-row">`
        + `<span class="device-tooltip-label">${label}:</span> ${escapeHtml(value)}</div>`;
}

// Colour and glyph both come from the worst event in the fold so they can never disagree: a
// cluster holding one kernel panic reads as a panic, not as the routine restart beside it.
function dominant(marks) {
    return marks.reduce((worst, m) =>
        (SEVERITY_RANK[m.severity] ?? 0) > (SEVERITY_RANK[worst.severity] ?? 0) ? m : worst);
}

function singleTooltip(mark) {
    const rows = [`<div class="chart-mark-title">${escapeHtml(mark.title)}</div>`];
    if (mark.device) rows.push(row('Device', mark.device));
    if (mark.port) rows.push(row('Port', mark.port));
    rows.push(row('When', formatTime(mark.time, DATE_TIME)));
    // Detail is prose, not a value, so it wraps full width under its own label rather than
    // being squeezed into the value column beside it.
    if (mark.detail) {
        rows.push(`<div class="device-tooltip-row chart-mark-row chart-mark-detail">`
            + `<span class="device-tooltip-label">Detail:</span> ${escapeHtml(mark.detail)}</div>`);
    }
    return rows.join('');
}

function foldedTooltip(marks) {
    // Time alone is ambiguous once a fold spans days, which it will at the 30d range.
    const days = new Set(marks.map(m => new Date(m.time).toDateString()));
    const opts = days.size > 1 ? DATE_TIME : TIME_ONLY;

    const items = marks.map(mark => {
        const where = [mark.device, mark.port ? `port ${mark.port}` : null].filter(Boolean).join(' . ');
        return `<div class="chart-mark-item">`
            + `<span class="chart-mark-item-time">${escapeHtml(formatTime(mark.time, opts))}</span>`
            + `<span class="chart-mark-item-body">`
            + `<span class="chart-mark-item-title">${escapeHtml(mark.title)}</span>`
            + (where ? `<span class="chart-mark-item-where">${escapeHtml(where)}</span>` : '')
            + `</span></div>`;
    }).join('');

    return `<div class="chart-mark-title">${marks.length} events</div>`
        + `<div class="chart-mark-list">${items}</div>`;
}

/**
 * Creates the mark layer for one chart set.
 *
 * @param {object} options
 * @param {() => Array<[object, Element]>} options.charts Every chart to mark, paired with the
 *   element it rendered into - ApexCharts draws annotation labels as SVG text, so the tooltip is
 *   attached by walking that SVG afterwards.
 */
export function createMarkLayer({ charts }) {
    let lastSignature = null;

    // Milliseconds per pixel of plot, from the charts' own geometry rather than the requested
    // range: gridWidth excludes the y-axis gutter, and minX/maxX are the extents ApexCharts
    // actually drew. Every chart in a set shares a width and an x-window, so the first that can
    // answer speaks for all of them - which also keeps the folds lined up vertically instead of
    // each chart clustering to its own slightly different answer.
    //
    // Asking every chart rather than a nominated one matters: a series that is null throughout
    // renders empty, so a set whose first chart has no data would otherwise lose folding on the
    // charts that do.
    function msPerPx() {
        for (const [chart] of charts()) {
            const g = chart?.w?.globals;
            if (!g || !(g.gridWidth > 0) || !(g.maxX > g.minX)) continue;
            return (g.maxX - g.minX) / g.gridWidth;
        }
        return null;
    }

    // Events arrive time-ordered from the server, so one pass does it. The gap is measured from
    // the cluster's FIRST member so the mark stays anchored where the run started rather than
    // drifting rightward as the cluster absorbs more.
    function cluster(marks) {
        const perPx = msPerPx();
        // No geometry yet (first paint, or nothing drawn to scale against) means no folding,
        // which is the un-folded behaviour rather than a guess.
        if (perPx == null) return marks.map(m => ({ at: new Date(m.time).getTime(), marks: [m] }));

        const gapMs = MARK_COLLISION_PX * perPx;
        const clusters = [];
        for (const mark of marks) {
            const at = new Date(mark.time).getTime();
            const open = clusters[clusters.length - 1];
            if (open && at - open.at <= gapMs) open.marks.push(mark);
            else clusters.push({ at, marks: [mark] });
        }
        return clusters;
    }

    // ApexCharts stamps each label with rel="<index into the annotation array>", so each cluster
    // is matched to its own label by that rather than by trusting NodeList order.
    //
    // Setting the attribute is all that is needed to get a tooltip: App.razor rescans for
    // [data-tooltip] and initializes Tippy on anything new, which is how the other dynamically
    // drawn badges work.
    function tag(el, clusters) {
        if (!el) return;
        clusters.forEach((c, i) => {
            const label = el.querySelector(`.apexcharts-xaxis-annotation-label[rel='${i}']`);
            if (!label) return;
            const target = hitTarget(label);
            target.setAttribute('data-tooltip',
                c.marks.length === 1 ? singleTooltip(c.marks[0]) : foldedTooltip(c.marks));
            target.setAttribute('data-tooltip-interactive', '');
            target.style.cursor = 'help';
            label.setAttribute(TAGGED, '');
        });
    }

    // Any series redraw - a filter toggle, a poll - re-renders the chart, and ApexCharts rebuilds
    // the annotation nodes from config when it does. The marks come back looking identical with
    // nothing bound to them. The signature covers what the marks are drawn FROM, so it cannot see
    // this, and a toggle that leaves the event set alone would strand every tooltip until the time
    // range moved. Re-tagging is all it takes: the annotations themselves are already correct.
    function retag(clusters) {
        for (const [, el] of charts()) {
            if (!el) continue;
            if (el.querySelectorAll(`.apexcharts-xaxis-annotation-label[${TAGGED}]`).length
                !== clusters.length) tag(el, clusters);
        }
    }

    // The whole box is the target, not the glyph inside it. An SVG <text> only hit-tests against
    // the strokes it paints, so binding the tooltip to the label leaves an 11px arrow with holes
    // in it - fine to hover by accident, near impossible to hit on purpose, and worse under a
    // fingertip. ApexCharts draws the label's background as a bare <rect> inserted directly
    // before the text, which is exactly the square the operator is aiming at.
    function hitTarget(label) {
        const box = label.previousElementSibling;
        if (box?.tagName !== 'rect') return label;
        // The text paints over the box, so it has to stay transparent to pointers or it punches
        // a dead hole through the middle of the target it sits on.
        label.style.pointerEvents = 'none';
        return box;
    }

    // Each redraw replaces the label elements, so the Tippy instances bound to the outgoing ones
    // have to go with them - otherwise a 5s poll leaves an orphan per mark per tick.
    function untag(el) {
        if (!el) return;
        el.querySelectorAll('.apexcharts-xaxis-annotation-label').forEach(label => {
            label._tippy?.destroy();
            label.previousElementSibling?._tippy?.destroy();
        });
    }

    return {
        /**
         * Draws the marks for the currently visible series.
         *
         * @param {Array<object>} events Events from the server, time-ordered.
         * @param {object} visibility Series key to false when hidden; anything else is visible.
         */
        apply(events, visibility) {
            // Filtering here rather than server-side keeps the badge toggles instant: hiding a
            // series drops its marks without a refetch, the same way it drops its line.
            const clusters = cluster((events || []).filter(e => visibility[e.key] !== false));

            // A poll usually returns the same events over the same geometry, and redrawing then
            // costs a full annotation teardown - and a Tippy rebuild - for no visible change.
            // The signature covers what the marks are drawn FROM, so a new event, a filter
            // toggle or a resize still gets through.
            const signature = JSON.stringify([msPerPx(), clusters.map(c => [c.at, c.marks.length])]);
            if (signature === lastSignature) { retag(clusters); return; }
            lastSignature = signature;

            const surface = chartSurfaceColor();
            const xaxis = clusters.map(c => {
                const lead = dominant(c.marks);
                const color = eventColor(lead.severity);
                const glyph = EVENT_GLYPH[lead.kind] || EVENT_GLYPH.alert;
                return {
                    x: c.at,
                    borderColor: color,
                    strokeDashArray: 4,
                    label: {
                        text: c.marks.length > 1 ? `${glyph}${c.marks.length}` : glyph,
                        borderColor: color,
                        // ApexCharts' own stylesheet puts pointer-events: none on every
                        // annotation label. The class lets app.css unlock it for the fallback
                        // in hitTarget; it is concatenated onto theirs, not swapped for it,
                        // so app.css can win on specificity without !important.
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

            for (const [chart, el] of charts()) {
                if (!chart) continue;
                untag(el);
                // Wrapped rather than chained directly: the labels only exist once the update
                // has rendered, and updateOptions is not promise-returning in every build.
                // Fourth argument false: grouped charts otherwise take each other's options,
                // and these marks belong to the chart they were measured against.
                Promise.resolve(chart.updateOptions({ annotations: { xaxis } }, false, false, false))
                    .then(() => tag(el, clusters))
                    .catch(e => console.warn('Chart event marks failed to draw', e));
            }
        },

        /** Forgets the last drawn state, so the next apply always redraws. */
        reset() { lastSignature = null; },
    };
}
