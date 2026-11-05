// Geometry for the signal hero sparklines (SignalSparkline.razor). The chart takes the exact
// top and height of the hero's gauge or bar stack, so a point level with the gauge's reading is
// that reading. It runs from just past the anchor to where the value lands on hover, and the
// value's slide distance is measured here because it depends on the rendered text widths.
// Everything is set as CSS variables and data attributes on the section: Blazor never renders
// either, so a re-render cannot wipe them.

const bound = new WeakMap();

const GAP_AFTER_ANCHOR = 10;
const GAP_BEFORE_VALUE = 14;
const GAP_BEFORE_NAV = 16;
const MIN_CHART_WIDTH = 72;

/**
 * Binds the hero section around a chart. Safe to call on every render: a bound section only re-measures.
 * @param {HTMLElement} chart the .signal-spark inside a .signal-spark-host
 */
export function bind(chart) {
    const section = chart?.closest('.signal-spark-host');
    if (!section) return;
    const existing = bound.get(section);
    if (existing) {
        existing.measure();
        return;
    }

    const measure = () => layout(section);
    const ro = new ResizeObserver(() => {
        if (!section.isConnected) {
            ro.disconnect();
            bound.delete(section);
            return;
        }
        measure();
    });
    ro.observe(section);
    const value = section.querySelector('[data-spark-value]');
    if (value) ro.observe(value);

    // Text changes inside the value do not always resize the observed box first.
    section.addEventListener('pointerenter', measure);

    bound.set(section, { measure });
    measure();
}

function layout(section) {
    const chart = section.querySelector('.signal-spark');
    const anchor = section.querySelector('[data-spark-anchor]');
    const value = section.querySelector('[data-spark-value]');
    if (!chart || !anchor || !value) {
        delete section.dataset.sparkReady;
        return;
    }

    // The anchor never moves, so its rect gives the sub-pixel box the line has to sit on (offset*
    // rounds, which left the chart half a pixel off the gauge). The value is measured with
    // offset*, layout geometry, so its own hover transform never feeds back into it.
    const box = section.getBoundingClientRect();
    const a = anchor.getBoundingClientRect();
    const top = a.top - box.top - section.clientTop;
    const left = a.right - box.left - section.clientLeft + GAP_AFTER_ANCHOR;
    const valueLeft = offsetWithin(value, section, 'left');

    const nav = section.querySelector('.modem-nav');
    const navUsed = nav && nav.querySelector('button');
    const model = section.querySelector('[data-spark-model]');
    const padRight = parseFloat(getComputedStyle(section).paddingRight) || 0;

    // The value stops short of the paging buttons (they stay put under the cursor); with none,
    // it takes the model's place at the right edge.
    let targetRight;
    if (navUsed) {
        targetRight = offsetWithin(nav, section, 'left') - GAP_BEFORE_NAV;
    } else if (model && model.offsetWidth > 0) {
        targetRight = offsetWithin(model, section, 'left') + model.offsetWidth;
    } else {
        targetRight = section.clientWidth - padRight;
    }

    // Wrapped onto a second line (narrow cards): slide only when the value shares the anchor's row.
    const sameRow = Math.abs(offsetWithin(value, section, 'top') + value.offsetHeight / 2
        - (top + a.height / 2)) < a.height;

    const finalLeft = targetRight - value.offsetWidth;
    const shift = Math.max(0, finalLeft - valueLeft);
    const width = finalLeft - GAP_BEFORE_VALUE - left;
    const roomy = sameRow && width >= MIN_CHART_WIDTH;

    const style = section.style;
    style.setProperty('--spark-top', `${top}px`);
    style.setProperty('--spark-height', `${a.height}px`);
    style.setProperty('--spark-left', `${left}px`);
    // Without room to slide, the chart is a backdrop across the row and the hover does nothing.
    style.setProperty('--spark-width', `${Math.max(0, roomy ? width : targetRight - left)}px`);
    style.setProperty('--spark-shift', `${roomy ? shift : 0}px`);
    if (roomy) section.dataset.sparkRoomy = '';
    else delete section.dataset.sparkRoomy;

    // Shown a frame after the first measure, so the draw-in runs on the measured box. History
    // that arrives after the bind gets the same entrance on its first measure.
    if (!('sparkReady' in section.dataset)) {
        requestAnimationFrame(() => {
            if (section.querySelector('.signal-spark')) section.dataset.sparkReady = '';
        });
    }
}

function offsetWithin(el, ancestor, side) {
    let v = 0;
    for (let n = el; n && n !== ancestor; n = n.offsetParent) {
        v += side === 'top' ? n.offsetTop : n.offsetLeft;
        if (n.offsetParent === ancestor) break;
        if (!n.offsetParent) break;
    }
    return v;
}
