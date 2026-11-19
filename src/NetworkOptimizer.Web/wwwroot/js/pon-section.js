// The PON charts and details table, shared by SFP Stats and ONT Stats.
//
// An ONT attached to a monitored SFP module and a standalone one report the same counters, so
// both tabs draw the same series under the same names off one definition. Chart mounting, sync
// groups and event marks stay with each tab, which owns its own layout.

import { alignedPoints } from './chart-tooltip.js?v=22';
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
            { name: `${prefix}Whole PON`, data: ponPoints(item, 'dsUsedMbps'), color: ponLoadColor(slot, 0, palette) },
            { name: `${prefix}This ONT`, data: ponPoints(item, 'dsOwnMbps'), color: ponLoadColor(slot, 1, palette) },
        ],
        // What the far end announces it launches, less what this ONT receives. With a reach
        // extender in the path the announced power is the extender's, and the name says so.
        lossSeries: [
            { name: `${prefix}${reachExtended(item) ? 'Path loss from extender' : 'Path loss'}`, data: ponPoints(item, 'pathLoss') },
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

/// One value of the load chart as rate and share of the PON's downstream: "53.9 Mbps (2.31%)".
export function fmtPonLoad(v, payloadMbps) {
    if (v == null) return '';
    return `${fmtMbps(v)} Mbps (${fmtShare(v / payloadMbps * 100)}%)`;
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
    if (minutes < 1.5) return `${Math.round(minutes * 60)} s`;
    if (minutes < 90) return `${Math.round(minutes)} min`;
    return `${(minutes / 60).toFixed(1)} h`;
}

// The stat strip and notes every PON card shows under its title. Labels and fixed text are
// trusted; callers escape anything that came from the device.
const stat = (label, value) =>
    `<span class="pon-stat"><span class="pon-stat-label">${label}</span><span class="pon-stat-value">${value}</span></span>`;

function statRow(parts, name) {
    if (!parts.length) return '';
    return `<div class="pon-stats">${name ? `<span class="pon-stat pon-stat-name">${escapeHtml(name)}</span>` : ''}${parts.join('')}</div>`;
}

function note(text, { level, time } = {}) {
    const when = time ? `<span class="pon-note-time">${escapeHtml(fmtWhen(time))}</span>` : '';
    return `<div class="pon-note${level ? ` ${level}` : ''}">${when}${text}</div>`;
}

const notesBlock = notes => notes.length ? `<div class="pon-notes">${notes.join('')}</div>` : '';
const dot = color => `<span class="pon-stat-dot" style="background: ${color};"></span>`;

/// The stat strip under the load card's title: which line is which, the latest and the highest
/// value of the window, and how long a stretch each point averages. Empty when no ONT reports a load.
export function ponLoadSummaryHtml(items, palette, slotOf) {
    const withLoad = (items || []).filter(item => (item.pon || []).some(p => p.dsUsedMbps != null));
    if (!withLoad.length) return '';
    const payload = ponPayloadMbps(withLoad);
    const multi = withLoad.length > 1;
    const span = pointSpanText(withLoad[0].pon);
    return withLoad.map((item, i) => {
        const slot = slotOf(item);
        const used = item.pon.map(p => p.dsUsedMbps).filter(v => v != null);
        const parts = [
            stat(`${dot(ponLoadColor(slot, 0, palette))}Whole PON`, fmtPonLoad(used[used.length - 1], payload)),
            stat('Peak', fmtPonLoad(Math.max(...used), payload)),
        ];
        const own = lastWithKey(item, 'dsOwnMbps');
        if (own != null) {
            parts.push(stat(`${dot(ponLoadColor(slot, 1, palette))}This ONT`, `${fmtMbps(own)} Mbps`));
            const share = lastWithKey(item, 'dsOwnShare');
            if (share != null) parts.push(stat('Share of PON', `${fmtShare(share)}%`));
        }
        if (i === 0 && span) parts.push(stat('Averaged Over', span));
        return statRow(parts, multi ? item.label : null);
    }).join('');
}

const fmtDb = v => `${v.toFixed(1)} dB`;
const fmtSignedDb = v => `${v > 0 ? '+' : ''}${v.toFixed(1)} dB`;
const fmtDbmSigned = v => `${v > 0 ? '+' : ''}${v.toFixed(1)} dBm`;
const CHANGE_LABELS = { odn_class: 'ODN class', reach_extender: 'Reach extender', launch_power: 'OLT launch power' };

/// The stat strip and notes under the Path Loss title, from the server's summary of the window
/// (item.ponPath): the latest loss against the budget of the class the OLT names, what moved
/// against the window's usual level and on which side of the fiber, and each change in the OLT's
/// own announcement. Empty when no ONT's OLT announces anything.
export function ponPathSummaryHtml(items) {
    const withPath = (items || []).filter(item => item.ponPath);
    if (!withPath.length) return '';
    const multi = withPath.length > 1;
    return withPath.map(item => {
        const s = item.ponPath;
        const cls = s.odnClass != null ? escapeHtml(s.odnClass) : null;
        const parts = [];
        const notes = [];
        if (s.lossDb != null) {
            parts.push(stat('Latest', fmtDb(s.lossDb)));
            if (s.launchDbm != null) parts.push(stat(s.reachExtender ? 'Extender Launch' : 'OLT Launch', fmtDbmSigned(s.launchDbm)));
            if (s.rxDbm != null) parts.push(stat('RX', fmtDbmSigned(s.rxDbm)));
        } else {
            notes.push(note('The OLT announces no launch power, so the path loss is not known.'));
        }
        if (cls != null) parts.push(stat('ODN Class', `${cls} (${s.classMinDb}-${s.classMaxDb} dB)`));
        if (s.marginDb != null && s.marginDb >= 0) parts.push(stat('Margin', fmtDb(s.marginDb)));

        const warn = text => notes.push(note(text, { level: 'warning' }));
        const hints = s.hints || [];
        if (hints.includes('over_limit')) warn(`${fmtDb(-s.marginDb)} over the class ${cls} limit: the link runs outside its loss budget.`);
        if (hints.includes('near_limit')) warn(`Only ${fmtDb(s.marginDb)} left to the class ${cls} limit.`);
        if (hints.includes('below_min')) warn(`${fmtDb(s.belowMinDb)} under the class ${cls} minimum: less loss than the class expects.`);
        if (hints.includes('loss_rise')) {
            const launch = Math.abs(s.launchChangeDb) < 0.05 ? 'OLT launch power unchanged' : `OLT launch power ${fmtSignedDb(s.launchChangeDb)}`;
            warn(`Up ${fmtDb(s.lossChangeDb)} from the usual ${fmtDb(s.baselineLossDb)} (RX ${fmtSignedDb(s.rxChangeDb)}, ${launch}). `
                + 'The change is on the fiber path: check connectors, the jumper, splices, and bends.');
        }
        if (hints.includes('unsteady')) warn(`Jumped ${s.jumps} times in this window${s.spreadDb != null ? ` across ${fmtDb(s.spreadDb)}` : ''}. A steady fiber does not do this: check for a loose or dirty connector.`);
        if (hints.includes('launch_drop')) warn(`OLT launch power ${fmtSignedDb(s.launchChangeDb)} from its usual level (RX ${fmtSignedDb(s.rxChangeDb)}). The launch power feeds the whole PON, so the change is on the provider's side.`);
        for (const c of s.changes || []) {
            notes.push(note(`${CHANGE_LABELS[c.what] || escapeHtml(c.what)} changed from ${escapeHtml(c.from)} to ${escapeHtml(c.to)}: a change on the provider side.`,
                { level: 'warning', time: c.time }));
        }
        const ranging = item.ponMore?.ranging;
        if (ranging?.ponIdStale) warn(`The OLT last announced its launch power ${fmtDuration(ranging.ponIdAgeSeconds)} ago, so the path loss uses an old value.`);
        return statRow(parts, multi ? item.label : null) + notesBlock(notes);
    }).join('');
}

/// What the ranging says: the equalization delay as a stat, and each step in it as a note with the
/// fiber length it stands for and what the loss did across it. Listed with the provider's
/// activity, since a step is the OLT measuring a different distance.
function rangingParts(r) {
    const parts = [];
    const notes = [];
    if (!r) return { parts, notes };
    if (r.eqdBits != null) {
        parts.push(stat('Equalization Delay', `${fmtInt(r.eqdBits)} bits (${r.eqdMicros.toFixed(1)} µs)`));
    }
    for (const s of r.steps || []) {
        const metres = Math.abs(s.deltaMetres);
        const length = `${metres >= 10 ? metres.toFixed(0) : metres.toFixed(1)} m ${s.deltaMetres > 0 ? 'longer' : 'shorter'}`;
        const loss = s.lossChangeDb == null ? ''
            : Math.abs(s.lossChangeDb) < 0.3 ? ', path loss unchanged'
            : `, path loss ${fmtSignedDb(s.lossChangeDb)}`;
        notes.push(note(`Equalization delay moved ${s.deltaBits > 0 ? '+' : ''}${fmtInt(s.deltaBits)} bits: a fiber path about ${length}${loss}. `
            + 'The OLT measured a different distance: a rerouted or respliced path, or another OLT port.', { level: 'warning', time: s.time }));
    }
    return { parts, notes };
}

/// The launch power the OLT announces, as a line for the RX / TX power chart: the far end of the
/// same fiber in the same unit. Empty where the OLT announces none.
export function ponLaunchSeries(item, label, color) {
    const data = ponPoints(item, 'oltLaunch');
    if (!data.length) return null;
    return { name: `${label}${reachExtended(item) ? ' Reach Extender' : ' OLT'} Launch`, color, data };
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
    light: 'loss of signal or frame',
    olt: 'the OLT deactivated the ONT',
    'light+olt': 'loss of signal, and the OLT deactivated the ONT',
    unknown: 'no alarm and no deactivation',
};

const plural = (n, word) => `${fmtInt(n)} ${word}${n === 1 ? '' : 's'}`;

/// The stat strip and notes under the Link Downtime title, from the server's summary of the
/// window (item.ponAvail): the share of time in O5 by the ONT's own clock, each stretch with a
/// drop and what its counters point at, and the ONT's reboots, which are not line downtime.
export function ponAvailSummaryHtml(items) {
    const withAvail = (items || []).filter(item => item.ponAvail);
    if (!withAvail.length) return '';
    const multi = withAvail.length > 1;
    return withAvail.map(item => {
        const s = item.ponAvail;
        const parts = [];
        const notes = [];
        if (s.availabilityPercent == null) {
            notes.push(note('No full interval in this window yet.'));
        } else {
            const pct = s.availabilityPercent >= 99.995 && s.downSeconds > 0 ? s.availabilityPercent.toFixed(4) : s.availabilityPercent.toFixed(2);
            parts.push(stat('In O5', `${pct}%`));
            parts.push(stat('Drops', fmtInt(s.drops || 0)));
            parts.push(stat('Downtime', fmtDuration(s.downSeconds || 0)));
        }
        if (s.losEvents) parts.push(stat('Loss of Signal', `${fmtInt(s.losEvents)} (${fmtDuration(s.losSeconds)})`));
        if (s.lofEvents) parts.push(stat('Loss of Frame', fmtInt(s.lofEvents)));
        if (s.oltDeactivations) parts.push(stat('OLT Deactivations', fmtInt(s.oltDeactivations)));
        for (const o of s.outages || []) {
            notes.push(note(`${o.drops ? `${plural(o.drops, 'drop')}, ` : ''}${fmtDuration(o.downSeconds)} down: ${OUTAGE_CAUSES[o.cause] || escapeHtml(o.cause)}.`,
                { level: 'warning', time: o.time }));
        }
        for (const r of s.reboots || []) {
            notes.push(note(`The ONT restarted${r.reason ? ` (reset reason: ${escapeHtml(r.reason)})` : ''}. This interval is not counted.`, { time: r.time }));
        }
        return statRow(parts, multi ? item.label : null) + notesBlock(notes);
    }).join('');
}

/// The stat strip under the Upstream Grants title: the latest rates, how much each burst carried,
/// and the one condition worth a warning whatever the OLT's usual rate is: in O5 with no grants.
export function ponGrantSummaryHtml(items) {
    const withGrants = (items || []).filter(item => (item.pon || []).some(p => p.grantsPerS != null));
    if (!withGrants.length) return '';
    const multi = withGrants.length > 1;
    const rate = v => v >= 100 ? fmtInt(Math.round(v)) : v.toFixed(1);
    return withGrants.map(item => {
        const last = [...item.pon].reverse().find(p => p.grantsPerS != null);
        const parts = [stat('Grants', `${rate(last.grantsPerS)}/s`)];
        if (last.burstsPerS != null) parts.push(stat('Bursts Sent', `${rate(last.burstsPerS)}/s`));
        if (last.bytesPerBurst != null) parts.push(stat('Per Burst', `${rate(last.bytesPerBurst)} bytes`));
        const starved = last.state === 'operation' && last.grantsPerS === 0;
        return statRow(parts, multi ? item.label : null) + (starved
            ? notesBlock([note('In O5 with no upstream grants: the ONT is registered but cannot send.', { level: 'warning' })])
            : '');
    }).join('');
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
    let chart = null, data = null, fetchedAt = 0, pending = null, epoch = 0, drawn = null;

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
        // Only a device that reports PON load has hours to show: without one, no four-week query.
        const withLoad = (visibleItems || []).filter(item => (item.pon || []).some(p => p.dsUsedMbps != null));
        if (!withLoad.length) { card.style.display = 'none'; return; }
        const started = epoch;
        const loaded = await load();
        if (started !== epoch) return;
        const ids = new Set(withLoad.map(i => String(i.id)));
        const entry = (loaded?.items || []).find(i => ids.has(String(i.id)) && i.hours?.length);
        if (!entry) { card.style.display = 'none'; return; }
        // Shown before the chart mounts: a chart first rendered in a hidden container stays zero-size.
        card.style.display = '';
        // The hours change at most once a fetch: redraw only when another fetch or device is shown.
        const key = `${fetchedAt}:${entry.id}:${ids.size}`;
        if (chart && drawn === key) return;
        drawn = key;

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
                // 24 labels do not fit a phone: label every third hour there, and keep the full hour in the tooltip.
                xaxis: {
                    type: 'category', title: { text: 'Hour of Day', style: { color: '#9ca3af' } },
                    labels: { rotate: 0, style: { colors: '#9ca3af' }, formatter: v => el.clientWidth < 600 && +v % 3 ? '' : v },
                },
                yaxis: { labels: { style: { colors: '#9ca3af' } } },
                grid: { borderColor: '#374151' },
                legend: { show: false },
                tooltip: { theme: 'dark', x: { formatter: v => `${v}:00` }, y: { formatter: v => v == null || v < 0 ? 'No data' : `${fmtShare(v)}% mean load` } },
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
            const hourText = c => `${c.name} ${String(c.h).padStart(2, '0')}:00 (${fmtShare(c.v)}%)`;
            const days = (Date.parse(entry.hours[entry.hours.length - 1].time) - Date.parse(entry.hours[0].time)) / 86400000;
            const label = (visibleItems || []).find(i => String(i.id) === String(entry.id))?.label;
            summaryEl.innerHTML = statRow([
                stat('Busiest', hourText(peak)),
                stat('Quietest', hourText(low)),
                stat('History', `${days < 1.5 ? plural(Math.round(days * 24), 'hour') : plural(Math.round(days), 'day')} of ${loaded.days}`),
            ], ids.size > 1 ? label : null);
        }
    }

    function destroy() {
        epoch++;
        drawn = null;
        if (chart) { chart.destroy(); chart = null; }
    }

    return { refresh, destroy };
}

const CHECK_LABELS = {
    light: 'Light', transmit: 'Transmit', registered: 'Registered', line_errors: 'Line Errors',
    management: 'Management', service: 'Service', host_link: 'Host Link', traffic: 'Traffic',
};
const CHECK_WORDS = { ok: 'OK', fail: 'Failed', unknown: 'Not reported' };

const sentence = text => text ? `${text.charAt(0).toUpperCase()}${text.slice(1)}` : '';

/// The connection check as a row of status chips, from the fiber up to traffic. Each
/// chip's tooltip says what it was read from; a failed step also gets a note below the row.
export function ponCheckHtml(items) {
    const withCheck = (items || []).filter(item => item.ponMore?.check);
    if (!withCheck.length) return '';
    const multi = withCheck.length > 1;
    return withCheck.map(item => {
        const label = step => escapeHtml(CHECK_LABELS[step.key] || step.key);
        const chips = item.ponMore.check.map(step => {
            const status = CHECK_WORDS[step.status] ? step.status : 'unknown';
            const tip = `${CHECK_WORDS[status]}: ${sentence(step.detail)}`;
            return `<span class="pon-check-step ${status}" data-tooltip="${escapeHtml(tip)}">${label(step)}</span>`;
        }).join('');
        const failed = item.ponMore.check.filter(step => step.status === 'fail')
            .map(step => note(`${label(step)}: ${escapeHtml(step.detail)}.`, { level: 'warning' }));
        return `<div class="pon-check">${multi ? `<span class="pon-stat-name">${escapeHtml(item.label)}</span>` : ''}${chips}</div>`
            + notesBlock(failed);
    }).join('');
}

function fmtBer(v) {
    if (v === 0) return '0';
    const exp = Math.floor(Math.log10(v));
    return `${(v / 10 ** exp).toFixed(1)}e${exp}`;
}

/// The stat strip under the PON Errors title: what the FEC had to correct over the window and
/// whether anything got past it.
export function ponFecSummaryHtml(items) {
    const withFec = (items || []).filter(item => item.ponMore?.fec);
    if (!withFec.length) return '';
    const multi = withFec.length > 1;
    return withFec.map(item => {
        const f = item.ponMore.fec;
        const parts = [stat('FEC Corrected', plural(f.correctedCodewords, 'codeword'))];
        if (f.correctedBits != null) parts.push(stat('Corrected Bits', fmtInt(f.correctedBits)));
        if (f.preFecBitErrorRatio != null) parts.push(stat('Pre-FEC BER', fmtBer(f.preFecBitErrorRatio)));
        parts.push(stat('BIP Errors', fmtInt(f.bipErrors)));
        const notes = [];
        if (f.level === 'warning') notes.push(note(`${plural(f.uncorrectableCodewords, 'codeword')} could not be corrected: data was lost on the fiber.`, { level: 'warning' }));
        if (f.level === 'notice') notes.push(note('FEC is correcting errors with no data lost. A count that keeps rising is an early sign of a weakening line.'));
        return statRow(parts, multi ? item.label : null) + notesBlock(notes);
    }).join('');
}

const MODULE_CONDITIONS = { temperature: 'temperature', voltage: 'supply voltage', bias_current: 'bias current', tx_power: 'TX power', rx_power: 'RX power' };

/// The stat strip and notes under the Laser Bias Current title: the bias current against the
/// module's own limit and against the start of the window, and any flag the module has raised.
export function ponLaserSummaryHtml(items) {
    const withLaser = (items || []).filter(item => item.ponMore?.laser);
    if (!withLaser.length) return '';
    const multi = withLaser.length > 1;
    const signed = (v, digits, unit) => `${v > 0 ? '+' : ''}${v.toFixed(digits)}${unit}`;
    return withLaser.map(item => {
        const l = item.ponMore.laser;
        const parts = [stat('Latest', `${l.biasMa.toFixed(1)} mA`)];
        if (l.warnHighMa != null) parts.push(stat('Module Warns At', `${l.warnHighMa.toFixed(0)} mA`));
        if (l.biasChangePercent != null) parts.push(stat('Window Change', signed(l.biasChangePercent, 1, '%')));
        if (l.txPowerChangeDb != null) parts.push(stat('TX Power', fmtSignedDb(l.txPowerChangeDb)));
        if (l.temperatureChangeC != null) parts.push(stat('Temperature', signed(l.temperatureChangeC, 1, ' °C')));
        const notes = [];
        const warn = text => notes.push(note(text, { level: 'warning' }));
        const hints = l.hints || [];
        if (hints.includes('bias_rise')) warn('Bias current rose with TX power and temperature steady: the sign of an aging laser. Compare over weeks before you act on it.');
        if (hints.includes('near_limit')) warn('Bias current is within 20% of the module\'s warning limit.');
        for (const a of l.moduleAlarms || []) {
            const [kind, condition] = a.split(':');
            const m = /^(.*)_(high|low)$/.exec(condition || '');
            warn(`The module raises ${kind === 'alarm' ? 'an alarm' : 'a warning'}: ${m ? `${MODULE_CONDITIONS[m[1]] || escapeHtml(m[1])} too ${m[2]}` : escapeHtml(a)}.`);
        }
        return statRow(parts, multi ? item.label : null) + notesBlock(notes);
    }).join('');
}

const PROVIDER_EVENTS = {
    config_written: () => 'The OLT wrote configuration to the ONT.',
    key_renewed: () => 'The OLT renewed the encryption key.',
    reranged: () => 'The OLT adjusted the ranging while the link stayed up.',
    rate_changed: e => `Provisioned rate changed from ${escapeHtml(e.from)} to ${escapeHtml(e.to)}.`,
    services_changed: e => `Provisioned services changed from ${escapeHtml(e.from)} to ${escapeHtml(e.to)}.`,
};

/// The Provider Activity card's body: the rate the OLT provisioned, its management pace, the
/// equalization delay, and each time it wrote to the ONT, renewed the key or adjusted the ranging.
export function ponProviderHtml(items) {
    const withProvider = (items || []).filter(item => item.ponMore?.provider || item.ponMore?.ranging?.eqdBits != null);
    if (!withProvider.length) return '';
    const multi = withProvider.length > 1;
    return withProvider.map(item => {
        const p = item.ponMore.provider || {};
        const parts = [];
        if (p.pirMbps != null) {
            parts.push(stat('Provisioned Rate', `${fmtInt(+p.pirMbps.toFixed(1))} Mbps`));
            if (p.cirMbps != null && p.cirMbps !== p.pirMbps) parts.push(stat('Committed Rate', `${fmtInt(+p.cirMbps.toFixed(1))} Mbps`));
        }
        if (p.services != null) parts.push(stat('Services', fmtInt(p.services)));
        if (p.omciPerMinute != null) parts.push(stat('OMCI Messages', `${fmtInt(p.omciPerMinute)}/min`));
        const ranging = rangingParts(item.ponMore.ranging);
        parts.push(...ranging.parts);
        const events = p.events || [];
        const notes = events.map(e => note((PROVIDER_EVENTS[e.what] || (() => `${escapeHtml(e.what)}.`))(e), { time: e.time }));
        if (!events.length && item.ponMore.provider) notes.push(note('No configuration writes, key renewals, or ranging adjustments in this window.'));
        notes.push(...ranging.notes);
        return statRow(parts, multi ? item.label : null) + notesBlock(notes);
    }).join('');
}

// Fill an HTML-only PON card (Connection Check, Provider Activity) and hide it when empty.
function updatePonHtmlCard(container, p, name, html) {
    const card = container.querySelector(`.${p}-pon-${name}-card`);
    if (!card) return;
    const body = card.querySelector(`.${p}-pon-${name}`);
    if (body) body.innerHTML = html;
    card.style.display = html ? '' : 'none';
}

/// Everything in the PON section that is not a chart series, for both tabs: the Connection Check
/// and Provider Activity cards, each card's stat strip, the load map, and the view tabs. `p` is
/// the tab's class prefix ('sfp' or 'ont'). Call after the chart cards are updated.
export function renderPonExtras(container, p, items, { palette, slotOf, loadMap }) {
    const section = container.querySelector(`.${p}-pon-section, .${p}-errors-section`);
    updatePonHtmlCard(container, p, 'check', ponCheckHtml(items));
    updatePonHtmlCard(container, p, 'provider', ponProviderHtml(items));
    for (const [name, html] of [
        ['loss', ponPathSummaryHtml(items)], ['bias', ponLaserSummaryHtml(items)],
        ['avail', ponAvailSummaryHtml(items)], ['fec', ponFecSummaryHtml(items)],
        ['util', ponLoadSummaryHtml(items, palette, slotOf)], ['grant', ponGrantSummaryHtml(items)],
    ]) {
        const target = container.querySelector(`.${p}-pon-${name}-summary`);
        if (target) target.innerHTML = html;
    }
    applyPonView(section);
    // Its own request, and a failure there must not hold up the rest. It can show or hide its
    // card, so the view runs again once it settles.
    loadMap.refresh(container, items).catch(() => {}).finally(() => applyPonView(section));
}

const ODN_CLASSES = ['A', 'B', 'B+', 'C', 'C+'];

function pathLossCell(item) {
    const loss = lastWithKey(item, 'pathLoss');
    const cls = ODN_CLASSES[lastWithKey(item, 'odnClass')];
    if (loss == null && cls == null) return null;
    const parts = [];
    if (loss != null) parts.push(`${loss.toFixed(1)} dB${reachExtended(item) ? ' from extender' : ''}`);
    if (cls != null) parts.push(`(class ${cls})`);
    return parts.join(' ');
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

// The PON section's views. Each card names its group with data-pon-group; a card without one
// (Connection Check) shows in every view. The choice is one per user for both tabs: the page
// seeds it from the user's preferences before mounting, and a pick is saved back through the page.
const PON_VIEWS = ['all', 'optical', 'link', 'traffic', 'provider'];

function loadView() {
    const v = window.__netoptPonView;
    return PON_VIEWS.includes(v) ? v : 'all';
}

function saveView(view) {
    if (!PON_VIEWS.includes(view)) return;
    window.__netoptPonView = view;
    window.__netoptLatencyRef?.invokeMethodAsync('OnPonViewChanged', view).catch(() => { /* the view holds for this page only */ });
}

// A group has content when one of its cards is not hidden by updatePonCard (style.display).
function groupsWithContent(section) {
    const groups = new Set();
    section.querySelectorAll('[data-pon-group]').forEach(card => {
        if (card.style.display !== 'none') groups.add(card.dataset.ponGroup);
    });
    return groups;
}

/// Show the selected view's cards and hide the rest. Call after every update that can show or
/// hide a card: a tab whose group has no card goes away, and with fewer than two groups the whole
/// tab row does. A saved view with nothing in it falls back to All without overwriting the choice.
export function applyPonView(section) {
    const tabs = section?.querySelector('.pon-view-tabs');
    if (!tabs) return;
    const groups = groupsWithContent(section);
    tabs.querySelectorAll('[data-pon-view]').forEach(tab => {
        const view = tab.dataset.ponView;
        tab.style.display = view === 'all' || groups.has(view) ? '' : 'none';
    });
    tabs.style.display = groups.size >= 2 ? '' : 'none';
    const saved = loadView();
    const view = groups.size >= 2 && groups.has(saved) ? saved : 'all';
    tabs.querySelectorAll('[data-pon-view]').forEach(tab => {
        const active = tab.dataset.ponView === view;
        tab.classList.toggle('active', active);
        tab.setAttribute('aria-selected', String(active));
    });
    section.querySelectorAll('[data-pon-group]').forEach(card => {
        card.classList.toggle('pon-view-hidden', view !== 'all' && card.dataset.ponGroup !== view);
    });
}

/// Wire the view tabs once per mount. Safe to call again: a row already wired is left alone.
export function initPonViewTabs(section) {
    const tabs = section?.querySelector('.pon-view-tabs');
    if (!tabs || tabs.dataset.wired) return;
    tabs.dataset.wired = '1';
    tabs.addEventListener('click', e => {
        const tab = e.target.closest('[data-pon-view]');
        if (!tab) return;
        saveView(tab.dataset.ponView);
        applyPonView(section);
        // A chart updated while its card was hidden has no width; let ApexCharts measure again.
        window.dispatchEvent(new Event('resize'));
    });
    applyPonView(section);
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
        {
            header: '<span data-tooltip="Downstream AES encryption, read from the ONT\'s GEM port table">AES DS</span>',
            cell: item => {
                const l = [...item.pon].reverse().find(p => p.aes != null);
                if (!l) return null;
                if (!l.aes) return 'off';
                return l.aesPorts == null ? 'on' : `on (${l.aesPorts} GEM port${l.aesPorts === 1 ? '' : 's'})`;
            },
        },
        { header: 'Response Time', cell: item => lastOf(item).respTime },
        {
            header: '<span data-tooltip="The launch power the OLT announces, minus the RX power this ONT measures. With a reach extender in the path, the loss is measured from the extender. The class is the loss budget the OLT says the network is built for.">Path Loss</span>',
            cell: pathLossCell,
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
