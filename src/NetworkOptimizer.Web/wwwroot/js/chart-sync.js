// Hover sync for a tab's stacked charts.
//
// Charts sharing an ApexCharts group draw their tooltip and crosshair together, so pointing at one
// reads the same instant on all of them - which is the question these tabs are for: what else was
// happening when this moved.
//
// The sync addresses a point by data INDEX, so every chart in a group must agree on what index i
// means. Series built from one source array agree by construction; series from a second query do
// not, and belong in their own group (or none) unless they are re-keyed onto the first one's rows.

let seq = 0;

/**
 * The chart identity that puts a chart in `group`, or nothing when `group` is falsy - for a chart
 * whose x-values are its own and would sync to the wrong moment.
 *
 * Ids only have to be unique, never meaningful: ApexCharts keeps one global registry, and a
 * counter keeps a remount from colliding with the instance it replaces.
 */
export function syncIdentity(group) {
    return group ? { id: `${group}-${++seq}`, group } : {};
}

/**
 * The first and last instant across every array given, as {minX, maxX} - or null when they are all
 * empty. Arrays are time-ordered, so only their ends are read.
 */
export function extentsOf(pointArrays, timeKey = 'time') {
    let minX = null, maxX = null;
    for (const pts of pointArrays) {
        if (!pts?.length) continue;
        const first = new Date(pts[0][timeKey]).getTime();
        const last = new Date(pts[pts.length - 1][timeKey]).getTime();
        if (!Number.isFinite(first) || !Number.isFinite(last)) continue;
        minX = minX == null ? first : Math.min(minX, first);
        maxX = maxX == null ? last : Math.max(maxX, last);
    }
    return minX == null ? null : { minX, maxX };
}

/**
 * Stretches a series to the group's extents with null points, so its chart reports the same minX
 * and maxX as the charts it is grouped with.
 *
 * This is what makes the sync happen at all. ApexCharts passes a hover to a grouped chart only if
 *   a.w.globals.minX === i.w.globals.minX && a.w.globals.maxX === i.w.globals.maxX
 * - an exact match on both ends. Charts drawn from one query agree by construction; one fed by its
 * own query lands on the same first and last timestamp only by luck, so the sync came and went as
 * the data moved, and did so in BOTH directions because the test is symmetric.
 *
 * A null point draws nothing, takes no hover dot and gets no tooltip row, so the padding is
 * invisible. Only one series per chart needs it - the extents are taken across all of them.
 */
export function spanTo(points, extents) {
    if (!extents || !points?.length) return points;
    const out = points.slice();
    if (out[0].x > extents.minX) out.unshift({ x: extents.minX, y: null });
    if (out[out.length - 1].x < extents.maxX) out.push({ x: extents.maxX, y: null });
    return out;
}
