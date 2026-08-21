// The PON charts and details table, shared by SFP Stats and ONT Stats.
//
// An ONT attached to a monitored SFP module and a standalone one report the same counters, so
// both tabs draw the same series under the same names off one definition. Chart mounting, sync
// groups and event marks stay with each tab, which owns its own layout.

import { alignedPoints } from './chart-tooltip.js?v=15';

const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }

// Same encoding PonLinkStateExtensions.ToInfluxValue uses for pon_link_status.
export const PLOAM_LABELS = {
    initial: 'Initializing (O1)', standby: 'Standby (O2)', serial_number: 'Authenticating (O3)',
    ranging: 'Ranging (O4)', operation: 'Connected (O5)', popup: 'Signal Lost (O6)',
    emergency_stop: 'Disabled (O7)',
};

// Error-chart colors are pinned per metric, not left to series position. Two of these series come
// and go - FEC only while the OLT profile has it on, GEM drops only where it is a separate counter -
// and positional colors would re-color everything after the one that appeared.
const ERR_METRICS = ['bip', 'hec', 'hecCorr', 'fec', 'fecCorr', 'bwmapUncorr', 'bwmapCorr', 'allocLost', 'gemDrop'];

function ponPoints(item, key) {
    return alignedPoints(item.pon || [], p => p[key]);
}

function sameSeries(a, b) {
    if (a.length !== b.length) return false;
    return a.every((p, i) => p.x === b[i].x && p.y === b[i].y);
}

/// Build the three charts' series for one ONT. `slot` is its stable index in the full list, so
/// hiding one never re-colors the ones that stay; `palette` is the caller's color list.
export function ponSeriesFor(item, prefix, slot, palette) {
    const err = (key, name) => ({
        name: `${prefix}${name}`,
        data: ponPoints(item, key),
        color: palette[(slot * ERR_METRICS.length + ERR_METRICS.indexOf(key)) % palette.length],
    });
    const hec = err('hec', 'HEC');
    const gemDrop = err('gemDrop', 'GEM drops');
    const fec = err('fec', 'FEC'), fecCorr = err('fecCorr', 'FEC corrected');

    const errSeries = [err('bip', 'BIP'), hec, err('hecCorr', 'HEC corrected')];
    // The FEC counters can only move while the OLT profile has FEC enabled, so on a link
    // where it is off they are two permanent zero lines. Test the whole window, not the
    // latest sample: FEC switched off mid-window leaves real deltas behind it. The flags
    // are optional in the contract, so counted errors are evidence in their own right.
    if ((item.pon || []).some(p => p.dsFec || p.usFec)
        || fec.data.some(p => p.y) || fecCorr.data.some(p => p.y)) {
        errSeries.push(fec, fecCorr);
    }
    errSeries.push(
        err('bwmapUncorr', 'BWmap'),
        err('bwmapCorr', 'BWmap corrected'),
        err('allocLost', 'Allocs lost'),
    );
    // Some ONTs report GEM drops off the same counter as uncorrectable HEC (every Lantiq
    // one does), which draws a second line exactly on top of HEC. Keep the series only
    // where the hardware really does count them separately.
    if (!sameSeries(gemDrop.data, hec.data)) {
        errSeries.push(gemDrop);
    }

    return {
        errSeries,
        gemSeries: [
            { name: `${prefix}RX frames`, data: ponPoints(item, 'gemRx') },
            { name: `${prefix}TX frames`, data: ponPoints(item, 'gemTx') },
        ],
        hostSeries: [
            { name: `${prefix}FCS errors`, data: ponPoints(item, 'lanFcs') },
            { name: `${prefix}TX drops`, data: ponPoints(item, 'lanDrop') },
            { name: `${prefix}Buffer overflows`, data: ponPoints(item, 'lanOvfl') },
        ],
    };
}

/// Draw one PON card, hiding it when nothing fills it. Which counters an ONT serves is a
/// property of the hardware and the contract's optional sections, so a card with no series
/// behind it is empty forever rather than empty for now. `prepare` is the caller's own
/// series transform, since the two tabs pad their x ranges differently.
export function updatePonCard(container, cardSelector, chart, series, prepare = x => x) {
    const card = container.querySelector(cardSelector);
    if (!card) return;
    const filled = series.filter(x => x.data.length);
    card.style.display = filled.length ? '' : 'none';
    if (filled.length && chart) chart.updateSeries(prepare(filled), false);
}

/// The details table. `labelHeader` names the first column, `extras` appends tab-specific ones.
/// A column no ONT fills is dropped rather than printed as a row of dashes.
export function ponDetailsHtml(items, labelHeader, extras = []) {
    const fmtUp = s => s == null ? null
        : `${Math.floor(s / 86400)}d ${Math.floor(s % 86400 / 3600)}h ${Math.floor(s % 3600 / 60)}m`;
    const lastOf = item => [...item.pon].reverse().find(p => p.state != null) || item.pon[item.pon.length - 1];

    const columns = [
        { header: escapeHtml(labelHeader), cell: item => escapeHtml(item.label), always: true },
        { header: 'PLOAM State', cell: item => { const l = lastOf(item); return l.state == null ? null : escapeHtml(PLOAM_LABELS[l.state] || l.state); } },
        { header: 'ONU ID', cell: item => lastOf(item).onuId },
        {
            header: 'FEC DS / US',
            cell: item => { const l = lastOf(item); return l.dsFec == null && l.usFec == null ? null : `${l.dsFec ? 'on' : 'off'} / ${l.usFec ? 'on' : 'off'}`; },
        },
        { header: 'Response Time', cell: item => lastOf(item).respTime },
        {
            header: '<span data-tooltip="Raw device enums for the module-to-gateway link. Read them for change: a value that moves means the host link renegotiated, which the PON-side fields never show.">Host Link</span>',
            cell: item => { const l = lastOf(item); return l.lanLink == null && l.lanMode == null ? null : `${l.lanLink ?? '-'} / ${l.lanMode ?? '-'}`; },
        },
        // Module uptime where the ONT reports it, link uptime where it reports that instead.
        // No provider serves both, so the column never has to choose between them.
        { header: 'ONT Uptime', cell: item => fmtUp(lastOf(item).uptime ?? item.linkUptime) },
        ...extras,
    ];

    const values = items.map(item => columns.map(c => c.cell(item)));
    const shown = columns.filter((c, i) => c.always || values.some(row => row[i] != null));
    const head = shown.map(c => `<th>${c.header}</th>`).join('');
    const rows = values.map(row =>
        `<tr>${shown.map(c => `<td>${row[columns.indexOf(c)] ?? '-'}</td>`).join('')}</tr>`).join('');
    return `<div class="table-responsive"><table class="data-table">
        <thead><tr>${head}</tr></thead>
        <tbody>${rows}</tbody></table></div>`;
}
