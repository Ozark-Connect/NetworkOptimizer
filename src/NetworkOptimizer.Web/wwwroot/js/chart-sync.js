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
