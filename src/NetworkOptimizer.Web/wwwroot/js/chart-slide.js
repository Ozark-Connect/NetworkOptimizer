// Smooth scrolling for a live time chart between full ApexCharts renders.
//
// A render rebuilds the whole SVG on the main thread (~30 ms on a phone), so a live chart renders
// only when data arrives. Between renders this slides the plotted series and the time labels left
// with a transform, one device pixel at a time. The y-axes and the grid stay put.
//
// Each render draws the series and the labels past the right edge by LEAD_MS (the caller extends
// its data that far), so new labels and the carried-forward line slide in from the edge rather
// than appearing. The time labels are drawn here, not as ApexCharts annotations: ApexCharts skips
// an annotation past xaxis.max, which is exactly the label about to slide in.
//
// While `isHeld()` (the value tooltip is open) the slide returns to 0, where the drawn content
// matches ApexCharts' own scale, so the tooltip and click-to-seek read the right instant.

const SVG = 'http://www.w3.org/2000/svg';
/** How far past the window's right edge each render draws at 1x, in ms. */
export const LEAD_MS = 20000;
// Playback seeks land once a wall second, so a fast playback needs the lead to cover that second
// (and the seek's fetch) in chart time; a fixed 20 s ran out a third of the way there at 60x.
const LEAD_WALL_MS = 2500;
const LEAD_MAX_MS = 600000;

/** The lead for a slide rate (chart ms per wall ms): the caller fetches and draws this far ahead. */
export function leadMs(rate) {
    return Math.min(LEAD_MAX_MS, Math.max(LEAD_MS, (rate || 0) * LEAD_WALL_MS));
}

// Re-render once this share of the lead has slid in, so the edge never runs out.
const STALE_SHARE = 0.75;
// The ease back to the live edge after a hold, and the longest hold it covers: the data kept ends
// at the window's left edge, so a longer catch-up would open a blank strip there.
const CATCH_MS = 400;
const CATCH_MAX_MS = 20000;
// Labels fade over this many px at each end instead of being cut by the clip.
const FADE_PX = 18;
let slideIds = 0;

/**
 * The tick step for a window: the densest of 20 s / 30 s / 1 min / 2 min whose labels fit the
 * plot width. A 12-hour label ("10:32:20 PM") is wider and gets a wider slot.
 */
export function tickStepMs(widthPx, spanMs) {
    const slots = Math.max(2, Math.floor(widthPx / (noTime.format().h24 ? 64 : 80)));
    for (const g of [20000, 30000, 60000, 120000]) {
        if (spanMs / g <= slots) return g;
    }
    return 120000;
}

/**
 * @param {object} o
 * @param {() => any} o.chart        the ApexCharts instance
 * @param {() => HTMLElement} o.el   the chart's container
 * @param {() => boolean} o.isHeld   true while the slide must sit at 0 (tooltip open)
 * @param {() => boolean} o.canMove  false when off-screen or hidden: the frame loop idles
 * @param {() => void} o.onStale     asked to re-render once the lead is nearly used up
 */
export function createTimeSlide(o) {
    const id = ++slideIds;
    let renderWall = 0;      // wall clock of that render: the slide runs from here at `rate`
    let spanMs = 0;
    let rate = 0;            // chart ms per wall ms: 0 parked, 1 live, the speed in playback
    let moving = [];         // the wrapper groups that take the transform
    let lastDx = 0;
    let rafId = 0;
    let lastAskAt = 0;
    let wasHeld = false;
    // After a hold the plot eases back to the live edge: it shows the window ending at now - lag,
    // and lag runs from what the hold left behind down to 0. Kept in time, not pixels, so a render
    // landing mid-catch-up carries on from the same place.
    let catchLagMs = 0;
    let catchStart = 0;

    function gridWidth() { return o.chart()?.w?.globals?.gridWidth || 0; }

    function lagMs() {
        if (!catchLagMs) return 0;
        const p = Math.min(1, (performance.now() - catchStart) / CATCH_MS);
        if (p >= 1) { catchLagMs = 0; return 0; }
        return catchLagMs * (1 - p) ** 3;
    }

    // The slide that puts the plot's right edge at now - lag, measured from the last render.
    function liveDx() {
        const gw = gridWidth();
        if (!(gw > 0) || !(spanMs > 0)) return 0;
        return (Date.now() - lagMs() - renderWall) * rate * gw / spanMs;
    }

    function setShift(dx) {
        if (dx === lastDx) return;
        lastDx = dx;
        const t = dx ? `translate(${-dx.toFixed(2)},0)` : '';
        for (const g of moving) {
            if (t) g.setAttribute('transform', t); else g.removeAttribute('transform');
        }
    }

    // A render request, at most once a second: the caller may decline one (a tooltip opened).
    function ask() {
        const now = Date.now();
        if (now - lastAskAt < 1000) return;
        lastAskAt = now;
        o.onStale();
    }

    function frame() {
        rafId = 0;
        if (!rate || !moving.length) return;
        if (o.canMove()) {
            if (!moving[0].isConnected) {
                // ApexCharts re-rendered on its own (a window resize) and dropped the wrappers.
                ask();
            } else if (o.isHeld()) {
                catchLagMs = 0;
                setShift(0);
                wasHeld = true;
            } else {
                if (wasHeld) {
                    // The window moved on while held. Ease back to the live edge rather than jump,
                    // and render the samples the hold kept out. A long hold snaps: its catch-up
                    // would cross a stretch of chart the new render does not draw.
                    wasHeld = false;
                    const behind = Date.now() - renderWall;
                    if (behind > 0 && behind * rate <= CATCH_MAX_MS) { catchLagMs = behind; catchStart = performance.now(); }
                    lastAskAt = 0;
                    ask();
                }
                const dx = liveDx();
                if (catchLagMs) {
                    setShift(dx); // every frame while catching up: the motion is the point
                } else {
                    // One device pixel per step: finer steps cannot be seen, and each step repaints.
                    const step = 1 / (window.devicePixelRatio || 1);
                    if (Math.abs(dx - lastDx) >= step) setShift(Math.round(dx / step) * step);
                }
                if ((Date.now() - renderWall) * rate >= leadMs(rate) * STALE_SHARE) ask();
            }
        }
        rafId = requestAnimationFrame(frame);
    }

    // Moves `nodes` into a new group under `parent`, clipped or masked by `defId`; returns the
    // inner group that takes the transform.
    function wrap(parent, before, nodes, attr, defId) {
        const outer = document.createElementNS(SVG, 'g');
        outer.setAttribute(attr, `url(#${defId})`);
        outer.setAttribute('class', 'no-slide-clip');
        const inner = document.createElementNS(SVG, 'g');
        inner.setAttribute('class', 'no-slide');
        outer.appendChild(inner);
        parent.insertBefore(outer, before);
        for (const n of nodes) inner.appendChild(n);
        return inner;
    }

    /**
     * Call after every render settles. `minMs`/`maxMs` are the window just drawn; `slideRate` is the
     * chart ms that pass per wall ms: 1 live, the speed in playback, 0 parked. Labels are drawn here
     * for the window plus the lead.
     */
    function rendered(minMs, maxMs, slideRate) {
        renderWall = Date.now();
        spanMs = maxMs - minMs;
        rate = slideRate;
        wasHeld = false;
        lastDx = 0;
        moving = [];
        const el = o.el();
        const w = o.chart()?.w;
        const gfx = el?.querySelector('.apexcharts-graphical');
        if (!gfx || !w) return;
        const gw = w.globals.gridWidth, gh = w.globals.gridHeight;
        if (!(gw > 0) || !(spanMs > 0)) return;
        // ApexCharts parks a hover marker at the plot's left edge for any series with a null, and
        // these charts paint their own hover dots (chart-tooltip.js), which hides ApexCharts' ones
        // only from the first hover on. Hidden from the first render instead.
        w.globals.dom.baseEl?.classList.add('netopt-own-hover-dots');

        // ApexCharts clips each path in the path's own coordinates, so a clip there would slide
        // along with it. The clip moves up to a wrapper that does not slide.
        let defs = gfx.querySelector(':scope > defs.no-slide-defs');
        if (!defs) {
            defs = document.createElementNS(SVG, 'defs');
            defs.setAttribute('class', 'no-slide-defs');
            gfx.insertBefore(defs, gfx.firstChild);
        }
        const clipId = `no-slide-clip-${id}`, maskId = `no-slide-mask-${id}`, gradId = `no-slide-fade-${id}`;
        defs.innerHTML =
            `<clipPath id="${clipId}"><rect x="-3" y="-3" width="${gw + 6}" height="${gh + 6}"/></clipPath>` +
            `<linearGradient id="${gradId}" x1="0" x2="${gw}" y1="0" y2="0" gradientUnits="userSpaceOnUse">` +
            `<stop offset="0" stop-color="#fff" stop-opacity="0"/>` +
            `<stop offset="${FADE_PX / gw}" stop-color="#fff" stop-opacity="1"/>` +
            `<stop offset="${1 - FADE_PX / gw}" stop-color="#fff" stop-opacity="1"/>` +
            `<stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient>` +
            `<mask id="${maskId}" maskUnits="userSpaceOnUse" x="-40" y="${gh}" width="${gw + 80}" height="40">` +
            `<rect x="-40" y="${gh}" width="${gw + 80}" height="40" fill="url(#${gradId})"/></mask>`;

        // A call with no render between (a playback speed change re-arms the slide) finds this call's
        // wrappers still in place: the old labels go, and the series wrapper is reused, or the labels
        // double and the line stops sliding.
        for (const outer of gfx.querySelectorAll(':scope > .no-slide-clip')) {
            if (outer.hasAttribute('mask')) { outer.remove(); continue; }
            outer.setAttribute('clip-path', `url(#${clipId})`);
            const inner = outer.querySelector(':scope > .no-slide');
            if (inner) { inner.removeAttribute('transform'); moving.push(inner); }
        }

        const series = [...gfx.querySelectorAll(':scope > .apexcharts-plot-series')];
        if (series.length) {
            // Paths only: the hover markers keep ApexCharts' own marker clip, which hides the
            // markers it parks off the plot for a series with nulls.
            for (const p of gfx.querySelectorAll('.apexcharts-plot-series path[clip-path]')) p.removeAttribute('clip-path');
            moving.push(wrap(gfx, series[0], series, 'clip-path', clipId));
        }

        // The time labels, from the step left of the window to the end of the lead.
        const step = tickStepMs(el.clientWidth || 800, spanMs);
        const ref = el.querySelector('.apexcharts-yaxis-label, .apexcharts-text');
        const labels = document.createElementNS(SVG, 'g');
        const span = maxMs - minMs;
        const lead = rate ? leadMs(rate) : 0;
        // From the step left of the window, and further while a catch-up shows an earlier window.
        for (let t = Math.ceil((minMs - step - catchLagMs * rate) / step) * step; t <= maxMs + lead; t += step) {
            const text = document.createElementNS(SVG, 'text');
            text.setAttribute('x', String((t - minMs) / span * gw));
            text.setAttribute('y', String(gh + 19));
            text.setAttribute('text-anchor', 'middle');
            text.setAttribute('font-size', '10px');
            text.setAttribute('font-family', ref?.getAttribute('font-family') || 'Helvetica, Arial, sans-serif');
            text.setAttribute('class', 'apexcharts-text wan-chart-tick-label');
            text.textContent = noTime.timeLabel(t);
            labels.appendChild(text);
        }
        const anchor = gfx.querySelector(':scope > .apexcharts-yaxis-annotations') || null;
        moving.push(wrap(gfx, anchor, [labels], 'mask', maskId));

        // Placed where the plot is now, so a render landing mid-catch-up carries on rather than snaps.
        lastDx = NaN;
        setShift(rate ? liveDx() : 0);
        if (rate && !rafId) rafId = requestAnimationFrame(frame);
    }

    return {
        rendered,
        /**
         * True while the plot sits slid away from where its last render drew it. A caller renders
         * fresh as the pointer arrives, so the tooltip hold has nothing to snap back from.
         */
        isShifted: () => rate > 0 && lastDx > 0,
        /** The slide in ms of chart time: add it to an instant read off the unshifted scale. */
        shiftMs: () => {
            const gw = gridWidth();
            return gw > 0 ? lastDx * spanMs / gw : 0;
        },
        dispose() {
            if (rafId) cancelAnimationFrame(rafId);
            rafId = 0;
            rate = 0;
            moving = [];
        },
    };
}
