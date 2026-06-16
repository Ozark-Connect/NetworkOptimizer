// Latency & Packet Loss charts — pure JS ApexCharts, fed by /api/monitoring/chart-data.
// Mounted from Blazor the same way as lan-flow-map.js.
// TODO: Extract time-range controls (presets, shift arrows, custom range popover,
// filter badges, poll interval scaling) into a shared module so latency-charts,
// device-health-charts, and future chart sets share one implementation.

import ApexCharts from '/_content/Blazor-ApexCharts/js/apexcharts.esm.js';
import { computeStats, renderStatsTable as renderTable } from './chart-stats.js?v=2';

const PALETTE = window.Apex?.colors || ['#7EB26D', '#EAB839', '#6ED0E0', '#EF843C', '#E24D42', '#1F78C1'];
const _colorCache = {};
function hashColor(id) {
    if (_colorCache[id]) return _colorCache[id];
    let h = 0;
    for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) >>> 0;
    const used = new Set(Object.values(_colorCache));
    let idx = h % PALETTE.length;
    const start = idx;
    while (used.has(PALETTE[idx])) {
        idx = (idx + 1) % PALETTE.length;
        if (idx === start) break;
    }
    _colorCache[id] = PALETTE[idx];
    return PALETTE[idx];
}
const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s; return _esc.innerHTML; }
const POLL_INTERVALS = { 0: 5000, 1: 5000, 6: 10000, 24: 15000, 168: 30000, 720: 30000 };
const RANGE_MS = { 0: 15 * 60000, 1: 3600000, 6: 6 * 3600000, 24: 86400000, 168: 7 * 86400000, 720: 30 * 86400000 };

let rttChart = null;
let lossChart = null;
let wanRateChart = null;
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
let lastFetchData = null;
let savedState = null;

function baseChartOpts(type, yTitle, yFormatter, extraOpts) {
    return {
        chart: {
            type: type,
            height: type === 'area' ? 200 : 260,
            background: 'transparent',
            toolbar: { show: false },
            zoom: { enabled: !matchMedia('(pointer:coarse)').matches, type: 'x', allowMouseWheelZoom: false },
            events: { beforeZoom: (ctx, opts) => applyDragZoom(opts?.xaxis) },
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
                min: 0, max: v => Math.max(v * 1.1, 5),
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

function formatBps(v) {
    if (v == null || v < 1) return '0';
    if (v >= 1e9) return (v / 1e9).toFixed(1) + ' Gbps';
    if (v >= 1e6) return (v / 1e6).toFixed(1) + ' Mbps';
    if (v >= 1e3) return (v / 1e3).toFixed(0) + ' Kbps';
    return v.toFixed(0) + ' bps';
}

function buildWanRateOpts() {
    return baseChartOpts('area', 'Throughput',
        v => v != null ? formatBps(v) : '',
        {
            colors: ['#3b82f6', '#10b981'],
            fill: {
                type: 'gradient',
                gradient: { shadeIntensity: 0.3, opacityFrom: 0.3, opacityTo: 0.05 },
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

    if (!el._delegated) {
        el._delegated = true;
        el.addEventListener('click', (e) => {
            const btn = e.target.closest('button[data-target]');
            if (!btn) return;
            const tid = btn.dataset.target;

            if (e.ctrlKey || e.metaKey) {
                visibility[tid] = visibility[tid] === false ? undefined : false;
            } else {
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
            }

            updateChartVisibility();
            renderBadges(container);
            if (lastFetchData) renderStatsTable(container, false);
        });
    }
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

    targetMeta = data.targets.map(t => ({
        id: t.targetId,
        name: t.name,
        color: hashColor(t.name),
    }));

    const rttSeries = data.targets.map(t => ({
        name: t.name,
        color: hashColor(t.name),
        data: (t.rtt || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value })),
    }));

    const lossSeries = data.targets.map(t => ({
        name: t.name,
        color: hashColor(t.name),
        data: (t.loss || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value })),
    }));

    lastFetchData = data;

    if (rttChart) rttChart.updateSeries(rttSeries, false);
    if (lossChart) lossChart.updateSeries(lossSeries, false);

    updateChartVisibility();

    const container = document.getElementById(containerId);
    if (container) {
        renderBadges(container);
        renderStatsTable(container);
    }

    // WAN rate chart - show for non-Fabric categories
    const showWanRate = currentCategory !== 'Fabric';
    const wanCard = container?.querySelector('.latency-wan-rate-card');
    if (wanCard) wanCard.style.display = showWanRate ? '' : 'none';

    if (showWanRate && wanRateChart) {
        const timeParams = buildQueryParams().replace(/category=[^&]*&?/, '');
        try {
            const resp = await fetch(`/api/monitoring/wan-rate-chart?${timeParams}`, { credentials: 'same-origin' });
            if (resp.ok) {
                const wan = await resp.json();
                const dlSeries = (wan.download || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value }));
                const ulSeries = (wan.upload || []).map(p => ({ x: new Date(p.time).getTime(), y: p.value }));
                wanRateChart.updateSeries([
                    { name: 'Download', data: dlSeries },
                    { name: 'Upload', data: ulSeries }
                ], false);
            }
        } catch { }
    }
}


function fmtRtt(v) { return v != null ? v.toFixed(3) : '-'; }
function fmtLossColored(v, redAt, orangeAt, yellowAt, lightAt, subtleAt, decimals) {
    if (v == null) return '-';
    const s = v.toFixed(decimals) + '%';
    if (v >= redAt) return `<span style="color:var(--danger-color)">${s}</span>`;
    if (v >= orangeAt) return `<span style="color:var(--accent-color)">${s}</span>`;
    if (v >= yellowAt) return `<span style="color:var(--warning-color)">${s}</span>`;
    if (v >= lightAt) return `<span style="color:#d4c06a">${s}</span>`;
    if (v > subtleAt) return `<span style="color:#c8c4a8">${s}</span>`;
    return s;
}
function fmtLossMean(v) { return fmtLossColored(v, 1, 0.2, 0.05, 0.005, 0.0005, 3); }
function fmtLossMax(v) { return fmtLossColored(v, 5, 2, 0.5, 0.005, 0.005, 2); }

function renderStatsTable(container, showAll) {
    const el = container.querySelector('.latency-stats-table');
    const data = lastFetchData;
    if (!el || !data?.targets?.length) { if (el) el.innerHTML = ''; return; }

    const rows = data.targets.map(t => {
        const rttVals = (t.rtt || []).map(p => p.value).filter(v => v != null && v > 0);
        const lossVals = (t.loss || []).map(p => p.value).filter(v => v != null);
        const rtt = computeStats(rttVals);
        const loss = computeStats(lossVals);
        const meta = targetMeta.find(m => m.id === t.targetId);
        return { id: t.targetId, label: t.name, color: meta?.color || '#9ca3af',
            visible: meta && visibility[meta.id] !== false,
            values: [rtt?.mean, rtt?.min, rtt?.max, rtt?.p95, rtt?.p99, loss?.mean, loss?.max] };
    });

    renderTable(el, container, {
        nameHeader: 'Target', rows, showAllRows: showAll,
        columns: [
            { header: 'RTT Mean', format: fmtRtt }, { header: 'Min', format: fmtRtt }, { header: 'Max', format: fmtRtt },
            { header: 'P95', format: fmtRtt }, { header: 'P99', format: fmtRtt },
            { header: 'Loss Mean', format: fmtLossMean }, { header: 'Loss Max', format: fmtLossMax },
        ],
        filter: { meta: () => targetMeta, key: 'id', visibility: () => visibility,
            resetVisibility: () => { visibility = {}; },
            onChanged: (c) => { updateChartVisibility(); renderBadges(c); renderStatsTable(c, true); } },
    });
}

function isVisible() { return isInViewport; }

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
    syncPopoverInputs(container);
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
    } else {
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

// Grafana-style drag-select on a chart becomes a custom time window,
// synced to the range selector (custom-range button + popover inputs).
function applyDragZoom(xaxis) {
    const container = document.getElementById(containerId);
    if (container && xaxis && Number.isFinite(xaxis.min) && Number.isFinite(xaxis.max) && xaxis.min < xaxis.max) {
        customFrom = new Date(xaxis.min);
        customTo = new Date(xaxis.max);
        isCustomRange = true;
        windowOffset = 0;
        container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        syncPopoverInputs(container);
        updateCustomLabel(container);
        loadAndUpdate();
        startPoll();
    }
    // Cancel ApexCharts' client-side zoom; the refetch repaints the selected window
    return { xaxis: { min: undefined, max: undefined } };
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

    const wanRateEl = container.querySelector('.latency-wan-rate-chart');

    if (rttChart) { rttChart.destroy(); rttChart = null; }
    if (lossChart) { lossChart.destroy(); lossChart = null; }
    if (wanRateChart) { wanRateChart.destroy(); wanRateChart = null; }

    rttChart = new ApexCharts(rttEl, { ...buildRttOpts(), series: [], colors: PALETTE });
    lossChart = new ApexCharts(lossEl, { ...buildLossOpts(), series: [], colors: PALETTE });

    await rttChart.render();
    await lossChart.render();

    if (wanRateEl) {
        wanRateChart = new ApexCharts(wanRateEl, { ...buildWanRateOpts(), series: [] });
        await wanRateChart.render();
    }

    // Category buttons - preserve current time window when switching
    container.querySelectorAll('[data-category]').forEach(btn => {
        btn.addEventListener('click', () => {
            currentCategory = btn.dataset.category;
            container.querySelectorAll('[data-category]').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            visibility = {};
            loadAndUpdate();
            startPoll();
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

    await loadAndUpdate();
    startPoll();
}

export function navigateToTime(isoTimestamp, category) {
    if (!savedState) {
        savedState = { category: currentCategory, rangeHours: currentRangeHours,
            customFrom, customTo, isCustomRange, windowOffset, visibility: { ...visibility } };
    }
    const ts = new Date(isoTimestamp).getTime();
    const windowMs = 10 * 60000; // 10 min window centered on event
    customFrom = new Date(ts - windowMs);
    customTo = new Date(ts + windowMs);
    isCustomRange = true;
    windowOffset = 0;
    if (category) currentCategory = category;

    const container = document.getElementById(containerId);
    if (container) {
        container.querySelectorAll('[data-category]').forEach(b => {
            b.classList.toggle('active', b.dataset.category === currentCategory);
        });
        container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        container.querySelector('.custom-range-btn')?.classList.add('active');
        syncPopoverInputs(container);
        updateCustomLabel(container);
    }
    loadAndUpdate();
    startPoll();
}

export function restoreState() {
    if (!savedState) return;
    currentCategory = savedState.category;
    currentRangeHours = savedState.rangeHours;
    customFrom = savedState.customFrom;
    customTo = savedState.customTo;
    isCustomRange = savedState.isCustomRange;
    windowOffset = savedState.windowOffset;
    visibility = savedState.visibility;
    savedState = null;

    const container = document.getElementById(containerId);
    if (container) {
        container.querySelectorAll('[data-category]').forEach(b => {
            b.classList.toggle('active', b.dataset.category === currentCategory);
        });
        if (isCustomRange) {
            container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
            container.querySelector('.custom-range-btn')?.classList.add('active');
        } else {
            container.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
            const btn = container.querySelector(`[data-range="${currentRangeHours}"]`);
            if (btn) btn.classList.add('active');
            container.querySelector('.custom-range-btn')?.classList.remove('active');
        }
        syncPopoverInputs(container);
        updateCustomLabel(container);
    }
    loadAndUpdate();
    startPoll();
}

export function setCategory(cat) {
    currentCategory = cat;
    const container = document.getElementById(containerId);
    if (container) {
        container.querySelectorAll('[data-category]').forEach(b => {
            b.classList.toggle('active', b.dataset.category === cat);
        });
    }
    visibility = {};
    loadAndUpdate();
    startPoll();
}

export function soloTarget(targetId) {
    if (!targetMeta.length) return;
    const match = targetMeta.find(t => t.id === targetId);
    if (!match) return;
    targetMeta.forEach(t => { visibility[t.id] = t.id === targetId; });
    updateChartVisibility();
    const container = document.getElementById(containerId);
    if (container) {
        renderBadges(container);
        if (lastFetchData) renderStatsTable(container, false);
    }
}

export function unmount() {
    stopPoll();
    if (visibilityObserver) { visibilityObserver.disconnect(); visibilityObserver = null; }
    if (fetchController) { fetchController.abort(); fetchController = null; }
    if (rttChart) { rttChart.destroy(); rttChart = null; }
    if (lossChart) { lossChart.destroy(); lossChart = null; }
    if (wanRateChart) { wanRateChart.destroy(); wanRateChart = null; }
    containerId = null;
    targetMeta = [];
    visibility = {};
    currentCategory = 'Fabric';
    currentRangeHours = 1;
    windowOffset = 0;
    isCustomRange = false;
    customFrom = null;
    customTo = null;
    lastFetchData = null;
    savedState = null;
    isInViewport = true;
}
