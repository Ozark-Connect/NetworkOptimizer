// The PON charts and details table, shared by SFP Stats and ONT Stats.
//
// An ONT attached to a monitored SFP module and a standalone one report the same counters, so
// both tabs draw the same series under the same names off one definition. Chart mounting, sync
// groups and event marks stay with each tab, which owns its own layout.

import { alignedPoints } from './chart-tooltip.js?v=16';
import { detailsTableHtml, escapeHtml, fmtUptime } from './detail-table.js?v=1';

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
    const lastOf = item => [...item.pon].reverse().find(p => p.state != null) || item.pon[item.pon.length - 1];

    const nameCol = items.length > 1
        ? [{ header: escapeHtml(labelHeader), cell: item => escapeHtml(item.label), always: true }]
        : [];
    return detailsTableHtml(items, [
        ...nameCol,
        { header: 'PLOAM State', cell: item => { const l = lastOf(item); return l.state == null ? null : escapeHtml(PLOAM_LABELS[l.state] || l.state); } },
        { header: 'PLOAM Uptime', cell: item => fmtUptimeMs(lastOf(item).ploamMs) },
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
        { header: 'ONT Uptime', cell: item => fmtUptime(lastOf(item).uptime ?? item.linkUptime) },
        ...extras,
    ]);
}

function fmtUptimeMs(ms) {
    if (ms == null) return null;
    return fmtUptime(Math.floor(ms / 1000));
}

function fmtCount(v) { return v != null ? v.toLocaleString() : null; }

/// Cumulative error counter totals from the latest PON sample. Columns with no data across
/// any item are dropped; FEC columns are also hidden when FEC is off on every item.
export function ponErrorTotalsHtml(items, labelHeader) {
    const lastOf = item => item.pon[item.pon.length - 1];
    const anyFec = items.some(item => (item.pon || []).some(p => p.dsFec || p.usFec));

    const nameCol = items.length > 1
        ? [{ header: escapeHtml(labelHeader), cell: item => escapeHtml(item.label), always: true }]
        : [];
    const columns = [
        ...nameCol,
        { header: '<span data-tooltip="Bit-interleaved parity errors">BIP</span>', cell: item => fmtCount(lastOf(item).bipTotal) },
        { header: '<span data-tooltip="Corrected GTC header errors">HEC Corr</span>', cell: item => fmtCount(lastOf(item).hecCorrTotal) },
        { header: '<span data-tooltip="Uncorrectable GTC header errors">HEC Uncorr</span>', cell: item => fmtCount(lastOf(item).hecTotal) },
    ];
    if (anyFec) {
        columns.push(
            { header: '<span data-tooltip="Corrected FEC codewords">FEC Corr</span>', cell: item => fmtCount(lastOf(item).fecCorrTotal) },
            { header: '<span data-tooltip="Uncorrectable FEC codewords">FEC Uncorr</span>', cell: item => fmtCount(lastOf(item).fecTotal) },
        );
    }
    columns.push(
        { header: '<span data-tooltip="Corrected upstream bandwidth-map errors">BWmap Corr</span>', cell: item => fmtCount(lastOf(item).bwmapCorrTotal) },
        { header: '<span data-tooltip="Uncorrectable upstream bandwidth-map errors">BWmap Uncorr</span>', cell: item => fmtCount(lastOf(item).bwmapUncorrTotal) },
        { header: '<span data-tooltip="Lost upstream bandwidth allocations">Allocs Lost</span>', cell: item => fmtCount(lastOf(item).allocLostTotal) },
        { header: '<span data-tooltip="GEM frames dropped at reassembly">GEM Drops</span>', cell: item => fmtCount(lastOf(item).gemDropTotal) },
        { header: '<span data-tooltip="Host-side FCS checksum errors">FCS Errors</span>', cell: item => fmtCount(lastOf(item).lanFcsTotal) },
        { header: '<span data-tooltip="Host-side transmit drop events">TX Drops</span>', cell: item => fmtCount(lastOf(item).lanDropTotal) },
        { header: '<span data-tooltip="Host-side buffer overflows">Buf Overflows</span>', cell: item => fmtCount(lastOf(item).lanOvflTotal) },
    );

    return detailsTableHtml(items, columns);
}
