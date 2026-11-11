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
        // The ONT's own clock on its activation states and alarms: seconds of each interval the
        // link was not in O5, and the seconds a loss of signal or of frame stood.
        availSeries: [
            { name: `${prefix}Link down`, data: ponPoints(item, 'downS') },
            { name: `${prefix}Loss of signal`, data: ponPoints(item, 'losS') },
            { name: `${prefix}Loss of frame`, data: ponPoints(item, 'lofS') },
        ],
        // What the OLT grants this ONT upstream, and what the ONT sends in those windows.
        grantSeries: [
            { name: `${prefix}Grants`, data: ponPoints(item, 'grantsPerS') },
            { name: `${prefix}Bursts sent`, data: ponPoints(item, 'burstsPerS') },
            { name: `${prefix}DBRu reports`, data: ponPoints(item, 'dbruPerS') },
        ],
        // The transmit laser's bias current, the ONT's own reading: flat for years on a healthy laser.
        biasSeries: [
            { name: `${prefix}Bias current`, data: ponPoints(item, 'bias') },
        ],
        // The ONT's own processor: a stick that is busy all the time explains odd readings elsewhere.
        cpuSeries: [
            { name: `${prefix}CPU busy`, data: ponPoints(item, 'cpuPct') },
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
        if (own != null) {
            const share = lastWithKey(item, 'dsOwnShare');
            parts.push(`${dot(ponLoadColor(slot, 1, palette))}This ONT: ${fmtPonLoad(own, payload)} latest`
                + (share != null ? `, ${fmtShare(share)} % of what the PON carried` : ''));
        }
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
        if (hints.includes('unsteady')) out.push(warn(`Path loss jumped ${s.jumps} times in this window${s.spreadDb != null ? `, ranging over ${fmtDb(s.spreadDb)}` : ''}: a steady fibre does not move like that, so this points at a loose or dirty connector.`));
        if (hints.includes('launch_drop')) out.push(warn(`The announced launch power is ${fmtSignedDb(s.launchChangeDb)} against this window's usual level, RX ${fmtSignedDb(s.rxChangeDb)}: the OLT side.`));
        for (const c of s.changes || []) {
            const when = noTime.string(Date.parse(c.time), { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
            out.push(warn(`${escapeHtml(when)}: ${CHANGE_LABELS[c.what] || escapeHtml(c.what)} changed from ${escapeHtml(c.from)} to ${escapeHtml(c.to)}, which points at work on the provider's side.`));
        }
        const ranging = item.ponMore?.ranging;
        if (ranging?.ponIdStale) out.push(warn(`The OLT last announced its launch power ${fmtDuration(ranging.ponIdAgeSeconds)} ago, so the loss above rests on an old figure.`));
        return out.join('');
    }).join('')
        + '<div>Launch power is what the OLT announces, possibly a configured figure rather than a measurement; the ONT\'s RX reading can be off by up to about 3 dB, so read the level loosely and the changes closely.</div>';
}

/// What the ranging says: the equalization delay, and each step in it with the fibre length it
/// stands for and what the loss did across it. Listed with the provider's activity, since a
/// step is the OLT measuring a different distance, and so that it shows without a path loss.
function rangingLines(r) {
    if (!r) return [];
    const warn = text => `<div style="color: var(--warning-color);">${text}</div>`;
    const out = [];
    if (r.eqdBits != null) {
        out.push(`<div>Equalization delay ${fmtInt(r.eqdBits)} bit times (${r.eqdMicros.toFixed(1)} µs)`
            + `${(r.steps || []).length ? '' : ', unchanged in this window'}. It is set against a reference only the OLT knows, so it is not a fibre length; a step is a change in length.</div>`);
    }
    for (const s of r.steps || []) {
        const metres = Math.abs(s.deltaMetres);
        const length = `about ${metres >= 10 ? metres.toFixed(0) : metres.toFixed(1)} m ${s.deltaMetres > 0 ? 'longer' : 'shorter'}`;
        const loss = s.lossChangeDb == null ? ''
            : Math.abs(s.lossChangeDb) < 0.3 ? ', path loss unchanged'
            : `, path loss ${fmtSignedDb(s.lossChangeDb)}`;
        out.push(warn(`${escapeHtml(fmtWhen(s.time))}: the equalization delay moved by ${s.deltaBits > 0 ? '+' : ''}${fmtInt(s.deltaBits)} bit times, a fibre path ${length}${loss}. The OLT measured a different distance: a rerouted or respliced path, or another OLT port.`));
    }
    return out;
}

/// The launch power the OLT announces, as a line for the RX / TX power chart: the far end of the
/// same fibre in the same unit. Empty where the OLT announces none.
export function ponLaunchSeries(item, label, color) {
    const data = ponPoints(item, 'oltLaunch');
    if (!data.length) return null;
    return { name: `${label}${reachExtended(item) ? ' reach extender' : ' OLT'} launch (announced)`, color, data };
}

// Counts in the new read-outs sit beside values with a decimal point, so they group the same way
// whatever the browser's locale: "15,999", never "15.999".
const fmtInt = v => v.toLocaleString('en-US');

function fmtDuration(seconds) {
    if (seconds < 60) return `${seconds < 10 ? seconds.toFixed(1) : Math.round(seconds)} s`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)} min ${Math.round(seconds % 60)} s`;
    return `${Math.floor(seconds / 3600)} h ${Math.round(seconds % 3600 / 60)} min`;
}

const fmtWhen = t => noTime.string(Date.parse(t), { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });

const OUTAGE_CAUSES = {
    light: 'the light went away (loss of signal or frame)',
    olt: 'the OLT sent the ONT away (deactivation message)',
    'light+olt': 'loss of signal and a deactivation message from the OLT',
    unknown: 'no alarm and no deactivation message in that interval',
};

/// The lines under the availability card's title, from the server's summary of the window
/// (item.ponAvail): the share of time in O5 by the ONT's own clock, each stretch with a drop and
/// what its counters point at, and the ONT's reboots, which are not downtime of the line.
export function ponAvailSummaryHtml(items) {
    const withAvail = (items || []).filter(item => item.ponAvail);
    if (!withAvail.length) return '';
    const multi = withAvail.length > 1;
    const warn = text => `<div style="color: var(--warning-color);">${text}</div>`;
    return withAvail.map(item => {
        const s = item.ponAvail;
        const out = [];
        const head = s.availabilityPercent == null
            ? 'No full interval in this window yet'
            : `In O5 for ${s.availabilityPercent >= 99.995 && s.downSeconds > 0 ? s.availabilityPercent.toFixed(4) : s.availabilityPercent.toFixed(2)} % of this window`
              + (s.drops || s.downSeconds
                  ? `: ${s.drops} drop${s.drops === 1 ? '' : 's'}, ${fmtDuration(s.downSeconds)} down`
                  : ', no drop');
        const alarms = [];
        if (s.losEvents) alarms.push(`${s.losEvents} loss of signal (${fmtDuration(s.losSeconds)})`);
        if (s.lofEvents) alarms.push(`${s.lofEvents} loss of frame`);
        if (s.oltDeactivations) alarms.push(`${s.oltDeactivations} deactivation by the OLT`);
        out.push(`<div>${multi ? `${escapeHtml(item.label)}: ` : ''}${head}${alarms.length ? `. Alarms: ${alarms.join(', ')}` : ''}.</div>`);
        for (const o of s.outages || []) {
            out.push(warn(`${escapeHtml(fmtWhen(o.time))}: ${o.drops ? `${o.drops} drop${o.drops === 1 ? '' : 's'}, ` : ''}${fmtDuration(o.downSeconds)} down, ${OUTAGE_CAUSES[o.cause] || escapeHtml(o.cause)}.`));
        }
        for (const r of s.reboots || []) {
            out.push(`<div>${escapeHtml(fmtWhen(r.time))}: the ONT restarted${r.reason ? ` (previous boot ended by: ${escapeHtml(r.reason)})` : ''}. Its counters began again, so that interval is left out.</div>`);
        }
        return out.join('');
    }).join('')
        + '<div>Timed by the ONT itself, so a drop between two polls counts. While the ONT restarts nothing is timed.</div>';
}

/// The line under the grants card's title: the latest rates, how much each burst carried, and the
/// one condition worth a warning whatever the OLT's usual rate is: in O5 with no grants at all.
export function ponGrantSummaryHtml(items) {
    const withGrants = (items || []).filter(item => (item.pon || []).some(p => p.grantsPerS != null));
    if (!withGrants.length) return '';
    const multi = withGrants.length > 1;
    const rate = v => v >= 100 ? fmtInt(Math.round(v)) : v.toFixed(1);
    return withGrants.map(item => {
        const last = [...item.pon].reverse().find(p => p.grantsPerS != null);
        const parts = [`${rate(last.grantsPerS)} grants/s`];
        if (last.burstsPerS != null) parts.push(`${rate(last.burstsPerS)} bursts/s sent`);
        if (last.bytesPerBurst != null) parts.push(`${rate(last.bytesPerBurst)} bytes per burst`);
        const line = `<div>${multi ? `${escapeHtml(item.label)}: ` : ''}Latest: ${parts.join(', ')}.</div>`;
        const starved = last.state === 'operation' && last.grantsPerS === 0;
        return line + (starved
            ? '<div style="color: var(--warning-color);">In O5, but the OLT grants this ONT nothing: it is registered and cannot send.</div>'
            : '');
    }).join('')
        + '<div>The usual grant rate is a property of the OLT, so read it for change, not against a fixed figure.</div>';
}

const WEEKDAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const LOAD_MAP_REFRESH_MS = 10 * 60 * 1000;
const LOAD_MAP_COLORS = ['#1e3a5f', '#1f5a94', '#1F78C1', '#4f9edb', '#9cc9ef'];

// Mean load per weekday and hour of day on the viewer's clock, from hourly values.
function loadMapCells(hours) {
    const sum = WEEKDAYS.map(() => new Array(24).fill(0));
    const count = WEEKDAYS.map(() => new Array(24).fill(0));
    for (const h of hours) {
        const p = noTime.parts(Date.parse(h.time));
        const day = (new Date(Date.UTC(p.year, p.month - 1, p.day)).getUTCDay() + 6) % 7;
        sum[day][p.hour] += h.pct;
        count[day][p.hour]++;
    }
    return { sum, count };
}

/// The hour-by-weekday map of the PON's downstream load: its own request (four weeks, hourly,
/// refetched every ten minutes at most) and its own chart, so both tabs mount it with one call.
/// `refresh` shows the map for the first visible ONT that has hours, and hides the card otherwise.
export function createPonLoadMap({ ApexCharts, url, cardSelector, chartSelector, summarySelector }) {
    // `epoch` moves on destroy, so a refresh still waiting on its request or its first render
    // when the tab is left does not mount a chart on an element that is gone and leave it there
    // for the next visit to update.
    let chart = null, data = null, fetchedAt = 0, pending = null, epoch = 0;

    async function load() {
        if (data && Date.now() - fetchedAt < LOAD_MAP_REFRESH_MS) return data;
        pending ??= fetch(url).then(r => r.ok ? r.json() : null).catch(() => null).finally(() => { pending = null; });
        const fresh = await pending;
        if (fresh) { data = fresh; fetchedAt = Date.now(); }
        return data;
    }

    async function refresh(container, visibleItems) {
        const card = container?.querySelector(cardSelector);
        if (!card) return;
        const started = epoch;
        const loaded = await load();
        if (started !== epoch) return;
        const ids = new Set((visibleItems || []).map(i => String(i.id)));
        const entry = (loaded?.items || []).find(i => ids.has(String(i.id)) && i.hours?.length);
        if (!entry) { card.style.display = 'none'; return; }
        // Shown before the chart mounts: a chart first rendered in a hidden container stays zero-size.
        card.style.display = '';

        const { sum, count } = loadMapCells(entry.hours);
        // Apex draws the first series at the bottom, so Sunday goes in first to put Monday on top.
        const series = WEEKDAYS.map((name, d) => ({
            name,
            // An hour without data is -1, which the color scale below paints as background: the
            // library draws a null cell white, and on a dark page a mostly empty map was all glare.
            data: sum[d].map((s, h) => ({ x: String(h).padStart(2, '0'), y: count[d][h] ? +(s / count[d][h]).toFixed(2) : -1 })),
        })).reverse();
        // Five steps up to the busiest hour, brighter for busier, so the scale fits a PON that
        // peaks at 3 percent as well as one that peaks at 60.
        const top = Math.max(0.01, ...series.flatMap(s => s.data.map(c => c.y)));
        const ranges = [{ from: -1, to: -0.5, color: '#1f2937', name: 'no data' }]
            .concat(LOAD_MAP_COLORS.map((color, i) => ({
                from: i === 0 ? 0 : +(top * i / LOAD_MAP_COLORS.length).toFixed(4),
                to: +(top * (i + 1) / LOAD_MAP_COLORS.length).toFixed(4),
                color,
            })));
        const heatmap = { radius: 1, enableShades: false, colorScale: { ranges } };

        const el = card.querySelector(chartSelector);
        if (el && !chart) {
            const mounted = chart = new ApexCharts(el, {
                chart: { type: 'heatmap', height: 230, background: 'transparent', toolbar: { show: false }, animations: { enabled: false } },
                series,
                dataLabels: { enabled: false },
                plotOptions: { heatmap },
                stroke: { width: 1, colors: ['#111827'] },
                xaxis: { type: 'category', labels: { style: { colors: '#9ca3af' } }, title: { text: 'hour of day', style: { color: '#9ca3af' } } },
                yaxis: { labels: { style: { colors: '#9ca3af' } } },
                grid: { borderColor: '#374151' },
                legend: { show: false },
                tooltip: { theme: 'dark', y: { formatter: v => v == null || v < 0 ? 'no data' : `${fmtShare(v)} % mean` } },
                noData: { text: 'No data yet', style: { color: '#64748b' } },
            });
            await mounted.render();
            if (started !== epoch) return;
        } else if (chart) {
            chart.updateOptions({ series, plotOptions: { heatmap } }, false, false);
        }

        const summaryEl = card.querySelector(summarySelector);
        if (summaryEl) {
            let peak = null, low = null;
            WEEKDAYS.forEach((name, d) => sum[d].forEach((s, h) => {
                if (!count[d][h]) return;
                const v = s / count[d][h];
                if (!peak || v > peak.v) peak = { v, name, h };
                if (!low || v < low.v) low = { v, name, h };
            }));
            const hourText = c => `${c.name} ${String(c.h).padStart(2, '0')}:00 (${fmtShare(c.v)} %)`;
            const days = (Date.parse(entry.hours[entry.hours.length - 1].time) - Date.parse(entry.hours[0].time)) / 86400000;
            const label = (visibleItems || []).find(i => String(i.id) === String(entry.id))?.label;
            summaryEl.innerHTML = `<div>${ids.size > 1 && label ? `${escapeHtml(label)}: ` : ''}Busiest hour ${hourText(peak)}, quietest ${hourText(low)}.`
                + ` Mean load of the whole PON per hour of the week, from ${days < 1.5 ? `${Math.round(days * 24)} hours` : `${Math.round(days)} days`} of data (up to ${loaded.days} days).</div>`;
        }
    }

    function destroy() {
        epoch++;
        if (chart) { chart.destroy(); chart = null; }
    }

    return { refresh, destroy };
}

const CHECK_LABELS = { light: 'Light', registered: 'Registered', management: 'Management channel', service: 'Service', traffic: 'Traffic' };
const CHECK_COLORS = { ok: '#7EB26D', fail: '#E24D42', unknown: '#6b7280' };
const CHECK_WORDS = { ok: 'ok', fail: 'failed', unknown: 'not known' };

/// The five-step connection check as a row of lights, from the fibre up to traffic. Read-only:
/// each light says what it was read from, and none of them does anything.
export function ponCheckHtml(items) {
    const withCheck = (items || []).filter(item => item.ponMore?.check);
    if (!withCheck.length) return '';
    const multi = withCheck.length > 1;
    const rows = withCheck.map(item => {
        const steps = item.ponMore.check.map(step =>
            `<span data-tooltip="${escapeHtml(step.detail)}" style="display:inline-flex;align-items:center;margin-right:1.1em;white-space:nowrap;">`
            + `<span style="display:inline-block;width:0.7em;height:0.7em;border-radius:50%;background:${CHECK_COLORS[step.status] || CHECK_COLORS.unknown};margin-right:0.4em"></span>`
            + `${escapeHtml(CHECK_LABELS[step.key] || step.key)}: ${CHECK_WORDS[step.status] || escapeHtml(step.status)}</span>`).join('');
        const failed = item.ponMore.check.filter(step => step.status === 'fail')
            .map(step => `${CHECK_LABELS[step.key] || step.key}: ${step.detail}`);
        return `<div style="margin: 0.15rem 0;">${multi ? `${escapeHtml(item.label)}: ` : ''}${steps}</div>`
            + (failed.length ? `<div style="color: var(--warning-color); font-size: 0.8rem;">${escapeHtml(failed.join('. '))}.</div>` : '');
    }).join('');
    return `<div class="chart-header"><h3 class="chart-title">Connection Check</h3></div>${rows}`;
}

function fmtBer(v) {
    if (v === 0) return '0';
    const exp = Math.floor(Math.log10(v));
    return `${(v / 10 ** exp).toFixed(1)}e${exp}`;
}

/// The line under the PON Errors title: what the FEC had to correct over the window and whether
/// anything got past it.
export function ponFecSummaryHtml(items) {
    const withFec = (items || []).filter(item => item.ponMore?.fec);
    if (!withFec.length) return '';
    const multi = withFec.length > 1;
    return withFec.map(item => {
        const f = item.ponMore.fec;
        const parts = [`${fmtInt(f.correctedCodewords)} codewords corrected`];
        if (f.correctedBits != null) parts.push(`${fmtInt(f.correctedBits)} bits`);
        if (f.preFecBitErrorRatio != null) parts.push(`bit error ratio before FEC ${fmtBer(f.preFecBitErrorRatio)}`);
        parts.push(`${fmtInt(f.bipErrors)} BIP errors`);
        const line = `<div>${multi ? `${escapeHtml(item.label)}: ` : ''}FEC in this window: ${parts.join(', ')}.</div>`;
        if (f.level === 'warning')
            return line + `<div style="color: var(--warning-color);">${fmtInt(f.uncorrectableCodewords)} codewords could not be corrected: data was lost on the fibre.</div>`;
        if (f.level === 'notice')
            return line + '<div>Corrections without a loss: the line works, and a count that keeps rising is the early sign of a weakening one.</div>';
        return line;
    }).join('');
}

const MODULE_CONDITIONS = { temperature: 'temperature', voltage: 'supply voltage', bias_current: 'bias current', tx_power: 'TX power', rx_power: 'RX power' };

/// The lines under the laser card's title: the bias current against the module's own limit and
/// against the start of the window, and any flag the module itself has raised.
export function ponLaserSummaryHtml(items) {
    const withLaser = (items || []).filter(item => item.ponMore?.laser);
    if (!withLaser.length) return '';
    const multi = withLaser.length > 1;
    const warn = text => `<div style="color: var(--warning-color);">${text}</div>`;
    return withLaser.map(item => {
        const l = item.ponMore.laser;
        const out = [];
        let head = `${l.biasMa.toFixed(1)} mA` + (l.warnHighMa != null ? `, the module warns at ${l.warnHighMa.toFixed(0)} mA` : '');
        if (l.biasChangePercent != null) {
            head += `. Across this window ${l.biasChangePercent > 0 ? '+' : ''}${l.biasChangePercent.toFixed(1)} %`
                + (l.txPowerChangeDb != null ? `, TX power ${fmtSignedDb(l.txPowerChangeDb)}` : '')
                + (l.temperatureChangeC != null ? `, temperature ${l.temperatureChangeC > 0 ? '+' : ''}${l.temperatureChangeC.toFixed(1)} C` : '');
        }
        out.push(`<div>${multi ? `${escapeHtml(item.label)}: ` : ''}${head}.</div>`);
        const hints = l.hints || [];
        if (hints.includes('bias_rise')) out.push(warn('More bias current for the same light at the same temperature: how an ageing laser shows. Compare over weeks before acting on it.'));
        if (hints.includes('near_limit')) out.push(warn('The bias current is within a fifth of the module\'s own warning limit.'));
        for (const a of l.moduleAlarms || []) {
            const [kind, condition] = a.split(':');
            const m = /^(.*)_(high|low)$/.exec(condition || '');
            out.push(warn(`The module itself raises ${kind === 'alarm' ? 'an alarm' : 'a warning'}: ${m ? `${MODULE_CONDITIONS[m[1]] || escapeHtml(m[1])} too ${m[2]}` : escapeHtml(a)}.`));
        }
        return out.join('');
    }).join('')
        + '<div>Ageing shows over months: use the 30 day window, and read the current only against the same TX power and temperature.</div>';
}

const PROVIDER_EVENTS = {
    config_written: () => 'the OLT wrote configuration to the ONT',
    key_renewed: () => 'the OLT renewed the encryption key',
    reranged: () => 'the OLT adjusted the ranging while the link stayed up',
    rate_changed: e => `the provisioned rate changed from ${escapeHtml(e.from)} to ${escapeHtml(e.to)}`,
    services_changed: e => `the provisioned services changed from ${escapeHtml(e.from)} to ${escapeHtml(e.to)}`,
};

/// What the provider's side did in the window: the OLT's management pace, the rate it has
/// provisioned, and each time it wrote to the ONT, renewed the key or adjusted the ranging.
export function ponProviderHtml(items) {
    const withProvider = (items || []).filter(item => item.ponMore?.provider || item.ponMore?.ranging?.eqdBits != null);
    if (!withProvider.length) return '';
    const multi = withProvider.length > 1;
    const body = withProvider.map(item => {
        const p = item.ponMore.provider || {};
        const parts = [];
        if (p.pirMbps != null) {
            parts.push(p.cirMbps != null && p.cirMbps !== p.pirMbps
                ? `Provisioned rate ${p.pirMbps.toFixed(1)} Mbit/s peak, ${p.cirMbps.toFixed(1)} Mbit/s committed`
                : `Provisioned rate ${p.pirMbps.toFixed(1)} Mbit/s`);
        }
        if (p.services != null) parts.push(`${p.services} service${p.services === 1 ? '' : 's'}`);
        if (p.omciPerMinute != null) parts.push(`${p.omciPerMinute} OMCI messages per minute from the OLT`);
        const lines = [`<div>${multi ? `${escapeHtml(item.label)}: ` : ''}${parts.join(', ') || 'Nothing reported'}.</div>`];
        const events = p.events || [];
        if (!events.length) lines.push('<div>No configuration write, key renewal or ranging adjustment in this window.</div>');
        for (const e of events) {
            lines.push(`<div>${escapeHtml(fmtWhen(e.time))}: ${(PROVIDER_EVENTS[e.what] || (() => escapeHtml(e.what)))(e)}.</div>`);
        }
        lines.push(...rangingLines(item.ponMore.ranging));
        return lines.join('');
    }).join('');
    return `<div class="chart-header"><h3 class="chart-title"><span data-tooltip="What the OLT did to this ONT, read from the ONT's management counters: the provisioned rate is the traffic descriptor the OLT created (OMCI), a configuration write is the MIB data sync counter moving, and key renewals and ranging adjustments are PLOAM messages that arrived while the link stayed up.">Provider Activity</span></h3></div>`
        + `<div style="color: var(--text-secondary); font-size: 0.8rem; margin-bottom: 0.5rem;">${body}</div>`;
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
        {
            header: '<span data-tooltip="Whether the ONT\'s host port, the side towards the gateway, has a link now. A state at poll time: a short drop between two polls does not show.">Host Port</span>',
            cell: item => { const v = lastWithKey(item, 'hostLink'); return v == null ? null : v === 1 ? 'link up' : 'no link'; },
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
