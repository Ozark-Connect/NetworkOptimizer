// Value formatting shared by the chart modules, so one rate reads the same on every chart.

/** A rate in bits per second, scaled to bps / Kbps / Mbps / Gbps. Null and sub-1 values read as 0. */
export function formatBps(v) {
    if (v == null || v < 1) return '0';
    if (v >= 1e9) return (v / 1e9).toFixed(1) + ' Gbps';
    if (v >= 1e6) return (v / 1e6).toFixed(1) + ' Mbps';
    if (v >= 1e3) return (v / 1e3).toFixed(0) + ' Kbps';
    return v.toFixed(0) + ' bps';
}
