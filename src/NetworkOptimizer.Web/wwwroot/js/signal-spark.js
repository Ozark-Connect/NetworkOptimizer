// Geometry for the signal sparklines (SignalSparkline.razor). Two shapes:
//  - A hero: the chart takes the exact top and height of the gauge or bar stack, so a point level
//    with the gauge's reading is that reading. On hover the gauge and its value slide right as one
//    group, and the chart runs from the left edge to where the gauge lands, so the line's newest
//    point meets the gauge at its own level. The slide distance depends on rendered text widths,
//    so it is measured here.
//  - A box ([data-spark-fill]): the chart covers that element, the rows it hides on hover.
//  - A stat tile ([data-spark-area]): the chart covers that strip; the tile's value stays.
// Everything is set as CSS variables and data attributes on the host: Blazor never renders
// either, so a re-render cannot wipe them.

const bound = new WeakMap();

const ANCHOR_OVERLAP = 1;
// Half the line's 1.75px stroke: edges, not the stroke's center, meet a fill top or a bar root.
const HALF_STROKE = 0.875;
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
    // A box's rows ([data-spark-fill], hidden on hover) or a tile's strip ([data-spark-area]).
    const fill = section.querySelector('[data-spark-fill], [data-spark-area]');
    const ok = fill ? layoutBox(section, fill) : layoutHero(section, chart);
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

function layoutHero(section, chart) {
    const anchor = section.querySelector('[data-spark-anchor]');
    const group = section.querySelector('[data-spark-value]');
    if (!anchor || !group) return false;

    // Rects for the sub-pixel box the line sits on (offset* rounds, which left the chart half a
    // pixel off the gauge). The group's left comes from offset*, layout geometry, so its own hover
    // transform never feeds back; the anchor's place inside the group is the same either way.
    const box = section.getBoundingClientRect();
    const a = anchor.getBoundingClientRect();
    const g = group.getBoundingClientRect();
    const groupLeft = offsetWithin(group, section, 'left');
    const root = anchor.dataset.sparkAnchor === 'root';
    // A bar stack is met at its first bar, not its container: a global .signal-bars rule pads it.
    const edge = root ? (anchor.firstElementChild ?? anchor) : anchor;
    const anchorInGroup = edge.getBoundingClientRect().left - g.left;

    const style = getComputedStyle(section);
    const padLeft = parseFloat(style.paddingLeft) || 0;
    const padRight = parseFloat(style.paddingRight) || 0;
    const padTop = parseFloat(style.paddingTop) || 0;
    const padBottom = parseFloat(style.paddingBottom) || 0;

    // A gauge: the chart is the gauge's own box. A bar stack (root): the bars move on hover, so
    // they are measured by layout offsets, and the chart takes the box height below a bar stack's
    // worth of headroom, so the bars can rise until their root meets any point of the line and
    // still stay inside the box.
    let top, height, lift = 0;
    const anchorTop = root ? offsetWithin(anchor, section, 'top') : a.top - box.top - section.clientTop;
    const anchorHeight = root ? anchor.offsetHeight : a.height;
    if (root) {
        top = Math.max(padTop, anchorHeight + 4);
        height = section.clientHeight - padBottom - top;
        if (height < 16) {
            top = padTop;
            height = section.clientHeight - padTop - padBottom;
        }
        const endY = parseFloat(chart.dataset.endY);
        // The root lands on the stroke's bottom edge, so the whole line sits inside the first bar.
        if (Number.isFinite(endY)) lift = top + (endY / 100) * height + HALF_STROKE - (anchorTop + anchorHeight);
    } else {
        // A fill gauge (cable modem SNR) is read at the fill's top edge: the stroke's top edge
        // meets it, so the line enters the lit fill rather than riding on top of it.
        top = anchorTop + (anchor.dataset.sparkAnchor === 'fill' ? HALF_STROKE : 0);
        height = anchorHeight;
    }
    const rowMid = anchorTop + anchorHeight / 2;
    const onRow = el => el && el.offsetWidth > 0
        && Math.abs(offsetWithin(el, section, 'top') + el.offsetHeight / 2 - rowMid) < anchorHeight / 2;

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
    // The line runs into the anchor: a pixel under the gauge's edge, or onto the first bar's root.
    const width = anchorFinal + ANCHOR_OVERLAP - padLeft;
    const roomy = width >= MIN_CHART_WIDTH;

    // Without room to slide, the chart is a backdrop across the row and the hover does nothing.
    setGeometry(section, {
        top,
        left: padLeft,
        width: roomy ? width : targetRight - padLeft,
        height,
        shift: roomy ? shift : 0,
        roomy,
    });
    section.style.setProperty('--spark-lift', `${roomy ? lift : 0}px`);
    // The tail thickens to the first bar's height where it meets the root.
    if (root) section.style.setProperty('--spark-root-h', `${edge.getBoundingClientRect().height}px`);
    return true;
}

function setGeometry(section, { top, left, width, height, shift, roomy }) {
    const style = section.style;
    style.setProperty('--spark-top', `${top}px`);
    style.setProperty('--spark-height', `${height}px`);
    style.setProperty('--spark-left', `${left}px`);
    style.setProperty('--spark-width', `${Math.max(0, width)}px`);
    style.setProperty('--spark-shift', `${shift}px`);
    // From the chart's bottom to the host's, so a caption can sit on the box rather than the chart.
    style.setProperty('--spark-floor', `${Math.max(0, section.clientHeight - top - height)}px`);
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
