// Channel Spectrum on CM Stats: the latest poll's channels placed by frequency, so tilt, a notch,
// or ingress in one band reads at a glance. Downstream bars grow from 0 dBmV and take their SNR
// grade; upstream bars rise to their power and take its headroom grade.
//
// Per-channel values are cached, not stored, so this always shows the latest poll and never
// follows the charts' time window.

const _esc = document.createElement('span');
function esc(s) { _esc.textContent = s ?? ''; return _esc.innerHTML; }
function attr(s) { return String(s).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;'); }

// SNR grades match the live card (CmStatsPanel); the power lines match the alert thresholds.
const SNR_GRADES = [
    { min: 36, cls: 'cmsp-excellent', label: 'Excellent' },
    { min: 33, cls: 'cmsp-good', label: 'Good' },
    { min: 30, cls: 'cmsp-fair', label: 'Fair' },
    { min: -Infinity, cls: 'cmsp-poor', label: 'Poor' },
];
const DS_RANGE_DBMV = 7;
const US_CLEAR_DBMV = 48;
const US_MAX_DBMV = 51;

// A poll older than this is shown as stale.
const STALE_MS = 15 * 60000;

// Pixel geometry. The top margin holds the uncorrectables lane on downstream.
const MARGIN = { l: 36, r: 6, t: 26, b: 22 };
// Mobile (the app's 768px breakpoint): the gutter fits a three-character tick and no more.
const MARGIN_MOBILE = { ...MARGIN, l: 28, r: 2 };
const MOBILE = window.matchMedia('(max-width: 768px)');
const LANE_Y = 3;
const LANE_H = 8;
const SLOT_W = 34;
const SLOT_GAP = 14;

function snrGrade(snr) {
    return snr == null ? null : SNR_GRADES.find(g => snr >= g.min);
}

function usGrade(pwr) {
    if (pwr == null) return null;
    if (pwr > US_MAX_DBMV) return { cls: 'cmsp-poor', label: `Above ${US_MAX_DBMV} dBmV` };
    if (pwr > US_CLEAR_DBMV) return { cls: 'cmsp-fair', label: 'Near the limit' };
    return { cls: 'cmsp-good', label: 'In range' };
}

const fmt1 = v => v == null ? '-' : v.toFixed(1);
const fmtSigned = v => v == null ? '-' : (v > 0 ? '+' : '') + v.toFixed(1);
const fmtMhz = hz => {
    const mhz = hz / 1e6;
    return Number.isInteger(mhz) ? `${mhz}` : mhz.toFixed(mhz < 100 ? 2 : 1).replace(/0+$/, '').replace(/\.$/, '');
};
const isOfdm = c => /^OFDMA?$/i.test(c.mod || '') || /^OFDMA?$/i.test(c.type || '');

// ---- Derived facts ----

function range(values) {
    const v = values.filter(x => x != null);
    return v.length ? [Math.min(...v), Math.max(...v)] : null;
}

// Fitted power change from the lowest to the highest SC-QAM channel. Null when there are too
// few channels or too narrow a span for a slope to mean anything.
function tilt(ds) {
    const pts = ds.filter(c => c.locked && c.freq > 0 && c.pwr != null && !isOfdm(c));
    if (pts.length < 4) return null;
    const xs = pts.map(c => c.freq / 1e6), ys = pts.map(c => c.pwr);
    const lo = Math.min(...xs), hi = Math.max(...xs);
    if (hi - lo < 60) return null;
    const mx = xs.reduce((a, b) => a + b, 0) / xs.length;
    const my = ys.reduce((a, b) => a + b, 0) / ys.length;
    let num = 0, den = 0;
    xs.forEach((x, i) => { num += (x - mx) * (ys[i] - my); den += (x - mx) ** 2; });
    return den ? (num / den) * (hi - lo) : null;
}

// Channel width from the spacing of adjacent channels: 6 MHz (North America) or 8 MHz (Europe)
// downstream, 3.2 or 6.4 MHz upstream. Falls back to the common case when channels are sparse.
function channelWidth(placed, dir) {
    const cap = dir === 'ds' ? 8.5e6 : 7e6;
    const f = placed.filter(c => !isOfdm(c)).map(c => c.freq).sort((a, b) => a - b);
    const gaps = [];
    for (let i = 1; i < f.length; i++) {
        const g = f[i] - f[i - 1];
        if (g > 0 && g <= cap) gaps.push(g);
    }
    if (!gaps.length) return dir === 'ds' ? 6e6 : 6.4e6;
    gaps.sort((a, b) => a - b);
    return gaps[Math.floor(gaps.length / 2)];
}

function niceStep(span, maxTicks, steps) {
    return steps.find(s => span / s <= maxTicks) ?? steps[steps.length - 1];
}

// ---- Tooltips ----

function tipRow(label, value) {
    return `<div class="device-tooltip-row"><span class="device-tooltip-label">${esc(label)}:</span> ${esc(value)}</div>`;
}

function dsTip(c) {
    const g = snrGrade(c.snr);
    const rows = [
        tipRow('Power', `${fmt1(c.pwr)} dBmV`),
        tipRow('SNR', c.snr == null ? '-' : `${fmt1(c.snr)} dB (${g.label})`),
    ];
    if (c.mod) rows.push(tipRow('Modulation', c.mod));
    rows.push(tipRow('Uncorrectables', (c.uncorr ?? 0).toLocaleString()));
    rows.push(tipRow('Correctables', (c.corr ?? 0).toLocaleString()));
    if (!c.locked) rows.push(tipRow('Status', 'Not locked'));
    if (!(c.freq > 0)) rows.push(tipRow('Frequency', 'Not reported by the modem'));
    return tipTitle(c) + rows.join('');
}

function usTip(c) {
    const g = usGrade(c.pwr);
    const rows = [tipRow('Power', c.pwr == null ? '-' : `${fmt1(c.pwr)} dBmV (${g.label})`)];
    const kind = [c.type, c.mod].filter(Boolean).join(' · ');
    if (kind) rows.push(tipRow('Type', kind));
    if (!c.locked) rows.push(tipRow('Status', 'Not locked'));
    if (!(c.freq > 0)) rows.push(tipRow('Frequency', 'Not reported by the modem'));
    return tipTitle(c) + rows.join('');
}

function tipTitle(c) {
    const where = c.freq > 0 ? ` · ${fmtMhz(c.freq)} MHz` : '';
    return `<div class="cm-spectrum-tip-title">Channel ${esc(String(c.id))}${where}</div>`;
}

// ---- Plot ----

// Draws one direction into `host` at its current width.
function drawPlot(host, channels, dir) {
    const W = Math.max(200, Math.floor(host.clientWidth));
    const H = W < 420 ? 150 : 176;
    const m = MOBILE.matches ? MARGIN_MOBILE : MARGIN;
    const placed = channels.filter(c => c.freq > 0).sort((a, b) => a.freq - b.freq);
    const unplaced = channels.filter(c => !(c.freq > 0));
    const slotsW = unplaced.length ? unplaced.length * SLOT_W + SLOT_GAP : 0;
    const x0 = m.l, x1 = W - m.r - slotsW;
    const yTop = m.t, yBottom = H - m.b;

    // Y domain: downstream is centered on 0 dBmV with the DOCSIS range always in view; upstream
    // always shows the 51 dBmV ceiling.
    const powers = channels.map(c => c.pwr).filter(v => v != null);
    const pMin = powers.length ? Math.min(...powers) : 0;
    const pMax = powers.length ? Math.max(...powers) : 0;
    const lo = dir === 'ds' ? Math.min(-10, Math.floor(pMin - 2)) : Math.min(30, Math.floor(pMin - 3));
    const hi = dir === 'ds' ? Math.max(10, Math.ceil(pMax + 2)) : Math.max(55, Math.ceil(pMax + 2));
    const y = v => yBottom - (v - lo) / (hi - lo) * (yBottom - yTop);
    const base = dir === 'ds' ? y(0) : yBottom;

    // X domain from the placed channels, padded by one channel width so edge bars stay whole.
    const cw = channelWidth(placed, dir);
    const ofdmHalf = cw * 2;
    let f0 = placed.length ? placed[0].freq : 0, f1 = placed.length ? placed[placed.length - 1].freq : 1;
    placed.forEach(c => { if (isOfdm(c)) { f0 = Math.min(f0, c.freq - ofdmHalf); f1 = Math.max(f1, c.freq + ofdmHalf); } });
    const pad = Math.max(cw, (f1 - f0) * 0.03) || cw * 10;
    f0 -= pad; f1 += pad;
    const x = f => x0 + (f - f0) / (f1 - f0) * (x1 - x0);
    const pxPerHz = (x1 - x0) / (f1 - f0);
    const barW = Math.max(2, Math.min(22, cw * pxPerHz * 0.82));
    const ofdmW = Math.max(barW * 4, Math.min(64, ofdmHalf * 2 * pxPerHz));

    const parts = [];

    // Grid and y ticks.
    const yStep = hi - lo > 40 ? 10 : 5;
    for (let v = Math.ceil(lo / yStep) * yStep; v <= hi; v += yStep) {
        const yy = y(v);
        const zero = dir === 'ds' && v === 0;
        parts.push(`<line class="cm-spectrum-grid${zero ? ' zero' : ''}" x1="${x0}" x2="${x1}" y1="${yy}" y2="${yy}"/>`);
        parts.push(`<text class="cm-spectrum-tick" x="${x0 - 6}" y="${yy + 3.5}" text-anchor="end">${dir === 'ds' && v > 0 ? '+' : ''}${v}</text>`);
    }

    // Reference ranges: the DOCSIS downstream window, and the upstream ceiling.
    if (dir === 'ds') {
        parts.push(`<rect class="cm-spectrum-range" x="${x0}" width="${x1 - x0}" y="${y(DS_RANGE_DBMV)}" height="${y(-DS_RANGE_DBMV) - y(DS_RANGE_DBMV)}"/>`);
    } else {
        const yl = y(US_MAX_DBMV);
        parts.push(`<line class="cm-spectrum-limit" x1="${x0}" x2="${x1}" y1="${yl}" y2="${yl}"/>`);
        parts.push(`<text class="cm-spectrum-limit-label" x="${x1 - 2}" y="${yl - 4}" text-anchor="end">${US_MAX_DBMV} dBmV</text>`);
    }

    // X ticks, spaced for the width available.
    if (placed.length) {
        const spanMhz = (f1 - f0) / 1e6;
        const step = niceStep(spanMhz, Math.max(2, Math.floor((x1 - x0) / 64)), [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500]) * 1e6;
        const ticks = [];
        for (let f = Math.ceil(f0 / step) * step; f <= f1; f += step) ticks.push(f);
        ticks.forEach((f, i) => {
            const xx = x(f);
            const label = fmtMhz(f) + (i === ticks.length - 1 ? ' MHz' : '');
            const anchor = i === ticks.length - 1 && xx > x1 - 30 ? 'end' : 'middle';
            parts.push(`<line class="cm-spectrum-axis" x1="${xx}" x2="${xx}" y1="${yBottom}" y2="${yBottom + 4}"/>`);
            parts.push(`<text class="cm-spectrum-tick" x="${xx}" y="${H - 6}" text-anchor="${anchor}">${label}</text>`);
        });
    } else {
        parts.push(`<text class="cm-spectrum-empty" x="${(x0 + x1) / 2}" y="${(yTop + yBottom) / 2}" text-anchor="middle">No frequencies reported</text>`);
    }
    parts.push(`<line class="cm-spectrum-axis" x1="${x0}" x2="${x1}" y1="${yBottom}" y2="${yBottom}"/>`);

    // Uncorrectables lane: which downstream channels carry errors, shaded by how many.
    const laneChannels = dir === 'ds' ? placed.filter(c => !isOfdm(c) && c.uncorr > 0) : [];
    const maxUncorr = Math.max(0, ...laneChannels.map(c => c.uncorr));
    if (laneChannels.length) {
        parts.push(`<text class="cm-spectrum-lane-label" x="${x0 - 6}" y="${LANE_Y + LANE_H - 0.5}" text-anchor="end">FEC</text>`);
    }

    // Channels. Each gets a hit column from the midpoint to its neighbours, so a 2px bar is
    // still easy to hover and tap.
    const centers = placed.map(c => x(c.freq));
    placed.forEach((c, i) => {
        const cx = centers[i];
        const left = i === 0 ? Math.max(x0, cx - 12) : (centers[i - 1] + cx) / 2;
        const right = i === placed.length - 1 ? Math.min(x1, cx + 12) : (cx + centers[i + 1]) / 2;
        const w = isOfdm(c) ? ofdmW : barW;
        parts.push(channelGroup(c, dir, cx, w, y, base, yTop, yBottom, Math.min(left, cx - w / 2), Math.max(right, cx + w / 2), maxUncorr));
    });

    // Channels with no frequency sit in slots to the right of the axis.
    if (unplaced.length) {
        const sx = x1 + SLOT_GAP;
        parts.push(`<line class="cm-spectrum-divider" x1="${x1 + SLOT_GAP / 2}" x2="${x1 + SLOT_GAP / 2}" y1="${yTop}" y2="${yBottom}"/>`);
        unplaced.forEach((c, i) => {
            const cx = sx + i * SLOT_W + SLOT_W / 2;
            const w = Math.min(SLOT_W - 10, 20);
            parts.push(channelGroup(c, dir, cx, w, y, base, yTop, yBottom, cx - SLOT_W / 2, cx + SLOT_W / 2, maxUncorr));
            const label = (isOfdm(c) ? (c.type || c.mod || 'OFDM') : `Ch ${c.id}`).toUpperCase();
            parts.push(`<text class="cm-spectrum-tick" x="${cx}" y="${H - 6}" text-anchor="middle">${esc(label)}</text>`);
        });
    }

    const summary = dir === 'ds' ? 'Downstream channel power by frequency' : 'Upstream channel power by frequency';
    host.innerHTML = `<svg class="cm-spectrum-svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="${summary}">${parts.join('')}</svg>`;
}

function channelGroup(c, dir, cx, w, y, base, yTop, yBottom, hitL, hitR, maxUncorr) {
    const grade = dir === 'ds' ? snrGrade(c.snr) : usGrade(c.pwr);
    const cls = ['cm-spectrum-bar', grade?.cls ?? 'cmsp-none'];
    if (isOfdm(c)) cls.push('ofdm');
    if (!c.locked) cls.push('unlocked');

    // A channel with no power reading still gets a stub, so it is visible and hoverable.
    let top, height;
    if (c.pwr == null) {
        top = base - 3; height = 3;
    } else {
        const yv = y(c.pwr);
        top = Math.min(yv, base);
        height = Math.max(1.5, Math.abs(base - yv));
    }

    const out = [`<g class="cm-spectrum-channel">`];
    out.push(`<rect class="${cls.join(' ')}" x="${cx - w / 2}" y="${top}" width="${w}" height="${height}" rx="${w > 6 ? 1.5 : 0}"/>`);

    if (dir === 'ds' && !isOfdm(c) && c.uncorr > 0 && maxUncorr > 0) {
        const share = Math.log1p(c.uncorr) / Math.log1p(maxUncorr);
        out.push(`<rect class="cm-spectrum-fec" x="${cx - Math.max(w, 3) / 2}" y="${LANE_Y}" width="${Math.max(w, 3)}" height="${LANE_H}" opacity="${(0.3 + 0.7 * share).toFixed(2)}"/>`);
    }

    const tip = dir === 'ds' ? dsTip(c) : usTip(c);
    out.push(`<rect class="cm-spectrum-hit" x="${hitL}" y="${LANE_Y}" width="${Math.max(6, hitR - hitL)}" height="${yBottom - LANE_Y}" data-tooltip="${attr(tip)}" data-tooltip-html/>`);
    out.push('</g>');
    return out.join('');
}

// ---- Card ----

function fact(label, value, unit, tooltip) {
    const t = tooltip ? ` data-tooltip="${attr(tooltip)}"` : '';
    return `<span class="cm-spectrum-fact"${t}>${esc(label)} <span class="cm-spectrum-fact-value">${esc(value)}</span>${unit ? ' ' + esc(unit) : ''}</span>`;
}

function lockedCount(channels) {
    const locked = channels.filter(c => c.locked).length;
    return locked === channels.length ? `${locked} locked` : `${locked} of ${channels.length} locked`;
}

function swatch(cls, label) {
    return `<span class="cm-spectrum-key"><span class="cm-spectrum-swatch ${cls}"></span>${esc(label)}</span>`;
}

function dsPanel(ds, idx) {
    if (!ds.length) return panelEmpty('Downstream', 'No downstream channels reported');
    const pr = range(ds.filter(c => c.locked).map(c => c.pwr));
    const sr = range(ds.filter(c => c.locked).map(c => c.snr));
    const t = tilt(ds);
    const facts = [];
    if (pr) facts.push(fact('Power', `${fmtSigned(pr[0])} to ${fmtSigned(pr[1])}`, 'dBmV'));
    if (sr) facts.push(fact('SNR', `${fmt1(sr[0])} to ${fmt1(sr[1])}`, 'dB'));
    if (t != null) facts.push(fact('Tilt', fmtSigned(t), 'dB',
        'Power change from the lowest to the highest SC-QAM channel, from a straight-line fit.'));

    const keys = [
        `<span class="cm-spectrum-key-label">SNR</span>`,
        swatch('cmsp-excellent', '36+ dB'), swatch('cmsp-good', '33-36'), swatch('cmsp-fair', '30-33'), swatch('cmsp-poor', 'Under 30'),
        swatch('range', `±${DS_RANGE_DBMV} dBmV range`),
    ];
    if (ds.some(c => !isOfdm(c) && c.uncorr > 0)) keys.push(swatch('fec', 'Uncorrectables'));
    if (ds.some(c => !c.locked)) keys.push(swatch('unlocked', 'Not locked'));

    return panel('Downstream', lockedCount(ds), facts, `ds-${idx}`, keys);
}

function usPanel(us, idx) {
    if (!us.length) return panelEmpty('Upstream', 'No upstream channels reported');
    const pr = range(us.filter(c => c.locked).map(c => c.pwr));
    const facts = pr ? [fact('Power', `${fmt1(pr[0])} to ${fmt1(pr[1])}`, 'dBmV')] : [];
    const keys = [
        `<span class="cm-spectrum-key-label">Power</span>`,
        swatch('cmsp-good', `Up to ${US_CLEAR_DBMV} dBmV`), swatch('cmsp-fair', `${US_CLEAR_DBMV}-${US_MAX_DBMV}`), swatch('cmsp-poor', `Over ${US_MAX_DBMV}`),
    ];
    if (us.some(c => !c.locked)) keys.push(swatch('unlocked', 'Not locked'));
    return panel('Upstream', lockedCount(us), facts, `us-${idx}`, keys);
}

function panel(title, count, facts, plotKey, keys) {
    return `<section class="cm-spectrum-panel">
        <div class="cm-spectrum-panel-head">
            <span class="cm-spectrum-panel-title">${title}</span>
            <span class="cm-spectrum-count">${esc(count)}</span>
            <span class="cm-spectrum-facts">${facts.join('')}</span>
        </div>
        <div class="cm-spectrum-plot" data-plot="${plotKey}"></div>
        <div class="cm-spectrum-legend">${keys.join('')}</div>
    </section>`;
}

function panelEmpty(title, text) {
    return `<section class="cm-spectrum-panel">
        <div class="cm-spectrum-panel-head"><span class="cm-spectrum-panel-title">${title}</span></div>
        <div class="cm-spectrum-panel-empty">${esc(text)}</div>
    </section>`;
}

function asOf(polledAt, live) {
    const t = new Date(polledAt);
    if (Number.isNaN(t.getTime())) return '';
    const stale = Date.now() - t.getTime() > STALE_MS;
    const sameDay = t.toDateString() === new Date().toDateString();
    const when = t.toLocaleString(undefined, sameDay
        ? { hour: 'numeric', minute: '2-digit', second: '2-digit' }
        : { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
    const tip = stale
        ? 'No poll has succeeded since then, so these levels may be out of date.'
        : 'Channel levels from the modem\'s most recent poll. The time window applies to the charts above, not to this.';
    const cls = ['cm-spectrum-asof'];
    if (stale) cls.push('stale');
    else if (!live) cls.push('not-window');
    const suffix = !stale && !live ? ' · not the selected window' : '';
    return `<span class="${cls.join(' ')}" data-tooltip="${attr(tip)}">Latest poll ${esc(when)}${suffix}</span>`;
}

// ---- Entry points ----

const state = new WeakMap();

/**
 * Renders a Channel Spectrum card per visible modem into `el`. `live` is false while the charts
 * show a shifted or custom window. Skips the redraw when nothing changed, so an open tooltip
 * survives the chart poll.
 */
export function renderSpectrum(el, devices, { live, showDeviceName }) {
    if (!el) return;
    const withChannels = (devices || []).filter(d => d.spectrum && (d.spectrum.ds?.length || d.spectrum.us?.length));

    let s = state.get(el);
    if (!s) {
        s = { sig: null, devices: [], width: 0 };
        s.observer = new ResizeObserver(() => {
            const w = el.clientWidth;
            if (w && w !== s.width) { s.width = w; drawAll(el, s.devices); }
        });
        s.observer.observe(el);
        state.set(el, s);
    }

    const sig = JSON.stringify([withChannels.map(d => [d.id, d.label, d.spectrum]), live, showDeviceName]);
    if (sig === s.sig) return;
    s.sig = sig;
    s.devices = withChannels;

    destroyTooltips(el);
    if (!withChannels.length) { el.innerHTML = ''; return; }

    el.innerHTML = withChannels.map((d, i) => `<div class="chart-card cm-spectrum-card" data-tour="cm-spectrum">
        <div class="chart-header cm-spectrum-header">
            <h3 class="chart-title">Channel Spectrum${showDeviceName ? ' - ' + esc(d.label) : ''}</h3>
            ${asOf(d.spectrum.polledAt, live)}
        </div>
        <div class="cm-spectrum-panels">
            ${dsPanel(d.spectrum.ds || [], i)}
            ${usPanel(d.spectrum.us || [], i)}
        </div>
    </div>`).join('');
    s.width = el.clientWidth;
    drawAll(el, withChannels);
}

function drawAll(el, devices) {
    destroyTooltips(el, '.cm-spectrum-plot');
    devices.forEach((d, i) => {
        const ds = el.querySelector(`[data-plot="ds-${i}"]`);
        const us = el.querySelector(`[data-plot="us-${i}"]`);
        if (ds) drawPlot(ds, d.spectrum.ds || [], 'ds');
        if (us) drawPlot(us, d.spectrum.us || [], 'us');
    });
}

// Tooltips bound to nodes about to be replaced would strand their popups.
function destroyTooltips(el, within) {
    const scope = within ? el.querySelectorAll(within) : [el];
    scope.forEach(root => root.querySelectorAll('[data-tooltip]').forEach(n => n._tippy?.destroy()));
}

export function disposeSpectrum(el) {
    const s = el && state.get(el);
    if (!s) return;
    s.observer.disconnect();
    destroyTooltips(el);
    el.innerHTML = '';
    state.delete(el);
}
