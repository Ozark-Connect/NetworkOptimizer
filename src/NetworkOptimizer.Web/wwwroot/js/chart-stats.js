export function percentile(sorted, p) {
    if (sorted.length === 0) return null;
    const idx = (p / 100) * (sorted.length - 1);
    const lo = Math.floor(idx);
    const hi = Math.ceil(idx);
    if (lo === hi) return sorted[lo];
    return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
}

export function computeStats(values) {
    if (!values || values.length === 0) return null;
    const sorted = [...values].sort((a, b) => a - b);
    return {
        mean: values.reduce((s, v) => s + v, 0) / values.length,
        min: sorted[0],
        max: sorted[sorted.length - 1],
        p95: percentile(sorted, 95),
        p99: percentile(sorted, 99),
    };
}

export function initStatsFilter(el, container, opts) {
    if (el._delegated) return;
    el._delegated = true;
    el.addEventListener('click', (e) => {
        const td = e.target.closest('[data-stat-id]');
        if (!td) return;
        const id = td.dataset.statId;
        const meta = opts.meta();
        const key = opts.key;
        const vis = opts.visibility();

        if (e.ctrlKey || e.metaKey) {
            vis[id] = vis[id] === false ? undefined : false;
        } else {
            const allVis = meta.every(m => vis[m[key]] !== false);
            const onlyThis = vis[id] !== false
                && meta.filter(m => m[key] !== id).every(m => vis[m[key]] === false);
            if (onlyThis) { opts.resetVisibility(); }
            else if (allVis) { meta.forEach(m => { vis[m[key]] = m[key] === id; }); }
            else { vis[id] = vis[id] === false; }
        }
        opts.onChanged(container);
    });
}
