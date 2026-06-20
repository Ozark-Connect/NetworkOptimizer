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
let paused = false;        // timeline paused on the live edge (distinct from historic scrub)
let tabsEl = null;
let activeTab = 'infra';   // 'infra' = gateways + switches, 'aps' = access points

const STORE_TAB = 'portStats.activeTab';
const STORE_VIS = 'portStats.visibility';

function savePrefs() {
    try {
        localStorage.setItem(STORE_TAB, activeTab);
        localStorage.setItem(STORE_VIS, JSON.stringify(visibility));
    } catch { /* localStorage unavailable */ }
}

function loadPrefs() {
    try {
        const t = localStorage.getItem(STORE_TAB);
        if (t === 'infra' || t === 'aps') activeTab = t;
        const v = JSON.parse(localStorage.getItem(STORE_VIS) || '{}');
        if (v && typeof v === 'object') visibility = v;
    } catch { /* localStorage unavailable */ }
}

function speedClassMbps(mbps) {
    if (mbps == null || mbps <= 0) return '';
    let cls = SPEED_STEPS[0][1];
    for (const [step, c] of SPEED_STEPS) if (mbps >= step * 0.9) cls = c;
    return 'port-speed-' + cls;
}

function fmtLinkSpeed(mbps) {
    if (mbps == null || mbps <= 0) return '';
    if (mbps >= 1000) { const g = mbps / 1000; return `${g % 1 === 0 ? g.toFixed(0) : g.toFixed(1)} Gbps`; }
    return `${mbps.toFixed(0)} Mbps`;
}

function fmtRate(bps) {
    if (bps == null) return '-';
    if (bps >= 1e9) return `${(bps / 1e9).toFixed(2)} Gbps`;
    if (bps >= 1e6) return `${(bps / 1e6).toFixed(1)} Mbps`;
    if (bps >= 1e3) return `${(bps / 1e3).toFixed(0)} Kbps`;
    return `${Math.round(bps)} bps`;
}

const fmtCount = v => v == null ? '-' : Number(v).toLocaleString();

// UniFi PortTable.SfpFound is authoritative; fall back to a name heuristic only
// when the correlation hasn't populated isSfp (e.g. virtual interfaces).
function portIsSfp(p) {
    if (p.isSfp === true) return true;
    if (p.isSfp === false) return false;
    return `${p.ifName || ''} ${p.portId || ''}`.toLowerCase().includes('sfp');
}

// Connector glyph (RJ45 jack or SFP cage), speed-coloured. When the port has a
// number it is rendered on the connector face like a labelled patch panel;
// numberless virtual interfaces keep the detailed RJ45 pins / SFP cage.
function connectorGlyph(p) {
    const sfp = portIsSfp(p);
    const n = p.portNumber;
    const head = '<svg width="28" height="26" viewBox="0 0 28 26" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round">';
    const num = (cy) => `<text x="14" y="${cy}" text-anchor="middle" dominant-baseline="central" font-size="${String(n).length > 1 ? 9 : 11}" font-weight="700" fill="currentColor" stroke="none">${n}</text>`;
    if (sfp) {
        // SFP/SFP+ cage: wider than the RJ45 and tall enough to seat the port number,
        // vertically centred, with the bottom key notch.
        const body = '<rect x="2.5" y="5" width="23" height="16" rx="1.5"/><path d="M11 21 v-2.5 h6 v2.5"/>';
        return head + body + (n != null ? num(12) : '') + '</svg>';
    }
    // RJ45 8P8C jack: wider than tall (between the female receptacle and the plug),
    // with the latch-tab slot on top (how UniFi mounts them).
    const body = '<rect x="3.5" y="6" width="21" height="15" rx="1.5"/><path d="M10.5 6 v-4 h7 V6"/>';
    const detail = n != null
        ? num(13.5)
        : '<path d="M8 14.5 v4.5 M11 14.5 v4.5 M14 14.5 v4.5 M17 14.5 v4.5 M20 14.5 v4.5"/>';  // RJ45 pins, bottom (opposite the top tab), centred on x=14
    return head + body + detail + '</svg>';
}

function portIcon(p) {
    const cls = speedClassMbps(p.linkSpeedMbps);
    const down = p.operStatus != null && p.operStatus !== 1;
    // The glyph colour already conveys link state, so the tooltip carries the
    // detail: "Status - Speed" (e.g. "Up - 1 Gbps", or just "Down").
    const status = p.operStatus == null ? '' : (down ? 'Down' : 'Up');
    const tip = [status, fmtLinkSpeed(p.linkSpeedMbps)].filter(Boolean).join(' - ');
    return `<span class="port-icon ${cls}${down ? ' port-icon-down' : ''}"${tip ? ` data-tooltip="${escapeHtml(tip)}"` : ''}>${connectorGlyph(p)}</span>`;
}

function portCell(p) {
    // Port number rides on the connector glyph; the label is just the UniFi
    // friendly name (mapped from the Linux ifName), or the raw name as fallback.
    const name = p.friendlyName || p.ifName || (p.portId ? `Port ${p.portId}` : '');
    return `<span class="port-cell">${portIcon(p)}<span class="port-label">${escapeHtml(name)}</span></span>`;
}

const COLUMNS = [
    { header: 'Port', format: v => v.html },
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

function isApDevice(d) { return (d.type || '').toLowerCase() === 'ap'; }

// Devices shown under the current tab: APs on the "APs" tab, everything else
// (gateways, switches, unknown) on the "Gateways & Switches" tab.
function tabDevices() {
    return lastDevices.filter(d => activeTab === 'aps' ? isApDevice(d) : !isApDevice(d));
}

function renderTabs() {
    if (!tabsEl) return;
    const hasAps = lastDevices.some(isApDevice);
    const hasInfra = lastDevices.some(d => !isApDevice(d));
    // Only worth splitting when both classes are present.
    if (!(hasAps && hasInfra)) { tabsEl.innerHTML = ''; return; }
    const tabs = [['infra', 'Gateways & Switches'], ['aps', 'APs']];
    tabsEl.innerHTML = tabs.map(([k, label]) =>
        `<button class="time-btn ${activeTab === k ? 'active' : ''}" data-tab="${k}">${label}</button>`).join('');
    if (!tabsEl._delegated) {
        tabsEl._delegated = true;
        tabsEl.addEventListener('click', (e) => {
            const btn = e.target.closest('button[data-tab]');
            if (!btn || btn.dataset.tab === activeTab) return;
            activeTab = btn.dataset.tab;
            savePrefs();
            rebuildMeta(tabDevices());
            renderTabs();
            renderBadges();
            renderTableNow();
        });
    }
}

// Down, unnamed tunnel/shaping root interfaces (gre0, ifb1, ip6gre0) are noise -
// hide them. Named or up ones (e.g. gre1 = a WAN, ifbeth0 = SQM) still show.
const NOISE_IF = /^(gre|ifb|ip6gre)\d+$/i;

function buildRows() {
    const rows = [];
    for (const d of tabDevices()) {
        const vis = visibility[d.mac] !== false;
        for (const p of (d.ports || [])) {
            const down = p.operStatus !== 1;
            if (down && !p.friendlyName && NOISE_IF.test(p.ifName || '')) continue;
            rows.push({
                // Row id is the device mac so the name-column click filter (and pills)
                // toggle the whole device; multiple port rows share the same id.
                id: d.mac,
                label: d.name || d.mac,
                color: hashColor(d.name || d.mac),
                visible: vis,
                values: [
                    { html: portCell(p) },
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
        // Colour by device name (same determinant as the Device Stats / Network
        // Performance tabs) so a device keeps one colour across the app.
        return { mac: d.mac, name, color: hashColor(d.name || d.mac) };
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
            savePrefs();
            renderBadges();
            renderTableNow(false);
        });
    }
}

function renderTableNow(showAll) {
    if (!tableEl) return;
    const rows = buildRows();
    if (rows.length === 0) { tableEl.innerHTML = ''; return; }
    renderTable(tableEl, container, {
        nameHeader: 'Device', title: '', rows, columns: COLUMNS, showAllRows: showAll,
        // Clicking a device name in the table filters that device, mirroring the pills.
        filter: {
            meta: () => deviceMeta,
            key: 'mac',
            visibility: () => visibility,
            resetVisibility: () => { visibility = {}; },
            onChanged: () => { savePrefs(); renderBadges(); renderTableNow(true); },
        },
    });
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
    rebuildMeta(tabDevices());
    if (pendingSelect) {
        const match = deviceMeta.find(d => d.mac.toLowerCase() === pendingSelect.toLowerCase());
        if (match) { deviceMeta.forEach(d => visibility[d.mac] = d.mac === match.mac); savePrefs(); }
        pendingSelect = null;
    }
    updateCardVisibility();
    renderTabs();
    renderBadges();
    renderTableNow();
}

function startPoll() {
    stopPoll();
    if (currentAt) return;            // historic playback: no polling
    if (paused) return;               // timeline paused on the live edge
    if (!isVisibleInViewport) return;
    pollTimer = setInterval(loadAndRender, LIVE_POLL_MS);
}
function stopPoll() { if (pollTimer) { clearInterval(pollTimer); pollTimer = null; } }

function setupObserver() {
    if (!('IntersectionObserver' in window) || !container) { isVisibleInViewport = true; return; }
    io = new IntersectionObserver((entries) => {
        isVisibleInViewport = entries.some(e => e.isIntersecting);
        if (isVisibleInViewport && !currentAt && !paused) startPoll();
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
            // Back at the live edge: refresh + resume polling unless paused.
            loadAndRender();
            startPoll();
        }
    },
    // Timeline paused/resumed on the live edge (mirrors the WAN live chart). Historic
    // scrubbing is handled by seekTime; pause only gates live auto-refresh.
    pause() {
        paused = true;
        stopPoll();
    },
    resume() {
        paused = false;
        if (!currentAt) { loadAndRender(); startPoll(); }
    },
    selectDevice(mac) {
        if (!mac) return;
        // Map double-click only fires for switches/gateways, so land on that tab.
        activeTab = 'infra';
        rebuildMeta(tabDevices());
        renderTabs();
        const match = deviceMeta.find(d => d.mac.toLowerCase() === mac.toLowerCase());
        if (match) {
            deviceMeta.forEach(d => visibility[d.mac] = d.mac === match.mac);
            renderBadges();
            renderTableNow();
        } else {
            pendingSelect = mac;
            loadAndRender();
        }
        savePrefs();
        if (typeof opts.onDeviceSelected === 'function') opts.onDeviceSelected(mac);
    },
    updateDeviceMeta(meta) {
        if (!Array.isArray(meta)) return;
        for (const d of meta) if (d && d.mac && d.name) nameOverrides[d.mac] = d.name;
        rebuildMeta(tabDevices());
        renderBadges();
    },
    unmount() {
        stopPoll();
        clearTimeout(seekDebounce);
        if (fetchController) fetchController.abort();
        if (io) { io.disconnect(); io = null; }
        if (window.__portStatsTable === api) window.__portStatsTable = null;
        container = badgesEl = tableEl = tabsEl = null;
        lastDevices = [];
        deviceMeta = [];
        visibility = {};
    },
};

export function mount(el, mountOpts = {}) {
    container = typeof el === 'string' ? document.getElementById(el) : el;
    if (!container) return;
    opts = mountOpts;
    loadPrefs();
    badgesEl = container.querySelector('#port-stats-filter-badges') || container.querySelector('.health-filter-badges');
    tableEl = container.querySelector('#port-stats-table');
    tabsEl = container.querySelector('#port-stats-tabs');
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
