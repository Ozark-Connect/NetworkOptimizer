// Geometry for the signal sparklines (SignalSparkline.razor). Two shapes:
//  - A hero: the chart takes the exact top and height of the gauge or bar stack, so a point level
//    with the gauge's reading is that reading. On hover the gauge and its value slide right as one
//    group, and the chart runs from the left edge to where the gauge lands, so the line's newest
//    point meets the gauge at its own level. The slide distance depends on rendered text widths,
//    so it is measured here.
//  - A box ([data-spark-fill]): the chart covers that element, the rows it hides on hover.
// Everything is set as CSS variables and data attributes on the host: Blazor never renders
// either, so a re-render cannot wipe them.

const bound = new WeakMap();

const GAP_BEFORE_ANCHOR = 10;
const GAP_BEFORE_NAV = 16;
const MIN_CHART_WIDTH = 72;

/**
 * Binds the host (hero section or metric box) around a chart. Safe to call on every render: a bound section only re-measures.
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
    if (!chart) {
        delete section.dataset.sparkReady;
        return;
    }
    const fill = section.querySelector('[data-spark-fill]');
    const ok = fill ? layoutBox(section, fill) : layoutHero(section);
    if (!ok) {
        delete section.dataset.sparkReady;
        return;
    }

    // Shown a frame after the first measure, so the draw-in runs on the measured box. History
    // that arrives after the bind gets the same entrance on its first measure.
    if (!('sparkReady' in section.dataset)) {
        requestAnimationFrame(() => {
            if (section.querySelector('.signal-spark')) section.dataset.sparkReady = '';
        });
    }
}

// offset*, not a rect: the rows are translated while hovered, and the chart must not follow them.
function layoutBox(section, fill) {
    setGeometry(section, {
        top: offsetWithin(fill, section, 'top'),
        left: offsetWithin(fill, section, 'left'),
        width: fill.offsetWidth,
        height: fill.offsetHeight,
        shift: 0,
        roomy: fill.offsetWidth >= MIN_CHART_WIDTH,
    });
    return true;
}

function layoutHero(section) {
    const anchor = section.querySelector('[data-spark-anchor]');
    const group = section.querySelector('[data-spark-value]');
    if (!anchor || !group) return false;

    // Rects for the sub-pixel box the line sits on (offset* rounds, which left the chart half a
    // pixel off the gauge). The group's left comes from offset*, layout geometry, so its own hover
    // transform never feeds back; the anchor's place inside the group is the same either way.
    const box = section.getBoundingClientRect();
    const a = anchor.getBoundingClientRect();
    const g = group.getBoundingClientRect();
    const top = a.top - box.top - section.clientTop;
    const groupLeft = offsetWithin(group, section, 'left');
    const anchorInGroup = a.left - g.left;

    const style = getComputedStyle(section);
    const padLeft = parseFloat(style.paddingLeft) || 0;
    const padRight = parseFloat(style.paddingRight) || 0;
    const rowMid = top + a.height / 2;
    const onRow = el => el && el.offsetWidth > 0
        && Math.abs(offsetWithin(el, section, 'top') + el.offsetHeight / 2 - rowMid) < a.height / 2;

    // The group stops short of the paging buttons (they stay put under the cursor); with none
    // on its row, it takes the model's place at the right edge.
    const nav = section.querySelector('.modem-nav');
    const model = section.querySelector('[data-spark-model]');
    let targetRight;
    if (nav && nav.querySelector('button') && onRow(nav)) {
        targetRight = offsetWithin(nav, section, 'left') - GAP_BEFORE_NAV;
    } else if (onRow(model)) {
        targetRight = offsetWithin(model, section, 'left') + model.offsetWidth;
    } else {
        targetRight = section.clientWidth - padRight;
    }

    const shift = Math.max(0, targetRight - group.offsetWidth - groupLeft);
    const anchorFinal = groupLeft + shift + anchorInGroup;
    const width = anchorFinal - GAP_BEFORE_ANCHOR - padLeft;
    const roomy = width >= MIN_CHART_WIDTH;

    // Without room to slide, the chart is a backdrop across the row and the hover does nothing.
    setGeometry(section, {
        top,
        left: padLeft,
        width: roomy ? width : targetRight - padLeft,
        height: a.height,
        shift: roomy ? shift : 0,
        roomy,
    });
    return true;
}

function setGeometry(section, { top, left, width, height, shift, roomy }) {
    const style = section.style;
    style.setProperty('--spark-top', `${top}px`);
    style.setProperty('--spark-height', `${height}px`);
    style.setProperty('--spark-left', `${left}px`);
    style.setProperty('--spark-width', `${Math.max(0, width)}px`);
    style.setProperty('--spark-shift', `${shift}px`);
    if (roomy) section.dataset.sparkRoomy = '';
    else delete section.dataset.sparkRoomy;
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
