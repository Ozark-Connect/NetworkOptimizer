const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }

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

export function renderStatsTable(el, container, opts) {
    if (!el) return;
    const { nameHeader = 'Name', rows, columns, filter, title = 'Statistics' } = opts;

    if (!rows || rows.length === 0) { el.innerHTML = ''; return; }

    if (opts.showAllRows !== undefined) el._showAllRows = opts.showAllRows;
    const display = (el._showAllRows ?? false) ? rows : rows.filter(r => r.visible !== false);
    if (display.length === 0) { el.innerHTML = ''; return; }

    const sortCol = el._sortCol ?? -1;
    const sortDir = el._sortDir ?? 'asc';
    const sorted = [...display];
    if (sortCol >= 0 && sortCol < columns.length) {
        sorted.sort((a, b) => {
            const av = a.values[sortCol], bv = b.values[sortCol];
            if (av == null && bv == null) return 0;
            if (av == null) return 1;
            if (bv == null) return -1;
            return sortDir === 'asc' ? av - bv : bv - av;
        });
    }

    const prev = el.querySelector('.table-responsive');
    const scrollLeft = prev ? prev.scrollLeft : 0;

    const headers = columns.map((col, i) => {
        // Columns can opt out of sorting (e.g. cells holding HTML, not a sortable value).
        if (col.sortable === false) {
            return `<th${col.cls ? ` class="${col.cls}"` : ''}>${col.header}</th>`;
        }
        const active = i === sortCol;
        const arrow = active ? (sortDir === 'asc' ? ' ▲' : ' ▼') : '';
        const classes = [active ? 'stats-sort-active' : '', col.cls || ''].filter(Boolean).join(' ');
        return `<th data-sort-col="${i}"${classes ? ` class="${classes}"` : ''}>${col.header}${arrow}</th>`;
    }).join('');

    const rowsHtml = sorted.map(r => {
        const filtered = r.visible === false;
        const cls = filtered ? ' class="stats-row-filtered"' : '';
        const cells = r.values.map((v, i) => {
            const colCls = columns[i].cls ? ` class="${columns[i].cls}"` : '';
            return `<td${colCls}>${filtered ? '-' : columns[i].format(v)}</td>`;
        }).join('');
        return `<tr${cls}>
            <td data-stat-id="${r.id}"><span class="stat-filter-badge"><span class="wan-badge-dot" style="background-color:${r.color}"></span>${escapeHtml(r.label)}</span></td>
            ${cells}
        </tr>`;
    }).join('');

    el.innerHTML = `<div class="chart-card" style="margin-top:1rem">
        ${title ? `<div class="chart-header"><h3 class="chart-title">${title}</h3></div>` : ''}
        <div class="table-responsive">
        <table class="data-table" style="font-size:0.8125rem">
            <thead><tr>
                <th>${nameHeader}</th>
                ${headers}
            </tr></thead>
            <tbody>${rowsHtml}</tbody>
        </table>
        </div>
    </div>`;

    const next = el.querySelector('.table-responsive');
    if (next && scrollLeft) next.scrollLeft = scrollLeft;

    el._lastRenderOpts = opts;

    if (!el._sortDelegated) {
        el._sortDelegated = true;
        el.addEventListener('click', (e) => {
            const th = e.target.closest('th[data-sort-col]');
            if (!th) return;
            const col = parseInt(th.dataset.sortCol);
            if (el._sortCol === col) {
                if (el._sortDir === 'asc') { el._sortDir = 'desc'; }
                else { el._sortCol = -1; el._sortDir = 'asc'; }
            } else {
                el._sortCol = col;
                el._sortDir = 'asc';
            }
            if (el._lastRenderOpts) renderStatsTable(el, container, el._lastRenderOpts);
        });
    }

    if (filter && !el._filterDelegated) {
        el._filterDelegated = true;
        el.addEventListener('click', (e) => {
            if (e.target.closest('th[data-sort-col]')) return;
            const td = e.target.closest('[data-stat-id]');
            if (!td) return;
            const id = td.dataset.statId;
            const meta = filter.meta();
            const key = filter.key;
            const vis = filter.visibility();

            if (e.ctrlKey || e.metaKey) {
                vis[id] = vis[id] === false ? undefined : false;
            } else {
                const allVis = meta.every(m => vis[m[key]] !== false);
                const onlyThis = vis[id] !== false
                    && meta.filter(m => m[key] !== id).every(m => vis[m[key]] === false);
                if (onlyThis) { filter.resetVisibility(); }
                else if (allVis) { meta.forEach(m => { vis[m[key]] = m[key] === id; }); }
                else { vis[id] = vis[id] === false; }
            }
            filter.onChanged(container);
        });
    }
}
