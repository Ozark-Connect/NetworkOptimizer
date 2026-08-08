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
export function spanTo(series, extents) {
    if (!extents || !series?.length) return series;
    const { minX, maxX } = extents;

    return series.map((s, i) => {
        // Trimmed as well as padded. Padding alone could only reach outwards, so a chart whose own
        // query started 24s earlier and ended 3s later than the rest kept its own ends and stayed
        // out of step - measured, not guessed: [sync] latency-12 -24000/0 then 0/+3000.
        let data = (s.data || []).filter(p => p.x >= minX && p.x <= maxX);
        if (i === 0) {
            if (!data.length || data[0].x > minX) data = [{ x: minX, y: null }, ...data];
            if (data[data.length - 1].x < maxX) data = [...data, { x: maxX, y: null }];
        }
        return { ...s, data };
    });
}

// ─── Diagnostics ───
//
// The sync either fires or it does not, with nothing on screen to say why, and the deciding test
// lives inside ApexCharts: a hover reaches a grouped chart only when its minX AND maxX equal the
// hovered chart's exactly. These read that state out of the library's own registry, so what is
// compared here is what the library compares - not our idea of it.
//
// Console only, and only when called. Nothing runs unless someone asks.

function groupRows() {
    return (window.Apex?._chartInstances || [])
        .filter(i => i.group)
        .map(i => {
            const g = i.chart?.w?.globals || {};
            const iso = ms => (Number.isFinite(ms) ? new Date(ms).toISOString().slice(11, 23) : String(ms));
            return {
                group: i.group,
                id: i.id,
                minX: g.minX,
                maxX: g.maxX,
                min: iso(g.minX),
                max: iso(g.maxX),
                series: g.series?.length ?? 0,
                points: g.dataPoints ?? 0,
            };
        });
}

/** Every grouped chart with its extents, and which of them ApexCharts would refuse to sync. */
export function syncReport() {
    const rows = groupRows();
    const byGroup = {};
    for (const r of rows) (byGroup[r.group] ||= []).push(r);

    for (const [group, members] of Object.entries(byGroup)) {
        const ref = members[0];
        for (const m of members) {
            m.dMin = m.minX - ref.minX;
            m.dMax = m.maxX - ref.maxX;
            m.syncs = m.dMin === 0 && m.dMax === 0 ? 'yes' : 'NO';
        }
        const broken = members.filter(m => m.syncs === 'NO');
        console.log(`[sync] ${group}: ${members.length} charts, ${broken.length} out of step`
            + (broken.length ? ` - deltas ms: ${broken.map(b => `${b.id} min${b.dMin} max${b.dMax}`).join(', ')}` : ''));
    }
    console.table(rows, ['group', 'id', 'min', 'max', 'dMin', 'dMax', 'syncs', 'series', 'points']);
    return rows;
}

/**
 * Reports only when a group's alignment CHANGES, which is what a flaky sync needs: leave it
 * running, use the page, and the log says when it broke and by how much rather than what it
 * looked like when someone thought to check.
 */
export function watchSync(intervalMs = 1000) {
    if (watchSync._timer) clearInterval(watchSync._timer);
    let last = {};
    watchSync._timer = setInterval(() => {
        const byGroup = {};
        for (const r of groupRows()) (byGroup[r.group] ||= []).push(r);
        for (const [group, members] of Object.entries(byGroup)) {
            const ref = members[0];
            const off = members.filter(m => m.minX !== ref.minX || m.maxX !== ref.maxX);
            const state = off.map(m => `${m.id}:${m.minX - ref.minX}/${m.maxX - ref.maxX}`).join(' ') || 'aligned';
            if (state === last[group]) continue;
            last[group] = state;
            console.log(`[sync] ${new Date().toISOString().slice(11, 23)} ${group} -> ${state}`);
        }
    }, intervalMs);
    console.log(`[sync] watching every ${intervalMs}ms - __netoptSync.stop() to end`);
}

export function stopWatch() {
    clearInterval(watchSync._timer);
    watchSync._timer = null;
}

window.__netoptSync = { report: syncReport, watch: watchSync, stop: stopWatch };
