// Port playback table for the Live View tab. Reads per-port interface_counters
// at the current map scrubber position (or live), renders them via the shared
// renderStatsTable(), and exposes selectDevice() so a map double-click can
// isolate a single switch/gateway.
import { renderStatsTable as renderTable } from './chart-stats.js?v=3';

const _esc = document.createElement('span');
function escapeHtml(s) { _esc.textContent = s == null ? '' : String(s); return _esc.innerHTML; }

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

const LIVE_POLL_MS = 5000;

// Standard negotiated rates (Mbps) mapped to the link-speed colour spectrum.
const SPEED_STEPS = [
    [10, '10m', '10M'], [100, '100m', '100M'], [1000, '1g', '1G'], [2500, '2_5g', '2.5G'],
    [5000, '5g', '5G'], [10000, '10g', '10G'], [20000, '20g', '20G'], [25000, '25g', '25G'],
    [40000, '40g', '40G'], [50000, '50g', '50G'], [100000, '100g', '100G'],
];

let container = null;
let badgesEl = null;
let tableEl = null;
let legendEl = null;
let opts = {};

let deviceMeta = [];        // [{ mac, name, color }]
let visibility = {};        // mac -> false hides the device
let nameOverrides = {};     // mac -> name supplied by the map snapshot
let lastDevices = [];       // raw devices from the most recent fetch
let pendingSelect = null;   // mac to isolate once data arrives

let currentAt = null;       // ISO timestamp for historic playback, null = live
let pollTimer = null;
let fetchController = null;
let seekDebounce = null;
let io = null;
let isVisibleInViewport = true;

function speedClass(speedBps) {
    if (speedBps == null) return '';
    const m = speedBps / 1e6;
    let cls = SPEED_STEPS[0][1];
    for (const [mbps, c] of SPEED_STEPS) if (m >= mbps * 0.9) cls = c;
    return 'port-speed-' + cls;
}

function fmtLinkSpeed(bps) {
    if (bps == null) return '';
    const m = bps / 1e6;
    if (m >= 1000) { const g = m / 1000; return `${g % 1 === 0 ? g.toFixed(0) : g.toFixed(1)} Gbps`; }
    if (m >= 1) return `${m.toFixed(0)} Mbps`;
    return `${Math.round(bps / 1e3)} Kbps`;
}

function fmtRate(bps) {
    if (bps == null) return '-';
    if (bps >= 1e9) return `${(bps / 1e9).toFixed(2)} Gbps`;
    if (bps >= 1e6) return `${(bps / 1e6).toFixed(1)} Mbps`;
    if (bps >= 1e3) return `${(bps / 1e3).toFixed(0)} Kbps`;
    return `${Math.round(bps)} bps`;
}

const fmtCount = v => v == null ? '-' : Number(v).toLocaleString();

function isSfp(p) {
    const n = `${p.ifName || ''} ${p.portId || ''}`.toLowerCase();
    if (n.includes('sfp')) return true;
    return (p.speedBps || 0) >= 10e9;  // 10G+ uplinks are typically SFP+
}

const RJ45_SVG = '<svg width="20" height="15" viewBox="0 0 20 15" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round">' +
    '<rect x="3" y="2" width="14" height="8" rx="1"/>' +
    '<path d="M7.5 10 v2.5 h5 V10"/>' +
    '<path d="M6 4 v3 M8 4 v3 M10 4 v3 M12 4 v3 M14 4 v3"/></svg>';

const SFP_SVG = '<svg width="22" height="15" viewBox="0 0 22 15" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round" stroke-linecap="round">' +
    '<rect x="7" y="3" width="12" height="9" rx="1.5"/>' +
    '<path d="M7 5 C3 5 3 10 7 10"/>' +
    '<path d="M11 3 V12"/>' +
    '<path d="M14 7.5 H17.5"/></svg>';

function portIcon(p) {
    const cls = speedClass(p.speedBps);
    const down = p.operStatus != null && p.operStatus !== 1;
    const inner = isSfp(p) ? SFP_SVG : RJ45_SVG;
    const tip = p.speedBps ? fmtLinkSpeed(p.speedBps) : (down ? 'Down' : '');
    return `<span class="port-icon ${cls}${down ? ' port-icon-down' : ''}"${tip ? ` data-tooltip="${escapeHtml(tip)}"` : ''}>${inner}</span>`;
}

function portCell(p) {
    const label = p.ifName || (p.portId ? `Port ${p.portId}` : '');
    return `<span class="port-cell">${portIcon(p)}<span class="port-label">${escapeHtml(label)}</span></span>`;
}

function statusBadge(operStatus) {
    if (operStatus == null) return '-';
    const up = operStatus === 1;
    return `<span class="port-status ${up ? 'port-status-up' : 'port-status-down'}">${up ? 'Up' : 'Down'}</span>`;
}

const COLUMNS = [
    { header: 'Port', format: v => v.html },
    { header: 'Status', format: statusBadge },
    { header: 'Rate In', format: fmtRate },
    { header: 'Rate Out', format: fmtRate },
    { header: 'Unicast In', format: fmtCount, cls: 'hide-mobile' },
    { header: 'Unicast Out', format: fmtCount, cls: 'hide-mobile' },
    { header: 'Multicast In', format: fmtCount, cls: 'hide-mobile' },
    { header: 'Multicast Out', format: fmtCount, cls: 'hide-mobile' },
    { header: 'Broadcast In', format: fmtCount, cls: 'hide-mobile' },
    { header: 'Broadcast Out', format: fmtCount, cls: 'hide-mobile' },
    { header: 'Errors In', format: fmtCount, cls: 'hide-mobile' },
    { header: 'Errors Out', format: fmtCount, cls: 'hide-mobile' },
    { header: 'Discards In', format: fmtCount, cls: 'hide-mobile' },
    { header: 'Discards Out', format: fmtCount, cls: 'hide-mobile' },
];

function buildRows() {
    const rows = [];
    for (const d of lastDevices) {
        const vis = visibility[d.mac] !== false;
        for (const p of (d.ports || [])) {
            rows.push({
                id: `${d.mac}|${p.ifName || p.portId}`,
                label: d.name || d.mac,
                color: hashColor(d.mac),
                visible: vis,
                values: [
                    { html: portCell(p) },
                    p.operStatus,
                    p.rateInBps, p.rateOutBps,
                    p.ucastPktsIn, p.ucastPktsOut,
                    p.mcastPktsIn, p.mcastPktsOut,
                    p.bcastPktsIn, p.bcastPktsOut,
                    p.errorsIn, p.errorsOut,
                    p.discardsIn, p.discardsOut,
                ],
            });
        }
    }
    return rows;
}

function rebuildMeta(devices) {
    deviceMeta = devices.map(d => {
        const name = (d.name && d.name !== d.mac) ? d.name : (nameOverrides[d.mac] || d.name || d.mac);
        return { mac: d.mac, name, color: hashColor(d.mac) };
    });
}

function renderBadges() {
    if (!badgesEl) return;
    if (deviceMeta.length <= 1) { badgesEl.innerHTML = ''; return; }
    badgesEl.innerHTML = deviceMeta.map(d => {
        const vis = visibility[d.mac] !== false;
        return `<button class="wan-filter-badge ${vis ? 'active' : 'inactive'}" data-mac="${escapeHtml(d.mac)}">
            <span class="wan-badge-dot" style="background-color: ${d.color}"></span>
            <span>${escapeHtml(d.name)}</span>
        </button>`;
    }).join('');
    if (!badgesEl._delegated) {
        badgesEl._delegated = true;
        badgesEl.addEventListener('click', (e) => {
            const btn = e.target.closest('button[data-mac]');
            if (!btn) return;
            const mac = btn.dataset.mac;
            if (e.ctrlKey || e.metaKey) {
                visibility[mac] = visibility[mac] === false ? undefined : false;
            } else {
                const allVis = deviceMeta.every(d => visibility[d.mac] !== false);
                const onlyThis = visibility[mac] !== false
                    && deviceMeta.filter(d => d.mac !== mac).every(d => visibility[d.mac] === false);
                if (onlyThis) visibility = {};
                else if (allVis) deviceMeta.forEach(d => visibility[d.mac] = d.mac === mac);
                else visibility[mac] = visibility[mac] === false;
            }
            renderBadges();
            renderTableNow();
        });
    }
}

function renderTableNow() {
    if (!tableEl) return;
    const rows = buildRows();
    if (rows.length === 0) { tableEl.innerHTML = ''; return; }
    renderTable(tableEl, container, { nameHeader: 'Device', title: '', rows, columns: COLUMNS });
}

function renderLegend() {
    if (!legendEl || legendEl._rendered) return;
    legendEl.innerHTML = SPEED_STEPS.map(([, cls, label]) =>
        `<span class="port-speed-key"><span class="port-speed-swatch port-speed-${cls}"></span>${label}</span>`).join('');
    legendEl._rendered = true;
}

function updateCardVisibility() {
    if (!container) return;
    container.style.display = lastDevices.length > 0 ? '' : 'none';
}

async function fetchData() {
    if (fetchController) fetchController.abort();
    fetchController = new AbortController();
    const params = new URLSearchParams();
    if (currentAt) params.set('at', currentAt);
    try {
        const resp = await fetch(`/api/monitoring/port-stats?${params.toString()}`, { signal: fetchController.signal });
        if (!resp.ok) return null;
        return await resp.json();
    } catch {
        return null;
    }
}

async function loadAndRender() {
    const data = await fetchData();
    if (!data) return;
    lastDevices = data.devices || [];
    rebuildMeta(lastDevices);
    if (pendingSelect) {
        const match = deviceMeta.find(d => d.mac.toLowerCase() === pendingSelect.toLowerCase());
        if (match) deviceMeta.forEach(d => visibility[d.mac] = d.mac === match.mac);
        pendingSelect = null;
    }
    updateCardVisibility();
    renderBadges();
    renderTableNow();
}

function startPoll() {
    stopPoll();
    if (currentAt) return;            // historic playback: no polling
    if (!isVisibleInViewport) return;
    pollTimer = setInterval(loadAndRender, LIVE_POLL_MS);
}
function stopPoll() { if (pollTimer) { clearInterval(pollTimer); pollTimer = null; } }

function setupObserver() {
    if (!('IntersectionObserver' in window) || !container) { isVisibleInViewport = true; return; }
    io = new IntersectionObserver((entries) => {
        isVisibleInViewport = entries.some(e => e.isIntersecting);
        if (isVisibleInViewport && !currentAt) startPoll();
        else stopPoll();
    }, { threshold: 0 });
    io.observe(container);
}

const api = {
    seekTime(isoTimestamp) {
        currentAt = isoTimestamp || null;
        if (currentAt) {
            stopPoll();
            clearTimeout(seekDebounce);
            seekDebounce = setTimeout(loadAndRender, 200);
        } else {
            loadAndRender();
            startPoll();
        }
    },
    selectDevice(mac) {
        if (!mac) return;
        const match = deviceMeta.find(d => d.mac.toLowerCase() === mac.toLowerCase());
        if (match) {
            deviceMeta.forEach(d => visibility[d.mac] = d.mac === match.mac);
            renderBadges();
            renderTableNow();
        } else {
            pendingSelect = mac;
            loadAndRender();
        }
        if (typeof opts.onDeviceSelected === 'function') opts.onDeviceSelected(mac);
    },
    updateDeviceMeta(meta) {
        if (!Array.isArray(meta)) return;
        for (const d of meta) if (d && d.mac && d.name) nameOverrides[d.mac] = d.name;
        rebuildMeta(lastDevices);
        renderBadges();
    },
    unmount() {
        stopPoll();
        clearTimeout(seekDebounce);
        if (fetchController) fetchController.abort();
        if (io) { io.disconnect(); io = null; }
        if (window.__portStatsTable === api) window.__portStatsTable = null;
        container = badgesEl = tableEl = null;
        lastDevices = [];
        deviceMeta = [];
        visibility = {};
    },
};

export function mount(el, mountOpts = {}) {
    container = typeof el === 'string' ? document.getElementById(el) : el;
    if (!container) return;
    opts = mountOpts;
    badgesEl = container.querySelector('#port-stats-filter-badges') || container.querySelector('.health-filter-badges');
    tableEl = container.querySelector('#port-stats-table');
    legendEl = container.querySelector('#port-stats-legend');
    renderLegend();
    if (Array.isArray(mountOpts.deviceMeta)) {
        for (const d of mountOpts.deviceMeta) if (d && d.mac && d.name) nameOverrides[d.mac] = d.name;
    }
    currentAt = null;
    isVisibleInViewport = true;
    window.__portStatsTable = api;
    setupObserver();
    loadAndRender();
    startPoll();
}

export const seekTime = (...a) => api.seekTime(...a);
export const selectDevice = (...a) => api.selectDevice(...a);
export const updateDeviceMeta = (...a) => api.updateDeviceMeta(...a);
export const unmount = () => api.unmount();
