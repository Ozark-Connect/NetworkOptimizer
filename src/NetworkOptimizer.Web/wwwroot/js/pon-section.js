// The PON charts and details table, shared by SFP Stats and ONT Stats.
//
// An ONT attached to a monitored SFP module and a standalone one report the same counters, so
// both tabs draw the same series under the same names off one definition. Chart mounting, sync
// groups and event marks stay with each tab, which owns its own layout.

import { alignedPoints } from './chart-tooltip.js?v=21';
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
        // The idle fill the OLT sends is the same for every ONT on the PON, so the first series is
        // the load of the whole PON's downstream, not of this ONT. The second is this ONT's own
        // GEM traffic on the same axis, which is what makes the difference readable. Colors are
        // pinned so the summary line under the title can name them: the chart has no legend.
        utilSeries: [
            { name: `${prefix}Whole PON (all ONTs)`, data: ponPoints(item, 'dsUsedMbps'), color: ponLoadColor(slot, 0, palette) },
            { name: `${prefix}This ONT`, data: ponPoints(item, 'dsOwnMbps'), color: ponLoadColor(slot, 1, palette) },
        ],
        // What the far end announces it launches, less what this ONT receives. With a reach
        // extender in the path the announced power is the extender's, and the name says so.
        lossSeries: [
            { name: `${prefix}${reachExtended(item) ? 'Path loss from reach extender' : 'Path loss'}`, data: ponPoints(item, 'pathLoss') },
        ],
        hostSeries: [
            { name: `${prefix}FCS errors`, data: ponPoints(item, 'lanFcs') },
            { name: `${prefix}TX drops`, data: ponPoints(item, 'lanDrop') },
            { name: `${prefix}Buffer overflows`, data: ponPoints(item, 'lanOvfl') },
        ],
    };
}

function ponLoadColor(slot, which, palette) {
    return palette[(slot * 2 + which) % palette.length];
}

function lastWithKey(item, key) {
    return [...(item.pon || [])].reverse().find(p => p[key] != null)?.[key] ?? null;
}

function reachExtended(item) {
    return lastWithKey(item, 'reachExt') === 1;
}

// A GPON downstream frame is 38880 bytes every 125 us; with FEC on, 16 parity bytes leave each of
// its 153 codewords. The same constants PonChartSeries.DownstreamUtilization divides by.
const GPON_DS_MBPS = 38880 * 8 * 8000 / 1e6;
const GPON_DS_FEC_MBPS = (38880 - 153 * 16) * 8 * 8000 / 1e6;

/// The downstream payload rate the load is a share of, by the FEC state the ONTs last reported.
export function ponPayloadMbps(items) {
    return (items || []).some(item => lastWithKey(item, 'dsFec') === 1) ? GPON_DS_FEC_MBPS : GPON_DS_MBPS;
}

function fmtMbps(v) {
    return v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2);
}

function fmtShare(pct) {
    return pct >= 10 ? pct.toFixed(1) : pct >= 0.1 ? pct.toFixed(2) : pct.toFixed(3);
}

/// One value of the load chart as rate and share of the PON's downstream: "53.9 Mbit/s (2.31 %)".
export function fmtPonLoad(v, payloadMbps) {
    if (v == null) return '';
    return `${fmtMbps(v)} Mbit/s (${fmtShare(v / payloadMbps * 100)} %)`;
}

// The time one point stands for, from the spacing of the points themselves: the polling interval
// on a short window, the server's aggregate window on a long one.
function pointSpanText(pon) {
    const times = pon.map(p => Date.parse(p.time)).filter(t => !isNaN(t));
    if (times.length < 2) return null;
    const deltas = [];
    for (let i = 1; i < times.length; i++) deltas.push(times[i] - times[i - 1]);
    deltas.sort((a, b) => a - b);
    const minutes = deltas[deltas.length >> 1] / 60000;
    if (minutes < 1.5) return `${Math.round(minutes * 60)}-second`;
    if (minutes < 90) return `${Math.round(minutes)}-minute`;
    return `${(minutes / 60).toFixed(1)}-hour`;
}

/// The line under the load card's title: which line is which, the latest and the highest value of
/// the window, and how long a stretch each point averages. Empty when no ONT reports a load.
export function ponLoadSummaryHtml(items, palette, slotOf) {
    const withLoad = (items || []).filter(item => (item.pon || []).some(p => p.dsUsedMbps != null));
    if (!withLoad.length) return '';
    const payload = ponPayloadMbps(withLoad);
    const dot = color => `<span style="display:inline-block;width:0.6em;height:0.6em;border-radius:50%;background:${color};margin-right:0.35em"></span>`;
    const multi = withLoad.length > 1;
    const lines = withLoad.map(item => {
        const slot = slotOf(item);
        const used = item.pon.map(p => p.dsUsedMbps).filter(v => v != null);
        const parts = [`${dot(ponLoadColor(slot, 0, palette))}Whole PON, all ONTs: ${fmtPonLoad(used[used.length - 1], payload)} latest, ${fmtPonLoad(Math.max(...used), payload)} highest in this window`];
        const own = lastWithKey(item, 'dsOwnMbps');
        if (own != null) parts.push(`${dot(ponLoadColor(slot, 1, palette))}This ONT: ${fmtPonLoad(own, payload)} latest`);
        return `<div>${multi ? `${escapeHtml(item.label)}: ` : ''}${parts.join(' &nbsp; ')}</div>`;
    });
    const span = pointSpanText(withLoad[0].pon);
    const note = span
        ? `Each point is a ${span} mean, so shorter peaks do not show.`
        : 'Each point is a mean over one polling interval, so shorter peaks do not show.';
    return `${lines.join('')}<div>${note}</div>`;
}

const fmtDb = v => `${v.toFixed(1)} dB`;
const fmtSignedDb = v => `${v > 0 ? '+' : ''}${v.toFixed(1)} dB`;
const fmtDbmSigned = v => `${v > 0 ? '+' : ''}${v.toFixed(1)} dBm`;
const CHANGE_LABELS = { odn_class: 'ODN class', reach_extender: 'reach extender', launch_power: 'announced launch power' };

/// The lines under the Path Loss title, from the server's summary of the window (item.ponPath):
/// the latest loss against the budget of the class the OLT names, what moved against the window's
/// usual level and on which side of the fibre, and each change in the OLT's own announcement.
/// Empty when no ONT's OLT announces anything.
export function ponPathSummaryHtml(items) {
    const withPath = (items || []).filter(item => item.ponPath);
    if (!withPath.length) return '';
    const multi = withPath.length > 1;
    const warn = text => `<div style="color: var(--warning-color);">${text}</div>`;
    return withPath.map(item => {
        const s = item.ponPath;
        const from = s.reachExtender ? 'the reach extender' : 'the OLT';
        const out = [];
        const head = [];
        if (s.lossDb != null) {
            head.push(`${fmtDb(s.lossDb)} from ${from}`
                + (s.launchDbm != null && s.rxDbm != null ? ` (${fmtDbmSigned(s.launchDbm)} announced, ${fmtDbmSigned(s.rxDbm)} received)` : ''));
        } else {
            head.push('The OLT announces no launch power, so there is no loss to work out');
        }
        if (s.odnClass != null) {
            head.push(`ODN class ${escapeHtml(s.odnClass)} allows ${s.classMinDb} to ${s.classMaxDb} dB`
                + (s.marginDb != null && s.marginDb >= 0 ? `, ${fmtDb(s.marginDb)} in reserve` : ''));
        }
        out.push(`<div>${multi ? `${escapeHtml(item.label)}: ` : ''}${head.join('. ')}.</div>`);

        const hints = s.hints || [];
        if (hints.includes('over_limit')) out.push(warn(`${fmtDb(-s.marginDb)} over the limit of class ${escapeHtml(s.odnClass)}: the link runs outside its budget.`));
        if (hints.includes('near_limit')) out.push(warn(`Only ${fmtDb(s.marginDb)} left to the limit of class ${escapeHtml(s.odnClass)}.`));
        if (hints.includes('below_min')) out.push(warn(`${fmtDb(s.belowMinDb)} under the minimum of class ${escapeHtml(s.odnClass)}: less loss than the class expects.`));
        if (hints.includes('loss_rise')) out.push(warn(`Path loss is ${fmtSignedDb(s.lossChangeDb)} against this window's usual ${fmtDb(s.baselineLossDb)}, RX ${fmtSignedDb(s.rxChangeDb)}`
            + ` with the announced launch power ${Math.abs(s.launchChangeDb) < 0.05 ? 'unchanged' : fmtSignedDb(s.launchChangeDb)}: the fibre path (connector, fibre, splice, splitter).`));
        if (hints.includes('launch_drop')) out.push(warn(`The announced launch power is ${fmtSignedDb(s.launchChangeDb)} against this window's usual level, RX ${fmtSignedDb(s.rxChangeDb)}: the OLT side.`));
        for (const c of s.changes || []) {
            const when = noTime.string(Date.parse(c.time), { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
            out.push(warn(`${escapeHtml(when)}: ${CHANGE_LABELS[c.what] || escapeHtml(c.what)} changed from ${escapeHtml(c.from)} to ${escapeHtml(c.to)}, which points at work on the provider's side.`));
        }
        return out.join('');
    }).join('')
        + '<div>Launch power is what the OLT announces, possibly a configured figure rather than a measurement; the ONT\'s RX reading can be off by up to about 3 dB, so read the level loosely and the changes closely.</div>';
}

/// The launch power the OLT announces, as a line for the RX / TX power chart: the far end of the
/// same fibre in the same unit. Empty where the OLT announces none.
export function ponLaunchSeries(item, label, color) {
    const data = ponPoints(item, 'oltLaunch');
    if (!data.length) return null;
    return { name: `${label}${reachExtended(item) ? ' reach extender' : ' OLT'} launch (announced)`, color, data };
}

const ODN_CLASSES = ['A (5-20 dB)', 'B (10-25 dB)', 'B+ (13-28 dB)', 'C (15-30 dB)', 'C+ (17-32 dB)'];

function pathLossCell(item) {
    const loss = lastWithKey(item, 'pathLoss');
    const cls = ODN_CLASSES[lastWithKey(item, 'odnClass')];
    if (loss == null && cls == null) return null;
    const parts = [];
    if (loss != null) parts.push(`${loss.toFixed(1)} dB${reachExtended(item) ? ' from reach extender' : ''}`);
    if (cls != null) parts.push(`class ${cls}`);
    return parts.join(', ');
}

function launchPowerCell(item) {
    const launch = lastWithKey(item, 'oltLaunch');
    if (launch == null) return null;
    return `${launch > 0 ? '+' : ''}${launch.toFixed(1)} dBm${reachExtended(item) ? ' (reach extender)' : ''}`;
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
    // Identity fields can ride on a different point than the state, so each takes its own latest.
    const lastWith = (item, key) => [...item.pon].reverse().find(p => p[key] != null)?.[key] ?? null;

    const nameCol = items.length > 1
        ? [{ header: escapeHtml(labelHeader), cell: item => escapeHtml(item.label), always: true }]
        : [];
    return detailsTableHtml(items, [
        ...nameCol,
        { header: 'PLOAM State', cell: item => { const l = lastOf(item); return l.state == null ? null : escapeHtml(PLOAM_LABELS[l.state] || l.state); } },
        { header: 'PLOAM Uptime', cell: item => fmtUptimeMs(lastOf(item).ploamMs) },
        { header: 'ONU ID', cell: item => lastOf(item).onuId },
        { header: 'Registered', cell: item => { const r = lastWith(item, 'registered'); return r == null ? null : escapeHtml(r); } },
        { header: 'OLT', cell: item => { const o = lastWith(item, 'olt') ?? item.olt; return o == null ? null : escapeHtml(o); } },
        { header: 'OLT Version', cell: item => { const v = lastWith(item, 'oltVersion'); return v == null ? null : escapeHtml(v); } },
        {
            header: 'FEC DS / US',
            cell: item => { const l = lastOf(item); return l.dsFec == null && l.usFec == null ? null : `${l.dsFec ? 'on' : 'off'} / ${l.usFec ? 'on' : 'off'}`; },
        },
        { header: 'Response Time', cell: item => lastOf(item).respTime },
        {
            header: '<span data-tooltip="Optical loss between the far end and this ONT: the launch power the OLT announces in its PON-ID message, less the power this ONT receives. With a reach extender in the path the announced power is the extender\'s, so the loss is that of the stretch from the extender. The class is the loss budget the OLT says the network is built for.">Path Loss</span>',
            cell: pathLossCell,
        },
        {
            header: '<span data-tooltip="Mean launch power as the OLT announces it in its PON-ID message, not a measurement of this ONT. Not every OLT sends one.">OLT Launch Power (announced)</span>',
            cell: launchPowerCell,
        },
        {
            header: '<span data-tooltip="Raw device enums for the module-to-gateway link. Read them for change: a value that moves means the host link renegotiated, which the PON-side fields never show.">Host Link</span>',
            cell: item => { const l = lastOf(item); return l.lanLink == null && l.lanMode == null ? null : `${l.lanLink ?? '-'} / ${l.lanMode ?? '-'}`; },
        },
        { header: 'ONT Uptime', cell: item => fmtUptime(lastOf(item).uptime ?? item.linkUptime) },
        { header: 'Firmware', cell: item => { const f = lastWith(item, 'firmware'); return f == null ? null : escapeHtml(f); } },
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
