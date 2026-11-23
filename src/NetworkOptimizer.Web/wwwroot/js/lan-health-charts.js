// LAN Health - Score History: hourly site score, with the LAN Health cutover marked.
import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';

const charts = new Map();

/** Draws or redraws the Score History chart in the element with the given id. */
export function renderHistory(elId, points, cutoverMs, use24h) {
    const el = document.getElementById(elId);
    if (!el) return;
    const data = (points || []).map(p => ({ x: p.t, y: Math.round(p.s * 10) / 10 }));
    const annotations = cutoverMs ? {
        xaxis: [{ x: cutoverMs, borderColor: '#9ca3af', strokeDashArray: 4, label: { text: 'LAN Health', style: { background: '#232326', color: '#a0a0a8', fontSize: '11px' } } }]
    } : {};
    const opts = {
        chart: { type: 'line', height: 220, background: 'transparent', toolbar: { show: false }, zoom: { enabled: false },
            animations: { enabled: false }, parentHeightOffset: 0 },
        theme: { mode: 'dark' },
        series: [{ name: 'LAN Health', data }],
        colors: ['#2ba89a'],
        stroke: { width: 2, curve: 'straight' },
        markers: { size: data.length === 1 ? 4 : 0 },
        grid: { borderColor: '#374151', strokeDashArray: 3 },
        xaxis: { type: 'datetime', labels: { datetimeUTC: false, style: { colors: '#9ca3af' },
            format: use24h ? 'MMM d HH:mm' : 'MMM d h tt' }, axisBorder: { show: false }, axisTicks: { show: false } },
        yaxis: { min: 0, max: 100, tickAmount: 4, labels: { style: { colors: '#9ca3af' }, formatter: v => Math.round(v) } },
        tooltip: { theme: 'dark', x: { format: use24h ? 'MMM d, HH:mm' : 'MMM d, h:mm tt' } },
        dataLabels: { enabled: false },
        legend: { show: false },
        annotations,
    };
    const existing = charts.get(elId);
    // The link page replaces the element; a chart bound to the old one would update off-screen.
    if (existing && existing.el === el) {
        existing.updateOptions(opts, true, false);
        return;
    }
    if (existing) { existing.destroy(); charts.delete(elId); }
    const chart = new ApexCharts(el, opts);
    charts.set(elId, chart);
    chart.render();
}

/**
 * Draws a link's Timeline as stacked columns per bucket: unexplained events, then explained ones,
 * with each Change Control change marked, so a run of drops thinning out or growing shows at a glance.
 */
export function renderTimeline(elId, buckets, changesMs, bucketMs, use24h) {
    const el = document.getElementById(elId);
    if (!el) return;
    const drops = (buckets || []).map(b => ({ x: b.t, y: b.drops }));
    const explained = (buckets || []).map(b => ({ x: b.t, y: b.explained }));
    const opts = {
        chart: { type: 'bar', stacked: true, height: 160, background: 'transparent', toolbar: { show: false }, zoom: { enabled: false },
            animations: { enabled: false }, parentHeightOffset: 0 },
        theme: { mode: 'dark' },
        series: [{ name: 'Drops', data: drops }, { name: 'Explained', data: explained }],
        colors: ['#ef5858', '#9ca3af'],
        plotOptions: { bar: { columnWidth: '80%' } },
        grid: { borderColor: '#374151', strokeDashArray: 3 },
        xaxis: { type: 'datetime', labels: { datetimeUTC: false, style: { colors: '#9ca3af' },
            format: bucketMs >= 86400000 ? 'MMM d' : (use24h ? 'MMM d HH:mm' : 'MMM d h tt') }, axisBorder: { show: false }, axisTicks: { show: false } },
        yaxis: { min: 0, forceNiceScale: true, labels: { style: { colors: '#9ca3af' }, formatter: v => Math.round(v) } },
        tooltip: { theme: 'dark', x: { format: use24h ? 'MMM d, HH:mm' : 'MMM d, h:mm tt' } },
        dataLabels: { enabled: false },
        legend: { show: true, labels: { colors: '#a0a0a8' }, position: 'top', horizontalAlign: 'left' },
        annotations: { xaxis: (changesMs || []).map(x => ({ x, borderColor: '#f59e0b', strokeDashArray: 4,
            label: { text: 'Change', style: { background: '#232326', color: '#a0a0a8', fontSize: '11px' } } })) },
    };
    const existing = charts.get(elId);
    if (existing && existing.el === el) { existing.updateOptions(opts, true, false); return; }
    if (existing) { existing.destroy(); charts.delete(elId); }
    const chart = new ApexCharts(el, opts);
    charts.set(elId, chart);
    chart.render();
}

/** Destroys the chart in the element, when the panel leaves the page. */
export function dispose(elId) {
    const chart = charts.get(elId);
    if (chart) { chart.destroy(); charts.delete(elId); }
}
