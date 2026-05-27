// 2D hierarchical LAN topology diagram.
// Renders SVG consuming the same API endpoints as the 3D LAN Flow Map.
// Animated data-flow particles show real-time traffic direction and rate.

const NS = 'http://www.w3.org/2000/svg';

// ---- Color palette (matches 3D map) ----
const COLORS = {
    bg:           '#202023',
    gateway:      '#facc15',
    switchNode:   '#9aa6b2',
    ap:           '#3385d6',
    wiredClient:  '#c9d2e0',
    wifiClient:   '#e2e8f0',
    cloud:        '#4d556b',
    virtualHub:   '#6b7785',
    downstream:   '#3385d6',
    upstream:     '#24bc70',
    pipeCool:     '#1f4068',
    pipeWarm:     '#e79613',
    pipeHot:      '#ee6368',
    band24:       '#fbbf24',
    band5:        '#3b82f6',
    band6:        '#a855f7',
    text:         '#f1f5f9',
    textSec:      '#cbd5e1',
    textMuted:    '#9ca3af',
    labelBg:      'rgba(16,24,32,0.82)',
    cloudStroke:  '#3b82f6',
};

const NODE_KIND = {
    Gateway: 0, Switch: 1, AccessPoint: 2,
    WiredClient: 3, WifiClient: 4, Cloud: 5, VirtualHub: 6,
};
const LINK_KIND = {
    Uplink: 0, WiredClient: 1, WifiClient: 2,
    Wan: 3, Transit: 4, MeshBackhaul: 5,
};

// ---- Layout geometry ----
const L = {
    tierGap:       130,
    cloudGap:      90,
    infraGap:      80,
    clientGap:     6,
    clientSize:    18,
    clientCols:    12,
    maxClients:    60,
    infraW:        60,
    infraH:        48,
    cloudR:        28,
    cornerR:       12,
    padding:       60,
    pipeBase:      2,
    pipeMax:       5.5,
    particleR:     3,
    labelFont:     11,
    rateFont:      10,
    nameFont:      11,
};

const RATE_THRESHOLD = 1_000_000;

// ---- Utility ----

function svg(tag, attrs, parent) {
    const e = document.createElementNS(NS, tag);
    if (attrs) for (const [k, v] of Object.entries(attrs)) {
        if (v != null) e.setAttribute(k, String(v));
    }
    if (parent) parent.appendChild(e);
    return e;
}

function hexToRgb(h) {
    return [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
}
function rgbHex(r, g, b) {
    return '#' + [r, g, b].map(c => Math.round(Math.max(0, Math.min(255, c))).toString(16).padStart(2, '0')).join('');
}
function lerpColor(a, b, t) {
    const [r1, g1, b1] = hexToRgb(a), [r2, g2, b2] = hexToRgb(b);
    return rgbHex(r1 + (r2 - r1) * t, g1 + (g2 - g1) * t, b1 + (b2 - b1) * t);
}

function bandColor(band) {
    if (band === '2.4') return COLORS.band24;
    if (band === '5')   return COLORS.band5;
    if (band === '6')   return COLORS.band6;
    return null;
}

function nodeColor(kind, band) {
    switch (kind) {
        case NODE_KIND.Gateway:     return COLORS.gateway;
        case NODE_KIND.Switch:      return COLORS.switchNode;
        case NODE_KIND.AccessPoint: return COLORS.ap;
        case NODE_KIND.WiredClient: return COLORS.wiredClient;
        case NODE_KIND.WifiClient:  return bandColor(band) || COLORS.wifiClient;
        case NODE_KIND.Cloud:       return COLORS.cloud;
        case NODE_KIND.VirtualHub:  return COLORS.virtualHub;
        default: return COLORS.textMuted;
    }
}

function isInfra(kind) {
    return kind <= NODE_KIND.AccessPoint || kind === NODE_KIND.VirtualHub;
}
function isClient(kind) {
    return kind === NODE_KIND.WiredClient || kind === NODE_KIND.WifiClient;
}

function pipeColorForUtil(util, band) {
    const cool = bandColor(band) || COLORS.pipeCool;
    if (util < 0.7) return cool;
    if (util < 0.9) return lerpColor(cool, COLORS.pipeWarm, (util - 0.7) / 0.2);
    return lerpColor(COLORS.pipeWarm, COLORS.pipeHot, Math.min((util - 0.9) / 0.1, 1));
}

function pipeWidth(capacityBps) {
    if (!capacityBps || capacityBps <= 0) return L.pipeBase;
    const gbps = capacityBps / 1e9;
    const t = Math.log10(Math.max(gbps, 0.01)) + 2;
    return L.pipeBase + Math.min(t, 3.5) * 0.8;
}

function formatBps(bps) {
    if (!Number.isFinite(bps) || bps <= 0) return '';
    const units = ['bps', 'Kbps', 'Mbps', 'Gbps', 'Tbps'];
    let i = 0, v = bps;
    while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
    return `${v >= 100 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`;
}

function formatLinkSpeed(mbps) {
    if (!mbps) return '';
    if (mbps >= 1000) {
        const g = mbps / 1000;
        return `${g % 1 === 0 ? g.toFixed(0) : g.toFixed(1)} Gbps`;
    }
    return `${mbps} Mbps`;
}

// ---- Orthogonal path computation ----

function orthoPath(x1, y1, x2, y2) {
    const r = L.cornerR;
    if (Math.abs(x1 - x2) < 0.5) return `M${x1} ${y1}L${x2} ${y2}`;

    const midY = (y1 + y2) / 2;
    const dx = x2 - x1;
    const s = dx > 0 ? 1 : -1;
    const ax = Math.abs(dx);
    const hv = Math.abs(y2 - y1) / 2;
    const cr = Math.min(r, ax / 2, hv);
    if (cr < 1) return `M${x1} ${y1}L${x1} ${midY}L${x2} ${midY}L${x2} ${y2}`;

    return `M${x1} ${y1}`
        + `L${x1} ${midY - cr}`
        + `Q${x1} ${midY} ${x1 + s * cr} ${midY}`
        + `L${x2 - s * cr} ${midY}`
        + `Q${x2} ${midY} ${x2} ${midY + cr}`
        + `L${x2} ${y2}`;
}

function orthoLength(x1, y1, x2, y2) {
    if (Math.abs(x1 - x2) < 0.5) return Math.abs(y2 - y1);
    const ax = Math.abs(x2 - x1);
    const hv = Math.abs(y2 - y1) / 2;
    const cr = Math.min(L.cornerR, ax / 2, hv);
    if (cr < 1) return Math.abs(y2 - y1) + ax;
    return (hv - cr) * 2 + Math.PI * cr + (ax - 2 * cr);
}

function orthoPointAt(x1, y1, x2, y2, t) {
    const dy = y2 - y1;
    if (Math.abs(x2 - x1) < 0.5) return { x: x1, y: y1 + dy * t };

    const midY = (y1 + y2) / 2;
    const dx = x2 - x1;
    const s = dx > 0 ? 1 : -1;
    const ax = Math.abs(dx);
    const hv = Math.abs(dy) / 2;
    const cr = Math.min(L.cornerR, ax / 2, hv);
    if (cr < 1) {
        const s1 = hv, s2 = ax, total = s1 + s2 + hv;
        let d = t * total;
        if (d <= s1) return { x: x1, y: y1 + d };
        d -= s1;
        if (d <= s2) return { x: x1 + s * d, y: midY };
        d -= s2;
        return { x: x2, y: midY + d };
    }

    const s1 = hv - cr;
    const s2 = (Math.PI / 2) * cr;
    const s3 = ax - 2 * cr;
    const s4 = s2;
    const s5 = hv - cr;
    const total = s1 + s2 + s3 + s4 + s5;
    let d = t * total;

    if (d <= s1) return { x: x1, y: y1 + d };
    d -= s1;
    if (d <= s2) {
        const a = (d / s2) * (Math.PI / 2);
        return { x: x1 + s * cr * Math.sin(a), y: (midY - cr) + cr * (1 - Math.cos(a)) };
    }
    d -= s2;
    if (d <= s3) return { x: x1 + s * (cr + d), y: midY };
    d -= s3;
    if (d <= s4) {
        const a = (d / s4) * (Math.PI / 2);
        return {
            x: (x2 - s * cr) + s * cr * Math.sin(a),
            y: midY + cr * (1 - Math.cos(a)),
        };
    }
    d -= s4;
    return { x: x2, y: midY + cr + d };
}

// ---- Layout tree node ----

class TNode {
    constructor(data) {
        this.d = data;
        this.infraChildren = [];
        this.clientChildren = [];
        this.x = 0;
        this.y = 0;
        this.w = 0;
    }
}

// ---- Main class ----

class LanFlowMap2D {
    constructor(container) {
        this._el = container;
        this._svgRoot = null;
        this._gLinks = null;
        this._gNodes = null;
        this._gParticles = null;
        this._gLabels = null;

        this._snapshot = null;
        this._treeNodes = new Map();
        this._root = null;
        this._clouds = [];
        this._linkEdges = [];
        this._liveRates = {};

        this._particles = [];
        this._animId = 0;
        this._lastFrame = 0;
        this._pollId = 0;
    }

    async start() {
        this._createSvg();
        await this._loadSnapshot();
        this._startPolling();
        this._lastFrame = performance.now();
        this._animate();
    }

    dispose() {
        cancelAnimationFrame(this._animId);
        clearInterval(this._pollId);
        this._el.innerHTML = '';
        this._particles = [];
    }

    // ---- SVG skeleton ----

    _createSvg() {
        this._el.innerHTML = '';
        const s = svg('svg', {
            'xmlns': NS,
            'preserveAspectRatio': 'xMidYMin meet',
            'class': 'lan-flow-map-2d-svg',
        }, this._el);
        this._svgRoot = s;

        const defs = svg('defs', null, s);

        // Glow filter for infra nodes
        const glow = svg('filter', { id: 'node-glow', x: '-50%', y: '-50%', width: '200%', height: '200%' }, defs);
        svg('feGaussianBlur', { in: 'SourceGraphic', stdDeviation: '4', result: 'blur' }, glow);
        const merge = svg('feMerge', null, glow);
        svg('feMergeNode', { in: 'blur' }, merge);
        svg('feMergeNode', { in: 'SourceGraphic' }, merge);

        // Particle glow
        const pglow = svg('filter', { id: 'particle-glow', x: '-100%', y: '-100%', width: '300%', height: '300%' }, defs);
        svg('feGaussianBlur', { in: 'SourceGraphic', stdDeviation: '2', result: 'blur' }, pglow);
        const pm = svg('feMerge', null, pglow);
        svg('feMergeNode', { in: 'blur' }, pm);
        svg('feMergeNode', { in: 'SourceGraphic' }, pm);

        this._gLinks = svg('g', { class: 'links' }, s);
        this._gParticles = svg('g', { class: 'particles' }, s);
        this._gNodes = svg('g', { class: 'nodes' }, s);
        this._gLabels = svg('g', { class: 'labels' }, s);
    }

    // ---- Data loading ----

    async _loadSnapshot() {
        try {
            const r = await fetch('/api/monitoring/lan-flow-map/snapshot');
            if (!r.ok) return;
            this._snapshot = await r.json();
        } catch { return; }

        this._liveRates = this._snapshot.liveRates || {};
        this._buildLayout();
        this._renderAll();
    }

    async _pollLive() {
        try {
            const r = await fetch('/api/monitoring/lan-flow-map/live');
            if (!r.ok) return;
            const update = await r.json();
            if (update.linkRates) {
                Object.assign(this._liveRates, update.linkRates);
                this._updateRates();
            }
        } catch { /* swallow */ }
    }

    _startPolling() {
        this._pollId = setInterval(() => this._pollLive(), 3000);
    }

    // ---- Layout ----

    _buildLayout() {
        const snap = this._snapshot;
        if (!snap) return;

        const nodesById = new Map();
        for (const n of snap.nodes) nodesById.set(n.id, new TNode(n));
        this._treeNodes = nodesById;

        // Build parent-child tree
        let root = null;
        for (const [, tn] of nodesById) {
            if (tn.d.kind === NODE_KIND.Gateway) root = tn;
            if (tn.d.parentId && nodesById.has(tn.d.parentId)) {
                const parent = nodesById.get(tn.d.parentId);
                if (isClient(tn.d.kind)) {
                    parent.clientChildren.push(tn);
                } else if (tn.d.kind !== NODE_KIND.Cloud) {
                    parent.infraChildren.push(tn);
                }
            }
        }
        this._root = root;

        // Clouds
        this._clouds = (snap.clouds || []).map(c => ({ data: c, x: 0, y: 0 }));

        // Link edges
        this._linkEdges = [];
        for (const link of snap.links) {
            this._linkEdges.push({
                link,
                fromNode: nodesById.get(link.fromNodeId),
                toNode: nodesById.get(link.toNodeId),
            });
        }

        if (!root) return;
        this._computeWidths(root);
        const totalW = root.w;
        this._assignPositions(root, L.padding, 0);
        this._positionClouds();
    }

    _computeWidths(node) {
        // Infra children
        let infraW = 0;
        for (const child of node.infraChildren) {
            this._computeWidths(child);
            infraW += child.w;
        }
        if (node.infraChildren.length > 1)
            infraW += (node.infraChildren.length - 1) * L.infraGap;

        // Client grid
        const nc = Math.min(node.clientChildren.length, L.maxClients);
        const cols = Math.min(nc, L.clientCols);
        const clientGridW = cols > 0 ? cols * (L.clientSize + L.clientGap) - L.clientGap : 0;

        // Node's own min width
        const selfW = isClient(node.d.kind) ? L.clientSize : L.infraW + 40;

        // Combined: infra and client grids side by side
        let childrenW = 0;
        if (infraW > 0 && clientGridW > 0) {
            childrenW = infraW + L.infraGap + clientGridW;
        } else {
            childrenW = infraW + clientGridW;
        }

        node.w = Math.max(selfW, childrenW);
    }

    _assignPositions(node, leftX, depth) {
        const yBase = L.padding + 60;
        node.y = yBase + depth * L.tierGap;

        if (node.infraChildren.length === 0 && node.clientChildren.length === 0) {
            node.x = leftX + node.w / 2;
            return;
        }

        let infraW = node.infraChildren.reduce((s, c) => s + c.w, 0);
        if (node.infraChildren.length > 1)
            infraW += (node.infraChildren.length - 1) * L.infraGap;

        const nc = Math.min(node.clientChildren.length, L.maxClients);
        const cols = Math.min(nc, L.clientCols);
        const clientGridW = cols > 0 ? cols * (L.clientSize + L.clientGap) - L.clientGap : 0;

        let combinedW = 0;
        if (infraW > 0 && clientGridW > 0) combinedW = infraW + L.infraGap + clientGridW;
        else combinedW = infraW + clientGridW;

        let cursor = leftX + (node.w - combinedW) / 2;

        // Place infra children
        for (const child of node.infraChildren) {
            this._assignPositions(child, cursor, depth + 1);
            cursor += child.w + L.infraGap;
        }

        // Place clients in grid
        if (nc > 0) {
            const gridLeft = cursor - (infraW > 0 ? 0 : 0);
            const clientY = yBase + (depth + 1) * L.tierGap;
            for (let i = 0; i < nc; i++) {
                const col = i % cols;
                const row = Math.floor(i / cols);
                const cn = node.clientChildren[i];
                cn.x = gridLeft + col * (L.clientSize + L.clientGap) + L.clientSize / 2;
                cn.y = clientY + row * (L.clientSize + L.clientGap);
            }
        }

        // Center parent over all children
        const allKids = [
            ...node.infraChildren,
            ...node.clientChildren.slice(0, nc),
        ];
        if (allKids.length > 0) {
            const minX = Math.min(...allKids.map(c => c.x));
            const maxX = Math.max(...allKids.map(c => c.x));
            node.x = (minX + maxX) / 2;
        } else {
            node.x = leftX + node.w / 2;
        }
    }

    _positionClouds() {
        if (!this._root || this._clouds.length === 0) return;
        const gx = this._root.x;
        const gy = this._root.y;
        const total = this._clouds.length;
        const spacing = L.cloudGap;
        const startX = gx - ((total - 1) * spacing) / 2;

        for (let i = 0; i < total; i++) {
            this._clouds[i].x = startX + i * spacing;
            this._clouds[i].y = gy - L.tierGap;
        }
    }

    // ---- Rendering ----

    _renderAll() {
        this._gLinks.innerHTML = '';
        this._gNodes.innerHTML = '';
        this._gParticles.innerHTML = '';
        this._gLabels.innerHTML = '';
        this._particles = [];

        if (!this._root) {
            this._renderEmpty();
            return;
        }

        this._setViewBox();
        this._renderCloudLinks();
        this._renderTreeLinks(this._root);
        this._renderClouds();
        this._renderTreeNodes(this._root);
        this._updateRates();
    }

    _renderEmpty() {
        const t = svg('text', {
            x: '50%', y: '200',
            'text-anchor': 'middle',
            fill: COLORS.textMuted,
            'font-size': '14',
            'font-family': 'system-ui, sans-serif',
        }, this._svgRoot);
        t.textContent = 'Waiting for topology data...';
        this._svgRoot.setAttribute('viewBox', '0 0 800 400');
    }

    _setViewBox() {
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;

        const expand = (x, y, r) => {
            minX = Math.min(minX, x - r);
            minY = Math.min(minY, y - r);
            maxX = Math.max(maxX, x + r);
            maxY = Math.max(maxY, y + r);
        };

        const visit = (node) => {
            expand(node.x, node.y, L.infraW);
            for (const c of node.infraChildren) visit(c);
            for (const c of node.clientChildren.slice(0, L.maxClients))
                expand(c.x, c.y, L.clientSize);
        };
        visit(this._root);
        for (const c of this._clouds) expand(c.x, c.y, L.cloudR + 20);

        const pad = L.padding;
        const vx = minX - pad;
        const vy = minY - pad;
        const vw = (maxX - minX) + pad * 2;
        const vh = (maxY - minY) + pad * 2 + 30;
        this._svgRoot.setAttribute('viewBox', `${vx} ${vy} ${vw} ${vh}`);
    }

    // ---- Link rendering ----

    _renderCloudLinks() {
        if (!this._root) return;
        const gw = this._root;
        const gwTop = gw.y - L.infraH / 2;

        for (const cloud of this._clouds) {
            const cy = cloud.y + L.cloudR + 6;
            const linkData = this._linkEdges.find(e =>
                (e.link.kind === LINK_KIND.Wan || e.link.kind === LINK_KIND.Transit)
                && ((e.link.fromNodeId === cloud.data.id || e.link.toNodeId === cloud.data.id)
                    || (e.fromNode?.d.kind === NODE_KIND.Gateway && e.toNode?.d.kind === NODE_KIND.Cloud))
            );

            const pathD = orthoPath(cloud.x, cy, gw.x, gwTop);
            const pathEl = svg('path', {
                d: pathD,
                fill: 'none',
                stroke: COLORS.pipeCool,
                'stroke-width': linkData ? pipeWidth(linkData.link.capacityBps) : L.pipeBase,
                'stroke-linecap': 'round',
                opacity: '0.7',
            }, this._gLinks);

            if (linkData) {
                linkData._pathEl = pathEl;
                linkData._x1 = cloud.x; linkData._y1 = cy;
                linkData._x2 = gw.x; linkData._y2 = gwTop;
                linkData._isWan = true;
            }
        }

        // Transit links between clouds
        const sortedClouds = [...this._clouds].sort((a, b) => a.data.order - b.data.order);
        for (let i = 0; i < sortedClouds.length - 1; i++) {
            const a = sortedClouds[i], b = sortedClouds[i + 1];
            const edge = this._linkEdges.find(e =>
                e.link.kind === LINK_KIND.Transit
                && ((e.link.fromNodeId === a.data.id && e.link.toNodeId === b.data.id)
                    || (e.link.fromNodeId === b.data.id && e.link.toNodeId === a.data.id))
            );
            if (!edge) continue;

            const pathD = `M${a.x + L.cloudR + 4} ${a.y}L${b.x - L.cloudR - 4} ${b.y}`;
            const pathEl = svg('path', {
                d: pathD,
                fill: 'none',
                stroke: COLORS.pipeCool,
                'stroke-width': L.pipeBase,
                'stroke-linecap': 'round',
                'stroke-dasharray': '6 4',
                opacity: '0.5',
            }, this._gLinks);
            edge._pathEl = pathEl;
            edge._x1 = a.x + L.cloudR + 4; edge._y1 = a.y;
            edge._x2 = b.x - L.cloudR - 4; edge._y2 = b.y;
        }
    }

    _renderTreeLinks(node) {
        const parentBottom = node.y + L.infraH / 2;

        for (const child of node.infraChildren) {
            const childTop = child.y - L.infraH / 2;
            this._drawLink(node, child, parentBottom, childTop, false);
            this._renderTreeLinks(child);
        }

        for (const child of node.clientChildren.slice(0, L.maxClients)) {
            const childTop = child.y - L.clientSize / 2;
            this._drawLink(node, child, parentBottom, childTop, true);
        }
    }

    _drawLink(parent, child, y1, y2, isClientLink) {
        const edge = this._linkEdges.find(e =>
            (e.link.fromNodeId === parent.d.id && e.link.toNodeId === child.d.id)
            || (e.link.fromNodeId === child.d.id && e.link.toNodeId === parent.d.id)
        );

        const band = edge?.link.band;
        const coolColor = bandColor(band) || COLORS.pipeCool;

        const pathD = orthoPath(parent.x, y1, child.x, y2);
        const pathEl = svg('path', {
            d: pathD,
            fill: 'none',
            stroke: coolColor,
            'stroke-width': edge ? pipeWidth(edge.link.capacityBps) : L.pipeBase,
            'stroke-linecap': 'round',
            opacity: isClientLink ? '0.35' : '0.6',
        }, this._gLinks);

        if (edge) {
            edge._pathEl = pathEl;
            edge._x1 = parent.x; edge._y1 = y1;
            edge._x2 = child.x; edge._y2 = y2;
            edge._isClientLink = isClientLink;
            edge._band = band;
        }

        // Capacity label for infra links
        if (!isClientLink && edge?.link.capacityBps) {
            const cap = edge.link.capacityBps / 1e6;
            if (cap > 0) {
                const label = formatLinkSpeed(cap);
                const midX = (parent.x + child.x) / 2;
                const midY = (y1 + y2) / 2;
                this._renderCapLabel(midX, midY, label);
            }
        }
    }

    _renderCapLabel(x, y, text) {
        const g = svg('g', { transform: `translate(${x},${y})` }, this._gLabels);

        const tw = text.length * 5.5 + 12;
        svg('rect', {
            x: -tw / 2, y: -8,
            width: tw, height: 16,
            rx: 4,
            fill: COLORS.labelBg,
        }, g);

        const t = svg('text', {
            x: 0, y: 4,
            'text-anchor': 'middle',
            fill: COLORS.textMuted,
            'font-size': L.rateFont,
            'font-family': 'system-ui, sans-serif',
        }, g);
        t.textContent = text;
    }

    // ---- Cloud rendering ----

    _renderClouds() {
        for (const cloud of this._clouds) {
            const g = svg('g', { transform: `translate(${cloud.x},${cloud.y})` }, this._gNodes);
            const r = L.cloudR;

            // Wireframe globe
            svg('circle', { cx: 0, cy: 0, r, fill: 'none', stroke: COLORS.cloudStroke, 'stroke-width': 1.5, opacity: 0.8 }, g);
            svg('ellipse', { cx: 0, cy: 0, rx: r * 0.5, ry: r, fill: 'none', stroke: COLORS.cloudStroke, 'stroke-width': 1, opacity: 0.4 }, g);
            svg('ellipse', { cx: 0, cy: 0, rx: r, ry: r * 0.35, fill: 'none', stroke: COLORS.cloudStroke, 'stroke-width': 1, opacity: 0.4 }, g);
            svg('ellipse', { cx: 0, cy: 0, rx: r, ry: r * 0.65, fill: 'none', stroke: COLORS.cloudStroke, 'stroke-width': 0.7, opacity: 0.25 }, g);

            // Subtle fill
            svg('circle', { cx: 0, cy: 0, r: r - 1, fill: COLORS.cloudStroke, opacity: 0.06 }, g);

            // Name label below
            const name = cloud.data.asnName || cloud.data.name || 'WAN';
            const label = svg('text', {
                x: 0, y: r + 16,
                'text-anchor': 'middle',
                fill: COLORS.textSec,
                'font-size': L.nameFont,
                'font-family': 'system-ui, sans-serif',
                'font-weight': 500,
            }, g);
            label.textContent = name;

            // RTT badge
            cloud._rttEl = svg('text', {
                x: 0, y: r + 30,
                'text-anchor': 'middle',
                fill: COLORS.textMuted,
                'font-size': L.rateFont - 1,
                'font-family': 'system-ui, sans-serif',
            }, g);
            this._updateCloudBadge(cloud);
        }
    }

    _updateCloudBadge(cloud) {
        if (!cloud._rttEl) return;
        const d = cloud.data;
        let txt = '';
        if (d.rttAvgMs != null) txt += `${d.rttAvgMs.toFixed(1)} ms`;
        if (d.lossPercent != null && d.lossPercent > 0) {
            txt += txt ? ' / ' : '';
            txt += `${d.lossPercent.toFixed(1)}% loss`;
        }
        cloud._rttEl.textContent = txt;
    }

    // ---- Node rendering ----

    _renderTreeNodes(node) {
        this._renderInfraNode(node);
        for (const child of node.infraChildren) this._renderTreeNodes(child);
        for (const child of node.clientChildren.slice(0, L.maxClients)) {
            this._renderClientNode(child);
        }
        // "+N more" indicator
        if (node.clientChildren.length > L.maxClients) {
            const last = node.clientChildren[L.maxClients - 1];
            const extra = node.clientChildren.length - L.maxClients;
            const t = svg('text', {
                x: last.x + L.clientSize + 8,
                y: last.y + 4,
                fill: COLORS.textMuted,
                'font-size': L.rateFont,
                'font-family': 'system-ui, sans-serif',
            }, this._gLabels);
            t.textContent = `+${extra}`;
        }
    }

    _renderInfraNode(node) {
        const g = svg('g', {
            transform: `translate(${node.x},${node.y})`,
            class: 'infra-node',
        }, this._gNodes);

        const color = nodeColor(node.d.kind);
        const hw = L.infraW / 2;
        const hh = L.infraH / 2;
        const iconSize = 40;

        // Glow ring behind
        svg('rect', {
            x: -hw - 4, y: -hh - 4,
            width: L.infraW + 8, height: L.infraH + 8,
            rx: 12,
            fill: color,
            opacity: 0.08,
            filter: 'url(#node-glow)',
        }, g);

        // Background card
        svg('rect', {
            x: -hw, y: -hh,
            width: L.infraW, height: L.infraH,
            rx: 8,
            fill: '#1a1d23',
            stroke: color,
            'stroke-width': 1.5,
            opacity: node.d.online ? 1 : 0.4,
        }, g);

        // Device icon or fallback
        if (node.d.model) {
            const imgPath = `/images/devices/${node.d.model.toLowerCase().replace(/ /g, '-')}.png`;
            const img = svg('image', {
                href: imgPath,
                x: -iconSize / 2, y: -iconSize / 2,
                width: iconSize, height: iconSize,
                opacity: node.d.online ? 1 : 0.4,
            }, g);
            img.addEventListener('error', () => {
                img.remove();
                this._renderFallbackIcon(g, node, color, iconSize);
            }, { once: true });
        } else {
            this._renderFallbackIcon(g, node, color, iconSize);
        }

        // Name label below
        const name = node.d.name || node.d.model || '';
        if (name) {
            const displayName = name.length > 14 ? name.slice(0, 13) + '…' : name;
            const t = svg('text', {
                x: 0, y: hh + 16,
                'text-anchor': 'middle',
                fill: COLORS.text,
                'font-size': L.nameFont,
                'font-family': 'system-ui, sans-serif',
                'font-weight': 500,
            }, g);
            t.textContent = displayName;
        }

        // Rate label (updated dynamically)
        node._rateGroup = svg('g', { transform: `translate(0, ${hh + 30})` }, g);
        node._rateDown = svg('text', {
            x: -4, y: 0,
            'text-anchor': 'end',
            fill: COLORS.downstream,
            'font-size': L.rateFont,
            'font-family': 'system-ui, sans-serif',
        }, node._rateGroup);
        node._rateUp = svg('text', {
            x: 4, y: 0,
            'text-anchor': 'start',
            fill: COLORS.upstream,
            'font-size': L.rateFont,
            'font-family': 'system-ui, sans-serif',
        }, node._rateGroup);
    }

    _renderFallbackIcon(g, node, color, size) {
        const hs = size / 2;
        svg('rect', {
            x: -hs + 4, y: -hs + 4,
            width: size - 8, height: size - 8,
            rx: 6,
            fill: color,
            opacity: 0.2,
        }, g);
        const letter = (node.d.name || 'D').charAt(0).toUpperCase();
        const t = svg('text', {
            x: 0, y: 5,
            'text-anchor': 'middle',
            fill: color,
            'font-size': 16,
            'font-weight': 600,
            'font-family': 'system-ui, sans-serif',
        }, g);
        t.textContent = letter;
    }

    _renderClientNode(node) {
        const g = svg('g', {
            transform: `translate(${node.x},${node.y})`,
            class: 'client-node',
        }, this._gNodes);

        const color = nodeColor(node.d.kind, node.d.band);
        const r = L.clientSize / 2 - 1;

        if (node.d.kind === NODE_KIND.WifiClient) {
            // Circle for WiFi client
            svg('circle', {
                cx: 0, cy: 0, r,
                fill: color,
                opacity: node.d.online ? 0.7 : 0.2,
            }, g);
            // WiFi arc indicator
            svg('path', {
                d: `M${-3} ${-1}Q0 ${-5} 3 ${-1}`,
                fill: 'none',
                stroke: '#fff',
                'stroke-width': 1,
                opacity: 0.5,
            }, g);
        } else {
            // Rounded rect for wired client (monitor shape)
            const s = r * 1.4;
            svg('rect', {
                x: -s, y: -s + 1,
                width: s * 2, height: s * 1.6,
                rx: 2,
                fill: color,
                opacity: node.d.online ? 0.6 : 0.2,
            }, g);
            // Stand
            svg('line', {
                x1: 0, y1: s * 0.6 + 1,
                x2: 0, y2: s + 1,
                stroke: color,
                'stroke-width': 1.5,
                opacity: node.d.online ? 0.6 : 0.2,
            }, g);
        }

        // Tooltip via data-tooltip
        const name = node.d.name || node.d.ip || '';
        if (name) g.setAttribute('data-tooltip', name);
    }

    // ---- Rate updates ----

    _updateRates() {
        this._updateNodeRateLabels();
        this._updatePipeColors();
        this._syncParticles();
    }

    _updateNodeRateLabels() {
        if (!this._root) return;

        const nodeRates = new Map();
        for (const edge of this._linkEdges) {
            const rates = this._liveRates[edge.link.portKey] || this._liveRates[edge.link.id];
            if (!rates) continue;
            const down = rates.downstreamBps ?? rates.DownstreamBps ?? 0;
            const up = rates.upstreamBps ?? rates.UpstreamBps ?? 0;

            // Accumulate on parent infrastructure node
            for (const nodeId of [edge.link.fromNodeId, edge.link.toNodeId]) {
                const tn = this._treeNodes.get(nodeId);
                if (tn && isInfra(tn.d.kind)) {
                    const cur = nodeRates.get(nodeId) || { down: 0, up: 0 };
                    cur.down += down;
                    cur.up += up;
                    nodeRates.set(nodeId, cur);
                }
            }
        }

        const updateNode = (node) => {
            const r = nodeRates.get(node.d.id);
            if (node._rateDown) {
                const d = r?.down || 0;
                const u = r?.up || 0;
                node._rateDown.textContent = d > RATE_THRESHOLD ? '↓ ' + formatBps(d) : '';
                node._rateUp.textContent = u > RATE_THRESHOLD ? '↑ ' + formatBps(u) : '';
            }
            for (const c of node.infraChildren) updateNode(c);
        };
        updateNode(this._root);
    }

    _updatePipeColors() {
        for (const edge of this._linkEdges) {
            if (!edge._pathEl) continue;
            const rates = this._liveRates[edge.link.portKey] || this._liveRates[edge.link.id];
            if (!rates) continue;
            const down = rates.downstreamBps ?? rates.DownstreamBps ?? 0;
            const up = rates.upstreamBps ?? rates.UpstreamBps ?? 0;
            const cap = edge.link.capacityBps || 1e9;
            const util = Math.max(down, up) / cap;
            const color = pipeColorForUtil(Math.min(util, 1), edge._band);
            edge._pathEl.setAttribute('stroke', color);
            const op = edge._isClientLink ? 0.35 + util * 0.4 : 0.5 + util * 0.5;
            edge._pathEl.setAttribute('opacity', String(Math.min(op, 1)));
        }
    }

    // ---- Particle system ----

    _syncParticles() {
        // Remove all existing particles
        this._gParticles.innerHTML = '';
        this._particles = [];

        for (const edge of this._linkEdges) {
            if (!edge._pathEl || edge._isClientLink) continue;
            const rates = this._liveRates[edge.link.portKey] || this._liveRates[edge.link.id];
            if (!rates) continue;
            const down = rates.downstreamBps ?? rates.DownstreamBps ?? 0;
            const up = rates.upstreamBps ?? rates.UpstreamBps ?? 0;

            const len = orthoLength(edge._x1, edge._y1, edge._x2, edge._y2);
            if (len < 10) continue;

            // Downstream particles (flow along path direction: parent → child)
            if (down > RATE_THRESHOLD) {
                const count = Math.min(Math.ceil(Math.log10(Math.max(down, 1)) / 2.5), 5);
                const speed = 0.3 + Math.min(Math.log10(Math.max(down, 1)) / 11, 1) * 0.7;
                for (let i = 0; i < count; i++) {
                    const el = svg('circle', {
                        r: L.particleR,
                        fill: COLORS.downstream,
                        opacity: 0.85,
                        filter: 'url(#particle-glow)',
                    }, this._gParticles);
                    this._particles.push({
                        el, edge,
                        t: i / count,
                        speed,
                        dir: 1,
                    });
                }
            }

            // Upstream particles (flow against path: child → parent)
            if (up > RATE_THRESHOLD) {
                const count = Math.min(Math.ceil(Math.log10(Math.max(up, 1)) / 2.5), 5);
                const speed = 0.3 + Math.min(Math.log10(Math.max(up, 1)) / 11, 1) * 0.7;
                for (let i = 0; i < count; i++) {
                    const el = svg('circle', {
                        r: L.particleR,
                        fill: COLORS.upstream,
                        opacity: 0.85,
                        filter: 'url(#particle-glow)',
                    }, this._gParticles);
                    this._particles.push({
                        el, edge,
                        t: i / count,
                        speed,
                        dir: -1,
                    });
                }
            }
        }
    }

    _animate() {
        const now = performance.now();
        const dt = Math.min((now - this._lastFrame) / 1000, 0.1);
        this._lastFrame = now;

        for (const p of this._particles) {
            p.t += p.speed * dt * p.dir;
            if (p.t > 1) p.t -= 1;
            if (p.t < 0) p.t += 1;

            const pt = orthoPointAt(
                p.edge._x1, p.edge._y1,
                p.edge._x2, p.edge._y2,
                p.t
            );
            p.el.setAttribute('cx', pt.x);
            p.el.setAttribute('cy', pt.y);
        }

        this._animId = requestAnimationFrame(() => this._animate());
    }
}

// ---- Module exports ----

let _instance = null;

export async function mount(containerId) {
    if (_instance) { _instance.dispose(); _instance = null; }
    const el = document.getElementById(containerId);
    if (!el) return;
    _instance = new LanFlowMap2D(el);
    await _instance.start();
}

export function unmount() {
    if (_instance) { _instance.dispose(); _instance = null; }
}
