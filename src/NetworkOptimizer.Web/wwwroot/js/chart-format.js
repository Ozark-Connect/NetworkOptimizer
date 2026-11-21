// Value formatting shared by the chart modules, so one rate reads the same on every chart.

/** A rate in bits per second, scaled to bps / Kbps / Mbps / Gbps. Null and sub-1 values read as 0. */
export function formatBps(v) {
    if (v == null || v < 1) return '0';
    if (v >= 1e9) return (v / 1e9).toFixed(1) + ' Gbps';
    if (v >= 1e6) return (v / 1e6).toFixed(1) + ' Mbps';
    if (v >= 1e3) return (v / 1e3).toFixed(0) + ' Kbps';
    return v.toFixed(0) + ' bps';
}

/**
 * A rate for a y-axis label: no trailing ".0" ("500 Mbps", "1 Gbps"), and on a phone the unit
 * letter alone ("500M", "1G"). Tooltips keep formatBps; a chart whose tooltip falls back to the
 * axis formatter must pass formatBps to it explicitly.
 */
export function formatBpsAxis(v) {
    if (v == null || v < 1) return '0';
    const narrow = typeof matchMedia === 'function' && matchMedia('(max-width: 768px)').matches;
    const [scale, unit] = v >= 1e9 ? [1e9, 'G'] : v >= 1e6 ? [1e6, 'M'] : v >= 1e3 ? [1e3, 'K'] : [1, ''];
    const n = v / scale;
    const text = n >= 100 ? n.toFixed(0) : String(+n.toFixed(1));
    return narrow ? text + unit : `${text} ${unit}bps`;
}
