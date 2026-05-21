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

const COLORS = {
    background: 0x101820,
    fog: 0x101820,
    gateway: 0xfacc15,
    switchNode: 0x9aa6b2,
    ap: 0x3385d6,
    wiredClient: 0xc9d2e0,
    wifiClient: 0xe2e8f0,
    cloud: 0x4d556b,
    accent: 0x2ba89a,

    // Direction palette (locked, spec 5.7.1)
    downstream: 0x3385d6,   // var(--speed-download-color)
    upstream: 0x24bc70,     // var(--speed-upload-color)

    // Pipe backdrop (health shift)
    pipeCool: 0x1f4068,
    pipeWarm: 0xe79613,
    pipeHot: 0xee6368,
};

const NODE_RADIUS = {
    gateway: 1.6,
    switch: 1.2,
    ap: 1.0,
    wiredClient: 0.45,
    wifiClient: 0.45,
    cloud: 2.4,
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
};

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
        this._overlays = {
            wifiClients: true,
            wiredClients: true,
            clouds: true,
            speedTests: false,    // off by default - heavy visual, opt-in
        };
        this._filter = {
            text: '',
            bands: { '2.4': true, '5': true, '6': true },
        };
        this._mode = 'live';      // 'live' | 'historic'
        this._historicAt = null;  // Date when in historic mode

        this._panels = {};        // DOM refs for overlay UI

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

        this.scene = new THREE.Scene();
        this.scene.background = new THREE.Color(COLORS.background);
        this.scene.fog = new THREE.Fog(COLORS.fog, 60, 220);

        this.camera = new THREE.PerspectiveCamera(45, width / height, 0.1, 1000);
        this.camera.position.set(40, 25, 40);
        this.camera.lookAt(0, 0, 0);

        this.controls = new OrbitControls(this.camera, this.renderer.domElement);
        this.controls.enableDamping = true;
        this.controls.dampingFactor = 0.08;
        this.controls.rotateSpeed = 0.65;
        this.controls.zoomSpeed = 0.75;
        this.controls.minDistance = 8;
        this.controls.maxDistance = 220;
        this.controls.target.set(0, 0, 0);

        // Subtle hemispheric lighting so nodes have a sense of depth without flat shading.
        const hemi = new THREE.HemisphereLight(0xb1d4ff, 0x1a2029, 0.55);
        const ambient = new THREE.AmbientLight(0xffffff, 0.35);
        const key = new THREE.DirectionalLight(0xffffff, 0.7);
        key.position.set(40, 60, 30);
        this.scene.add(hemi, ambient, key);

        // Ground grid for spatial reference.
        const grid = new THREE.GridHelper(120, 24, 0x2a3340, 0x1c232e);
        grid.material.opacity = 0.55;
        grid.material.transparent = true;
        grid.position.y = -8;
        this.scene.add(grid);

        // Container groups so toggling layers is cheap.
        this.nodeGroup = new THREE.Group();
        this.linkGroup = new THREE.Group();
        this.cloudGroup = new THREE.Group();
        this.particleGroup = new THREE.Group();
        this.labelGroup = new THREE.Group();
        this.scene.add(this.nodeGroup, this.linkGroup, this.cloudGroup, this.particleGroup, this.labelGroup);
    }

    _initInteractions() {
        this._resizeObserver = new ResizeObserver(() => this._handleResize());
        this._resizeObserver.observe(this.canvas.parentElement || this.canvas);
    }

    _handleResize() {
        const rect = this.canvas.getBoundingClientRect();
        const width = Math.max(rect.width || this.canvas.clientWidth || 800, 320);
        const height = Math.max(rect.height || this.canvas.clientHeight || 480, 240);
        this.renderer.setSize(width, height, false);
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
        if (this._raf) cancelAnimationFrame(this._raf);
        if (this._pollTimer) clearInterval(this._pollTimer);
        if (this._resizeObserver) this._resizeObserver.disconnect();
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
        if (this.nodeGroup) disposeGroup(this.nodeGroup);
        if (this.linkGroup) disposeGroup(this.linkGroup);
        if (this.cloudGroup) disposeGroup(this.cloudGroup);
        if (this.particleGroup) disposeGroup(this.particleGroup);
        if (this.labelGroup) disposeGroup(this.labelGroup);
    }

    // ------------------------------------------------------------------------
    // Data loading
    // ------------------------------------------------------------------------

    async _loadSnapshot() {
        try {
            const res = await fetch(`${this.apiBase}/snapshot`, { credentials: 'same-origin' });
            if (!res.ok) throw new Error(`snapshot HTTP ${res.status}`);
            const snap = await res.json();
            this._snapshot = snap;

            this._layoutNodes(snap);
            this._buildNodes(snap);
            this._buildLinks(snap);
            this._buildClouds(snap);
            this._applyLiveRates(snap.liveRates || {});
        } catch (err) {
            this.onError(err);
        }
    }

    async _pollLive() {
        if (this._destroyed) return;
        try {
            const res = await fetch(`${this.apiBase}/live`, { credentials: 'same-origin' });
            if (!res.ok) return;
            const update = await res.json();
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
        // Normalize anchor coordinates to a scene-sized sphere (~30 unit radius).
        const sceneRadius = 30.0;
        const scale = sceneRadius / Math.max(bounds.radius, 1.0);

        const positions = new Map();
        const anchors = new Map();

        for (const node of snap.nodes) {
            const p = node.placement;
            if (p && p.source === PLACEMENT_SOURCE.Anchor) {
                positions.set(node.id, {
                    x: p.x * scale,
                    y: p.z * scale * 0.4,    // floors get vertical separation but compressed
                    z: p.y * scale,
                    pinned: true,
                });
                anchors.set(node.id, true);
            } else if (p && p.source === PLACEMENT_SOURCE.Interpolated) {
                positions.set(node.id, {
                    x: p.x * scale,
                    y: p.z * scale * 0.4 - 4,
                    z: p.y * scale,
                    pinned: false,
                });
            }
        }

        // Initial positions for unpinned nodes: scattered around the origin, with clouds
        // pushed far out along +X so they read as "outside" the LAN.
        let cloudIndex = 0;
        for (const node of snap.nodes) {
            if (positions.has(node.id)) continue;
            if (node.kind === NODE_KIND.Cloud) {
                cloudIndex += 1;
                positions.set(node.id, { x: 50 + cloudIndex * 14, y: 6, z: -10 + cloudIndex * 6, pinned: true });
                continue;
            }
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

            const group = new THREE.Group();
            const radius = this._nodeRadius(node.kind);
            const color = this._nodeColor(node.kind);

            // Soft outer halo
            const halo = new THREE.Mesh(
                new THREE.SphereGeometry(radius * 1.6, 24, 16),
                new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.12, depthWrite: false }),
            );
            group.add(halo);

            // Core sphere
            const core = new THREE.Mesh(
                new THREE.SphereGeometry(radius, 32, 24),
                new THREE.MeshStandardMaterial({
                    color,
                    emissive: color,
                    emissiveIntensity: 0.35,
                    roughness: 0.55,
                    metalness: 0.05,
                }),
            );
            group.add(core);

            group.position.set(pos.x, pos.y, pos.z);
            if (!node.online) {
                core.material.opacity = 0.55;
                core.material.transparent = true;
                halo.material.opacity = 0.05;
            }
            group.userData = { node };
            this.nodeGroup.add(group);
            this._nodeMeshes.set(node.id, group);

            // Text label, kept simple as a sprite billboard.
            if (node.name) {
                const sprite = this._makeLabelSprite(node.name);
                sprite.position.set(0, radius + 0.8, 0);
                group.add(sprite);
                this._labelSprites.set(node.id, sprite);
            }
        }
    }

    _buildLinks(snap) {
        for (const link of snap.links || []) {
            const a = this._positions.get(link.fromNodeId);
            const b = this._positions.get(link.toNodeId);
            if (!a || !b) continue;

            const pipe = this._makePipeMesh(a, b, link);
            this.linkGroup.add(pipe);

            // Particle streams - two ParticleStream instances (one per direction).
            const down = new ParticleStream({
                from: a, to: b, color: COLORS.downstream, particleCount: 0,
            });
            const up = new ParticleStream({
                from: b, to: a, color: COLORS.upstream, particleCount: 0,
            });
            this.particleGroup.add(down.mesh, up.mesh);

            this._linkMeshes.set(link.id, { pipe, down, up, link });
            this._nodesByLink.set(link.id, [link.fromNodeId, link.toNodeId]);
        }
    }

    _buildClouds(snap) {
        for (const cloud of snap.clouds || []) {
            const node = (snap.nodes || []).find((n) => n.id === cloud.id) || null;
            // Clouds may not have their own LanNode (the data is in LanCloud), so position
            // them outboard along +X by their Order.
            const x = 60 + cloud.order * 22;
            const y = 4 + (cloud.order % 2) * 4;
            const z = -8 + cloud.order * 5;
            const pos = { x, y, z, pinned: true };
            this._positions.set(cloud.id, pos);

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

            const geo = new THREE.SphereGeometry(NODE_RADIUS.cloud, 32, 24);
            const mat = new THREE.MeshStandardMaterial({
                color: baseColor,
                emissive: 0x1d2330,
                emissiveIntensity: 0.3,
                roughness: 0.95,
                metalness: 0.02,
                transparent: true,
                opacity: baseOpacity,
            });
            const blob = new THREE.Mesh(geo, mat);
            group.add(blob);

            // Outer wisp shell to read as a cloud, not a sphere.
            const wisp = new THREE.Mesh(
                new THREE.SphereGeometry(NODE_RADIUS.cloud * 1.7, 24, 16),
                new THREE.MeshBasicMaterial({ color: baseColor, transparent: true, opacity: 0.12, depthWrite: false }),
            );
            group.add(wisp);

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
            default: return COLORS.accent;
        }
    }

    _makeLabelSprite(text, subText = null) {
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
        ctx.fillStyle = 'rgba(16, 24, 32, 0.85)';
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
        for (const [linkId, link] of this._linkMeshes) {
            const r = rates[linkId];
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
        const tick = (now) => {
            if (this._destroyed) return;
            const dt = Math.min((now - this._lastFrame) / 1000, 0.1);
            this._lastFrame = now;
            this.controls?.update();
            for (const link of this._linkMeshes.values()) {
                link.down.advance(dt);
                link.up.advance(dt);
            }
            this.renderer.render(this.scene, this.camera);
            this._raf = requestAnimationFrame(tick);
        };
        this._lastFrame = performance.now();
        this._raf = requestAnimationFrame(tick);
    }

    _startPolling() {
        if (this._pollTimer) clearInterval(this._pollTimer);
        this._pollTimer = setInterval(() => {
            if (this._mode === 'live') this._pollLive();
        }, this.pollIntervalMs);
    }

    // ------------------------------------------------------------------------
    // Overlay UI (controls, filter, legend, scrubber, mode indicator)
    // ------------------------------------------------------------------------

    _buildOverlayUI() {
        if (!this.stage) return;

        // Filter panel (top-left)
        const filter = this._makePanel('lan-flow-map-filter');
        filter.innerHTML = `
            <div class="lan-flow-map-panel-title">Filter clients</div>
            <input class="lan-flow-map-search" type="search" placeholder="Search by name or MAC" />
            <div class="lan-flow-map-chips" data-chip-group="band">
                <span class="lan-flow-map-chip is-on" data-band="2.4">2.4 GHz</span>
                <span class="lan-flow-map-chip is-on" data-band="5">5 GHz</span>
                <span class="lan-flow-map-chip is-on" data-band="6">6 GHz</span>
            </div>
        `;
        const search = filter.querySelector('.lan-flow-map-search');
        search.addEventListener('input', (e) => {
            this._filter.text = (e.target.value || '').toLowerCase().trim();
            this._applyFilter();
        });
        filter.querySelectorAll('.lan-flow-map-chip').forEach((chip) => {
            chip.addEventListener('click', () => {
                const b = chip.dataset.band;
                this._filter.bands[b] = !this._filter.bands[b];
                chip.classList.toggle('is-on', this._filter.bands[b]);
                this._applyFilter();
            });
        });
        this._panels.filter = filter;

        // Controls panel (top-right) - overlay toggles
        const controls = this._makePanel('lan-flow-map-controls');
        controls.innerHTML = `
            <div class="lan-flow-map-panel-title">Overlays</div>
        `;
        const overlayDefs = [
            ['wifiClients', 'Wi-Fi clients'],
            ['wiredClients', 'Wired clients'],
            ['clouds', 'WAN clouds'],
            ['speedTests', 'Speed test paths'],
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
            });
            controls.appendChild(row);
        }
        this._panels.controls = controls;

        // Legend (bottom-right)
        const legend = this._makePanel('lan-flow-map-legend');
        legend.innerHTML = `
            <span class="lan-flow-map-legend-dot down"></span> Download
            <span class="lan-flow-map-legend-dot up"></span> Upload
        `;
        this._panels.legend = legend;

        // Status / mode indicator (bottom-left)
        const status = this._makePanel('lan-flow-map-status');
        const modeBadge = document.createElement('span');
        modeBadge.className = 'lan-flow-map-mode';
        modeBadge.textContent = 'Live';
        status.appendChild(modeBadge);
        this._panels.status = status;
        this._panels.modeBadge = modeBadge;

        // Timeline scrubber (bottom center)
        const scrubber = document.createElement('div');
        scrubber.className = 'lan-flow-map-scrubber';
        scrubber.innerHTML = `
            <div class="lan-flow-map-scrubber-row">
                <span data-role="left">-24h</span>
                <input class="lan-flow-map-scrubber-range" type="range" min="0" max="1000" value="1000" />
                <span data-role="right">Live</span>
            </div>
        `;
        const range = scrubber.querySelector('.lan-flow-map-scrubber-range');
        range.addEventListener('input', (e) => this._onScrubberInput(Number(e.target.value)));
        range.addEventListener('change', (e) => this._onScrubberChange(Number(e.target.value)));
        this.stage.appendChild(scrubber);
        this._panels.scrubber = scrubber;
        this._panels.scrubberRange = range;
        this._panels.scrubberLeft = scrubber.querySelector('[data-role="left"]');
        this._panels.scrubberRight = scrubber.querySelector('[data-role="right"]');
    }

    _makePanel(extraClass) {
        const el = document.createElement('div');
        el.className = `lan-flow-map-panel ${extraClass}`;
        this.stage.appendChild(el);
        return el;
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
                (value >= 998) ? 'Live' : at.toLocaleString();
        }
    }

    async _onScrubberChange(value) {
        if (value >= 998) {
            // Snap back to live.
            this._mode = 'live';
            this._historicAt = null;
            if (this._panels.modeBadge) {
                this._panels.modeBadge.textContent = 'Live';
                this._panels.modeBadge.classList.remove('is-historic');
            }
            await this._pollLive();
            return;
        }
        const at = this._scrubberValueToTime(value);
        this._mode = 'historic';
        this._historicAt = at;
        if (this._panels.modeBadge) {
            this._panels.modeBadge.textContent = 'Historic';
            this._panels.modeBadge.classList.add('is-historic');
        }
        await this._loadHistoric(at);
    }

    _scrubberValueToTime(value) {
        // Range 0..1000 maps to 24 hours ago .. now.
        const now = Date.now();
        const ms = now - (1000 - value) * (24 * 60 * 60 * 1000 / 1000);
        return new Date(ms);
    }

    async _loadHistoric(at) {
        try {
            const url = `${this.apiBase}/history?at=${encodeURIComponent(at.toISOString())}`;
            const res = await fetch(url, { credentials: 'same-origin' });
            if (!res.ok) return;
            const update = await res.json();
            this._applyLiveRates(update.linkRates || {});
        } catch (err) {
            // Surface but don't crash the rendering loop.
        }
    }
}

// ----------------------------------------------------------------------------
// ParticleStream - GPU-friendly one-direction particle flow along a link.
// Density and velocity both scale with rate (spec 5.7.1 hybrid dot semantics).
// ----------------------------------------------------------------------------

class ParticleStream {
    constructor({ from, to, color, particleCount = 0 }) {
        const fromV = new THREE.Vector3(from.x, from.y, from.z);
        const toV = new THREE.Vector3(to.x, to.y, to.z);
        this._from = fromV;
        this._to = toV;
        this._direction = toV.clone().sub(fromV);
        this._length = this._direction.length();
        this._direction.normalize();

        const MAX = 80;
        this._max = MAX;
        const positions = new Float32Array(MAX * 3);
        const t = new Float32Array(MAX);
        for (let i = 0; i < MAX; i += 1) {
            t[i] = -1;  // inactive
        }
        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        const material = new THREE.PointsMaterial({
            color,
            size: 0.55,
            transparent: true,
            opacity: 0.92,
            depthWrite: false,
            blending: THREE.AdditiveBlending,
        });
        this.mesh = new THREE.Points(geometry, material);
        this._t = t;
        this._positions = positions;

        this._rateBps = 0;
        this._spawnAccumulator = 0;
        this._density = 0;     // 0..1 (fraction of MAX)
        this._velocity = 0.4;  // units/sec along the link
    }

    setRate(bps) {
        this._rateBps = Math.max(bps, 0);
        // Log scale density: 100kbps -> 0.05, 10Mbps -> 0.20, 100Mbps -> 0.40,
        // 1Gbps -> 0.60, 10Gbps -> 0.85.
        const ratio = Math.log10(Math.max(this._rateBps, 1)) / 11;  // log10(1e11) = 11
        this._density = Math.max(0, Math.min(1, ratio));
        // Velocity scales similarly but with a floor so even idle traffic moves a tick.
        this._velocity = 0.6 + this._density * 8.0;
    }

    advance(dt) {
        const desired = this._density * this._max;
        // Spawn new particles to maintain desired density.
        let active = 0;
        for (let i = 0; i < this._max; i += 1) {
            if (this._t[i] >= 0) active += 1;
        }
        const need = Math.max(0, desired - active);
        this._spawnAccumulator += need * dt * 2.5;
        while (this._spawnAccumulator >= 1) {
            this._spawnAccumulator -= 1;
            for (let i = 0; i < this._max; i += 1) {
                if (this._t[i] < 0) {
                    this._t[i] = Math.random() * 0.15;
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
                this._positions[i * 3 + 0] = 0;
                this._positions[i * 3 + 1] = 0;
                this._positions[i * 3 + 2] = 0;
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
