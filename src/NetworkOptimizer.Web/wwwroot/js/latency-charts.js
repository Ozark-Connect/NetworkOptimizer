// Latency & Packet Loss charts — pure JS ApexCharts, fed by /api/monitoring/chart-data.
// Mounted from Blazor the same way as lan-flow-map.js.
// TODO: Extract time-range controls (presets, shift arrows, custom range popover,
// filter badges, poll interval scaling) into a shared module so latency-charts,
// device-health-charts, and future chart sets share one implementation.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';

const PALETTE = ['#2ba89a', '#3b82f6', '#a78bfa', '#ef5858', '#f59e0b', '#10b981'];
const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }
const POLL_INTERVALS = { 0: 5000, 1: 5000, 6: 10000, 24: 15000, 168: 30000, 720: 30000 };
const RANGE_MS = { 0: 15 * 60000, 1: 3600000, 6: 6 * 3600000, 24: 86400000, 168: 7 * 86400000, 720: 30 * 86400000 };

let rttChart = null;
let lossChart = null;
let pollTimer = null;
let currentCategory = 'Fabric';
let currentRangeHours = 1;
let customFrom = null;  // Date or null
let customTo = null;    // Date or null
let isCustomRange = false;
let windowOffset = 0;   // ms offset from "now" for shift arrows
let visibility = {};
let targetMeta = [];
let containerId = null;
let fetchController = null;
let visibilityObserver = null;
let isInViewport = true;
let isMapFullscreen = false;
let fsHandler = null;

function baseChartOpts(type, yTitle, yFormatter, extraOpts) {
    return {
        chart: {
            type: type,
            height: type === 'area' ? 200 : 260,
            background: 'transparent',
            toolbar: { show: false },
            zoom: { enabled: false },
            animations: { enabled: false },
        },
        stroke: { curve: 'smooth', width: 2 },
        markers: { size: 0 },
        dataLabels: { enabled: false },
        xaxis: {
            type: 'datetime',
            labels: {
                style: { colors: '#9ca3af' },
                datetimeUTC: false,
                datetimeFormatter: { hour: 'HH:mm', day: 'MMM dd' },
            },
        },
        yaxis: {
            min: 0,
            title: { text: yTitle, style: { color: '#9ca3af' } },
            labels: {
                style: { colors: '#9ca3af' },
                formatter: yFormatter,
            },
        },
        grid: { borderColor: '#374151', strokeDashArray: 3 },
        legend: { show: false },
        tooltip: {
            theme: 'dark',
            shared: true,
            x: { format: 'MMM dd, HH:mm:ss' },
        },
        noData: { text: 'No data in this time range', style: { color: '#64748b' } },
        ...extraOpts,
    };
}

function buildRttOpts() {
    return baseChartOpts('line', 'ms',
        v => v != null ? v.toFixed(1) : '');
}

function buildLossOpts() {
    return baseChartOpts('area', '% loss',
        v => v != null ? v.toFixed(1) + '%' : '',
        {
            yaxis: {
                min: 0, max: 100,
                title: { text: '% loss', style: { color: '#9ca3af' } },
                labels: {
                    style: { colors: '#9ca3af' },
                    formatter: v => v != null ? v.toFixed(1) + '%' : '',
                },
            },
            fill: {
                type: 'gradient',
                gradient: { shadeIntensity: 0.3, opacityFrom: 0.4, opacityTo: 0.05 },
            },
        });
}

function buildQueryParams() {
    let params = `category=${currentCategory}`;
    if (isCustomRange && customFrom && customTo) {
        params += `&from=${customFrom.toISOString()}&to=${customTo.toISOString()}`;
    } else {
        params += `&rangeHours=${currentRangeHours}`;
        if (windowOffset !== 0) {
            const now = Date.now();
            const rangeMs = RANGE_MS[currentRangeHours] || 3600000;
            const to = new Date(now + windowOffset);
            const from = new Date(to.getTime() - rangeMs);
            params = `category=${currentCategory}&from=${from.toISOString()}&to=${to.toISOString()}`;
        }
    }
    return params;
}

async function fetchData() {
    if (fetchController) fetchController.abort();
    fetchController = new AbortController();
    try {
        const resp = await fetch(
            `/api/monitoring/chart-data?${buildQueryParams()}`,
            { signal: fetchController.signal });
        if (!resp.ok) return null;
        return await resp.json();
    } catch (e) {
        if (e.name === 'AbortError') return null;
        return null;
    }
}

function renderBadges(container) {
    const el = container.querySelector('.latency-filter-badges');
    if (!el) return;
    if (targetMeta.length <= 1) { el.innerHTML = ''; return; }

    el.innerHTML = targetMeta.map(t => {
        const vis = visibility[t.id] !== false;
        return `<button class="wan-filter-badge ${vis ? 'active' : 'inactive'}" data-target="${t.id}">
            <span class="wan-badge-dot" style="background-color: ${t.color}"></span>
            <span>${escapeHtml(t.name)}</span>
        </button>`;
    }).join('');

    el.querySelectorAll('button').forEach(btn => {
        btn.addEventListener('click', () => {
            const tid = btn.dataset.target;
            const allVis = targetMeta.every(t => visibility[t.id] !== false);
            const onlyThis = visibility[tid] !== false
                && targetMeta.filter(t => t.id !== tid).every(t => visibility[t.id] === false);

            if (onlyThis) {
                visibility = {};
            } else if (allVis) {
                targetMeta.forEach(t => visibility[t.id] = t.id === tid);
            } else {
                visibility[tid] = visibility[tid] === false;
            }
            updateChartVisibility();
            renderBadges(container);
        });
    });
}

function updateChartVisibility() {
    if (!rttChart || !lossChart) return;
    targetMeta.forEach((t, i) => {
        const vis = visibility[t.id] !== false;
        if (vis) {
            rttChart.showSeries(t.name);
            lossChart.showSeries(t.name);
        } else {
            rttChart.hideSeries(t.name);
            lossChart.hideSeries(t.name);
        }
    });
}

async function loadAndUpdate() {
    const data = await fetchData();
    if (!data || !data.targets) return;

    targetMeta = data.targets.map((t, i) => ({
        id: t.targetId,
        name: t.name,
        color: PALETTE[i % PALETTE.length],
    }));

    const rttSeries = data.targets.map((t, i) => ({
        name: t.name,
        color: PALETTE[i % PALETTE.length],
        data: (t.rtt || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value })),
    }));

    const lossSeries = data.targets.map((t, i) => ({
        name: t.name,
        color: PALETTE[i % PALETTE.length],
        data: (t.loss || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value })),
    }));

    if (rttChart) rttChart.updateSeries(rttSeries, false);
    if (lossChart) lossChart.updateSeries(lossSeries, false);

    updateChartVisibility();

    const container = document.getElementById(containerId);
    if (container) renderBadges(container);
}

function isVisible() { return isInViewport && !isMapFullscreen; }

function startPoll() {
    stopPoll();
    if (windowOffset !== 0 || isCustomRange) return;
    if (!isVisible()) return;
    const interval = POLL_INTERVALS[currentRangeHours] || 30000;
    pollTimer = setInterval(loadAndUpdate, interval);
}

function stopPoll() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
}

function selectPresetRange(container, hours) {
    currentRangeHours = hours;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
    const btn = container.querySelector(`[data-range="${hours}"]`);
    if (btn) btn.classList.add('active');
    container.querySelector('.custom-range-btn')?.classList.remove('active');
    updateCustomLabel(container);
    loadAndUpdate();
    startPoll();
}

function shiftWindow(direction) {
    const rangeMs = isCustomRange && customFrom && customTo
        ? customTo.getTime() - customFrom.getTime()
        : RANGE_MS[currentRangeHours] || 3600000;
    const shiftMs = rangeMs * 0.5;

    if (isCustomRange && customFrom && customTo) {
        const delta = direction === 'back' ? -shiftMs : shiftMs;
        customFrom = new Date(customFrom.getTime() + delta);
        customTo = new Date(customTo.getTime() + delta);
    } else {
        windowOffset += direction === 'back' ? -shiftMs : shiftMs;
        if (windowOffset > 0) windowOffset = 0;
    }

    const container = document.getElementById(containerId);
    if (container) {
        syncPopoverInputs(container);
        updateCustomLabel(container);
    }

    loadAndUpdate();
    startPoll();
}

function syncPopoverInputs(container) {
    const fromInput = container.querySelector('[data-input="from"]');
    const toInput = container.querySelector('[data-input="to"]');
    if (!fromInput || !toInput) return;

    if (isCustomRange && customFrom && customTo) {
        fromInput.value = toLocalDatetimeString(customFrom);
        toInput.value = toLocalDatetimeString(customTo);
    } else if (windowOffset !== 0) {
        const now = Date.now();
        const rangeMs = RANGE_MS[currentRangeHours] || 3600000;
        const to = new Date(now + windowOffset);
        const from = new Date(to.getTime() - rangeMs);
        fromInput.value = toLocalDatetimeString(from);
        toInput.value = toLocalDatetimeString(to);
    }
}

function toLocalDatetimeString(d) {
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function updateCustomLabel(container) {
    const btn = container.querySelector('.custom-range-btn');
    if (!btn) return;
    const label = btn.querySelector('.custom-range-label');
    if (label) label.remove();

    const active = isCustomRange || windowOffset !== 0;
    let clearBtn = btn.querySelector('.custom-range-clear');
    if (active) {
        btn.classList.add('active');
        const from = getEffectiveFrom();
        const to = getEffectiveTo();
        if (from && to) {
            const fmt = d => d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
            btn.setAttribute('data-tooltip', `${fmt(from)} - ${fmt(to)}`);
        }
        if (!clearBtn) {
            clearBtn = document.createElement('span');
            clearBtn.className = 'custom-range-clear';
            clearBtn.textContent = '×';
            clearBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                const ctr = document.getElementById(containerId);
                if (ctr) selectPresetRange(ctr, currentRangeHours);
            });
            btn.appendChild(clearBtn);
        }
    } else {
        btn.classList.remove('active');
        btn.setAttribute('data-tooltip', 'Custom date range');
        if (clearBtn) clearBtn.remove();
    }
}

function getEffectiveFrom() {
    if (isCustomRange && customFrom) return customFrom;
    if (windowOffset !== 0) {
        const rangeMs = RANGE_MS[currentRangeHours] || 3600000;
        return new Date(Date.now() + windowOffset - rangeMs);
    }
    return null;
}

function getEffectiveTo() {
    if (isCustomRange && customTo) return customTo;
    if (windowOffset !== 0) return new Date(Date.now() + windowOffset);
    return null;
}

export async function mount(elId) {
    containerId = elId;
    const container = document.getElementById(elId);
    if (!container) return;

    const rttEl = container.querySelector('.latency-rtt-chart');
    const lossEl = container.querySelector('.latency-loss-chart');
    if (!rttEl || !lossEl) return;

    if (rttChart) { rttChart.destroy(); rttChart = null; }
    if (lossChart) { lossChart.destroy(); lossChart = null; }

    rttChart = new ApexCharts(rttEl, { ...buildRttOpts(), series: [], colors: PALETTE });
    lossChart = new ApexCharts(lossEl, { ...buildLossOpts(), series: [], colors: PALETTE });

    await rttChart.render();
    await lossChart.render();

    // Category buttons
    container.querySelectorAll('[data-category]').forEach(btn => {
        btn.addEventListener('click', () => {
            currentCategory = btn.dataset.category;
            container.querySelectorAll('[data-category]').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            visibility = {};
            windowOffset = 0;
            isCustomRange = false;
            selectPresetRange(container, currentRangeHours);
        });
    });

    // Preset range buttons
    container.querySelectorAll('[data-range]').forEach(btn => {
        btn.addEventListener('click', () => selectPresetRange(container, parseInt(btn.dataset.range)));
    });

    // Shift arrows
    container.querySelectorAll('[data-shift]').forEach(btn => {
        btn.addEventListener('click', () => shiftWindow(btn.dataset.shift));
    });

    // Custom range popover
    const popover = container.querySelector('[data-popover="custom-range"]');
    const fromInput = container.querySelector('[data-input="from"]');
    const toInput = container.querySelector('[data-input="to"]');

    container.querySelector('[data-action="custom-range"]')?.addEventListener('click', () => {
        const now = new Date();
        const rangeMs = RANGE_MS[currentRangeHours] || 3600000;
        if (!fromInput.value) fromInput.value = toLocalDatetimeString(new Date(now.getTime() - rangeMs));
        if (!toInput.value) toInput.value = toLocalDatetimeString(now);
        popover?.classList.toggle('open');
    });

    container.querySelector('[data-action="cancel-custom"]')?.addEventListener('click', () => {
        popover?.classList.remove('open');
    });

    document.addEventListener('click', (e) => {
        if (!popover?.classList.contains('open')) return;
        const customBtn = container.querySelector('[data-action="custom-range"]');
        if (popover.contains(e.target) || customBtn?.contains(e.target)) return;
        popover.classList.remove('open');
    });

    container.querySelector('[data-action="apply-custom"]')?.addEventListener('click', () => {
        const from = fromInput?.value ? new Date(fromInput.value) : null;
        const to = toInput?.value ? new Date(toInput.value) : null;
        if (!from || !to || isNaN(from) || isNaN(to) || from >= to) return;
        customFrom = from;
        customTo = to;
        isCustomRange = true;
        windowOffset = 0;
        container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        container.querySelector('.custom-range-btn')?.classList.add('active');
        popover?.classList.remove('open');
        updateCustomLabel(container);
        loadAndUpdate();
        startPoll();
    });

    visibilityObserver = new IntersectionObserver(([entry]) => {
        const was = isVisible();
        isInViewport = entry.isIntersecting;
        if (isVisible() && !was) { loadAndUpdate(); startPoll(); }
        else if (!isVisible() && was) { stopPoll(); }
    }, { threshold: 0 });
    visibilityObserver.observe(container);

    fsHandler = (e) => {
        const was = isVisible();
        isMapFullscreen = e.detail.fullscreen;
        if (isVisible() && !was) { loadAndUpdate(); startPoll(); }
        else if (!isVisible() && was) { stopPoll(); }
    };
    document.addEventListener('lanflowmap-fullscreen', fsHandler);

    await loadAndUpdate();
    startPoll();
}

export function unmount() {
    stopPoll();
    if (visibilityObserver) { visibilityObserver.disconnect(); visibilityObserver = null; }
    if (fsHandler) { document.removeEventListener('lanflowmap-fullscreen', fsHandler); fsHandler = null; }
    if (fetchController) { fetchController.abort(); fetchController = null; }
    if (rttChart) { rttChart.destroy(); rttChart = null; }
    if (lossChart) { lossChart.destroy(); lossChart = null; }
    containerId = null;
    targetMeta = [];
    visibility = {};
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    isInViewport = true;
    isMapFullscreen = false;
}
