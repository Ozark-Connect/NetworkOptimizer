// 3D LAN Flow Map (spec 5.7)
//
// Three.js scene that paints the LAN topology with rate-proportional
// bidirectional particle streams. Direction is pre-resolved on the server
// (spec 5.7.1): every link's DownstreamBps is gateway -> device (blue
// --speed-download-color), UpstreamBps is device -> gateway (green
// --speed-upload-color). The JS layer never re-derives direction from the
// underlying SNMP/UniFi data - it just paints what the server pre-resolves.

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { buildBuildings } from './lan-flow-buildings.js';

const COLORS = {
    background: 0x202023,
    fog: 0x202023,
    gateway: 0xfacc15,
    switchNode: 0x9aa6b2,
    ap: 0x3385d6,
    wiredClient: 0xc9d2e0,
    wifiClient: 0xe2e8f0,
    cloud: 0x4d556b,
    accent: 0x2ba89a,
    // Virtual hub: dim neutral so it reads as "logical, not a real device"
    // and visually clusters its member leaves without competing for attention.
    virtualHub: 0x6b7785,

    // Direction palette (locked, spec 5.7.1)
    downstream: 0x3385d6,   // var(--speed-download-color)
    upstream: 0x24bc70,     // var(--speed-upload-color)

    // Pipe backdrop (health shift)
    pipeCool: 0x1f4068,
    pipeWarm: 0xe79613,
    pipeHot: 0xee6368,

    // WiFi band palette - matches the WiFi Optimizer + CLAUDE.md spec. Used as the
    // base "cool" color for wifi-client + mesh-backhaul links, so the pipe color
    // identifies the band at a glance and only shifts toward amber/red when the
    // link approaches capacity.
    band24: 0xfbbf24, // 2.4 GHz amber
    band5:  0x3b82f6, // 5 GHz blue
    band6:  0xa855f7, // 6 GHz purple
};

function bandBaseColor(band) {
    if (band === '2.4') return COLORS.band24;
    if (band === '5')   return COLORS.band5;
    if (band === '6')   return COLORS.band6;
    return COLORS.pipeCool;
}

const NODE_RADIUS = {
    gateway: 1.6,
    switch: 1.2,
    ap: 1.0,
    wiredClient: 0.45,
    wifiClient: 0.45,
    cloud: 2.4,
    virtualHub: 0.55,
};

const LINK_KIND = {
    Uplink: 0,
    WiredClient: 1,
    WifiClient: 2,
    Wan: 3,
    Transit: 4,
    MeshBackhaul: 5,
};

const NODE_KIND = {
    Gateway: 0,
    Switch: 1,
    AccessPoint: 2,
    WiredClient: 3,
    WifiClient: 4,
    Cloud: 5,
    // Synthetic grouping node when several wired clients share one physical
    // switch port (server with VLAN sub-interfaces, etc.). Server inserts it
    // and reparents the members so the leaves don't fan out.
    VirtualHub: 6,
};

// Threshold below which link rate labels stay hidden. Keeps idle topology clean;
// only links carrying meaningful traffic get a label. 1 Mbps either direction
// is the cutoff - low enough to surface most user-noticeable flows, high enough
// that monitoring's own ping/loss probes don't render labels everywhere.
const LINK_LABEL_THRESHOLD_BPS = 1_000_000;

// Camera-distance cutoff (scene units) above which leaf link labels
// (WiredClient / WifiClient) stop rendering. Keeps the wide-angle view
// uncluttered - leaf details only surface when the user zooms in. Trunks and
// WAN labels are always visible (above the rate threshold).
const LEAF_LABEL_MAX_DIST = 35;

const PLACEMENT_SOURCE = {
    Layout: 0,
    Anchor: 1,
    Interpolated: 2,
};

const CLOUD_TIER = {
    // Per spec 5.7: solid = real router target, PathProxy = dashed "via path",
    // Unresolved = discovery still pending (no live stats yet).
    Solid: 0,
    PathProxy: 1,
    Unresolved: 2,
};

export class LanFlowMap {
    constructor(canvasEl, options = {}) {
        this.canvas = canvasEl;
        this.stage = canvasEl.parentElement || canvasEl;
        this.apiBase = options.apiBase ?? '/api/monitoring/lan-flow-map';
        this.pollIntervalMs = options.pollIntervalMs ?? 2000;
        this.onError = options.onError ?? ((err) => console.error('[LanFlowMap]', err));

        this._snapshot = null;
        this._nodesByLink = new Map();
        this._nodeMeshes = new Map();   // nodeId -> THREE.Group
        this._linkMeshes = new Map();   // linkId -> { pipe, particlesDown, particlesUp }
        this._cloudMeshes = new Map();  // cloudId -> THREE.Group
        this._labelSprites = new Map(); // nodeId -> THREE.Sprite
        this._speedTestOverlay = null;  // currently-rendered overlay tubes

        // Overlay + filter state. Defaults match spec 5.7.1 ("default 'all on' so a
        // first-time user sees the full picture, but power users can declutter").
        // Restore persisted overlay toggles, falling back to defaults.
        const defaultOverlays = {
            wifiClients: true,
            wiredClients: true,
            clouds: true,
            speedTests: false,
            buildings: true,
        };
        try {
            const saved = JSON.parse(localStorage.getItem('lanFlowMapOverlays'));
            this._overlays = saved ? { ...defaultOverlays, ...saved } : { ...defaultOverlays };
        } catch {
            this._overlays = { ...defaultOverlays };
        }
        this._filter = {
            text: '',
            bands: { '2.4': true, '5': true, '6': true },
        };
        this._mode = 'live';      // 'live' | 'historic'
        this._historicAt = null;  // Date when in historic mode
        this._dotnetRef = window.__monitoringRef || null;
        this._playbackSpeed = 1;  // real-time multiplier
        this._playbackAccum = 0;  // fractional slider unit accumulator
        this._scrubberOrigin = Date.now() - 24 * 3600000; // left edge anchored at mount - 24h

        this._panels = {};        // DOM refs for overlay UI
        this._floatingLabels = new Map();  // nodeId -> { el, nameEl, rateEl }
        this._linkLabels = new Map();      // linkId -> { el } - rate pill at link midpoint
        this._wanPills = new Map();        // wanNodeId -> el
        this._latestSpeedTestByWan = new Map();  // wanInterface -> SpeedTestOverlayItem
        this._raycaster = new THREE.Raycaster();
        this._pointerNdc = new THREE.Vector2();
        this._pointerScreen = { x: 0, y: 0 };
        this._hoverTarget = null;

        // Reposition mode state
        this._repositionMode = false;
        this._repositionNode = null;   // node being moved
        this._repositionGroup = null;  // THREE.Group for the node
        this._repositionOrigPos = null; // original position for cancel

        this._raf = null;
        this._pollTimer = null;
        this._lastFrame = performance.now();
        this._destroyed = false;

        this._initScene();
        this._initInteractions();
    }

    // ------------------------------------------------------------------------
    // Scene setup
    // ------------------------------------------------------------------------

    _initScene() {
        const rect = this.canvas.getBoundingClientRect();
        const width = Math.max(rect.width || this.canvas.clientWidth || 800, 320);
        const height = Math.max(rect.height || this.canvas.clientHeight || 480, 240);

        this.renderer = new THREE.WebGLRenderer({
            canvas: this.canvas,
            antialias: true,
            alpha: false,
            powerPreference: 'high-performance',
        });
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
        this.renderer.setSize(width, height, false);
        this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
        this.renderer.toneMappingExposure = 1.15;

        this.scene = new THREE.Scene();
        this.scene.background = new THREE.Color(COLORS.background);
        this.scene.fog = new THREE.Fog(COLORS.fog, 70, 260);

        this.camera = new THREE.PerspectiveCamera(45, width / height, 0.1, 1000);
        // Will animate IN from here on first mount via _flyIn().
        this.camera.position.set(95, 60, 95);
        this.camera.lookAt(0, 0, 0);

        // Post-processing: bloom for the luminous-particles look. Selective bloom would
        // require layers + a separate composer pass; for now, full-scene bloom with
        // conservative parameters keeps device meshes from blowing out while making
        // the particle streams genuinely glow.
        this.composer = new EffectComposer(this.renderer);
        this.composer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
        this.composer.setSize(width, height);
        this.composer.addPass(new RenderPass(this.scene, this.camera));
        this.bloomPass = new UnrealBloomPass(
            new THREE.Vector2(width, height),
            0.45,   // strength (0.85 was too intense, 0.21 was too tame, this is the middle)
            0.45,   // radius
            0.45,   // threshold (between original 0.32 and the over-conservative 0.55)
        );
        this.composer.addPass(this.bloomPass);
        this.composer.addPass(new OutputPass());

        this.controls = new OrbitControls(this.camera, this.renderer.domElement);
        this.controls.enableDamping = true;
        this.controls.dampingFactor = 0.08;
        this.controls.rotateSpeed = 0.65;
        this.controls.zoomSpeed = 0.75;
        this.controls.minDistance = 5;
        this.controls.maxDistance = 220;
        this.controls.target.set(0, 0, 0);

        // Subtle hemispheric lighting so nodes have a sense of depth without flat shading.
        const hemi = new THREE.HemisphereLight(0xb1d4ff, 0x1a2029, 0.55);
        const ambient = new THREE.AmbientLight(0xffffff, 0.35);
        const key = new THREE.DirectionalLight(0xffffff, 0.7);
        key.position.set(40, 60, 30);
        this.scene.add(hemi, ambient, key);


        // Container groups so toggling layers is cheap.
        this.buildingGroup = new THREE.Group();
        this.nodeGroup = new THREE.Group();
        this.linkGroup = new THREE.Group();
        this.cloudGroup = new THREE.Group();
        this.particleGroup = new THREE.Group();
        this.labelGroup = new THREE.Group();
        this.scene.add(this.buildingGroup, this.nodeGroup, this.linkGroup, this.cloudGroup, this.particleGroup, this.labelGroup);
    }

    _initInteractions() {
        this._resizeObserver = new ResizeObserver(() => this._handleResize());
        this._resizeObserver.observe(this.canvas.parentElement || this.canvas);

        this.canvas.addEventListener('pointermove', (e) => this._onPointerMove(e));
        this.canvas.addEventListener('pointerleave', () => this._clearHover());
        this.canvas.addEventListener('dblclick', (e) => this._onDoubleClick(e));
        this.canvas.addEventListener('contextmenu', (e) => this._onContextMenu(e));
        this.canvas.addEventListener('pointerdown', (e) => {
            if (this._repositionMode) return;
            this._dismissContextMenu();
        });

        // WASD keyboard navigation: W/S = zoom in/out, A/D = orbit left/right
        this._keys = {};
        this._onKeyDown = (e) => {
            if (e.key === 'Escape') {
                if (this._repositionMode) {
                    this._cancelReposition();
                    return;
                }
                if (this.stage?.classList.contains('lan-flow-map-fullscreen')) {
                    this._toggleFullscreen();
                    return;
                }
            }
            if (!this._shouldAcceptKeys()) return;
            if (['w','a','s','d','q','e'].includes(e.key.toLowerCase())) {
                this._keys[e.key.toLowerCase()] = true;
            }
        };
        this._onKeyUp = (e) => {
            this._keys[e.key.toLowerCase()] = false;
        };
        document.addEventListener('keydown', this._onKeyDown);
        document.addEventListener('keyup', this._onKeyUp);
    }

    _shouldAcceptKeys() {
        const tag = document.activeElement?.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return false;
        if (document.activeElement?.isContentEditable) return false;
        const rect = this.canvas.getBoundingClientRect();
        return rect.bottom > 0 && rect.top < window.innerHeight;
    }

    _toggleFullscreen() {
        const el = this.stage;
        const isFs = el.classList.contains('lan-flow-map-fullscreen');
        if (isFs) {
            el.classList.remove('lan-flow-map-fullscreen');
            this._panels.fullscreenBtn.innerHTML = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                <polyline points="3 8 3 3 8 3"></polyline><polyline points="16 3 21 3 21 8"></polyline>
                <polyline points="21 16 21 21 16 21"></polyline><polyline points="8 21 3 21 3 16"></polyline></svg>`;
            this._panels.fullscreenBtn.setAttribute('data-tooltip', 'Fullscreen');
        } else {
            el.classList.add('lan-flow-map-fullscreen');
            this._panels.fullscreenBtn.innerHTML = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                <polyline points="4 10 10 10 10 4"></polyline><polyline points="14 4 14 10 20 10"></polyline>
                <polyline points="20 14 14 14 14 20"></polyline><polyline points="10 20 10 14 4 14"></polyline></svg>`;
            this._panels.fullscreenBtn.setAttribute('data-tooltip', 'Exit fullscreen (Esc)');
        }
        document.dispatchEvent(new CustomEvent('lanflowmap-fullscreen', {
            detail: { fullscreen: !isFs }
        }));
        setTimeout(() => this._handleResize(), 50);
    }

    _handleResize() {
        const rect = this.canvas.getBoundingClientRect();
        const width = Math.max(rect.width || this.canvas.clientWidth || 800, 320);
        const height = Math.max(rect.height || this.canvas.clientHeight || 480, 240);
        this.renderer.setSize(width, height, false);
        this.composer?.setSize(width, height);
        this.bloomPass?.setSize(width, height);
        this.camera.aspect = width / height;
        this.camera.updateProjectionMatrix();
    }

    // ------------------------------------------------------------------------
    // Public lifecycle
    // ------------------------------------------------------------------------

    async start() {
        this._buildOverlayUI();
        await this._loadSnapshot();
        await this._loadInitialSpeedTests();
        this._startAnimation();
        this._startPolling();
    }

    dispose() {
        this._destroyed = true;
        if (this._repositionMode) this._exitRepositionMode();
        this._dismissContextMenu();
        // The render loop registered via setAnimationLoop checks _destroyed
        // and tears itself down. Belt-and-suspenders null it here too.
        this.renderer?.setAnimationLoop(null);
        if (this._raf) cancelAnimationFrame(this._raf);
        if (this._pollTimer) clearInterval(this._pollTimer);
        if (this._historicPlaybackTimer) clearInterval(this._historicPlaybackTimer);
        if (this._resizeObserver) this._resizeObserver.disconnect();
        if (this._onKeyDown) document.removeEventListener('keydown', this._onKeyDown);
        if (this._onKeyUp) document.removeEventListener('keyup', this._onKeyUp);
        this.controls?.dispose();
        this.renderer?.dispose();
        this._disposeScene();
        // Tear down overlay UI added to the stage.
        for (const key of Object.keys(this._panels)) {
            const el = this._panels[key];
            if (el && el.remove) el.remove();
        }
        this._panels = {};
    }

    _disposeScene() {
        const disposeMat = (m) => {
            if (m.map) m.map.dispose();
            m.dispose();
        };
        const disposeGroup = (g) => {
            g.traverse((obj) => {
                if (obj.geometry) obj.geometry.dispose();
                if (obj.material) {
                    if (Array.isArray(obj.material)) obj.material.forEach(disposeMat);
                    else disposeMat(obj.material);
                }
            });
            while (g.children.length) g.remove(g.children[0]);
        };
        if (this.buildingGroup) disposeGroup(this.buildingGroup);
        if (this.nodeGroup) disposeGroup(this.nodeGroup);
        if (this.linkGroup) disposeGroup(this.linkGroup);
        if (this.cloudGroup) disposeGroup(this.cloudGroup);
        if (this.particleGroup) disposeGroup(this.particleGroup);
        if (this.labelGroup) disposeGroup(this.labelGroup);
    }

    // ------------------------------------------------------------------------
    // Data loading
    // ------------------------------------------------------------------------

    /// External reload: tear down the scene meshes + DOM labels + bookkeeping
    /// state, then re-fetch the snapshot. Used by the wizard panel after the
    /// user saves new monitoring targets so the map picks up the change
    /// without a full page reload.
    async _reloadSnapshot() {
        this._disposeScene();
        this._nodeMeshes.clear();
        this._linkMeshes.clear();
        this._cloudMeshes.clear();
        this._positions.clear();
        this._currentRates = {};
        if (this._labelsLayer) {
            for (const { el } of this._floatingLabels.values()) el.remove();
            for (const { el } of this._linkLabels.values()) el.remove();
            for (const el of this._wanPills.values()) el.remove();
        }
        this._floatingLabels.clear();
        this._linkLabels.clear();
        this._wanPills.clear();
        await this._loadSnapshot();
    }

    async _loadSnapshot() {
        try {
            const res = await fetch(`${this.apiBase}/snapshot`, { credentials: 'same-origin' });
            if (!res.ok) throw new Error(`snapshot HTTP ${res.status}`);
            const snap = await res.json();
            this._snapshot = snap;

            this._layoutNodes(snap);
            this._rebuildBuildings(snap);
            this._buildNodes(snap);
            // Clouds must build before links - links reference cloud node IDs in their
            // FromNodeId/ToNodeId and _buildLinks looks positions up via _positions.
            this._buildClouds(snap);
            this._buildLinks(snap);
            this._buildFloatingLabels(snap);
            this._applyLiveRates(snap.liveRates || {});
        } catch (err) {
            this.onError(err);
        }
    }

    _rebuildBuildings(snap) {
        const disposeGroup = (g) => {
            g.traverse((obj) => {
                if (obj.geometry) obj.geometry.dispose();
                if (obj.material) {
                    if (Array.isArray(obj.material)) obj.material.forEach((m) => m.dispose());
                    else obj.material.dispose();
                }
            });
            while (g.children.length) g.remove(g.children[0]);
        };
        if (this.buildingGroup) {
            disposeGroup(this.buildingGroup);
            this.scene.remove(this.buildingGroup);
        }
        this.buildingGroup = buildBuildings(snap);
        this.buildingGroup.visible = this._overlays.buildings;
        this.scene.add(this.buildingGroup);
    }

    async _pollLive() {
        if (this._destroyed) return;
        try {
            const res = await fetch(`${this.apiBase}/live`, { credentials: 'same-origin' });
            if (!res.ok) return;
            const update = await res.json();
            this._currentBadges = update.nodeBadges || {};
            this._applyLiveRates(update.linkRates || {});
        } catch (err) {
            // Keep ticking; transient network errors are fine.
        }
    }

    // ------------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------------

    _layoutNodes(snap) {
        const bounds = snap.bounds || { radius: 1.0, anchorCount: 0 };
        // Normalize anchor coordinates to a scene-sized sphere (~30 unit radius)
        // and then spread by ANCHOR_SPREAD_FACTOR so interpolated / unanchored
        // devices have room to settle between the pinned APs without crowding.
        const sceneRadius = 30.0;
        const ANCHOR_SPREAD_FACTOR = 1.875;
        const scale = (sceneRadius / Math.max(bounds.radius, 1.0)) * ANCHOR_SPREAD_FACTOR;

        const positions = new Map();
        const anchors = new Map();

        // Mount height offsets within a floor (in meters, pre-scale).
        // ceiling = near ceiling, wall = mid-height, desktop = near floor.
        const WALL_H_M = 2.9;
        const mountOffsetM = { ceiling: WALL_H_M * 0.85, wall: WALL_H_M * 0.5, desktop: WALL_H_M * 0.15 };

        for (const node of snap.nodes) {
            const p = node.placement;
            if (p && p.source === PLACEMENT_SOURCE.Anchor) {
                // Vertical offset within the floor by device type:
                // APs use their mount type, clients mid-floor, infra at desk level.
                const isClient = node.kind === NODE_KIND.WiredClient || node.kind === NODE_KIND.WifiClient;
                const isInfra = node.kind === NODE_KIND.Switch || node.kind === NODE_KIND.Gateway;
                const mountM = node.mountType ? (mountOffsetM[node.mountType] || 0)
                    : isClient ? WALL_H_M * 0.5
                    : isInfra ? WALL_H_M * 0.15
                    : 0;
                positions.set(node.id, {
                    x: -p.x * scale,
                    y: p.z * scale * 0.8 + mountM * scale * 0.8,
                    z: p.y * scale,
                    pinned: true,
                });
                anchors.set(node.id, true);
            } else if (p && p.source === PLACEMENT_SOURCE.Interpolated) {
                positions.set(node.id, {
                    x: -p.x * scale,
                    y: p.z * scale * 0.8 - 4,
                    z: p.y * scale,
                    pinned: false,
                });
            }
        }

        // Initial positions for unpinned nodes: scattered around the origin. Clouds are
        // not LanNode entries (they live in snap.clouds and get positions in _buildClouds);
        // we don't see Cloud kind here in practice.
        for (const node of snap.nodes) {
            if (positions.has(node.id)) continue;
            const theta = (Math.random() * 2 - 1) * Math.PI;
            const r = 12 + Math.random() * 8;
            positions.set(node.id, {
                x: Math.cos(theta) * r,
                y: (Math.random() - 0.5) * 5,
                z: Math.sin(theta) * r,
                pinned: false,
            });
        }

        // Force-directed relaxation: spring along every link, Coulomb-style repulsion
        // between all pairs. Anchors stay fixed. Converges in a few hundred iterations
        // since the graph is small (< ~200 nodes for a typical home/prosumer LAN).
        const links = (snap.links || []).filter((l) => positions.has(l.fromNodeId) && positions.has(l.toNodeId));
        const ids = Array.from(positions.keys());
        const repulsion = 28.0;
        const springRest = 6.0;
        const springK = 0.22;
        const damping = 0.78;

        const velocities = new Map(ids.map((id) => [id, { vx: 0, vy: 0, vz: 0 }]));
        for (let iter = 0; iter < 350; iter += 1) {
            // Pairwise repulsion.
            for (let i = 0; i < ids.length; i += 1) {
                const a = positions.get(ids[i]);
                if (a.pinned) continue;
                let fx = 0, fy = 0, fz = 0;
                for (let j = 0; j < ids.length; j += 1) {
                    if (i === j) continue;
                    const b = positions.get(ids[j]);
                    const dx = a.x - b.x;
                    const dy = a.y - b.y;
                    const dz = a.z - b.z;
                    const d2 = dx * dx + dy * dy + dz * dz + 0.001;
                    const d = Math.sqrt(d2);
                    const f = repulsion / d2;
                    fx += (dx / d) * f;
                    fy += (dy / d) * f;
                    fz += (dz / d) * f;
                }
                const v = velocities.get(ids[i]);
                v.vx = (v.vx + fx) * damping;
                v.vy = (v.vy + fy) * damping;
                v.vz = (v.vz + fz) * damping;
            }
            // Springs along links.
            for (const link of links) {
                const a = positions.get(link.fromNodeId);
                const b = positions.get(link.toNodeId);
                const dx = b.x - a.x;
                const dy = b.y - a.y;
                const dz = b.z - a.z;
                const d = Math.sqrt(dx * dx + dy * dy + dz * dz) + 0.001;
                const f = springK * (d - springRest);
                const ux = dx / d, uy = dy / d, uz = dz / d;
                if (!a.pinned) {
                    const v = velocities.get(link.fromNodeId);
                    v.vx += ux * f;
                    v.vy += uy * f;
                    v.vz += uz * f;
                }
                if (!b.pinned) {
                    const v = velocities.get(link.toNodeId);
                    v.vx -= ux * f;
                    v.vy -= uy * f;
                    v.vz -= uz * f;
                }
            }
            // Integrate.
            for (const id of ids) {
                const p = positions.get(id);
                if (p.pinned) continue;
                const v = velocities.get(id);
                p.x += v.vx;
                p.y += v.vy;
                p.z += v.vz;
            }
        }

        // Post-layout: push WiFi clients outward from their parent AP so they
        // fan out rather than clustering tightly around the infrastructure.
        const WIFI_SPREAD = 1.4;
        for (const node of snap.nodes) {
            if (node.kind !== NODE_KIND.WifiClient) continue;
            const p = positions.get(node.id);
            if (!p || p.pinned) continue;
            const parentId = node.parentId;
            if (!parentId) continue;
            const pp = positions.get(parentId);
            if (!pp) continue;
            const dx = p.x - pp.x;
            const dy = p.y - pp.y;
            const dz = p.z - pp.z;
            const d = Math.sqrt(dx * dx + dy * dy + dz * dz);
            if (d < 0.1) continue;
            p.x = pp.x + dx * WIFI_SPREAD;
            p.y = pp.y + dy * WIFI_SPREAD;
            p.z = pp.z + dz * WIFI_SPREAD;
        }

        this._positions = positions;
    }

    // ------------------------------------------------------------------------
    // Node + link + cloud meshes
    // ------------------------------------------------------------------------

    _buildNodes(snap) {
        for (const node of snap.nodes) {
            if (node.kind === NODE_KIND.Cloud) continue;  // clouds handled separately
            const pos = this._positions.get(node.id);
            if (!pos) continue;

            const radius = this._nodeRadius(node.kind);
            const color = this._nodeColor(node.kind);
            const group = new THREE.Group();

            // Soft outer halo - sized to the bounding sphere of whatever shape we draw.
            const halo = new THREE.Mesh(
                new THREE.SphereGeometry(radius * 1.7, 24, 16),
                new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.12, depthWrite: false }),
            );
            group.add(halo);

            // Distinct shape per kind. Gateway = rounded box (taller); switch = flat
            // chassis cuboid; AP = thin disc-like cylinder; clients = small icosahedra.
            // Each one reads at-a-glance and the bloom pass makes them genuinely glow.
            const baseEmissive = 0.45;
            const core = this._makeDeviceCore(node.kind, radius, color, baseEmissive);
            group.add(core);

            group.position.set(pos.x, pos.y, pos.z);
            if (!node.online) {
                core.material.opacity = 0.55;
                core.material.transparent = true;
                halo.material.opacity = 0.05;
            }
            group.userData = { node, core, baseEmissive };
            this.nodeGroup.add(group);
            this._nodeMeshes.set(node.id, group);

            // Sprite labels for all devices. Infrastructure devices also get DOM
            // labels (with rate badges) but sprites provide 3D depth sorting.
            if (node.name) {
                const sprite = this._makeLabelSprite(node.name);
                sprite.position.set(0, radius + 0.8, 0);
                group.add(sprite);
                this._labelSprites.set(node.id, sprite);
            }
        }
    }

    _makeDeviceCore(kind, radius, color, baseEmissive) {
        const material = (extra = {}) => new THREE.MeshStandardMaterial({
            color,
            emissive: color,
            emissiveIntensity: baseEmissive,
            roughness: 0.55,
            metalness: 0.15,
            ...extra,
        });
        let geo;
        switch (kind) {
            case NODE_KIND.Gateway:
                // Stacked rounded chassis: wider than tall to read as a router.
                geo = new THREE.BoxGeometry(radius * 2.4, radius * 1.3, radius * 1.6);
                break;
            case NODE_KIND.Switch:
                // Low-profile rack-unit cuboid.
                geo = new THREE.BoxGeometry(radius * 2.6, radius * 0.7, radius * 1.4);
                break;
            case NODE_KIND.AccessPoint:
                // Flat disc evocative of a ceiling AP.
                geo = new THREE.CylinderGeometry(radius * 1.3, radius * 1.3, radius * 0.45, 28);
                break;
            case NODE_KIND.WiredClient:
                geo = new THREE.IcosahedronGeometry(radius * 0.95, 0);
                break;
            case NODE_KIND.WifiClient:
                geo = new THREE.OctahedronGeometry(radius * 0.95, 0);
                break;
            case NODE_KIND.VirtualHub:
                // Squat torus reads as "junction / fanout", not a device.
                geo = new THREE.TorusGeometry(radius * 0.85, radius * 0.25, 12, 24);
                break;
            default:
                geo = new THREE.SphereGeometry(radius, 24, 18);
        }
        return new THREE.Mesh(geo, material());
    }

    _buildLinks(snap) {
        for (const link of snap.links || []) {
            const a = this._positions.get(link.fromNodeId);
            const b = this._positions.get(link.toNodeId);
            if (!a || !b) continue;

            // For WAN/Transit links, shorten the cloud end to the globe surface
            // so the pipe terminates at the wireframe, not the center.
            let effA = a, effB = b;
            const isWan = link.kind === LINK_KIND.Wan || link.kind === LINK_KIND.Transit;
            if (isWan) {
                const r = NODE_RADIUS.cloud;
                const cloudEnd = this._cloudMeshes.has(link.toNodeId) ? 'b'
                    : this._cloudMeshes.has(link.fromNodeId) ? 'a' : null;
                if (cloudEnd) {
                    const from = cloudEnd === 'b' ? a : b;
                    const to = cloudEnd === 'b' ? b : a;
                    const dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
                    const d = Math.sqrt(dx * dx + dy * dy + dz * dz) || 1;
                    const surfPt = { x: to.x - (dx / d) * r, y: to.y - (dy / d) * r, z: to.z - (dz / d) * r };
                    if (cloudEnd === 'b') effB = surfPt; else effA = surfPt;
                }
            }

            const pipe = this._makePipeMesh(effA, effB, link);
            this.linkGroup.add(pipe);

            const down = new ParticleStream({
                from: effA, to: effB, color: COLORS.downstream, particleCount: 0,
            });
            const up = new ParticleStream({
                from: effB, to: effA, color: COLORS.upstream, particleCount: 0,
            });
            this.particleGroup.add(down.mesh, up.mesh);

            this._linkMeshes.set(link.id, { pipe, down, up, link });
            this._nodesByLink.set(link.id, [link.fromNodeId, link.toNodeId]);
        }
    }

    _buildClouds(snap) {
        // Place clouds along the bisector of the widest XZ angular gap between
        // LAN-fabric vertices as seen from the gateway, so the WAN link is
        // equidistant from the two nearest fabric nodes on either side. This
        // avoids crossings over LAN devices regardless of how the force solver
        // settled the topology. Vertical (Y) is ignored - the gap math is
        // purely on the horizontal plane.
        const gatewayNode = (snap.nodes || []).find((n) => n.kind === NODE_KIND.Gateway);
        const gwPos = gatewayNode ? this._positions.get(gatewayNode.id) : null;
        let gwX = 0, gwY = 0, gwZ = 0;
        if (gwPos) { gwX = gwPos.x; gwY = gwPos.y; gwZ = gwPos.z; }

        // Collect bearings of every other fabric vertex (switch + AP) from
        // the gateway. Exclude clients - they're leaves and don't constrain
        // the trunk lines visually.
        const bearings = [];
        for (const n of snap.nodes || []) {
            if (n.id === gatewayNode?.id) continue;
            if (n.kind !== NODE_KIND.Switch && n.kind !== NODE_KIND.AccessPoint) continue;
            const p = this._positions.get(n.id);
            if (!p) continue;
            const dx = p.x - gwX, dz = p.z - gwZ;
            if (Math.hypot(dx, dz) < 0.5) continue;
            bearings.push(Math.atan2(dz, dx));
        }

        let outBearing = 0;
        if (bearings.length === 0) {
            // No fabric to dodge - default to +X.
            outBearing = 0;
        } else if (bearings.length === 1) {
            // Single peer - put the cloud diametrically opposite it.
            outBearing = bearings[0] + Math.PI;
        } else {
            bearings.sort((a, b) => a - b);
            let maxGap = -Infinity;
            for (let i = 0; i < bearings.length; i++) {
                const next = i === bearings.length - 1
                    ? bearings[0] + 2 * Math.PI
                    : bearings[i + 1];
                const gap = next - bearings[i];
                if (gap > maxGap) {
                    maxGap = gap;
                    outBearing = (bearings[i] + next) / 2;
                }
            }
        }
        const dirX = Math.cos(outBearing);
        const dirZ = Math.sin(outBearing);

        // Access cloud on the outbound axis; all other clouds (transits +
        // path-ends) fan out around it in an arc. Arc grows with sibling
        // count up to a near-full 270 deg so 10-12 clouds clearly form a
        // half-bowl around the access cloud rather than stacking. Stagger
        // both radius (alternating inner/outer ring) and Y so the labels
        // don't pile up.
        const accessRadius = 40;
        const innerRadius = 80;
        const outerRadius = 100;
        const allClouds = snap.clouds || [];
        const accessClouds = allClouds.filter((c) => c.order === 0);
        const siblings = allClouds.filter((c) => c.order > 0);
        const fanRad = Math.min(Math.PI * 1.5, Math.max(Math.PI * 0.5, siblings.length * (Math.PI / 8)));
        const arcStep = siblings.length > 1 ? fanRad / (siblings.length - 1) : 0;
        const arcStart = -fanRad / 2;

        const placeCloud = (cloud, x, y, z) => {
            const pos = { x, y, z, pinned: true };
            this._positions.set(cloud.id, pos);
            return pos;
        };

        const cloudPositions = new Map();
        for (const cloud of accessClouds) {
            const x = gwX + dirX * accessRadius;
            const y = gwY + 4;
            const z = gwZ + dirZ * accessRadius;
            cloudPositions.set(cloud.id, placeCloud(cloud, x, y, z));
        }
        for (let i = 0; i < siblings.length; i++) {
            const cloud = siblings[i];
            const angle = outBearing + arcStart + arcStep * i;
            const r = (i % 2 === 0) ? innerRadius : outerRadius;
            const x = gwX + Math.cos(angle) * r;
            const y = gwY + 4 + (i % 3) * 4;
            const z = gwZ + Math.sin(angle) * r;
            cloudPositions.set(cloud.id, placeCloud(cloud, x, y, z));
        }

        for (const cloud of allClouds) {
            const pos = cloudPositions.get(cloud.id);
            if (!pos) continue;

            const group = new THREE.Group();
            // Tier (DiscoveryMethod) drives the visual posture:
            //   Solid     - bright, opaque cloud
            //   PathProxy - dashed/dimmer, "via path" tag overlaid on label
            //   Unresolved - neutral grey, "discovery pending" tag, no RTT badge
            const tier = cloud.tier ?? CLOUD_TIER.Solid;
            const baseOpacity = tier === CLOUD_TIER.Solid ? 0.85
                              : tier === CLOUD_TIER.PathProxy ? 0.55
                              : 0.35;
            const baseColor = tier === CLOUD_TIER.Unresolved ? 0x2a3340 : COLORS.cloud;

            // Wireframe globe - no solid sphere, just the lat/lng grid lines.
            // LineBasicMaterial.linewidth doesn't work on WebGL, so we use
            // thin tube geometry for each line to get visible thickness.
            const r = NODE_RADIUS.cloud;
            const gridColor = tier === CLOUD_TIER.Unresolved ? 0x3a4455 : 0x3385d6;
            const tubeMat = new THREE.MeshBasicMaterial({
                color: gridColor,
                transparent: true,
                opacity: baseOpacity * 0.6,
            });
            const tubeRadius = r * 0.02;
            // Latitude lines
            for (let lat = -75; lat <= 75; lat += 25) {
                const phi = (90 - lat) * Math.PI / 180;
                const pts = [];
                for (let lng = 0; lng <= 360; lng += 10) {
                    const theta = lng * Math.PI / 180;
                    pts.push(new THREE.Vector3(
                        r * Math.sin(phi) * Math.cos(theta),
                        r * Math.cos(phi),
                        r * Math.sin(phi) * Math.sin(theta),
                    ));
                }
                const curve = new THREE.CatmullRomCurve3(pts);
                group.add(new THREE.Mesh(new THREE.TubeGeometry(curve, pts.length, tubeRadius, 4, false), tubeMat));
            }
            // Longitude lines
            for (let lng = 0; lng < 360; lng += 30) {
                const theta = lng * Math.PI / 180;
                const pts = [];
                for (let lat = -90; lat <= 90; lat += 10) {
                    const phi = (90 - lat) * Math.PI / 180;
                    pts.push(new THREE.Vector3(
                        r * Math.sin(phi) * Math.cos(theta),
                        r * Math.cos(phi),
                        r * Math.sin(phi) * Math.sin(theta),
                    ));
                }
                const curve = new THREE.CatmullRomCurve3(pts);
                group.add(new THREE.Mesh(new THREE.TubeGeometry(curve, pts.length, tubeRadius, 4, false), tubeMat));
            }

            // Subtle outer glow
            const glow = new THREE.Mesh(
                new THREE.SphereGeometry(r * 1.5, 24, 16),
                new THREE.MeshBasicMaterial({ color: 0x1a3050, transparent: true, opacity: 0.08, depthWrite: false }),
            );
            group.add(glow);

            // Label: ASN name (primary), with sub-tags for access-tech / OUI / tier hint.
            const labelText = cloud.name || (cloud.asn ? `AS${cloud.asn}` : 'Cloud');
            const subText = this._buildCloudSubLabel(cloud, tier);
            const label = this._makeLabelSprite(labelText, subText);
            label.position.set(0, NODE_RADIUS.cloud + 1.2, 0);
            group.add(label);

            group.position.set(pos.x, pos.y, pos.z);
            group.userData = { cloud };
            this.cloudGroup.add(group);
            this._cloudMeshes.set(cloud.id, group);
        }
    }

    _makePipeMesh(a, b, link) {
        const from = new THREE.Vector3(a.x, a.y, a.z);
        const to = new THREE.Vector3(b.x, b.y, b.z);
        const dir = to.clone().sub(from);
        const length = dir.length();
        if (length < 0.01) return new THREE.Group();

        const baseRadius = this._pipeRadiusForCapacity(link.capacityBps);
        const geo = new THREE.CylinderGeometry(baseRadius, baseRadius, length, 14, 1, true);
        const mat = new THREE.MeshStandardMaterial({
            color: COLORS.pipeCool,
            emissive: COLORS.pipeCool,
            emissiveIntensity: 0.25,
            roughness: 0.8,
            metalness: 0.0,
            transparent: true,
            opacity: 0.45,
        });
        const mesh = new THREE.Mesh(geo, mat);

        // CylinderGeometry is aligned to the Y axis. Orient it along the link vector and
        // position at the midpoint.
        const mid = from.clone().add(to).multiplyScalar(0.5);
        mesh.position.copy(mid);
        mesh.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.clone().normalize());
        mesh.userData = { link, baseRadius };
        return mesh;
    }

    _pipeRadiusForCapacity(capacityBps) {
        if (!capacityBps || capacityBps <= 0) return 0.10;
        // Log scale: 100 Mbps -> 0.13, 1 Gbps -> 0.18, 10 Gbps -> 0.24, 25 Gbps -> 0.28.
        const gbps = capacityBps / 1_000_000_000;
        const t = Math.log10(Math.max(gbps, 0.01)) + 2;  // 1 Mbps -> 0, 10 Gbps -> 3
        return 0.10 + Math.min(t, 3.5) * 0.05;
    }

    _nodeRadius(kind) {
        switch (kind) {
            case NODE_KIND.Gateway: return NODE_RADIUS.gateway;
            case NODE_KIND.Switch: return NODE_RADIUS.switch;
            case NODE_KIND.AccessPoint: return NODE_RADIUS.ap;
            case NODE_KIND.WiredClient: return NODE_RADIUS.wiredClient;
            case NODE_KIND.WifiClient: return NODE_RADIUS.wifiClient;
            case NODE_KIND.VirtualHub: return NODE_RADIUS.virtualHub;
            default: return 0.6;
        }
    }

    _nodeColor(kind) {
        switch (kind) {
            case NODE_KIND.Gateway: return COLORS.gateway;
            case NODE_KIND.Switch: return COLORS.switchNode;
            case NODE_KIND.AccessPoint: return COLORS.ap;
            case NODE_KIND.WiredClient: return COLORS.wiredClient;
            case NODE_KIND.WifiClient: return COLORS.wifiClient;
            case NODE_KIND.VirtualHub: return COLORS.virtualHub;
            default: return COLORS.accent;
        }
    }

    _makeLabelSprite(text, subText = null) {
        const dm = window.DemoMask;
        if (dm?.isEnabled()) {
            text = dm.maskString?.(text) ?? text;
            if (subText) subText = dm.maskString?.(subText) ?? subText;
        }
        const canvas = document.createElement('canvas');
        const ctx = canvas.getContext('2d');
        const fontSize = 36;
        const subFontSize = 22;
        const pad = 16;
        ctx.font = `${fontSize}px ui-sans-serif, system-ui, sans-serif`;
        const titleW = Math.ceil(ctx.measureText(text).width);
        let subW = 0;
        if (subText) {
            ctx.font = `${subFontSize}px ui-sans-serif, system-ui, sans-serif`;
            subW = Math.ceil(ctx.measureText(subText).width);
        }
        const w = Math.max(titleW, subW) + pad * 2;
        const h = subText ? fontSize + subFontSize + pad * 2 + 6 : fontSize + pad * 2;
        canvas.width = w;
        canvas.height = h;
        ctx.fillStyle = 'rgba(6, 8, 12, 0.92)';
        roundRect(ctx, 0, 0, w, h, 12);
        ctx.fillStyle = '#f1f5f9';
        ctx.textBaseline = 'top';
        ctx.font = `${fontSize}px ui-sans-serif, system-ui, sans-serif`;
        ctx.fillText(text, pad, pad);
        if (subText) {
            ctx.fillStyle = '#94a3b8';
            ctx.font = `${subFontSize}px ui-sans-serif, system-ui, sans-serif`;
            ctx.fillText(subText, pad, pad + fontSize + 6);
        }
        const tex = new THREE.CanvasTexture(canvas);
        tex.needsUpdate = true;
        const mat = new THREE.SpriteMaterial({ map: tex, transparent: true, depthWrite: false });
        const sprite = new THREE.Sprite(mat);
        const scaleY = 1.2 * (h / (fontSize + pad * 2));
        const scaleX = scaleY * (w / h);
        sprite.scale.set(scaleX, scaleY, 1);
        return sprite;
    }

    _buildCloudSubLabel(cloud, tier) {
        const parts = [];
        if (cloud.accessTechnology) parts.push(cloud.accessTechnology);
        if (cloud.l2NeighborOui) parts.push(cloud.l2NeighborOui);
        if (cloud.isCgnat) parts.push('CGNAT');
        if (tier === CLOUD_TIER.PathProxy) parts.push('via path');
        if (tier === CLOUD_TIER.Unresolved) parts.push('discovery pending');
        if (cloud.rttAvgMs && Number.isFinite(cloud.rttAvgMs)) {
            parts.push(`${cloud.rttAvgMs.toFixed(1)} ms`);
        }
        return parts.length ? parts.join('  ·  ') : null;
    }

    // ------------------------------------------------------------------------
    // Live rate -> particle stream + pipe color
    // ------------------------------------------------------------------------

    _applyLiveRates(rates) {
        // Merge the incoming rates with whatever we already had. The 2s live tick
        // payload only includes link types the backend actively refreshes (uplink,
        // wifi, WAN) - wired client links are seeded only at snapshot rebuild
        // (every 30s). A wholesale replace was wiping wired client rates 2 seconds
        // after each snapshot, leaving the leaf pipes idle forever.
        this._currentRates = { ...(this._currentRates || {}), ...rates };
        for (const [linkId, link] of this._linkMeshes) {
            const r = this._currentRates[linkId];
            if (!r) {
                link.down.setRate(0);
                link.up.setRate(0);
                this._setPipeHealth(link.pipe, 0);
                continue;
            }
            link.down.setRate(r.downstreamBps || 0);
            link.up.setRate(r.upstreamBps || 0);

            // Health: utilization = max(down, up) / capacity. If we don't have capacity,
            // fall back to a fixed 1 Gbps reference so it still reads.
            const capacity = (link.link.capacityBps && link.link.capacityBps > 0)
                ? link.link.capacityBps
                : 1_000_000_000;
            const peak = Math.max(r.downstreamBps || 0, r.upstreamBps || 0);
            const util = Math.min(peak / capacity, 1.0);
            this._setPipeHealth(link.pipe, util);
        }
        // Refresh the device-rate text on the floating DOM labels.
        this._refreshDeviceLabelRates();
        this._refreshLinkLabels();
    }

    _refreshLinkLabels() {
        if (!this._linkLabels || this._linkLabels.size === 0) return;
        for (const [linkId, { el }] of this._linkLabels) {
            const r = this._currentRates?.[linkId];
            const down = r?.downstreamBps || 0;
            const up = r?.upstreamBps || 0;
            if (down < LINK_LABEL_THRESHOLD_BPS && up < LINK_LABEL_THRESHOLD_BPS) {
                el.classList.remove('is-visible');
                el._hasData = false;
                continue;
            }
            el.innerHTML = `<span class="down">↓ ${formatBps(down)}</span><span class="up">↑ ${formatBps(up)}</span>`;
            // Don't add is-visible here — _updateFloatingLabels will add it
            // after positioning, so the label never flashes at a stale position.
            el._hasData = true;
        }
    }

    _setPipeHealth(pipe, utilization) {
        if (!pipe.material) return;
        const u = Math.max(0, Math.min(1, utilization));
        // 0 ..  0.7  : cool blue
        // 0.7 .. 0.9 : warm amber
        // 0.9 ..  1  : red
        let color;
        if (u < 0.7) {
            color = lerpColor(COLORS.pipeCool, COLORS.pipeCool, 0);
        } else if (u < 0.9) {
            color = lerpColor(COLORS.pipeCool, COLORS.pipeWarm, (u - 0.7) / 0.2);
        } else {
            color = lerpColor(COLORS.pipeWarm, COLORS.pipeHot, (u - 0.9) / 0.1);
        }
        pipe.material.color.setHex(color);
        pipe.material.emissive.setHex(color);
        pipe.material.emissiveIntensity = 0.25 + u * 0.55;
        pipe.material.opacity = 0.45 + u * 0.35;
    }

    // ------------------------------------------------------------------------
    // Animation + polling
    // ------------------------------------------------------------------------

    _startAnimation() {
        // Schedule a one-shot camera fly-in on first frame.
        this._flyInUntil = performance.now() + 1300;
        this._flyInTargetCam = new THREE.Vector3(40, 25, 40);
        this._flyInStartCam = this.camera.position.clone();

        // Cap render rate at 120 fps. setAnimationLoop is the modern Three.js
        // entry point (also required for WebXR). We accumulate elapsed time
        // and only do the render+update work once per frameInterval, so on
        // 240+ Hz monitors we run at 120 instead of burning GPU for an
        // essentially-static scene. 120 over the more usual 60 because the
        // particle streams look noticeably smoother at the higher cap.
        const TARGET_FPS = 120;
        const FRAME_MS = 1000 / TARGET_FPS;
        let lastTickMs = 0;
        let accumulator = 0;

        this.renderer.setAnimationLoop((now) => {
            if (this._destroyed) {
                this.renderer.setAnimationLoop(null);
                return;
            }
            // Clamp delta so a backgrounded tab (which throttles RAF to ~1 Hz)
            // doesn't dump multiple seconds of accumulated dt into the particle
            // physics when the tab regains focus.
            const rawDelta = lastTickMs ? Math.min(now - lastTickMs, 100) : 0;
            lastTickMs = now;
            accumulator += rawDelta;
            if (accumulator < FRAME_MS) return;
            // dt = time since the last ACCEPTED render, not since the last RAF
            // callback. Otherwise on a 240 Hz monitor with a 120 fps cap, we'd
            // advance particles by ~4 ms of physics while ~8 ms of wall-clock
            // actually elapsed, making them look like they're moving at half
            // speed. Read accumulator before modulo'ing it down.
            const dt = Math.min(accumulator / 1000, 0.1);
            // Use modulo (not reset) to preserve leftover time and avoid drift
            // that would settle the effective cap below the target.
            accumulator = accumulator % FRAME_MS;

            // Camera fly-in (easeOutCubic) on first ~1.3 s.
            if (now < this._flyInUntil) {
                const t = 1 - (this._flyInUntil - now) / 1300;
                const eased = 1 - Math.pow(1 - t, 3);
                this.camera.position.lerpVectors(this._flyInStartCam, this._flyInTargetCam, eased);
                this.camera.lookAt(0, 0, 0);
            } else if (this._repositionMode && this._repositionGroup) {
                // In reposition mode WASD nudges the device on the XZ plane
                if (this._keys) {
                    const step = 0.35;
                    const cam = this.camera;
                    const forward = new THREE.Vector3();
                    cam.getWorldDirection(forward);
                    forward.y = 0;
                    forward.normalize();
                    const right = new THREE.Vector3();
                    right.crossVectors(forward, cam.up).normalize();

                    const g = this._repositionGroup;
                    if (this._keys['w']) { g.position.x += forward.x * step; g.position.z += forward.z * step; }
                    if (this._keys['s']) { g.position.x -= forward.x * step; g.position.z -= forward.z * step; }
                    if (this._keys['d']) { g.position.x += right.x * step; g.position.z += right.z * step; }
                    if (this._keys['a']) { g.position.x -= right.x * step; g.position.z -= right.z * step; }
                    if (this._keys['e']) { g.position.y += step; this._repositionPlane.constant = -g.position.y; }
                    if (this._keys['q']) { g.position.y -= step; this._repositionPlane.constant = -g.position.y; }
                    if (this._keys['w'] || this._keys['a'] || this._keys['s'] || this._keys['d'] || this._keys['q'] || this._keys['e']) {
                        this._updateAdjacentLinks();
                    }
                }
            } else {
                // WASD: W/S zoom, A/D orbit around target
                if (this._keys) {
                    const cam = this.camera;
                    const target = this.controls.target;
                    const offset = cam.position.clone().sub(target);
                    const dist = offset.length();
                    const zoomStep = dist * 0.0225;
                    const panDist = dist * 0.012;

                    if (this._keys['w'] && dist > this.controls.minDistance + zoomStep) {
                        offset.multiplyScalar(1 - zoomStep / dist);
                        cam.position.copy(target).add(offset);
                    }
                    if (this._keys['s'] && dist < this.controls.maxDistance - zoomStep) {
                        offset.multiplyScalar(1 + zoomStep / dist);
                        cam.position.copy(target).add(offset);
                    }
                    if (this._keys['a'] || this._keys['d']) {
                        const right = new THREE.Vector3();
                        cam.getWorldDirection(right);
                        right.cross(cam.up).normalize();
                        const panOffset = right.multiplyScalar(this._keys['a'] ? -panDist : panDist);
                        cam.position.add(panOffset);
                        target.add(panOffset);
                    }
                }
                this.controls?.update();
            }

            // Freeze particle motion while paused (Live or Historic) so the
            // dots visibly stop with the polling; otherwise they keep flowing
            // off whatever the last rate snapshot was.
            if (!this._paused) {
                for (const link of this._linkMeshes.values()) {
                    link.down.advance(dt);
                    link.up.advance(dt);
                }
            }
            // Subtle node breathing: every infrastructure node's emissive intensity
            // oscillates around its base value on a ~3 s cycle. Idle, traffic-less
            // scenes still feel alive.
            this._pulseNodes(now);

            // Render through the composer so bloom + tone mapping apply.
            this.composer.render();
            // After the render we have up-to-date matrixWorld for every node group;
            // project them to screen space for the floating DOM labels and WAN pills.
            this._updateFloatingLabels();
        });
    }

    _pulseNodes(nowMs) {
        const phase = (nowMs % 3000) / 3000;       // 0..1
        const factor = 0.85 + 0.15 * Math.sin(phase * Math.PI * 2);
        for (const group of this._nodeMeshes.values()) {
            const core = group.userData?.core;
            const base = group.userData?.baseEmissive;
            if (!core || base == null) continue;
            core.material.emissiveIntensity = base * factor;
        }
    }

    _startPolling() {
        if (this._pollTimer) clearInterval(this._pollTimer);
        this._pollTimer = setInterval(() => {
            if (this._mode === 'live' && !this._paused) this._pollLive();
        }, this.pollIntervalMs);
    }

    // Play/Pause: in Live mode, pause freezes rates by skipping the poll. In
    // Historic mode, play advances the scrubber forward over time so the user
    // can watch the recent window roll back into Live.
    _togglePlayPause() {
        this._paused = !this._paused;
        this._syncPlayPauseIcon();
        if (this._paused) {
            this._stopHistoricPlayback();
            return;
        }
        if (this._mode === 'historic') this._startHistoricPlayback();
        else this._pollLive();
    }

    _startHistoricPlayback() {
        if (this._historicPlaybackTimer) return;
        // Track playback as a continuous timestamp, not integer slider units.
        // The slider and time label update every tick; the map and stat cards
        // refresh on a throttled cadence (~3 s wall-clock) to avoid flooding
        // the API while still feeling responsive.
        const TICK_MS = 1000;
        const DATA_REFRESH_TICKS = 3; // load new data every 3 ticks
        this._playbackTime = this._historicAt
            || this._scrubberValueToTime(Number(this._panels.scrubberRange?.value ?? 500));
        let tickCount = 0;
        this._historicPlaybackTimer = setInterval(() => {
            if (this._paused) return;
            const range = this._panels.scrubberRange;
            if (!range) return;
            // Advance the continuous timestamp
            this._playbackTime = new Date(
                this._playbackTime.getTime() + TICK_MS * this._playbackSpeed);
            // Derive slider position from the timestamp (inverse of _scrubberValueToTime)
            const now = Date.now();
            const span = now - this._scrubberOrigin;
            const value = span > 0
                ? Math.round((this._playbackTime.getTime() - this._scrubberOrigin) / span * 1000)
                : 1000;
            const clamped = Math.max(0, Math.min(1000, value));
            range.value = clamped;
            // Update the time label from the continuous timestamp, not the
            // integer slider position (which only moves every ~86s at 1x).
            if (this._panels.scrubberRight) {
                this._panels.scrubberRight.textContent =
                    (clamped >= 998) ? 'Live' : _fmtDateTime(this._playbackTime);
            }
            tickCount++;
            // Refresh map and stat cards periodically
            if (tickCount % DATA_REFRESH_TICKS === 0 || clamped >= 998) {
                this._playbackAdvancing = true;
                try {
                    if (clamped >= 998) {
                        this._stopHistoricPlayback();
                        this._onScrubberChange(1000);
                    } else {
                        this._historicAt = this._playbackTime;
                        this._notifyStatCards(this._playbackTime);
                        this._loadHistoric(this._playbackTime);
                    }
                } finally {
                    this._playbackAdvancing = false;
                }
            }
        }, TICK_MS);
    }

    _stopHistoricPlayback() {
        if (this._historicPlaybackTimer) {
            clearInterval(this._historicPlaybackTimer);
            this._historicPlaybackTimer = null;
        }
    }

    // ------------------------------------------------------------------------
    // Overlay UI (controls, filter, legend, scrubber, mode indicator)
    // ------------------------------------------------------------------------

    _buildOverlayUI() {
        if (!this.stage) return;

        const isMobile = window.matchMedia('(max-width: 768px)').matches;

        // Filter panel (top-left)
        const filter = this._makePanel('lan-flow-map-filter');
        const filterTitle = document.createElement('div');
        filterTitle.className = 'lan-flow-map-panel-title';
        filterTitle.textContent = isMobile ? 'Filter' : 'Filter clients';
        if (isMobile) filterTitle.classList.add('lan-flow-map-panel-title-toggle');
        filter.appendChild(filterTitle);
        const filterBody = document.createElement('div');
        filterBody.className = 'lan-flow-map-panel-body';
        filterBody.innerHTML = `
            <input class="lan-flow-map-search" type="search" placeholder="Search by name or MAC" />
            <div class="lan-flow-map-chips" data-chip-group="band">
                <span class="lan-flow-map-chip is-on" data-band="2.4">2.4 GHz</span>
                <span class="lan-flow-map-chip is-on" data-band="5">5 GHz</span>
                <span class="lan-flow-map-chip is-on" data-band="6">6 GHz</span>
            </div>
        `;
        if (isMobile) filterBody.hidden = true;
        filter.appendChild(filterBody);
        if (isMobile) {
            filterTitle.addEventListener('click', () => this._toggleMobilePanel('filter'));
        }
        const search = filterBody.querySelector('.lan-flow-map-search');
        search.addEventListener('input', (e) => {
            this._filter.text = (e.target.value || '').toLowerCase().trim();
            this._applyFilter();
        });
        const bandChips = Array.from(filterBody.querySelectorAll('.lan-flow-map-chip'));
        bandChips.forEach((chip) => {
            chip.addEventListener('click', () => {
                const b = chip.dataset.band;
                const allOn = bandChips.every((c) => this._filter.bands[c.dataset.band]);
                if (allOn) {
                    for (const c of bandChips) {
                        const cb = c.dataset.band;
                        this._filter.bands[cb] = (cb === b);
                        c.classList.toggle('is-on', this._filter.bands[cb]);
                    }
                } else {
                    const onlyThisOn = this._filter.bands[b]
                        && bandChips.every((c) => c.dataset.band === b || !this._filter.bands[c.dataset.band]);
                    if (onlyThisOn) {
                        for (const c of bandChips) {
                            this._filter.bands[c.dataset.band] = true;
                            c.classList.add('is-on');
                        }
                    } else {
                        this._filter.bands[b] = !this._filter.bands[b];
                        chip.classList.toggle('is-on', this._filter.bands[b]);
                    }
                }
                this._applyFilter();
            });
        });
        this._panels.filter = filter;
        this._panels.filterBody = filterBody;

        // Controls panel (top-right) - overlay toggles
        const controls = this._makePanel('lan-flow-map-controls');
        const controlsTitle = document.createElement('div');
        controlsTitle.className = 'lan-flow-map-panel-title';
        controlsTitle.textContent = 'Overlays';
        if (isMobile) controlsTitle.classList.add('lan-flow-map-panel-title-toggle');
        controls.appendChild(controlsTitle);
        const controlsBody = document.createElement('div');
        controlsBody.className = 'lan-flow-map-panel-body';
        if (isMobile) controlsBody.hidden = true;
        controls.appendChild(controlsBody);
        if (isMobile) {
            controlsTitle.addEventListener('click', () => this._toggleMobilePanel('controls'));
        }
        const overlayDefs = [
            ['wifiClients', 'Wi-Fi clients'],
            ['wiredClients', 'Wired clients'],
            ['clouds', 'WAN globes'],
            ['buildings', 'Buildings'],
            // TODO: Speed test path overlay needs rework - see research/monitoring/3d-map-overlays-TODO.md
        ];
        for (const [key, label] of overlayDefs) {
            const row = document.createElement('div');
            row.className = `lan-flow-map-toggle ${this._overlays[key] ? 'is-on' : ''}`;
            row.innerHTML = `<span>${label}</span><span class="lan-flow-map-toggle-pill"></span>`;
            row.addEventListener('click', () => {
                this._overlays[key] = !this._overlays[key];
                row.classList.toggle('is-on', this._overlays[key]);
                this._applyOverlayVisibility();
                if (key === 'speedTests') this._renderSpeedTestOverlay();
                try { localStorage.setItem('lanFlowMapOverlays', JSON.stringify(this._overlays)); } catch {}
            });
            controlsBody.appendChild(row);
        }
        this._panels.controls = controls;
        this._panels.controlsBody = controlsBody;

        // Fullscreen toggle
        const fsBtn = document.createElement('button');
        fsBtn.className = 'lan-flow-map-fullscreen-btn';
        fsBtn.setAttribute('data-tooltip', 'Fullscreen');
        fsBtn.setAttribute('data-tooltip-hover-only', '');
        fsBtn.innerHTML = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="3 8 3 3 8 3"></polyline>
            <polyline points="16 3 21 3 21 8"></polyline>
            <polyline points="21 16 21 21 16 21"></polyline>
            <polyline points="8 21 3 21 3 16"></polyline>
        </svg>`;
        fsBtn.addEventListener('click', () => this._toggleFullscreen());
        this.stage.appendChild(fsBtn);
        this._panels.fullscreenBtn = fsBtn;

        // Legend (bottom-right)
        const legend = this._makePanel('lan-flow-map-legend');
        legend.innerHTML = `
            <span class="lan-flow-map-legend-dot down"></span> Download
            <span class="lan-flow-map-legend-dot up"></span> Upload
        `;
        this._panels.legend = legend;

        // Controls help (collapsed pill, expands on click). OrbitControls handles
        // the actual input bindings; this just documents them so users can find
        // their way around without trial and error.
        const help = this._makePanel('lan-flow-map-help');
        help.innerHTML = `
            <button class="lan-flow-map-help-toggle" type="button" aria-expanded="false">
                <span class="lan-flow-map-help-icon">?</span>
                <span class="lan-flow-map-help-label">Controls</span>
            </button>
            <div class="lan-flow-map-help-body" hidden>
                <div class="lan-flow-map-help-row"><span>Rotate</span><span class="kbd">Left-drag</span></div>
                <div class="lan-flow-map-help-row"><span>Pan</span><span class="kbd">Right-drag</span> or <span class="kbd">A</span> <span class="kbd">D</span></div>
                <div class="lan-flow-map-help-row"><span>Zoom</span><span class="kbd">Scroll</span> or <span class="kbd">W</span> <span class="kbd">S</span></div>
                <div class="lan-flow-map-help-row"><span>Hover detail</span><span class="kbd">Mouse over</span></div>
                <div class="lan-flow-map-help-row"><span>Open client</span><span class="kbd">Double-click</span></div>
                <div class="lan-flow-map-help-row"><span>Move device</span><span class="kbd">Right-click</span></div>
                <div class="lan-flow-map-help-row"><span>Fullscreen</span><span class="kbd">Esc</span> to exit</div>
            </div>
        `;
        const helpToggle = help.querySelector('.lan-flow-map-help-toggle');
        const helpBody = help.querySelector('.lan-flow-map-help-body');
        helpToggle.addEventListener('click', () => {
            const isOpen = helpBody.hasAttribute('hidden') === false;
            if (isOpen) { helpBody.setAttribute('hidden', ''); helpToggle.setAttribute('aria-expanded', 'false'); }
            else { helpBody.removeAttribute('hidden'); helpToggle.setAttribute('aria-expanded', 'true'); }
        });
        this._panels.help = help;

        // Status / mode indicator (bottom-left)
        const status = this._makePanel('lan-flow-map-status');
        const modeBadge = document.createElement('span');
        modeBadge.className = 'lan-flow-map-mode';
        modeBadge.textContent = 'Live';
        modeBadge.setAttribute('data-tooltip-hover-only', '');
        modeBadge.addEventListener('click', () => {
            if (this._mode === 'live') return;
            const range = this._panels.scrubberRange;
            if (range) range.value = 1000;
            this._onScrubberChange(1000);
        });
        status.appendChild(modeBadge);
        this._panels.status = status;
        this._panels.modeBadge = modeBadge;

        // Timeline scrubber (bottom center). The right-side label swaps between
        // "Live" and a long locale datetime as you scrub; without a min-width on
        // that span the row reflows and the slider thumb jumps the moment you
        // approach the right edge, making it almost impossible to land on Live.
        const scrubber = document.createElement('div');
        scrubber.className = 'lan-flow-map-scrubber';
        scrubber.innerHTML = `
            <div class="lan-flow-map-scrubber-row">
                <button class="lan-flow-map-scrubber-playpause" data-role="playpause" type="button" aria-label="Pause">⏸</button>
                <div class="lan-flow-map-speed-control" data-role="speed">
                    <button class="lan-flow-map-speed-step" data-dir="-1" type="button" aria-label="Slower">-</button>
                    <span class="lan-flow-map-speed-label" data-role="speed-label">1x</span>
                    <button class="lan-flow-map-speed-step" data-dir="1" type="button" aria-label="Faster">+</button>
                </div>
                <span data-role="left">-24h</span>
                <input class="lan-flow-map-scrubber-range" type="range" min="0" max="1000" value="1000" />
                <span data-role="right">Live</span>
            </div>
        `;
        const range = scrubber.querySelector('.lan-flow-map-scrubber-range');
        range.addEventListener('input', (e) => this._onScrubberInput(Number(e.target.value)));
        range.addEventListener('change', (e) => this._onScrubberChange(Number(e.target.value)));
        // User grabbing the thumb implicitly cancels any active historic playback.
        range.addEventListener('pointerdown', () => this._stopHistoricPlayback());
        const playPause = scrubber.querySelector('[data-role="playpause"]');
        playPause.addEventListener('click', () => this._togglePlayPause());
        const SPEED_STEPS = [1, 2, 5, 10, 30, 60, 120, 360, 720, 1440];
        this._speedSteps = SPEED_STEPS;
        this._speedIndex = 0; // starts at 1x
        for (const btn of scrubber.querySelectorAll('.lan-flow-map-speed-step')) {
            btn.addEventListener('click', () => {
                const dir = Number(btn.dataset.dir);
                const newIdx = Math.max(0, Math.min(SPEED_STEPS.length - 1, this._speedIndex + dir));
                if (newIdx === this._speedIndex) return;
                this._speedIndex = newIdx;
                this._playbackSpeed = SPEED_STEPS[newIdx];
                this._syncSpeedLabel();
                if (this._mode === 'historic' && this._historicPlaybackTimer) {
                    this._stopHistoricPlayback();
                    this._startHistoricPlayback();
                }
            });
        }
        if (isMobile) {
            this.stage.parentElement.insertBefore(scrubber, this.stage.nextSibling);
        } else {
            this.stage.appendChild(scrubber);
        }
        this._panels.scrubber = scrubber;
        this._panels.scrubberRange = range;
        this._panels.scrubberLeft = scrubber.querySelector('[data-role="left"]');
        this._panels.scrubberRight = scrubber.querySelector('[data-role="right"]');
        this._panels.scrubberPlayPause = playPause;
        this._paused = false;
        this._syncSpeedLabel();
    }

    _makePanel(extraClass) {
        const el = document.createElement('div');
        el.className = `lan-flow-map-panel ${extraClass}`;
        this.stage.appendChild(el);
        return el;
    }

    _toggleMobilePanel(panelKey) {
        const panels = { filter: this._panels.filterBody, controls: this._panels.controlsBody };
        const target = panels[panelKey];
        if (!target) return;
        const wasHidden = target.hidden;
        for (const [key, body] of Object.entries(panels)) {
            if (!body) continue;
            body.hidden = true;
            this._panels[key]?.querySelector('.lan-flow-map-panel-title-toggle')?.classList.remove('is-expanded');
        }
        if (wasHidden) {
            target.hidden = false;
            this._panels[panelKey]?.querySelector('.lan-flow-map-panel-title-toggle')?.classList.add('is-expanded');
        }
    }

    _applyFilter() {
        for (const node of (this._snapshot?.nodes || [])) {
            if (node.kind !== NODE_KIND.WiredClient && node.kind !== NODE_KIND.WifiClient) continue;
            const group = this._nodeMeshes.get(node.id);
            if (!group) continue;
            const visible = this._isClientVisible(node);
            group.visible = visible;
            // Hide the matching link too.
            const linkId = 'cli-link-' + node.mac;
            const link = this._linkMeshes.get(linkId);
            if (link) {
                link.pipe.visible = visible;
                link.down.mesh.visible = visible;
                link.up.mesh.visible = visible;
            }
        }
    }

    _isClientVisible(node) {
        // Overlay master toggle wins first.
        if (node.kind === NODE_KIND.WifiClient && !this._overlays.wifiClients) return false;
        if (node.kind === NODE_KIND.WiredClient && !this._overlays.wiredClients) return false;
        // Text search.
        if (this._filter.text) {
            const hay = `${node.name || ''} ${node.mac || ''} ${node.ssid || ''}`.toLowerCase();
            if (!hay.includes(this._filter.text)) return false;
        }
        // Band filter (WiFi only).
        if (node.kind === NODE_KIND.WifiClient && node.band) {
            if (this._filter.bands[node.band] === false) return false;
        }
        return true;
    }

    _applyOverlayVisibility() {
        if (this.cloudGroup) this.cloudGroup.visible = this._overlays.clouds;
        if (this.buildingGroup) this.buildingGroup.visible = this._overlays.buildings;
        this._applyFilter();
    }

    // ------------------------------------------------------------------------
    // Speed test overlay (spec 5.7.2)
    // ------------------------------------------------------------------------

    async _loadInitialSpeedTests() {
        try {
            const res = await fetch(`${this.apiBase}/speed-tests`, { credentials: 'same-origin' });
            if (!res.ok) return;
            this._speedTests = await res.json();
        } catch {
            this._speedTests = [];
        }

        // Build the per-WAN lookup. Tests whose WanNetworkGroup is set get keyed to
        // that interface. WAN tests without a group default to the primary WAN per
        // user direction; we use a "*wan*" wildcard key the pill lookup falls back to.
        this._latestSpeedTestByWan.clear();
        for (const t of (this._speedTests || [])) {
            if (t.testType !== 'wan') continue;
            const key = t.wanNetworkGroup
                ? t.wanNetworkGroup.toLowerCase()
                : '*wan*';
            const existing = this._latestSpeedTestByWan.get(key);
            if (!existing || new Date(t.testTime) > new Date(existing.testTime)) {
                this._latestSpeedTestByWan.set(key, t);
            }
        }
        this._refreshWanPills();

        if (this._overlays.speedTests) this._renderSpeedTestOverlay();
    }

    _renderSpeedTestOverlay() {
        if (this._speedTestOverlay) {
            this.particleGroup.remove(this._speedTestOverlay);
            this._speedTestOverlay.traverse((o) => {
                if (o.geometry) o.geometry.dispose();
                if (o.material) o.material.dispose();
            });
            this._speedTestOverlay = null;
        }
        if (!this._overlays.speedTests) return;
        const tests = this._speedTests || [];
        if (tests.length === 0) return;

        // Render only the most recent test of each type (WAN + LAN) to avoid clutter.
        const wan = tests.find((t) => t.testType === 'wan');
        const lan = tests.find((t) => t.testType === 'lan');
        const recent = [wan, lan].filter(Boolean);

        const group = new THREE.Group();
        for (const test of recent) {
            this._addSpeedTestOverlayRibbon(group, test);
        }
        this.particleGroup.add(group);
        this._speedTestOverlay = group;
    }

    _addSpeedTestOverlayRibbon(parent, test) {
        // Walk the hops in order, drawing a paired blue + green ribbon along the device-MAC
        // chain. The server pre-resolved FromDeviceBps / ToDeviceBps direction per spec 5.7.2,
        // so the JS layer just paints what it's given.
        const hops = (test.hops || []).filter((h) => h.deviceMac);
        if (hops.length < 2) return;
        const positions = [];
        for (const hop of hops) {
            const pos = this._positions.get('dev-' + hop.deviceMac);
            if (pos) positions.push(pos);
        }
        if (positions.length < 2) return;

        const curve = new THREE.CatmullRomCurve3(positions.map((p) => new THREE.Vector3(p.x, p.y + 1.2, p.z)));
        const downGeo = new THREE.TubeGeometry(curve, 64, 0.20, 8, false);
        const upGeo = new THREE.TubeGeometry(
            new THREE.CatmullRomCurve3(positions.map((p) => new THREE.Vector3(p.x, p.y + 2.0, p.z))),
            64, 0.20, 8, false,
        );
        const downMat = new THREE.MeshBasicMaterial({
            color: COLORS.downstream, transparent: true, opacity: 0.55,
            blending: THREE.AdditiveBlending, depthWrite: false,
        });
        const upMat = new THREE.MeshBasicMaterial({
            color: COLORS.upstream, transparent: true, opacity: 0.55,
            blending: THREE.AdditiveBlending, depthWrite: false,
        });
        parent.add(new THREE.Mesh(downGeo, downMat));
        parent.add(new THREE.Mesh(upGeo, upMat));
    }

    // ------------------------------------------------------------------------
    // Timeline scrubber
    // ------------------------------------------------------------------------

    _onScrubberInput(value) {
        // Visual-only update while dragging - cheap label refresh.
        const at = this._scrubberValueToTime(value);
        if (this._panels.scrubberRight) {
            this._panels.scrubberRight.textContent =
                (value >= 998) ? 'Live' : _fmtDateTime(at);
        }
    }

    async _onScrubberChange(value) {
        if (value >= 998) {
            // Snap back to live.
            this._stopHistoricPlayback();
            this._mode = 'live';
            this._historicAt = null;
            this._onScrubberInput(1000);
            if (this._panels.modeBadge) {
                this._panels.modeBadge.textContent = 'Live';
                this._panels.modeBadge.classList.remove('is-historic');
                this._panels.modeBadge.style.cursor = '';
                this._panels.modeBadge.removeAttribute('data-tooltip');
                if (this._panels.modeBadge._tippy) this._panels.modeBadge._tippy.destroy();
            }
            // Returning to live resumes polling; clear the paused state so the
            // play button reflects "playing" again.
            this._paused = false;
            this._syncPlayPauseIcon();
            this._syncSpeedLabel();
            this._notifyStatCards(null);
            await this._pollLive();
            return;
        }
        const at = this._scrubberValueToTime(value);
        this._mode = 'historic';
        this._historicAt = at;
        if (this._panels.modeBadge) {
            this._panels.modeBadge.textContent = 'Historic';
            this._panels.modeBadge.classList.add('is-historic');
            this._panels.modeBadge.style.cursor = 'pointer';
            this._panels.modeBadge.setAttribute('data-tooltip', 'Click to return to live');
        }
        this._syncSpeedLabel();
        // Scrubbing back into historic by the user lands paused so they can
        // inspect the point they picked. The playback timer also calls this
        // method on every tick (to load the historic snapshot for the new
        // slider position); skip the auto-pause in that case or playback
        // would stop after one tick.
        if (!this._playbackAdvancing && !this._paused) {
            this._paused = true;
            this._syncPlayPauseIcon();
            this._stopHistoricPlayback();
        }
        this._notifyStatCards(at);
        await this._loadHistoric(at);
    }

    _notifyStatCards(at) {
        if (!this._dotnetRef) {
            console.warn('[LanFlowMap] _notifyStatCards: dotnetRef is null');
            return;
        }
        const iso = at ? at.toISOString() : null;
        this._dotnetRef.invokeMethodAsync('OnMapTimeChanged', iso)
            .catch(err => console.warn('[LanFlowMap] OnMapTimeChanged failed:', err));
    }

    _syncPlayPauseIcon() {
        const btn = this._panels.scrubberPlayPause;
        if (!btn) return;
        btn.textContent = this._paused ? '▶' : '⏸';
        btn.setAttribute('aria-label', this._paused ? 'Play' : 'Pause');
    }

    _syncSpeedLabel() {
        const label = this._panels.scrubber?.querySelector('[data-role="speed-label"]');
        if (label) label.textContent = `${this._playbackSpeed}x`;
    }

    _scrubberValueToTime(value) {
        // Range 0..1000 maps from the anchored origin (mount - 24h) to now.
        // The window grows as the page stays open so recent time is always reachable.
        const now = Date.now();
        const ms = this._scrubberOrigin + (value / 1000) * (now - this._scrubberOrigin);
        return new Date(ms);
    }

    async _loadHistoric(at) {
        try {
            const url = `${this.apiBase}/history?at=${encodeURIComponent(at.toISOString())}`;
            const res = await fetch(url, { credentials: 'same-origin' });
            if (!res.ok) return;
            const update = await res.json();
            this._applyLiveRates(update.linkRates || {});
            if (update.nodeBadges) this._currentBadges = update.nodeBadges;
        } catch (err) {
            // Keep ticking; transient errors are fine.
        }
    }

    // ------------------------------------------------------------------------
    // Floating DOM labels (per-device rates) + WAN speed test pill + hover tooltip
    // ------------------------------------------------------------------------

    _ensureLabelsLayer() {
        if (this._labelsLayer) return;
        const layer = document.createElement('div');
        layer.className = 'lan-flow-map-labels';
        this.stage.appendChild(layer);
        this._labelsLayer = layer;

        const tip = document.createElement('div');
        tip.className = 'lan-flow-map-tooltip';
        this.stage.appendChild(tip);
        this._tooltipEl = tip;
    }

    _buildFloatingLabels(snap) {
        this._ensureLabelsLayer();
        // Clear previous labels.
        for (const { el } of this._floatingLabels.values()) el.remove();
        this._floatingLabels.clear();
        for (const { el } of this._linkLabels.values()) el.remove();
        this._linkLabels.clear();
        for (const el of this._wanPills.values()) el.remove();
        this._wanPills.clear();

        // Per-link rate pills - one per link, only shown when traffic on either
        // direction is above the threshold (LINK_LABEL_THRESHOLD_BPS). Lets the
        // map convey "how busy" at the link level without cluttering idle links.
        for (const link of snap.links || []) {
            const el = document.createElement('div');
            el.className = 'lan-flow-map-link-label';
            // Park offscreen until the first projection update positions it so
            // a stray is-visible flash doesn't render the label at canvas
            // top-left (CSS default 0,0).
            el.style.left = '-9999px';
            el.style.top = '-9999px';
            this._labelsLayer.appendChild(el);
            this._linkLabels.set(link.id, { el, kind: link.kind });
        }

        // Device labels: infrastructure devices (gateway, switch, AP) get DOM labels.
        // Clients stay as sprites for proper 3D depth sorting.
        for (const node of snap.nodes) {
            if (node.kind === NODE_KIND.WiredClient || node.kind === NODE_KIND.WifiClient) continue;
            if (node.kind === NODE_KIND.Cloud) continue;
            const el = document.createElement('div');
            el.className = 'lan-flow-map-label';
            // Same offscreen park as link labels and WAN pills.
            el.style.left = '-9999px';
            el.style.top = '-9999px';
            const nameEl = document.createElement('div');
            nameEl.className = 'lan-flow-map-label-name';
            nameEl.textContent = node.name || (node.mac || '');
            const rateEl = document.createElement('div');
            rateEl.className = 'lan-flow-map-label-rates';
            rateEl.innerHTML = `<span class="down">↓ -</span> &nbsp; <span class="up">↑ -</span>`;
            el.appendChild(nameEl);
            el.appendChild(rateEl);
            this._labelsLayer.appendChild(el);
            this._floatingLabels.set(node.id, { el, nameEl, rateEl });
        }

        // WAN speed test pills: one per access-ISP cloud, anchored to the cloud mesh.
        // Key by wanInterface so the JS lookup can fall back to the primary WAN when
        // a test has no WanNetworkGroup set.
        for (const cloud of (snap.clouds || [])) {
            if (cloud.kind !== 0 /* AccessIsp */) continue;
            if (!cloud.wanInterface) continue;
            const pill = document.createElement('div');
            pill.className = 'lan-flow-map-wan-pill';
            // Park offscreen until the first projection update places it. Without
            // this the pill briefly shows at top-left (CSS default 0,0) the moment
            // _refreshWanPills adds is-visible but before _updateFloatingLabels
            // has projected the access cloud's screen coords.
            pill.style.left = '-9999px';
            pill.style.top = '-9999px';
            this._labelsLayer.appendChild(pill);
            this._wanPills.set(cloud.wanInterface, pill);
        }

        // Hide the existing 3D sprite labels for devices we now show via DOM (keeps
        // the scene from double-labeling them).
        for (const [id, sprite] of this._labelSprites) {
            const node = snap.nodes.find((n) => n.id === id);
            if (!node) continue;
            if (node.kind !== NODE_KIND.WiredClient && node.kind !== NODE_KIND.WifiClient
                && node.kind !== NODE_KIND.Cloud) {
                sprite.visible = false;
            }
        }
    }

    _updateFloatingLabels() {
        if (!this._labelsLayer) return;
        const rect = this.canvas.getBoundingClientRect();
        const halfW = rect.width / 2;
        const halfH = rect.height / 2;
        const tmp = new THREE.Vector3();
        const camPos = this.camera.position;
        // Reference distance: roughly the fly-in camera target distance (~60u).
        // At this distance labels render at 100%. Farther = smaller, closer = larger.
        const REF_DIST = 60;
        const MIN_SCALE = 0.4;
        const MAX_SCALE = 1.2;

        for (const [nodeId, { el }] of this._floatingLabels) {
            const group = this._nodeMeshes.get(nodeId);
            if (!group) { el.classList.remove('is-visible'); continue; }
            if (!group.visible) { el.classList.remove('is-visible'); continue; }
            tmp.setFromMatrixPosition(group.matrixWorld);
            tmp.y += 1.8;  // anchor above the node sphere
            const dist = tmp.distanceTo(camPos);
            tmp.project(this.camera);
            if (tmp.z > 1) { el.classList.remove('is-visible'); continue; }
            const x = (tmp.x * halfW) + halfW;
            const y = -(tmp.y * halfH) + halfH;
            const scale = Math.max(MIN_SCALE, Math.min(MAX_SCALE, REF_DIST / Math.max(dist, 1)));
            el.style.transform = `translate(-50%, -100%) scale(${scale.toFixed(3)})`;
            el.style.transformOrigin = 'center bottom';
            el.style.left = `${x}px`;
            el.style.top = `${y}px`;
            el.classList.add('is-visible');
        }

        for (const [wanIface, pill] of this._wanPills) {
            const group = this._cloudMeshes.get(`cloud-access-${wanIface}`);
            if (!group) { pill.classList.remove('is-visible'); continue; }
            tmp.setFromMatrixPosition(group.matrixWorld);
            tmp.y -= NODE_RADIUS.cloud + 0.5;
            tmp.project(this.camera);
            if (tmp.z > 1) { pill.classList.remove('is-visible'); continue; }
            const x = (tmp.x * halfW) + halfW;
            const y = -(tmp.y * halfH) + halfH;
            pill.style.left = `${x}px`;
            pill.style.top = `${y}px`;
        }

        // Link rate pills: positioned at the link midpoint. Visibility + text is
        // driven by _refreshLinkLabels (set is-visible class only when above
        // threshold). Per-frame we project the position + scale, and apply a
        // camera-distance gate: leaf links (wired / wifi client) hide their
        // label when zoomed out, unless a filter has reduced the parent
        // switch's visible-leaf count to <= 25% - in that sparsely-filtered
        // case we always show, since clutter is no longer a concern.
        // Pre-compute visible-leaf ratio per parent so the inner loop is O(1).
        const leafByParent = new Map();
        for (const [, { link }] of this._linkMeshes) {
            const k = link.kind;
            if (k !== LINK_KIND.WiredClient && k !== LINK_KIND.WifiClient) continue;
            const parentId = link.fromNodeId;
            let counts = leafByParent.get(parentId);
            if (!counts) { counts = { total: 0, visible: 0 }; leafByParent.set(parentId, counts); }
            counts.total++;
            const leafGroup = this._nodeMeshes.get(link.toNodeId);
            if (leafGroup && leafGroup.visible) counts.visible++;
        }

        const midA = new THREE.Vector3();
        const midB = new THREE.Vector3();
        for (const [linkId, { el }] of this._linkLabels) {
            const link = this._linkMeshes.get(linkId);
            if (!link || !el._hasData) { el.classList.remove('is-visible'); continue; }
            const fromPos = this._positions.get(link.link.fromNodeId);
            const toPos = this._positions.get(link.link.toNodeId);
            if (!fromPos || !toPos) continue;
            midA.set(fromPos.x, fromPos.y, fromPos.z);
            midB.set(toPos.x, toPos.y, toPos.z);
            tmp.copy(midA).add(midB).multiplyScalar(0.5);
            const dist = tmp.distanceTo(camPos);
            const kind = link.link.kind;
            const isLeaf = kind === LINK_KIND.WiredClient || kind === LINK_KIND.WifiClient;
            if (isLeaf && dist > LEAF_LABEL_MAX_DIST) {
                const counts = leafByParent.get(link.link.fromNodeId);
                const sparselyFiltered = counts && counts.total > 0
                    && counts.visible > 0
                    && (counts.visible / counts.total) <= 0.25;
                if (!sparselyFiltered) {
                    el.classList.remove('is-visible');
                    continue;
                }
            }
            tmp.project(this.camera);
            if (tmp.z > 1) { el.classList.remove('is-visible'); continue; }
            const x = (tmp.x * halfW) + halfW;
            const y = -(tmp.y * halfH) + halfH;
            const scale = Math.max(MIN_SCALE, Math.min(MAX_SCALE, REF_DIST / Math.max(dist, 1)));
            el.style.transform = `translate(-50%, -50%) scale(${scale.toFixed(3)})`;
            el.style.left = `${x}px`;
            el.style.top = `${y}px`;
            el.classList.add('is-visible');
        }
    }

    _refreshDeviceLabelRates() {
        // Floating labels render only on infrastructure (gateway / switch / AP).
        // Aggregate across adjacent links using internet-centric direction:
        //   blue down arrow  = download-direction sum (data flowing toward leaves)
        //   green up arrow   = upload-direction sum (data flowing toward internet)
        // For WAN/uplink/wifi links the backend's UpstreamBps holds the
        // download-direction value (display-layer swap). WiredClient links are
        // the exception: backend's DownstreamBps already holds the
        // download-direction value (SNMP port TX = toward leaf), so no swap.
        for (const [nodeId, { rateEl }] of this._floatingLabels) {
            let downBps = 0;
            let upBps = 0;
            let anyData = false;
            // Prefer the per-device aggregate badge if the server published one.
            // For switches the server emits fabric ingress/egress (sum across
            // every port_table entry) so multi-trunk switches don't under-
            // count egress. Other fabric devices use the trunk-port rate.
            // Either way it's better than summing adjacent links, which
            // double-counts any flow that traverses the device.
            const badge = this._currentBadges?.[nodeId];
            const hasFabric = badge && (badge.fabricIngressBps != null || badge.fabricEgressBps != null);
            const hasAggregate = badge && (badge.aggregateInBps != null || badge.aggregateOutBps != null);
            if (hasFabric) {
                downBps = badge.fabricIngressBps || 0;
                upBps = badge.fabricEgressBps || 0;
                anyData = (downBps > 0 || upBps > 0);
            } else if (hasAggregate) {
                // For APs the aggregate badge comes from the parent switch's
                // port_table TX/RX, which UniFi reports from the connected
                // device's perspective (port TX = bytes the AP sent up,
                // RX = bytes the AP received). The tooltip's "Ingress"
                // (= aggregateInBps) reads naturally as "client data
                // arriving at the AP" under that convention. The floating
                // label is gateway-relative though - blue down arrow is
                // always data flowing FROM the gateway - so for APs the
                // mapping has to swap.
                const node = this._nodeMeshes.get(nodeId)?.userData?.node;
                if (node?.kind === NODE_KIND.AccessPoint) {
                    downBps = badge.aggregateOutBps || 0;
                    upBps = badge.aggregateInBps || 0;
                } else {
                    downBps = badge.aggregateInBps || 0;
                    upBps = badge.aggregateOutBps || 0;
                }
                anyData = (downBps > 0 || upBps > 0);
            } else {
                for (const [linkId, link] of this._linkMeshes) {
                    if (link.link.fromNodeId !== nodeId && link.link.toNodeId !== nodeId) continue;
                    const r = this._currentRates?.[linkId];
                    if (!r) continue;
                    anyData = true;
                    // Backend emits DownstreamBps = downloads, UpstreamBps =
                    // uploads on every link kind, so no leaf/non-leaf swap.
                    downBps += r.downstreamBps || 0;
                    upBps += r.upstreamBps || 0;
                }
            }
            if (!anyData) {
                rateEl.innerHTML = `<span class="down">↓ -</span> &nbsp; <span class="up">↑ -</span>`;
                continue;
            }
            rateEl.innerHTML =
                `<span class="down">↓ ${formatBps(downBps)}</span> &nbsp; ` +
                `<span class="up">↑ ${formatBps(upBps)}</span>`;
        }
    }

    _refreshWanPills() {
        const primary = this._snapshot?.primaryWanInterface;
        for (const [wanIface, pill] of this._wanPills) {
            // First look for a test bound to this specific WAN. If none, and this is
            // the primary WAN, fall back to any ungrouped WAN test ("*wan*" wildcard).
            let test = this._latestSpeedTestByWan.get(wanIface);
            if (!test && wanIface === primary) {
                test = this._latestSpeedTestByWan.get('*wan*');
            }
            if (!test) { pill.classList.remove('is-visible'); continue; }
            const dl = test.downloadMbps ?? 0;
            const ul = test.uploadMbps ?? 0;
            const ageMs = Date.now() - new Date(test.testTime).getTime();
            const ageLabel = formatAge(ageMs);
            pill.textContent = `Last test: ${dl.toFixed(0)} / ${ul.toFixed(0)} Mbps  ·  ${ageLabel}`;
            pill.classList.add('is-visible');
        }
    }

    _onPointerMove(e) {
        const rect = this.canvas.getBoundingClientRect();
        this._pointerScreen.x = e.clientX - rect.left;
        this._pointerScreen.y = e.clientY - rect.top;
        this._pointerNdc.x = (this._pointerScreen.x / rect.width) * 2 - 1;
        this._pointerNdc.y = -(this._pointerScreen.y / rect.height) * 2 + 1;
        if (this._repositionMode) return;
        this._raycaster.setFromCamera(this._pointerNdc, this.camera);
        // Raycast against device node spheres - cheap, ~tens of meshes.
        const candidates = [];
        for (const group of this._nodeMeshes.values()) {
            if (!group.visible) continue;
            for (const child of group.children) {
                if (child.isMesh) candidates.push(child);
            }
        }
        const hits = this._raycaster.intersectObjects(candidates, false);
        if (hits.length === 0) { this._clearHover(); return; }
        const hit = hits[0].object;
        // Walk up to find the group with userData.node.
        let g = hit;
        while (g && !(g.userData && g.userData.node)) g = g.parent;
        if (!g) { this._clearHover(); return; }
        this._showHover(g.userData.node);
    }

    _onDoubleClick(e) {
        if (this._repositionMode) return;
        const rect = this.canvas.getBoundingClientRect();
        const ndc = new THREE.Vector2(
            ((e.clientX - rect.left) / rect.width) * 2 - 1,
            -((e.clientY - rect.top) / rect.height) * 2 + 1
        );
        this._raycaster.setFromCamera(ndc, this.camera);
        const candidates = [];
        for (const group of this._nodeMeshes.values()) {
            if (!group.visible) continue;
            for (const child of group.children) {
                if (child.isMesh) candidates.push(child);
            }
        }
        const hits = this._raycaster.intersectObjects(candidates, false);
        if (hits.length === 0) return;
        let g = hits[0].object;
        while (g && !(g.userData?.node)) g = g.parent;
        if (!g) return;
        const node = g.userData.node;
        if (node.kind !== NODE_KIND.WifiClient && node.kind !== NODE_KIND.WiredClient) return;
        const ip = node.ip;
        if (!ip) return;
        window.location.href = `/client-dashboard?ip=${encodeURIComponent(ip)}`;
    }

    _showHover(node) {
        if (!this._tooltipEl) return;
        const rows = [];
        if (node.ip) rows.push(['IP', node.ip]);
        if (node.mac) rows.push(['MAC', node.mac]);
        if (node.model) rows.push(['Model', node.model]);
        if (node.band) rows.push(['Band', `${node.band} GHz`]);
        if (node.ssid) rows.push(['SSID', node.ssid]);
        if (node.signalDbm) rows.push(['Signal', `${node.signalDbm} dBm`]);
        if (node.switchPortName) rows.push(['Switch port', node.switchPortName]);
        if (node.wiredLinkSpeedMbps) rows.push(['Link speed', formatLinkSpeed(node.wiredLinkSpeedMbps)]);
        if (node.network) rows.push(['Network', node.network]);
        // Device health from NodeLiveBadge (infrastructure nodes only)
        const badge = this._currentBadges?.[node.id];
        if (badge?.cpuPercent != null) rows.push(['CPU', `${badge.cpuPercent.toFixed(0)}%`]);
        if (badge?.memoryUsedPercent != null) rows.push(['Memory', `${badge.memoryUsedPercent.toFixed(0)}%`]);
        if (badge?.temperatureC != null) rows.push(['Temp', `${badge.temperatureC.toFixed(0)} °C`]);
        if (badge?.uptimeSeconds != null) {
            const d = Math.floor(badge.uptimeSeconds / 86400);
            const h = Math.floor((badge.uptimeSeconds % 86400) / 3600);
            rows.push(['Uptime', d > 0 ? `${d}d ${h}h` : `${h}h`]);
        }
        // Aggregate rate across adjacent links. For fabric devices (gateway /
        // switch / AP) we show ingress (data flowing INTO the device across
        // every port) and egress (data flowing OUT). For client nodes the
        // single leaf link makes ingress == download-to-client and egress ==
        // upload-from-client, so we keep the friendlier Download / Upload
        // wording for them.
        //
        // Fabric devices (gateway/switch/AP): use the per-device badge so the
        // tooltip numbers match the floating label exactly. The badge is the
        // boundary throughput from the trunk/uplink port (or gateway WAN
        // port), single-counted. Summing every adjacent link instead
        // double-counts any flow that traverses the device.
        // Client devices: the leaf link is the only adjacent rate, no
        // double-count risk - sum the single link with direction resolved.
        const isFabric = node.kind === NODE_KIND.Gateway
            || node.kind === NODE_KIND.Switch
            || node.kind === NODE_KIND.AccessPoint;
        let ingressBps = 0, egressBps = 0, anyData = false;
        if (isFabric) {
            const badge = this._currentBadges?.[node.id];
            if (badge && (badge.fabricIngressBps != null || badge.fabricEgressBps != null)) {
                ingressBps = badge.fabricIngressBps || 0;
                egressBps = badge.fabricEgressBps || 0;
                anyData = (ingressBps > 0 || egressBps > 0);
            } else if (badge && (badge.aggregateInBps != null || badge.aggregateOutBps != null)) {
                ingressBps = badge.aggregateInBps || 0;
                egressBps = badge.aggregateOutBps || 0;
                anyData = (ingressBps > 0 || egressBps > 0);
            }
        } else {
            for (const [, link] of this._linkMeshes) {
                if (link.link.fromNodeId !== node.id && link.link.toNodeId !== node.id) continue;
                const r = this._currentRates?.[link.link.id];
                if (!r) continue;
                anyData = true;
                // Backend emits DownstreamBps = downloads (parent -> child)
                // and UpstreamBps = uploads (child -> parent) uniformly.
                const dl = r.downstreamBps || 0;
                const ul = r.upstreamBps || 0;
                if (link.link.toNodeId === node.id) {
                    // This node sits at the "leaf" end of the link, so the
                    // downstream flow arrives at us (ingress) and the upstream
                    // flow leaves us (egress).
                    ingressBps += dl;
                    egressBps += ul;
                } else {
                    // This node is the "parent" end of the link, so uploads
                    // arrive from the leaf (ingress) and downloads leave us
                    // toward the leaf (egress).
                    ingressBps += ul;
                    egressBps += dl;
                }
            }
        }
        if (anyData) {
            if (isFabric) {
                rows.push(['Ingress', formatBps(ingressBps)]);
                rows.push(['Egress', formatBps(egressBps)]);
            } else {
                rows.push(['Download', formatBps(ingressBps)]);
                rows.push(['Upload', formatBps(egressBps)]);
            }
        }
        const html = `<div class="lan-flow-map-tooltip-title">${escapeHtml(node.name || node.mac || '')}</div>` +
            rows.map(([k, v]) => `<div class="lan-flow-map-tooltip-row"><span>${k}</span><span class="v">${escapeHtml(String(v))}</span></div>`).join('');
        this._tooltipEl.innerHTML = html;
        this._tooltipEl.style.left = `${this._pointerScreen.x + 14}px`;
        this._tooltipEl.style.top = `${this._pointerScreen.y + 14}px`;
        this._tooltipEl.classList.add('is-visible');
        this._hoverTarget = node;
    }

    _clearHover() {
        if (this._tooltipEl) this._tooltipEl.classList.remove('is-visible');
        this._hoverTarget = null;
    }

    // ------------------------------------------------------------------------
    // Right-click context menu + device reposition
    // ------------------------------------------------------------------------

    _onContextMenu(e) {
        e.preventDefault();
        if (this._repositionMode) return;

        const rect = this.canvas.getBoundingClientRect();
        const ndc = new THREE.Vector2(
            ((e.clientX - rect.left) / rect.width) * 2 - 1,
            -((e.clientY - rect.top) / rect.height) * 2 + 1
        );
        this._raycaster.setFromCamera(ndc, this.camera);
        const candidates = [];
        for (const group of this._nodeMeshes.values()) {
            if (!group.visible) continue;
            for (const child of group.children) {
                if (child.isMesh) candidates.push(child);
            }
        }
        const hits = this._raycaster.intersectObjects(candidates, false);
        if (hits.length === 0) return;
        let g = hits[0].object;
        while (g && !(g.userData?.node)) g = g.parent;
        if (!g) return;
        const node = g.userData.node;
        if (node.kind === NODE_KIND.Cloud || node.kind === NODE_KIND.VirtualHub) return;

        this._showContextMenu(e.clientX, e.clientY, node, g);
    }

    _showContextMenu(clientX, clientY, node, group) {
        this._dismissContextMenu();
        const menu = document.createElement('div');
        menu.className = 'lan-flow-map-context-menu';
        const item = document.createElement('div');
        item.className = 'lan-flow-map-context-menu-item';
        item.textContent = 'Reposition Device';
        item.addEventListener('click', (e) => {
            e.stopPropagation();
            this._dismissContextMenu();
            this._enterReposition(node, group);
        });
        menu.appendChild(item);

        const stageRect = this.stage.getBoundingClientRect();
        menu.style.left = `${clientX - stageRect.left}px`;
        menu.style.top = `${clientY - stageRect.top}px`;
        this.stage.appendChild(menu);
        this._contextMenuEl = menu;
    }

    _dismissContextMenu() {
        if (this._contextMenuEl) {
            this._contextMenuEl.remove();
            this._contextMenuEl = null;
        }
    }

    _enterReposition(node, group) {
        this._repositionMode = true;
        this._repositionNode = node;
        this._repositionGroup = group;
        this._repositionOrigPos = group.position.clone();
        this._clearHover();

        this.controls.enabled = false;
        this.canvas.style.cursor = 'move';

        // Show the reposition HUD banner
        const hud = document.createElement('div');
        hud.className = 'lan-flow-map-reposition-hud';
        hud.innerHTML = `
            <span class="lan-flow-map-reposition-title">Moving: ${escapeHtml(node.name || node.mac || 'Device')}</span>
            <span class="lan-flow-map-reposition-keys">
                <span class="kbd">W</span><span class="kbd">A</span><span class="kbd">S</span><span class="kbd">D</span> move
                &nbsp;&middot;&nbsp; <span class="kbd">Q</span><span class="kbd">E</span> / <span class="kbd">Scroll</span> height
                &nbsp;&middot;&nbsp; Drag to move
                &nbsp;&middot;&nbsp; <span class="kbd">Click</span> place
                &nbsp;&middot;&nbsp; <span class="kbd">Esc</span> cancel
            </span>
        `;
        this.stage.appendChild(hud);
        this._repositionHud = hud;

        // Set up mouse-drag on the XZ plane at the device's Y height.
        // Confirm on pointerup only if the pointer barely moved (click, not drag).
        this._repositionDragging = false;
        this._repositionDragMoved = false;
        this._repositionDownPt = null;
        this._repositionPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), -group.position.y);
        this._repositionMoveHandler = (e) => this._onRepositionPointerMove(e);
        this._repositionDownHandler = (e) => {
            if (e.button === 0) {
                this._repositionDragging = true;
                this._repositionDragMoved = false;
                this._repositionDownPt = { x: e.clientX, y: e.clientY };
                e.preventDefault();
            }
        };
        this._repositionUpHandler = (e) => {
            if (e.button === 0 && this._repositionMode) {
                this._repositionDragging = false;
                if (!this._repositionDragMoved) {
                    this._confirmReposition();
                }
            }
        };
        this._repositionWheelHandler = (e) => {
            if (!this._repositionMode) return;
            e.preventDefault();
            const step = e.deltaY > 0 ? -0.5 : 0.5;
            this._repositionGroup.position.y += step;
            this._repositionPlane.constant = -this._repositionGroup.position.y;
            this._updateAdjacentLinks();
        };
        this.canvas.addEventListener('pointermove', this._repositionMoveHandler);
        this.canvas.addEventListener('pointerdown', this._repositionDownHandler, true);
        this.canvas.addEventListener('pointerup', this._repositionUpHandler);
        this.canvas.addEventListener('wheel', this._repositionWheelHandler, { passive: false });
    }

    _onRepositionPointerMove(e) {
        if (!this._repositionMode || !this._repositionDragging) return;

        // Track whether the pointer actually moved (drag vs click)
        if (this._repositionDownPt) {
            const dx = e.clientX - this._repositionDownPt.x;
            const dy = e.clientY - this._repositionDownPt.y;
            if (dx * dx + dy * dy > 9) this._repositionDragMoved = true;
        }
        if (!this._repositionDragMoved) return;

        const rect = this.canvas.getBoundingClientRect();
        const ndc = new THREE.Vector2(
            ((e.clientX - rect.left) / rect.width) * 2 - 1,
            -((e.clientY - rect.top) / rect.height) * 2 + 1
        );
        this._raycaster.setFromCamera(ndc, this.camera);
        const intersection = new THREE.Vector3();
        if (this._raycaster.ray.intersectPlane(this._repositionPlane, intersection)) {
            this._repositionGroup.position.x = intersection.x;
            this._repositionGroup.position.z = intersection.z;
            this._updateAdjacentLinks();
        }
    }

    _updateAdjacentLinks() {
        if (!this._repositionNode) return;
        const nodeId = this._repositionNode.id;
        const pos = this._repositionGroup.position;

        // If moving a gateway, drag all connected clouds along (they're positioned
        // relative to the gateway and have no independent geo coords).
        if (this._repositionNode.kind === NODE_KIND.Gateway) {
            if (!this._repositionCloudOffsets) {
                this._repositionCloudOffsets = new Map();
                for (const [cloudId, group] of this._cloudMeshes) {
                    this._repositionCloudOffsets.set(cloudId, {
                        dx: group.position.x - this._repositionOrigPos.x,
                        dy: group.position.y - this._repositionOrigPos.y,
                        dz: group.position.z - this._repositionOrigPos.z,
                    });
                }
            }
            for (const [cloudId, offset] of this._repositionCloudOffsets) {
                const group = this._cloudMeshes.get(cloudId);
                if (!group) continue;
                group.position.set(pos.x + offset.dx, pos.y + offset.dy, pos.z + offset.dz);
                this._positions.set(cloudId, {
                    x: group.position.x, y: group.position.y, z: group.position.z, pinned: true,
                });
            }
        }

        for (const [linkId, linkObj] of this._linkMeshes) {
            if (linkObj.link.fromNodeId !== nodeId && linkObj.link.toNodeId !== nodeId) continue;
            const otherNodeId = linkObj.link.fromNodeId === nodeId ? linkObj.link.toNodeId : linkObj.link.fromNodeId;
            const otherGroup = this._nodeMeshes.get(otherNodeId) || this._cloudMeshes.get(otherNodeId);
            if (!otherGroup) continue;

            const a = linkObj.link.fromNodeId === nodeId ? pos : otherGroup.position;
            const b = linkObj.link.toNodeId === nodeId ? pos : otherGroup.position;

            // Update pipe mesh
            this._updatePipePosition(linkObj.pipe, a, b);
            // Update particle stream endpoints
            if (linkObj.link.fromNodeId === nodeId) {
                linkObj.down.updateEndpoints(pos, otherGroup.position);
                linkObj.up.updateEndpoints(otherGroup.position, pos);
            } else {
                linkObj.down.updateEndpoints(otherGroup.position, pos);
                linkObj.up.updateEndpoints(pos, otherGroup.position);
            }
        }
    }

    _updatePipePosition(pipe, a, b) {
        if (!pipe.isMesh) return;
        const from = new THREE.Vector3(a.x, a.y, a.z);
        const to = new THREE.Vector3(b.x, b.y, b.z);
        const dir = to.clone().sub(from);
        const length = dir.length();
        if (length < 0.01) return;

        const mid = from.clone().add(to).multiplyScalar(0.5);
        pipe.position.copy(mid);
        pipe.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.clone().normalize());
        const origHeight = pipe.geometry.parameters?.height || 1;
        pipe.scale.y = length / origHeight;
    }

    async _confirmReposition() {
        if (!this._repositionMode) return;
        const group = this._repositionGroup;
        const node = this._repositionNode;
        const pos = { x: group.position.x, y: group.position.y, z: group.position.z };

        this._exitRepositionMode();

        // Update the internal positions map
        this._positions.set(node.id, {
            x: pos.x, y: pos.y, z: pos.z, pinned: true,
        });

        // Reverse-project 3D scene coords back to geo
        const bounds = this._snapshot?.bounds;
        if (!bounds?.centerLat || !bounds?.lngScale) {
            console.warn('[LanFlowMap] No projection params - cannot save placement');
            return;
        }
        const sceneRadius = 30.0;
        const ANCHOR_SPREAD_FACTOR = 1.875;
        const scale = (sceneRadius / Math.max(bounds.radius, 1.0)) * ANCHOR_SPREAD_FACTOR;
        const EARTH_RADIUS = 6_371_000.0;

        // Undo JS transform: posX = -(local.x * scale), posZ = local.y * scale
        // (posY = local.z * scale * 0.8 but we don't save floor from 3D)
        const localX = -(pos.x / scale);
        const localY = pos.z / scale; // JS z maps to projection y

        // Undo equirectangular projection
        const dLng = localX / (bounds.lngScale * EARTH_RADIUS);
        const dLat = localY / EARTH_RADIUS;
        const lat = bounds.centerLat + dLat * 180 / Math.PI;
        const lng = bounds.centerLng + dLng * 180 / Math.PI;

        // Reverse Y → floor: posY = floor * 3.0 * scale * 0.8
        const floor = Math.round(pos.y / (scale * 0.8) / 2.9);

        try {
            await fetch(`${this.apiBase}/device-placement`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: JSON.stringify({ mac: node.mac, latitude: lat, longitude: lng, floor }),
            });
        } catch (err) {
            console.error('[LanFlowMap] Failed to save placement:', err);
        }
    }

    _cancelReposition() {
        if (!this._repositionMode) return;
        this._repositionGroup.position.copy(this._repositionOrigPos);
        // Restore cloud positions if we moved a gateway
        if (this._repositionCloudOffsets) {
            for (const [cloudId, offset] of this._repositionCloudOffsets) {
                const group = this._cloudMeshes.get(cloudId);
                if (!group) continue;
                group.position.set(
                    this._repositionOrigPos.x + offset.dx,
                    this._repositionOrigPos.y + offset.dy,
                    this._repositionOrigPos.z + offset.dz,
                );
            }
        }
        this._updateAdjacentLinks();
        this._exitRepositionMode();
    }

    _exitRepositionMode() {
        this._repositionMode = false;
        this._repositionNode = null;
        this._repositionGroup = null;
        this._repositionOrigPos = null;
        this._repositionDragging = false;
        this._repositionDragMoved = false;
        this._repositionDownPt = null;
        this._repositionCloudOffsets = null;

        this.controls.enabled = true;
        this.canvas.style.cursor = '';

        if (this._repositionHud) { this._repositionHud.remove(); this._repositionHud = null; }
        this.canvas.removeEventListener('pointermove', this._repositionMoveHandler);
        this.canvas.removeEventListener('pointerdown', this._repositionDownHandler, true);
        this.canvas.removeEventListener('pointerup', this._repositionUpHandler);
        this.canvas.removeEventListener('wheel', this._repositionWheelHandler);
    }
}

// ----------------------------------------------------------------------------
// ParticleStream - GPU-friendly one-direction particle flow along a link.
// Density and velocity both scale with rate (spec 5.7.1 hybrid dot semantics).
// ----------------------------------------------------------------------------

// Shared circular-dot texture so PointsMaterial renders round dots instead
// of the default square gl_Point quads. Generated lazily on first use,
// then memoized so every stream instance shares the same texture handle.
let _dotTexture = null;
function _getDotTexture() {
    if (_dotTexture) return _dotTexture;
    const size = 64;
    const canvas = document.createElement('canvas');
    canvas.width = size; canvas.height = size;
    const ctx = canvas.getContext('2d');
    // Radial gradient: bright center -> soft edge -> transparent at the rim.
    // Soft edge keeps the dot reading as round even at small on-screen sizes.
    const grad = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
    grad.addColorStop(0.0, 'rgba(255,255,255,1)');
    grad.addColorStop(0.35, 'rgba(255,255,255,0.85)');
    grad.addColorStop(0.7, 'rgba(255,255,255,0.25)');
    grad.addColorStop(1.0, 'rgba(255,255,255,0)');
    ctx.fillStyle = grad;
    ctx.fillRect(0, 0, size, size);
    _dotTexture = new THREE.CanvasTexture(canvas);
    _dotTexture.needsUpdate = true;
    return _dotTexture;
}

class ParticleStream {
    constructor({ from, to, color, particleCount = 0 }) {
        const fromV = new THREE.Vector3(from.x, from.y, from.z);
        const toV = new THREE.Vector3(to.x, to.y, to.z);
        this._from = fromV;
        this._to = toV;
        this._direction = toV.clone().sub(fromV);
        this._length = this._direction.length();
        this._direction.normalize();

        const MAX = 200;
        this._max = MAX;
        // Inactive particles are parked outside the camera's far plane (1000 units)
        // so they don't render. Without this, all 80 vertices start at (0, 0, 0) -
        // with additive blending + bloom across every stream, that piles into a
        // bright artifact right at world origin.
        const PARK = 1e6;
        this._park = PARK;
        const positions = new Float32Array(MAX * 3);
        for (let i = 0; i < MAX; i += 1) {
            positions[i * 3 + 0] = PARK;
            positions[i * 3 + 1] = PARK;
            positions[i * 3 + 2] = PARK;
        }
        const t = new Float32Array(MAX);
        for (let i = 0; i < MAX; i += 1) {
            t[i] = -1;  // inactive
        }
        // Per-particle size attribute. Each dot keeps the size it was
        // emitted at, so a burst of heavy traffic doesn't retroactively
        // bloat dots that were already in flight when the link was idle.
        const sizes = new Float32Array(MAX);
        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geometry.setAttribute('size', new THREE.BufferAttribute(sizes, 1));
        // Custom shader that mimics PointsMaterial(sizeAttenuation=true) but
        // reads gl_PointSize from a per-vertex attribute. Three.js's
        // PointsMaterial has no per-vertex size hook, so we re-implement the
        // size-attenuation formula here. The scale factor mirrors what the
        // stock shader uses: canvas drawing-buffer height in pixels, with a
        // perspective falloff of (scale / -mvPosition.z).
        const material = new THREE.ShaderMaterial({
            uniforms: {
                color: { value: new THREE.Color(color) },
                map: { value: _getDotTexture() },
                scale: { value: 600.0 },
            },
            vertexShader: `
                attribute float size;
                uniform float scale;
                void main() {
                    vec4 mv = modelViewMatrix * vec4(position, 1.0);
                    gl_PointSize = size * (scale / max(-mv.z, 1.0));
                    gl_Position = projectionMatrix * mv;
                }
            `,
            fragmentShader: `
                uniform vec3 color;
                uniform sampler2D map;
                void main() {
                    vec4 tex = texture2D(map, gl_PointCoord);
                    if (tex.a < 0.01) discard;
                    gl_FragColor = vec4(color, tex.a * 0.92);
                }
            `,
            transparent: true,
            depthWrite: false,
            depthTest: false, // additive dots should always blend on top - the pipe's
                              // transparent geometry was occluding them from the side
                              // opposite the link's near surface.
            blending: THREE.AdditiveBlending,
        });
        this._material = material;
        this._sizes = sizes;
        this.mesh = new THREE.Points(geometry, material);
        // Disable frustum culling: parking inactive particles outside the camera
        // far plane (above) means the auto-computed bounding sphere centers at the
        // park position, and Three.js culls the entire mesh as "off-screen" even
        // when active particles are inside the view. Skipping the cull is essentially
        // free for 80-vertex Points meshes.
        this.mesh.frustumCulled = false;
        this._t = t;
        this._positions = positions;

        this._rateBps = 0;
        this._spawnAccumulator = 0;
        this._density = 0;     // 0..1 (fraction of MAX)
        this._velocity = 0.4;  // units/sec along the link
        // Size that NEW particles will be born at - written into the size
        // attribute when the particle spawns. Existing in-flight particles
        // keep whatever size they were emitted with.
        this._currentSize = 0.05;
    }

    setRate(bps) {
        this._rateBps = Math.max(bps, 0);
        // Unfloored log intensity: full range 1bps -> 100Gbps (log10 0..11)
        // mapped to 0..1. No threshold below which the stream goes dark, so
        // even tiny housekeeping traffic shows a wisp of flow.
        const intensity = Math.max(0, Math.min(1,
            Math.log10(Math.max(this._rateBps, 1)) / 11));
        this._density = intensity;
        // Particle size on a squared curve so the low end collapses to
        // pinprick wisps and heavy traffic blooms to chunky dots:
        //   100bps  -> ~0.05  (tiny)
        //   100kbps -> ~0.28  (small)
        //   10Mbps  -> ~0.56  (mid)
        //   1Gbps   -> ~0.92  (chunky)
        //   100Gbps -> 1.35   (max)
        // Stored for use at spawn time only - particles already in flight
        // keep their birth size so a sudden rate change doesn't visually
        // resize dots that have already left the sender.
        this._currentSize = 0.0375 + (intensity * intensity) * 1.3125;
        // Velocity: 2.5 idle -> 6.5 saturated. Still communicates throughput
        // without slamming between crawl and jet on per-poll rate fluctuations.
        this._velocity = 2.5 + intensity * 4.0;
    }

    updateEndpoints(from, to) {
        this._from.set(from.x, from.y, from.z);
        this._to.set(to.x, to.y, to.z);
        this._direction.copy(this._to).sub(this._from);
        this._length = this._direction.length();
        this._direction.normalize();
    }

    advance(dt) {
        // Model dot emission like the sender pushing bytes onto the wire:
        // a constant per-second emission rate proportional to bitrate,
        // independent of link length. Link length only changes how long
        // each dot lives in flight, so a longer link naturally accumulates
        // more dots at steady state - but the dots arrive at the receiver
        // at the same rate (matching the bitrate) and are spaced the same
        // absolute distance apart whether the link is short or long.
        //
        // Emission rate scales non-linearly with the log-mapped density so
        // visible spacing differs noticeably across rate decades:
        //   density 0.25 (~Kbps)  -> ~0.75 dots/sec, big gaps
        //   density 0.5  (~Mbps)  -> ~3 dots/sec, breezy stream
        //   density 0.75 (~Gbps)  -> ~6.75 dots/sec, busy stream
        //   density 1.0  (saturat)-> ~12 dots/sec, thick stream
        // Each emission interval is jittered ±40% so the stream looks like
        // organic packet traffic rather than a metronome of dots.
        const EMIT_PER_SEC_MAX = 12;
        this._spawnAccumulator += this._density * this._density * EMIT_PER_SEC_MAX * dt;
        let sizesDirty = false;
        while (this._spawnAccumulator >= 1) {
            this._spawnAccumulator -= (0.6 + Math.random() * 0.8);
            for (let i = 0; i < this._max; i += 1) {
                if (this._t[i] < 0) {
                    // Spawn at the sender end of the link and freeze the
                    // size at the rate observed AT THIS INSTANT.
                    this._t[i] = 0;
                    this._sizes[i] = this._currentSize;
                    sizesDirty = true;
                    break;
                }
            }
        }

        // Advance existing particles.
        const v = this._velocity / Math.max(this._length, 0.001);  // normalised /sec
        for (let i = 0; i < this._max; i += 1) {
            if (this._t[i] < 0) continue;
            this._t[i] += v * dt;
            if (this._t[i] >= 1) {
                this._t[i] = -1;
                // Park outside the camera frustum so the inactive slot doesn't
                // contribute a vertex at world origin (which would pile up bright
                // additive-blended dots under bloom).
                this._positions[i * 3 + 0] = this._park;
                this._positions[i * 3 + 1] = this._park;
                this._positions[i * 3 + 2] = this._park;
                // Zero the slot's size so a stale uniform doesn't bleed a
                // dot at the park location if anything ever renders it.
                this._sizes[i] = 0;
                sizesDirty = true;
                continue;
            }
            const lerpX = this._from.x + (this._to.x - this._from.x) * this._t[i];
            const lerpY = this._from.y + (this._to.y - this._from.y) * this._t[i];
            const lerpZ = this._from.z + (this._to.z - this._from.z) * this._t[i];
            this._positions[i * 3 + 0] = lerpX;
            this._positions[i * 3 + 1] = lerpY;
            this._positions[i * 3 + 2] = lerpZ;
        }
        this.mesh.geometry.attributes.position.needsUpdate = true;
        if (sizesDirty) this.mesh.geometry.attributes.size.needsUpdate = true;
    }
}

// ----------------------------------------------------------------------------
// Small utilities
// ----------------------------------------------------------------------------

function roundRect(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
    ctx.fill();
}


function formatLinkSpeed(mbps) {
    if (!Number.isFinite(mbps) || mbps <= 0) return '';
    if (mbps >= 1000) {
        const gbps = mbps / 1000;
        // Common negotiated rates render cleanly: 1, 2.5, 5, 10 Gbps
        return `${gbps % 1 === 0 ? gbps.toFixed(0) : gbps.toFixed(1)} Gbps`;
    }
    return `${mbps} Mbps`;
}

function formatBps(bps) {
    if (!Number.isFinite(bps) || bps <= 0) return '0 bps';
    const units = ['bps', 'Kbps', 'Mbps', 'Gbps', 'Tbps'];
    let i = 0;
    let v = bps;
    while (v >= 1000 && i < units.length - 1) { v /= 1000; i += 1; }
    return `${v >= 100 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`;
}

function formatAge(ms) {
    if (!Number.isFinite(ms) || ms < 0) return 'just now';
    const s = Math.floor(ms / 1000);
    if (s < 60) return `${s}s ago`;
    const m = Math.floor(s / 60);
    if (m < 60) return `${m}m ago`;
    const h = Math.floor(m / 60);
    if (h < 24) return `${h}h ago`;
    const d = Math.floor(h / 24);
    return `${d}d ago`;
}

const _p = (n) => String(n).padStart(2, '0');
function _fmtDateTime(d) {
    return `${_p(d.getMonth()+1)}/${_p(d.getDate())}/${String(d.getFullYear()).slice(2)} ${_p(d.getHours())}:${_p(d.getMinutes())}:${_p(d.getSeconds())}`;
}

function escapeHtml(s) {
    return String(s)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

function lerpColor(a, b, t) {
    const ar = (a >> 16) & 0xff;
    const ag = (a >> 8) & 0xff;
    const ab = a & 0xff;
    const br = (b >> 16) & 0xff;
    const bg = (b >> 8) & 0xff;
    const bb = b & 0xff;
    const r = Math.round(ar + (br - ar) * t);
    const g = Math.round(ag + (bg - ag) * t);
    const bl = Math.round(ab + (bb - ab) * t);
    return (r << 16) | (g << 8) | bl;
}

function makeRadialBackgroundTexture(width, height) {
    // Render at actual screen resolution to avoid upscale banding, and add
    // subtle noise dithering to break up the 8-bit color quantization in
    // very dark gradients.
    const w = Math.max(width, 512);
    const h = Math.max(height, 256);
    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext('2d');
    const grd = ctx.createRadialGradient(w / 2, h / 2, w * 0.05, w / 2, h / 2, w * 0.65);
    grd.addColorStop(0, '#0a0b0e');
    grd.addColorStop(0.55, '#060708');
    grd.addColorStop(1, '#030304');
    ctx.fillStyle = grd;
    ctx.fillRect(0, 0, w, h);

    const tex = new THREE.CanvasTexture(canvas);
    tex.minFilter = THREE.LinearFilter;
    tex.magFilter = THREE.LinearFilter;
    tex.needsUpdate = true;
    return tex;
}

// Entry point used by Blazor JS interop.
let _instance = null;

export async function mount(canvasId, options = {}) {
    if (_instance) {
        _instance.dispose();
        _instance = null;
    }
    const el = document.getElementById(canvasId);
    if (!el) throw new Error(`Canvas #${canvasId} not found`);
    _instance = new LanFlowMap(el, options);
    await _instance.start();
    return _instance;
}

export function unmount() {
    if (_instance) {
        _instance.dispose();
        _instance = null;
    }
}

// Re-fetch the snapshot and rebuild the scene from scratch. Used by the
// upstream tracer wizard after committing new monitoring targets so the
// map picks up the freshly committed clouds without needing a page reload.
export async function reload() {
    if (!_instance) return;
    await _instance._reloadSnapshot();
}
