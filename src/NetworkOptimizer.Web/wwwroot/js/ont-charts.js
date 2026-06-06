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
        document.querySelectorAll('#ont-charts-container .time-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                document.querySelectorAll('#ont-charts-container .time-btn').forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
                rangeHours = parseInt(btn.dataset.hours);
                customFrom = null;
                customTo = null;
                fetchAndRender();
            });
        });
    }

    function setupVisibility() {
        const el = document.getElementById('ont-charts-container');
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
            let url = `/api/monitoring/ont-chart?rangeHours=${rangeHours}`;
            if (customFrom && customTo) {
                url = `/api/monitoring/ont-chart?from=${customFrom}&to=${customTo}`;
            }
            if (soloDevice) url += `&ontId=${soloDevice}`;

            const resp = await fetch(url);
            if (!resp.ok) return;
            const json = await resp.json();
            renderCharts(json.devices || []);
        } catch (_) { }
    }

    function renderCharts(devices) {
        renderDualChart('ont-power-chart', devices, 'rx', 'tx', 'RX / TX Power (dBm)', 'dBm');
        renderChart('ont-temp-chart', devices, 'temp', 'Temperature (C)', 'C');
        renderChart('ont-olt-rx-chart', devices, 'oltRx', 'OLT RX Power (dBm)', 'dBm');
    }

    function renderDualChart(containerId, devices, field1, field2, title, unit) {
        const el = document.getElementById(containerId);
        if (!el) return;

        const series = [];
        devices.forEach((dev, i) => {
            series.push({
                name: dev.label + ' RX',
                data: dev.data.map(p => [new Date(p.time).getTime(), p[field1]])
            });
            series.push({
                name: dev.label + ' TX',
                data: dev.data.map(p => [new Date(p.time).getTime(), p[field2]])
            });
        });

        const opts = chartOpts(series, unit, 280);

        if (charts[containerId]) {
            charts[containerId].updateOptions(opts, true, false);
        } else {
            charts[containerId] = new ApexCharts(el, opts);
            charts[containerId].render();
        }
    }

    function renderChart(containerId, devices, field, title, unit) {
        const el = document.getElementById(containerId);
        if (!el) return;

        const series = devices.map((dev, i) => ({
            name: dev.label,
            data: dev.data.map(p => [new Date(p.time).getTime(), p[field]])
        }));

        const opts = chartOpts(series, unit, 220);

        if (charts[containerId]) {
            charts[containerId].updateOptions(opts, true, false);
        } else {
            charts[containerId] = new ApexCharts(el, opts);
            charts[containerId].render();
        }
    }

    function chartOpts(series, unit, height) {
        return {
            chart: {
                type: 'line',
                height: height,
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
    }

    window.ontCharts = { init, destroy };
})();
