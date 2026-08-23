// The current-state table drawn under a tab's time-series charts, shared by every device type.
//
// One renderer, a column list per device: a column no device in the series fills is dropped
// rather than printed as a row of dashes, so the same tab shows seven columns for hardware
// that reports everything and three for hardware that reports little.

const _esc = document.createElement('span');
export function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }

/// Format seconds as the uptime string these tables use.
export function fmtUptime(s) {
    if (s == null) return null;
    return `${Math.floor(s / 86400)}d ${Math.floor(s % 86400 / 3600)}h ${Math.floor(s % 3600 / 60)}m`;
}

/// The last point in a series that filled `key`, for a table showing current state rather
/// than history. Devices stop reporting individual fields while still reporting others.
export function lastValue(points, key) {
    if (!points?.length) return null;
    for (let i = points.length - 1; i >= 0; i--) {
        if (points[i]?.[key] != null) return points[i][key];
    }
    return null;
}

/// Render the table. Each column is `{ header, cell(item), always }`; `always` pins a column
/// that must show even when empty, which is how the name column survives.
export function detailsTableHtml(items, columns) {
    if (!items?.length) return '';

    const values = items.map(item => columns.map(c => c.cell(item)));
    const shown = columns.filter((c, i) => c.always || values.some(row => row[i] != null));
    if (!shown.length) return '';

    const head = shown.map(c => `<th>${c.header}</th>`).join('');
    const rows = values.map(row =>
        `<tr>${shown.map(c => `<td>${row[columns.indexOf(c)] ?? '-'}</td>`).join('')}</tr>`).join('');

    return `<div class="table-responsive detail-table"><table class="data-table">
        <thead><tr>${head}</tr></thead>
        <tbody>${rows}</tbody></table></div>`;
}
