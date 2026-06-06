'use strict';

(function () {
    let pollTimer = null;
    let rangeHours = 24;
    let customFrom = null;
    let customTo = null;
    let charts = {};
    let observer = null;
    let visible = true;
    let soloDevice = null;

    const COLORS = ['#2ba89a', '#3b82f6', '#a78bfa', '#ef5858', '#f59e0b', '#10b981'];

    function init(deviceId) {
        soloDevice = deviceId || null;
        setupTimeRange();
        setupVisibility();
        fetchAndRender();
        startPolling();
    }

    function destroy() {
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
        if (observer) { observer.disconnect(); observer = null; }
        Object.values(charts).forEach(c => { try { c.destroy(); } catch (_) { } });
        charts = {};
    }

    function setupTimeRange() {
        document.querySelectorAll('#cm-charts-container .time-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                document.querySelectorAll('#cm-charts-container .time-btn').forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
                rangeHours = parseInt(btn.dataset.hours);
                customFrom = null;
                customTo = null;
                fetchAndRender();
            });
        });
    }

    function setupVisibility() {
        const el = document.getElementById('cm-charts-container');
        if (!el) return;
        observer = new IntersectionObserver(entries => {
            visible = entries[0].isIntersecting;
        }, { threshold: 0.1 });
        observer.observe(el);
    }

    function startPolling() {
        const interval = rangeHours <= 1 ? 15000 : rangeHours <= 6 ? 30000 : 60000;
        if (pollTimer) clearInterval(pollTimer);
        pollTimer = setInterval(() => { if (visible) fetchAndRender(); }, interval);
    }

    async function fetchAndRender() {
        try {
            let url = `/api/monitoring/cm-chart?rangeHours=${rangeHours}`;
            if (customFrom && customTo) {
                url = `/api/monitoring/cm-chart?from=${customFrom}&to=${customTo}`;
            }
            if (soloDevice) url += `&cmId=${soloDevice}`;

            const resp = await fetch(url);
            if (!resp.ok) return;
            const json = await resp.json();
            renderCharts(json.devices || []);
        } catch (_) { }
    }

    function renderCharts(devices) {
        renderChart('cm-ds-power-chart', devices, 'dsPower', 'DS Power (dBmV)', 'dBmV');
        renderChart('cm-ds-snr-chart', devices, 'dsSnr', 'DS SNR (dB)', 'dB');
        renderChart('cm-us-power-chart', devices, 'usPower', 'US Power (dBmV)', 'dBmV');
        renderChart('cm-errors-chart', devices, 'uncorrDelta', 'Uncorrectable Errors (per interval)', 'errors');
    }

    function renderChart(containerId, devices, field, title, unit) {
        const el = document.getElementById(containerId);
        if (!el) return;

        const series = devices.map((dev, i) => ({
            name: dev.label,
            data: dev.data.map(p => [new Date(p.time).getTime(), p[field]])
        }));

        const opts = {
            chart: {
                type: 'line',
                height: 220,
                animations: { enabled: false },
                toolbar: { show: false },
                zoom: { enabled: false },
            },
            series: series,
            colors: COLORS.slice(0, series.length),
            stroke: { width: 2, curve: 'smooth' },
            xaxis: {
                type: 'datetime',
                labels: { style: { colors: '#9ca3af' }, datetimeUTC: false },
            },
            yaxis: {
                labels: { style: { colors: '#9ca3af' }, formatter: v => v != null ? v.toFixed(1) : '' },
                title: { text: unit, style: { color: '#9ca3af' } }
            },
            grid: { borderColor: '#374151' },
            tooltip: { theme: 'dark', x: { format: 'HH:mm:ss' } },
            legend: { labels: { colors: '#9ca3af' } },
        };

        if (charts[containerId]) {
            charts[containerId].updateOptions(opts, true, false);
        } else {
            charts[containerId] = new ApexCharts(el, opts);
            charts[containerId].render();
        }
    }

    window.cmCharts = { init, destroy };
})();
