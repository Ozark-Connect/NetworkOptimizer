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
 * Anchors a chart's series to the window on screen, so it reports exactly that window as its own
 * extents.
 *
 * This is what makes the sync happen at all. ApexCharts passes a hover to a grouped chart only if
 *   a.w.globals.minX === i.w.globals.minX && a.w.globals.maxX === i.w.globals.maxX
 * - an exact match on both ends, and symmetric, which is why a mismatch silenced BOTH directions.
 *
 * Deriving those ends from the data cannot settle it: each query is polled on its own schedule, so
 * whichever wrote last owns the later final sample, and the answer changed from one load to the
 * next with nothing else different. The window every chart on the tab asked the server for is the
 * one thing they all agree on without consulting each other.
 *
 * Points outside it are dropped - a sample a second past the window's end, from a poller whose
 * clock ran ahead, is exactly what made the extents disagree. The first series is then padded to
 * both ends with null points, which draw nothing, take no hover dot and get no tooltip row.
 */
export function toWindow(series, win) {
    if (!win?.from || !win?.to || !series?.length) return series;
    const min = win.from.getTime(), max = win.to.getTime();
    if (!Number.isFinite(min) || !Number.isFinite(max)) return series;

    return series.map((s, i) => {
        let data = (s.data || []).filter(p => p.x >= min && p.x <= max);
        if (i === 0) {
            if (!data.length || data[0].x > min) data = [{ x: min, y: null }, ...data];
            if (data[data.length - 1].x < max) data = [...data, { x: max, y: null }];
        }
        return { ...s, data };
    });
}
