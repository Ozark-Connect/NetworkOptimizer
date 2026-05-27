// 2D hierarchical LAN topology diagram.
// Subscribes to lan-flow-data.js (published by the 3D map) so there are
// zero duplicate API calls. Renders SVG with animated data-flow particles.

import * as flowData from './lan-flow-data.js';

const NS = 'http://www.w3.org/2000/svg';

// ---- Color palette (matches 3D map) ----
const C = {
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
    globeStroke:  '#3b82f6',
};

const NK = { Gateway:0, Switch:1, AP:2, WiredClient:3, WifiClient:4, Cloud:5, VirtualHub:6 };
const LK = { Uplink:0, WiredClient:1, WifiClient:2, Wan:3, Transit:4, MeshBackhaul:5 };

// ---- Layout geometry ----
const G = {
    tierGap:     140,
    cloudGap:    100,
    infraGap:    90,
    clientCellW: 80,
    clientCellH: 50,
    clientR:     7,
    clientCols:  8,
    maxClients:  80,
    iconSize:    52,
    boxW:        68,
    boxH:        60,
    cloudR:      30,
    cornerR:     12,
    pad:         80,
    pipeBase:    2,
    pipeMax:     6,
    labelFont:   11,
    rateFont:    10,
    nameFont:    11,
    clientNameFont: 9,
};

// ---- Helpers ----

function el(tag, a, p) {
    const e = document.createElementNS(NS, tag);
    if (a) for (const [k, v] of Object.entries(a)) if (v != null) e.setAttribute(k, String(v));
    if (p) p.appendChild(e);
    return e;
}

function hexRgb(h) { return [parseInt(h.slice(1,3),16), parseInt(h.slice(3,5),16), parseInt(h.slice(5,7),16)]; }
function rgbHex(r,g,b) { return '#'+[r,g,b].map(c=>Math.round(Math.max(0,Math.min(255,c))).toString(16).padStart(2,'0')).join(''); }
function lerp(a,b,t) { return a+(b-a)*t; }
function lerpColor(a,b,t) { const [r1,g1,b1]=hexRgb(a),[r2,g2,b2]=hexRgb(b); return rgbHex(lerp(r1,r2,t),lerp(g1,g2,t),lerp(b1,b2,t)); }

function bandClr(b) { return b==='2.4'?C.band24:b==='5'?C.band5:b==='6'?C.band6:null; }
function nodeClr(k, b) {
    if (k===NK.Gateway) return C.gateway;
    if (k===NK.Switch) return C.switchNode;
    if (k===NK.AP) return C.ap;
    if (k===NK.WiredClient) return C.wiredClient;
    if (k===NK.WifiClient) return bandClr(b)||C.wifiClient;
    if (k===NK.Cloud) return C.cloud;
    if (k===NK.VirtualHub) return C.virtualHub;
    return C.textMuted;
}
function isInfra(k) { return k<=NK.AP || k===NK.VirtualHub; }
function isClient(k) { return k===NK.WiredClient || k===NK.WifiClient; }

function pipeClr(u, band) {
    const cool = bandClr(band) || C.pipeCool;
    if (u<0.7) return cool;
    if (u<0.9) return lerpColor(cool, C.pipeWarm, (u-0.7)/0.2);
    return lerpColor(C.pipeWarm, C.pipeHot, Math.min((u-0.9)/0.1, 1));
}
function pipeW(cap) {
    if (!cap||cap<=0) return G.pipeBase;
    const t = Math.log10(Math.max(cap/1e9, 0.01))+2;
    return G.pipeBase + Math.min(t, 3.5)*0.9;
}

function formatBps(bps) {
    if (!Number.isFinite(bps)||bps<=0) return '';
    const u=['bps','Kbps','Mbps','Gbps','Tbps'];
    let i=0, v=bps;
    while (v>=1000&&i<u.length-1){v/=1000;i++;}
    return `${v>=100?v.toFixed(0):v.toFixed(1)} ${u[i]}`;
}
function formatSpeed(mbps) {
    if (!mbps) return '';
    if (mbps>=1000){const g=mbps/1000;return `${g%1===0?g.toFixed(0):g.toFixed(1)} Gbps`;}
    return `${mbps} Mbps`;
}

// ---- Orthogonal path (H→R→V never diagonal) ----

function orthoPath(x1, y1, x2, y2) {
    const r = G.cornerR;
    if (Math.abs(x1-x2)<0.5) return `M${x1} ${y1}L${x2} ${y2}`;
    const midY=(y1+y2)/2, dx=x2-x1, s=dx>0?1:-1, ax=Math.abs(dx), hv=Math.abs(y2-y1)/2;
    const cr=Math.min(r, ax/2, hv);
    if (cr<1) return `M${x1} ${y1}L${x1} ${midY}L${x2} ${midY}L${x2} ${y2}`;
    return `M${x1} ${y1}L${x1} ${midY-cr}Q${x1} ${midY} ${x1+s*cr} ${midY}L${x2-s*cr} ${midY}Q${x2} ${midY} ${x2} ${midY+cr}L${x2} ${y2}`;
}

function orthoLen(x1, y1, x2, y2) {
    if (Math.abs(x1-x2)<0.5) return Math.abs(y2-y1);
    const ax=Math.abs(x2-x1), hv=Math.abs(y2-y1)/2, cr=Math.min(G.cornerR, ax/2, hv);
    if (cr<1) return Math.abs(y2-y1)+ax;
    return (hv-cr)*2+Math.PI*cr+(ax-2*cr);
}

function orthoAt(x1, y1, x2, y2, t) {
    const dy=y2-y1;
    if (Math.abs(x2-x1)<0.5) return {x:x1, y:y1+dy*t};
    const midY=(y1+y2)/2, dx=x2-x1, s=dx>0?1:-1, ax=Math.abs(dx), hv=Math.abs(dy)/2;
    const cr=Math.min(G.cornerR, ax/2, hv);
    if (cr<1) {
        const tot=hv+ax+hv; let d=t*tot;
        if (d<=hv) return {x:x1,y:y1+d}; d-=hv;
        if (d<=ax) return {x:x1+s*d,y:midY}; d-=ax;
        return {x:x2,y:midY+d};
    }
    const s1=hv-cr, s2=Math.PI/2*cr, s3=ax-2*cr, s4=s2, s5=hv-cr;
    const tot=s1+s2+s3+s4+s5; let d=t*tot;
    if (d<=s1) return {x:x1,y:y1+d}; d-=s1;
    if (d<=s2) {const a=d/s2*Math.PI/2; return {x:x1+s*cr*Math.sin(a),y:midY-cr+cr*(1-Math.cos(a))};} d-=s2;
    if (d<=s3) return {x:x1+s*(cr+d),y:midY}; d-=s3;
    if (d<=s4) {const a=d/s4*Math.PI/2; return {x:(x2-s*cr)+s*cr*Math.sin(a),y:midY+cr*(1-Math.cos(a))};} d-=s4;
    return {x:x2, y:midY+cr+d};
}

// ---- Layout tree ----

class TN {
    constructor(d) { this.d=d; this.infra=[]; this.clients=[]; this.x=0; this.y=0; this.w=0; }
}

// ---- Persistent particle stream (mirrors 3D map's ParticleStream) ----

const MAX_DOTS = 16;
const EMIT_MAX = 12;

class Stream {
    constructor(edge, dir, color, layer) {
        this._edge = edge;
        this._dir = dir;
        this._color = color;
        this._layer = layer;
        this._density = 0;
        this._velNorm = 0;
        this._dotSize = 0.4;
        this._spawnAcc = 0;
        this._pathLen = orthoLen(edge._x1, edge._y1, edge._x2, edge._y2);
        this._slots = [];
        for (let i = 0; i < MAX_DOTS; i++) {
            const dot = el('circle', { r: 0.4, fill: color, opacity: 0, filter: 'url(#pg)' }, layer);
            this._slots.push({ el: dot, t: -1, size: 0 });
        }
    }

    setRate(bps) {
        const intensity = Math.max(0, Math.min(1, Math.log10(Math.max(bps, 1)) / 11));
        this._density = intensity;
        this._dotSize = 0.4 + (intensity * intensity) * 1.2;
        // Constant visual speed: absolute SVG px/sec divided by path length
        // (mirrors 3D: velocity = 2.5+intensity*4 scene-units/sec / link-length)
        const absPxSec = 50 + intensity * 80;
        this._velNorm = absPxSec / Math.max(this._pathLen, 1);
    }

    advance(dt) {
        const e = this._edge;
        // Emit new particles based on density
        this._spawnAcc += this._density * this._density * EMIT_MAX * dt;
        while (this._spawnAcc >= 1) {
            this._spawnAcc -= (0.6 + Math.random() * 0.8);
            for (const slot of this._slots) {
                if (slot.t < 0) {
                    slot.t = this._dir > 0 ? 0 : 1;
                    slot.size = this._dotSize;
                    slot.el.setAttribute('r', this._dotSize);
                    slot.el.setAttribute('opacity', 0.85);
                    break;
                }
            }
        }

        // Advance existing particles
        for (const slot of this._slots) {
            if (slot.t < 0) continue;
            slot.t += this._velNorm * dt * this._dir;
            if (slot.t > 1 || slot.t < 0) {
                slot.t = -1;
                slot.el.setAttribute('opacity', 0);
                continue;
            }
            const pt = orthoAt(e._x1, e._y1, e._x2, e._y2, slot.t);
            slot.el.setAttribute('cx', pt.x);
            slot.el.setAttribute('cy', pt.y);
        }
    }

    dispose() {
        for (const slot of this._slots) slot.el.remove();
    }
}

// ---- Main class ----

class LanFlowMap2D {
    constructor(container) {
        this._el = container;
        this._svg = null;
        this._gLinks = null;
        this._gParts = null;
        this._gNodes = null;
        this._gLabels = null;

        this._root = null;
        this._treeMap = new Map();
        this._clouds = [];
        this._edges = [];
        this._liveRates = {};

        this._particles = [];
        this._animId = 0;
        this._lastFrame = 0;
        this._unsub = null;

        // Viewport / pan-zoom state
        this._vx = 0; this._vy = 0; this._vw = 800; this._vh = 600;
        this._zoom = 1;
        this._panX = 0; this._panY = 0;
        this._baseVx = 0; this._baseVy = 0; this._baseVw = 800; this._baseVh = 600;
        this._dragging = false;
        this._dragStart = null;
    }

    async start() {
        this._createSvg();

        const snap = flowData.getSnapshot();
        if (snap) {
            this._liveRates = { ...flowData.getLiveRates() };
            this._buildLayout(snap);
            this._renderAll();
        }

        this._unsub = flowData.subscribe((ev) => {
            if (ev === 'snapshot') {
                const s = flowData.getSnapshot();
                if (s) {
                    this._liveRates = { ...flowData.getLiveRates() };
                    this._buildLayout(s);
                    this._renderAll();
                }
            } else if (ev === 'live') {
                Object.assign(this._liveRates, flowData.getLiveRates());
                this._updateRates();
            }
        });

        this._lastFrame = performance.now();
        this._animate();
    }

    dispose() {
        cancelAnimationFrame(this._animId);
        if (this._unsub) this._unsub();
        if (this._streams) for (const s of this._streams) s.dispose();
        this._streams = [];
        this._el.innerHTML = '';
    }

    // ---- SVG skeleton ----

    _createSvg() {
        this._el.innerHTML = '';
        const s = el('svg', { xmlns: NS, 'preserveAspectRatio': 'xMidYMin meet', class: 'lfm2d' }, this._el);
        this._svg = s;

        const defs = el('defs', null, s);
        // Glow for particles
        const pg = el('filter', { id: 'pg', x:'-80%', y:'-80%', width:'260%', height:'260%' }, defs);
        el('feGaussianBlur', { in:'SourceGraphic', stdDeviation:'2.5', result:'b' }, pg);
        const m = el('feMerge', null, pg);
        el('feMergeNode', { in:'b' }, m);
        el('feMergeNode', { in:'SourceGraphic' }, m);

        this._gLinks = el('g', { class:'links' }, s);
        this._gParts = el('g', { class:'particles' }, s);
        this._gNodes = el('g', { class:'nodes' }, s);
        this._gLabels = el('g', { class:'labels' }, s);

        // Pan/zoom events
        s.addEventListener('wheel', (e) => this._onWheel(e), { passive: false });
        s.addEventListener('pointerdown', (e) => this._onPointerDown(e));
        s.addEventListener('pointermove', (e) => this._onPointerMove(e));
        s.addEventListener('pointerup', (e) => this._onPointerUp(e));
        s.addEventListener('pointerleave', (e) => this._onPointerUp(e));
    }

    // ---- Pan / Zoom ----

    _onWheel(e) {
        e.preventDefault();
        const rect = this._svg.getBoundingClientRect();
        const mx = (e.clientX - rect.left) / rect.width;
        const my = (e.clientY - rect.top) / rect.height;

        const step = 1 + Math.min(Math.abs(e.deltaY), 100) * 0.001;
        const factor = e.deltaY > 0 ? step : 1 / step;
        const nw = this._vw * factor;
        const nh = this._vh * factor;

        // Zoom toward mouse position
        this._vx += (this._vw - nw) * mx;
        this._vy += (this._vh - nh) * my;
        this._vw = nw;
        this._vh = nh;
        this._applyViewBox();
    }

    _onPointerDown(e) {
        if (e.button !== 0) return;
        this._dragging = true;
        this._dragStart = { x: e.clientX, y: e.clientY, vx: this._vx, vy: this._vy };
        this._svg.style.cursor = 'grabbing';
        this._svg.setPointerCapture(e.pointerId);
    }

    _onPointerMove(e) {
        if (!this._dragging || !this._dragStart) return;
        const rect = this._svg.getBoundingClientRect();
        const sx = this._vw / rect.width;
        const sy = this._vh / rect.height;
        this._vx = this._dragStart.vx - (e.clientX - this._dragStart.x) * sx;
        this._vy = this._dragStart.vy - (e.clientY - this._dragStart.y) * sy;
        this._applyViewBox();
    }

    _onPointerUp(e) {
        if (!this._dragging) return;
        this._dragging = false;
        this._dragStart = null;
        this._svg.style.cursor = 'grab';
    }

    _applyViewBox() {
        this._svg.setAttribute('viewBox', `${this._vx} ${this._vy} ${this._vw} ${this._vh}`);
    }

    _fitAll() {
        this._vx = this._baseVx;
        this._vy = this._baseVy;
        this._vw = this._baseVw;
        this._vh = this._baseVh;
        this._applyViewBox();
    }

    // ---- Layout ----

    _buildLayout(snap) {
        const byId = new Map();
        for (const n of snap.nodes) byId.set(n.id, new TN(n));
        this._treeMap = byId;

        // Build adjacency from LINKS (not parentId - parentId is sparse).
        // This mirrors how the 3D map determines connectivity.
        const adj = new Map();
        for (const lk of snap.links) {
            if (lk.kind === LK.Wan || lk.kind === LK.Transit) continue;
            const a = lk.fromNodeId, b = lk.toNodeId;
            if (!byId.has(a) || !byId.has(b)) continue;
            if (!adj.has(a)) adj.set(a, []);
            if (!adj.has(b)) adj.set(b, []);
            adj.get(a).push({ to: b, lk });
            adj.get(b).push({ to: a, lk });
        }

        // BFS from gateway to build tree
        let root = null;
        for (const [, tn] of byId) {
            if (tn.d.kind === NK.Gateway) { root = tn; break; }
        }
        this._root = root;

        if (root) {
            const visited = new Set([root.d.id]);
            const queue = [root];
            while (queue.length > 0) {
                const par = queue.shift();
                for (const { to } of (adj.get(par.d.id) || [])) {
                    if (visited.has(to)) continue;
                    visited.add(to);
                    const child = byId.get(to);
                    if (!child) continue;
                    if (isClient(child.d.kind)) {
                        par.clients.push(child);
                    } else {
                        par.infra.push(child);
                        queue.push(child);
                    }
                }
            }
        }

        this._clouds = (snap.clouds||[]).map(c => ({ d:c, x:0, y:0 }));

        this._edges = [];
        for (const lk of snap.links) {
            this._edges.push({ lk, fn: byId.get(lk.fromNodeId), tn: byId.get(lk.toNodeId) });
        }

        if (!root) return;
        this._widths(root);
        this._positions(root, G.pad, 0);
        this._placeClouds();
    }

    _widths(n) {
        let iw = 0;
        for (const c of n.infra) { this._widths(c); iw += c.w; }
        if (n.infra.length > 1) iw += (n.infra.length - 1) * G.infraGap;

        const nc = Math.min(n.clients.length, G.maxClients);
        const cols = Math.min(nc, G.clientCols);
        const rows = Math.ceil(nc / Math.max(cols, 1));
        const cw = cols > 0 ? cols * G.clientCellW : 0;

        const self = isClient(n.d.kind) ? G.clientCellW : G.boxW + 30;
        // Sum infra + client widths so each subtree gets its own space
        let childW = 0;
        if (iw > 0 && cw > 0) childW = iw + G.infraGap + cw;
        else childW = iw + cw;

        n.w = Math.max(self, childW);
    }

    _positions(n, lx, depth) {
        const yOff = G.pad + 80;
        n.y = yOff + depth * G.tierGap;

        if (n.infra.length === 0 && n.clients.length === 0) {
            n.x = lx + n.w / 2;
            return;
        }

        // Compute sub-widths
        let iw = n.infra.reduce((s, c) => s + c.w, 0);
        if (n.infra.length > 1) iw += (n.infra.length - 1) * G.infraGap;
        const nc = Math.min(n.clients.length, G.maxClients);
        const cols = Math.min(nc, G.clientCols);
        const cw = cols > 0 ? cols * G.clientCellW : 0;

        let combW = 0;
        if (iw > 0 && cw > 0) combW = iw + G.infraGap + cw;
        else combW = iw + cw;

        // All children at depth+1 - infra first, then clients right
        let cur = lx + (n.w - combW) / 2;

        for (const c of n.infra) {
            this._positions(c, cur, depth + 1);
            cur += c.w + G.infraGap;
        }

        const clientY = yOff + (depth + 1) * G.tierGap;
        for (let i = 0; i < nc; i++) {
            const col = i % cols, row = Math.floor(i / cols);
            const cn = n.clients[i];
            cn.x = cur + col * G.clientCellW + G.clientCellW / 2;
            cn.y = clientY + row * G.clientCellH;
        }

        // Center parent over ALL children
        const allK = [...n.infra, ...n.clients.slice(0, nc)];
        if (allK.length > 0) {
            n.x = (Math.min(...allK.map(c => c.x)) + Math.max(...allK.map(c => c.x))) / 2;
        } else {
            n.x = lx + n.w / 2;
        }
    }

    _placeClouds() {
        if (!this._root || this._clouds.length === 0) return;
        const gx = this._root.x, gy = this._root.y;
        const total = this._clouds.length, sp = G.cloudGap;
        const sx = gx - ((total - 1) * sp) / 2;
        for (let i = 0; i < total; i++) {
            this._clouds[i].x = sx + i * sp;
            this._clouds[i].y = gy - G.tierGap;
        }
    }

    // ---- Render ----

    _renderAll() {
        if (this._streams) for (const s of this._streams) s.dispose();
        this._streams = [];
        this._gLinks.innerHTML = '';
        this._gNodes.innerHTML = '';
        this._gParts.innerHTML = '';
        this._gLabels.innerHTML = '';

        if (!this._root) return;

        this._calcViewBox();
        this._drawCloudLinks();
        this._drawTreeLinks(this._root);
        this._drawClouds();
        this._drawTree(this._root);
        this._initStreams();
        this._drawToolbar();
        this._updateRates();
        this._svg.style.cursor = 'grab';
    }

    _calcViewBox() {
        let x0=Infinity, y0=Infinity, x1=-Infinity, y1=-Infinity;
        const exp = (x, y, r) => { x0=Math.min(x0,x-r); y0=Math.min(y0,y-r); x1=Math.max(x1,x+r); y1=Math.max(y1,y+r); };
        const vis = (n) => {
            exp(n.x, n.y, G.boxW);
            for (const c of n.infra) vis(c);
            for (const c of n.clients.slice(0, G.maxClients)) exp(c.x, c.y, G.clientCellW / 2);
        };
        vis(this._root);
        for (const c of this._clouds) exp(c.x, c.y, G.cloudR + 30);

        const p = G.pad;
        this._baseVx = x0 - p;
        this._baseVy = y0 - p;
        this._baseVw = (x1 - x0) + p * 2;
        this._baseVh = (y1 - y0) + p * 2 + 50;
        this._fitAll();
    }

    // ---- Cloud links ----

    _drawCloudLinks() {
        if (!this._root) return;
        const gw = this._root, gwT = gw.y - G.boxH / 2;

        for (const cloud of this._clouds) {
            const cy = cloud.y + G.cloudR + 8;
            const edge = this._edges.find(e =>
                (e.lk.kind === LK.Wan || e.lk.kind === LK.Transit)
                && (e.lk.fromNodeId === cloud.d.id || e.lk.toNodeId === cloud.d.id));

            const d = orthoPath(cloud.x, cy, gw.x, gwT);
            const pe = el('path', { d, fill:'none', stroke:C.pipeCool,
                'stroke-width': edge ? pipeW(edge.lk.capacityBps) : G.pipeBase,
                'stroke-linecap':'round', opacity:'0.65' }, this._gLinks);

            if (edge) {
                edge._pe = pe;
                edge._x1 = cloud.x; edge._y1 = cy;
                edge._x2 = gw.x; edge._y2 = gwT;
                edge._isWan = true;

                // WAN live rate label
                const midX = (cloud.x + gw.x) / 2;
                const midY = (cy + gwT) / 2 + 12;
                const rg = el('g', { transform:`translate(${midX},${midY})`, class:'rate-lbl' }, this._gLabels);
                const rbg = el('rect', { x:-50, y:-8, width:100, height:16, rx:4, fill:C.labelBg, opacity:0 }, rg);
                const rd = el('text', { x:-3, y:4, 'text-anchor':'end', fill:C.downstream,
                    'font-size':G.rateFont, 'font-family':'system-ui,sans-serif' }, rg);
                const ru = el('text', { x:3, y:4, 'text-anchor':'start', fill:C.upstream,
                    'font-size':G.rateFont, 'font-family':'system-ui,sans-serif' }, rg);
                edge._rlG = rg; edge._rlBg = rbg; edge._rlD = rd; edge._rlU = ru;
            }
        }

        // Transit links between clouds
        const sc = [...this._clouds].sort((a, b) => a.d.order - b.d.order);
        for (let i = 0; i < sc.length - 1; i++) {
            const a = sc[i], b = sc[i + 1];
            const edge = this._edges.find(e =>
                e.lk.kind === LK.Transit
                && ((e.lk.fromNodeId === a.d.id && e.lk.toNodeId === b.d.id)
                    || (e.lk.fromNodeId === b.d.id && e.lk.toNodeId === a.d.id)));
            if (!edge) continue;
            const pe = el('path', {
                d: `M${a.x+G.cloudR+4} ${a.y}L${b.x-G.cloudR-4} ${b.y}`,
                fill:'none', stroke:C.pipeCool, 'stroke-width':G.pipeBase,
                'stroke-linecap':'round', 'stroke-dasharray':'6 4', opacity:'0.45'
            }, this._gLinks);
            edge._pe = pe;
            edge._x1 = a.x+G.cloudR+4; edge._y1 = a.y;
            edge._x2 = b.x-G.cloudR-4; edge._y2 = b.y;
        }
    }

    // ---- Tree links ----

    _drawTreeLinks(n) {
        const pB = n.y + G.boxH / 2;
        for (const c of n.infra) {
            this._drawLink(n, c, pB, c.y - G.boxH / 2, false);
            this._drawTreeLinks(c);
        }
        for (const c of n.clients.slice(0, G.maxClients)) {
            this._drawLink(n, c, pB, c.y - G.clientR, true);
        }
    }

    _drawLink(par, child, y1, y2, isCl) {
        const edge = this._edges.find(e =>
            (e.lk.fromNodeId === par.d.id && e.lk.toNodeId === child.d.id)
            || (e.lk.fromNodeId === child.d.id && e.lk.toNodeId === par.d.id));

        const band = edge?.lk.band;
        const cool = bandClr(band) || C.pipeCool;

        const d = orthoPath(par.x, y1, child.x, y2);
        const pe = el('path', { d, fill:'none', stroke:cool,
            'stroke-width': edge ? pipeW(edge.lk.capacityBps) : G.pipeBase,
            'stroke-linecap':'round', opacity: isCl ? '0.3' : '0.55' }, this._gLinks);

        if (edge) {
            edge._pe = pe; edge._x1 = par.x; edge._y1 = y1;
            edge._x2 = child.x; edge._y2 = y2;
            edge._isCl = isCl; edge._band = band;
        }

        if (!isCl && edge?.lk.capacityBps) {
            const cap = edge.lk.capacityBps / 1e6;
            if (cap > 0) this._capLabel((par.x + child.x) / 2, (y1 + y2) / 2, formatSpeed(cap));
        }

        // Live rate label (updated dynamically) for non-client links
        if (!isCl && edge) {
            const midX = (par.x + child.x) / 2;
            const midY = (y1 + y2) / 2 + 12;
            const rg = el('g', { transform:`translate(${midX},${midY})`, class:'rate-lbl' }, this._gLabels);
            const rbg = el('rect', { x:-50, y:-8, width:100, height:16, rx:4, fill:C.labelBg, opacity:0 }, rg);
            const rd = el('text', { x:-3, y:4, 'text-anchor':'end', fill:C.downstream,
                'font-size':G.rateFont, 'font-family':'system-ui,sans-serif' }, rg);
            const ru = el('text', { x:3, y:4, 'text-anchor':'start', fill:C.upstream,
                'font-size':G.rateFont, 'font-family':'system-ui,sans-serif' }, rg);
            edge._rlG = rg; edge._rlBg = rbg; edge._rlD = rd; edge._rlU = ru;
        }
    }

    _capLabel(x, y, txt) {
        const g = el('g', { transform:`translate(${x},${y})` }, this._gLabels);
        const tw = txt.length * 5.5 + 12;
        el('rect', { x:-tw/2, y:-8, width:tw, height:16, rx:4, fill:C.labelBg }, g);
        const t = el('text', { x:0, y:4, 'text-anchor':'middle', fill:C.textMuted,
            'font-size':G.rateFont, 'font-family':'system-ui,sans-serif' }, g);
        t.textContent = txt;
    }

    // ---- Cloud nodes ----

    _drawClouds() {
        for (const cloud of this._clouds) {
            const g = el('g', { transform:`translate(${cloud.x},${cloud.y})` }, this._gNodes);
            const r = G.cloudR;

            // Blue wireframe globe
            el('circle', { cx:0, cy:0, r, fill:'none', stroke:C.globeStroke, 'stroke-width':1.5, opacity:0.8 }, g);
            el('ellipse', { cx:0, cy:0, rx:r*0.45, ry:r, fill:'none', stroke:C.globeStroke, 'stroke-width':1, opacity:0.35 }, g);
            el('ellipse', { cx:0, cy:0, rx:r, ry:r*0.35, fill:'none', stroke:C.globeStroke, 'stroke-width':1, opacity:0.35 }, g);
            el('ellipse', { cx:0, cy:0, rx:r, ry:r*0.65, fill:'none', stroke:C.globeStroke, 'stroke-width':0.7, opacity:0.2 }, g);
            el('circle', { cx:0, cy:0, r:r-1, fill:C.globeStroke, opacity:0.05 }, g);

            const name = cloud.d.asnName || cloud.d.name || 'WAN';
            const lb = el('text', { x:0, y:r+18, 'text-anchor':'middle', fill:C.textSec,
                'font-size':G.nameFont, 'font-family':'system-ui,sans-serif', 'font-weight':500 }, g);
            lb.textContent = name;

            cloud._rtt = el('text', { x:0, y:r+31, 'text-anchor':'middle', fill:C.textMuted,
                'font-size':G.rateFont-1, 'font-family':'system-ui,sans-serif' }, g);
            this._cloudBadge(cloud);
        }
    }

    _cloudBadge(c) {
        if (!c._rtt) return;
        const d = c.d; let t = '';
        if (d.rttAvgMs != null) t += `${d.rttAvgMs.toFixed(1)} ms`;
        if (d.lossPercent && d.lossPercent > 0) t += (t ? ' / ' : '') + `${d.lossPercent.toFixed(1)}% loss`;
        c._rtt.textContent = t;
    }

    // ---- Infrastructure + client nodes ----

    _drawTree(n) {
        this._drawInfra(n);
        for (const c of n.infra) this._drawTree(c);
        for (const c of n.clients.slice(0, G.maxClients)) this._drawClient(c);
        if (n.clients.length > G.maxClients) {
            const last = n.clients[G.maxClients - 1];
            const extra = n.clients.length - G.maxClients;
            const t = el('text', { x:last.x + G.clientR + 10, y:last.y + 4,
                fill:C.textMuted, 'font-size':G.rateFont, 'font-family':'system-ui,sans-serif' }, this._gLabels);
            t.textContent = `+${extra}`;
        }
    }

    _drawInfra(n) {
        const g = el('g', { transform:`translate(${n.x},${n.y})`, class:'inf' }, this._gNodes);
        const color = nodeClr(n.d.kind);
        const hw = G.boxW / 2, hh = G.boxH / 2;

        // Glow
        el('rect', { x:-hw-5, y:-hh-5, width:G.boxW+10, height:G.boxH+10,
            rx:14, fill:color, opacity:0.07 }, g);

        // Card
        el('rect', { x:-hw, y:-hh, width:G.boxW, height:G.boxH, rx:10,
            fill:'#1a1d23', stroke:color, 'stroke-width':1.5,
            opacity: n.d.online ? 1 : 0.35 }, g);

        // Device icon (large, fills the card)
        const iSz = G.iconSize;
        if (n.d.model) {
            const path = `/images/devices/${n.d.model.toLowerCase().replace(/ /g, '-')}.png`;
            const img = el('image', { href:path, x:-iSz/2, y:-iSz/2, width:iSz, height:iSz,
                opacity: n.d.online ? 1 : 0.35 }, g);
            img.addEventListener('error', () => { img.remove(); this._fallback(g, n, color); }, { once: true });
        } else {
            this._fallback(g, n, color);
        }

        // Name
        const name = n.d.name || n.d.model || '';
        if (name) {
            const dn = name.length > 16 ? name.slice(0, 15) + '…' : name;
            const t = el('text', { x:0, y:hh+17, 'text-anchor':'middle', fill:C.text,
                'font-size':G.nameFont, 'font-family':'system-ui,sans-serif', 'font-weight':500 }, g);
            t.textContent = dn;
        }

        // Rate label area
        n._rd = el('text', { x:-3, y:hh+31, 'text-anchor':'end', fill:C.downstream,
            'font-size':G.rateFont, 'font-family':'system-ui,sans-serif' }, g);
        n._ru = el('text', { x:3, y:hh+31, 'text-anchor':'start', fill:C.upstream,
            'font-size':G.rateFont, 'font-family':'system-ui,sans-serif' }, g);
    }

    _fallback(g, n, color) {
        const s = G.iconSize / 2 - 6;
        el('rect', { x:-s, y:-s, width:s*2, height:s*2, rx:6, fill:color, opacity:0.2 }, g);
        const t = el('text', { x:0, y:6, 'text-anchor':'middle', fill:color,
            'font-size':18, 'font-weight':600, 'font-family':'system-ui,sans-serif' }, g);
        t.textContent = (n.d.name || 'D').charAt(0).toUpperCase();
    }

    _drawClient(n) {
        const g = el('g', { transform:`translate(${n.x},${n.y})`, class:'cl' }, this._gNodes);
        const color = nodeClr(n.d.kind, n.d.band);
        const r = G.clientR;
        const op = n.d.online ? 0.7 : 0.2;

        if (n.d.kind === NK.WifiClient) {
            el('circle', { cx:0, cy:0, r, fill:color, opacity:op }, g);
            el('path', { d:`M${-2.5} ${-0.5}Q0 ${-4} 2.5 ${-0.5}`, fill:'none',
                stroke:'#fff', 'stroke-width':0.8, opacity:0.5 }, g);
        } else {
            const s = r * 0.9;
            el('rect', { x:-s, y:-s+0.5, width:s*2, height:s*1.5, rx:1.5, fill:color, opacity:op }, g);
            el('line', { x1:0, y1:s*0.5+0.5, x2:0, y2:s+1, stroke:color, 'stroke-width':1.2, opacity:op }, g);
        }

        // Visible name label below
        const name = n.d.name || n.d.ip || '';
        if (name) {
            const dn = name.length > 14 ? name.slice(0, 13) + '…' : name;
            const t = el('text', { x:0, y:r + 11, 'text-anchor':'middle', fill:C.textMuted,
                'font-size':G.clientNameFont, 'font-family':'system-ui,sans-serif' }, g);
            t.textContent = dn;
            g.setAttribute('data-tooltip', name);
        }
    }

    // ---- Toolbar ----

    _drawToolbar() {
        const tb = document.createElement('div');
        tb.className = 'lfm2d-toolbar';
        tb.innerHTML = `<button class="lfm2d-btn" data-action="zin" title="Zoom in">+</button>`
            + `<button class="lfm2d-btn" data-action="zout" title="Zoom out">&minus;</button>`
            + `<button class="lfm2d-btn" data-action="fit" title="Fit all">&#x2922;</button>`;
        tb.addEventListener('click', (e) => {
            const a = e.target.closest('[data-action]')?.dataset.action;
            if (a === 'zin') { this._zoomBy(0.8); }
            else if (a === 'zout') { this._zoomBy(1.25); }
            else if (a === 'fit') { this._fitAll(); }
        });
        this._el.appendChild(tb);
    }

    _zoomBy(factor) {
        const cx = this._vx + this._vw / 2;
        const cy = this._vy + this._vh / 2;
        this._vw *= factor;
        this._vh *= factor;
        this._vx = cx - this._vw / 2;
        this._vy = cy - this._vh / 2;
        this._applyViewBox();
    }

    // ---- Rate updates ----

    _updateRates() {
        this._updateLabels();
        this._updatePipes();
        this._updateCloudStats();
        this._updateStreamRates();
    }

    _updateLabels() {
        if (!this._root) return;
        // Use node badges from the shared data (same source as 3D map).
        // fabricIngress/Egress for switches, aggregateIn/Out for APs.
        // Direction: blue ↓ = download = data flowing toward leaves.
        const badges = flowData.getNodeBadges();

        const upd = (n) => {
            if (n._rd) {
                let downBps = 0, upBps = 0, any = false;
                const b = badges?.[n.d.id];
                const hasFab = b && (b.fabricIngressBps != null || b.fabricEgressBps != null);
                const hasAgg = b && (b.aggregateInBps != null || b.aggregateOutBps != null);

                if (hasFab) {
                    downBps = b.fabricIngressBps || 0;
                    upBps = b.fabricEgressBps || 0;
                    any = downBps > 0 || upBps > 0;
                } else if (hasAgg) {
                    // APs: aggregateIn = client data arriving at AP, aggregateOut = data leaving AP.
                    // Label is gateway-relative (↓=from gateway) so APs swap.
                    if (n.d.kind === NK.AP) {
                        downBps = b.aggregateOutBps || 0;
                        upBps = b.aggregateInBps || 0;
                    } else {
                        downBps = b.aggregateInBps || 0;
                        upBps = b.aggregateOutBps || 0;
                    }
                    any = downBps > 0 || upBps > 0;
                } else {
                    // Fallback: sum adjacent link rates
                    for (const e of this._edges) {
                        if (e.lk.fromNodeId !== n.d.id && e.lk.toNodeId !== n.d.id) continue;
                        const r = this._liveRates[e.lk.portKey] || this._liveRates[e.lk.id];
                        if (!r) continue;
                        any = true;
                        downBps += r.downstreamBps ?? 0;
                        upBps += r.upstreamBps ?? 0;
                    }
                }

                n._rd.textContent = (any && downBps > 100000) ? '↓' + formatBps(downBps) : '';
                n._ru.textContent = (any && upBps > 100000) ? '↑' + formatBps(upBps) : '';
            }
            for (const c of n.infra) upd(c);
        };
        upd(this._root);
    }

    _updatePipes() {
        const THRESH = 1_000_000;
        for (const e of this._edges) {
            if (!e._pe) continue;
            const r = this._liveRates[e.lk.portKey] || this._liveRates[e.lk.id];
            if (!r) continue;
            const dn = r.downstreamBps ?? 0, up = r.upstreamBps ?? 0;
            const cap = e.lk.capacityBps || 1e9;
            const u = Math.max(dn, up) / cap;
            e._pe.setAttribute('stroke', pipeClr(Math.min(u, 1), e._band));
            const op = e._isCl ? 0.3 + u * 0.45 : 0.5 + u * 0.5;
            e._pe.setAttribute('opacity', String(Math.min(op, 1)));

            // Live rate label - show both directions when either exceeds threshold
            if (e._rlD) {
                if (dn > THRESH || up > THRESH) {
                    e._rlD.textContent = '↓' + (dn > 0 ? formatBps(dn) : '0 bps');
                    e._rlU.textContent = '↑' + (up > 0 ? formatBps(up) : '0 bps');
                    const tw = Math.max((e._rlD.textContent.length + e._rlU.textContent.length) * 5 + 16, 40);
                    e._rlBg.setAttribute('x', -tw / 2);
                    e._rlBg.setAttribute('width', tw);
                    e._rlBg.setAttribute('opacity', 1);
                } else {
                    e._rlD.textContent = '';
                    e._rlU.textContent = '';
                    e._rlBg.setAttribute('opacity', 0);
                }
            }
        }
    }

    _updateCloudStats() {
        const cs = flowData.getCloudStats();
        for (const cloud of this._clouds) {
            const live = cs?.[cloud.d.id];
            if (live) {
                if (live.rttAvgMs != null) cloud.d.rttAvgMs = live.rttAvgMs;
                if (live.lossPercent != null) cloud.d.lossPercent = live.lossPercent;
            }
            this._cloudBadge(cloud);
        }
    }

    // ---- Particle system (persistent emission, matching 3D map) ----
    // Each link gets two Stream objects (downstream + upstream). setRate()
    // adjusts emission parameters. advance(dt) spawns new dots and moves
    // existing ones. Particles are never bulk-recreated on rate updates.

    _initStreams() {
        this._streams = [];
        for (const e of this._edges) {
            if (!e._pe) continue;
            if (e._x1 == null) continue;
            const len = orthoLen(e._x1, e._y1, e._x2, e._y2);
            if (len < 5) continue;
            e._sDown = new Stream(e, 1, C.downstream, this._gParts);
            e._sUp   = new Stream(e, -1, C.upstream, this._gParts);
            this._streams.push(e._sDown, e._sUp);
        }
    }

    _updateStreamRates() {
        for (const e of this._edges) {
            if (!e._sDown) continue;
            const r = this._liveRates[e.lk.portKey] || this._liveRates[e.lk.id];
            e._sDown.setRate(r?.downstreamBps ?? 0);
            e._sUp.setRate(r?.upstreamBps ?? 0);
        }
    }

    _animate() {
        const now = performance.now();
        const dt = Math.min((now - this._lastFrame) / 1000, 0.1);
        this._lastFrame = now;

        for (const s of this._streams) s.advance(dt);

        this._animId = requestAnimationFrame(() => this._animate());
    }
}

// ---- Module exports ----
let _inst = null;

export async function mount(containerId) {
    if (_inst) { _inst.dispose(); _inst = null; }
    const container = document.getElementById(containerId);
    if (!container) return;
    _inst = new LanFlowMap2D(container);
    await _inst.start();
}

export function unmount() {
    if (_inst) { _inst.dispose(); _inst = null; }
}
