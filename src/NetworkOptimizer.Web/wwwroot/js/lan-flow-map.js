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

export class LanFlowMap {
    constructor(canvasEl, options = {}) {
        this.canvas = canvasEl;
        this.apiBase = options.apiBase ?? '/api/monitoring/lan-flow-map';
        this.pollIntervalMs = options.pollIntervalMs ?? 2000;
        this.onError = options.onError ?? ((err) => console.error('[LanFlowMap]', err));

        this._snapshot = null;
        this._nodesByLink = new Map();
        this._nodeMeshes = new Map();   // nodeId -> THREE.Group
        this._linkMeshes = new Map();   // linkId -> { pipe, particlesDown, particlesUp }
        this._cloudMeshes = new Map();  // cloudId -> THREE.Group
        this._labelSprites = new Map(); // nodeId -> THREE.Sprite

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
        await this._loadSnapshot();
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
            // Cloud "blob" using a large sphere with displaced normals via a noise material.
            const geo = new THREE.SphereGeometry(NODE_RADIUS.cloud, 32, 24);
            const mat = new THREE.MeshStandardMaterial({
                color: COLORS.cloud,
                emissive: 0x1d2330,
                emissiveIntensity: 0.3,
                roughness: 0.95,
                metalness: 0.02,
                transparent: true,
                opacity: cloud.isPathProxy ? 0.55 : 0.85,
            });
            const blob = new THREE.Mesh(geo, mat);
            group.add(blob);

            // Outer wisp shell to read as a cloud, not a sphere.
            const wisp = new THREE.Mesh(
                new THREE.SphereGeometry(NODE_RADIUS.cloud * 1.7, 24, 16),
                new THREE.MeshBasicMaterial({ color: COLORS.cloud, transparent: true, opacity: 0.12, depthWrite: false }),
            );
            group.add(wisp);

            const label = this._makeLabelSprite(cloud.name || `AS${cloud.asn || ''}`);
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

    _makeLabelSprite(text) {
        const canvas = document.createElement('canvas');
        const ctx = canvas.getContext('2d');
        const fontSize = 36;
        const pad = 16;
        ctx.font = `${fontSize}px ui-sans-serif, system-ui, sans-serif`;
        const w = Math.ceil(ctx.measureText(text).width) + pad * 2;
        const h = fontSize + pad * 2;
        canvas.width = w;
        canvas.height = h;
        // Re-set font after canvas resize (canvas resize clears state).
        ctx.font = `${fontSize}px ui-sans-serif, system-ui, sans-serif`;
        ctx.fillStyle = 'rgba(16, 24, 32, 0.85)';
        roundRect(ctx, 0, 0, w, h, 12);
        ctx.fillStyle = '#f1f5f9';
        ctx.textBaseline = 'middle';
        ctx.fillText(text, pad, h / 2);
        const tex = new THREE.CanvasTexture(canvas);
        tex.needsUpdate = true;
        const mat = new THREE.SpriteMaterial({ map: tex, transparent: true, depthWrite: false });
        const sprite = new THREE.Sprite(mat);
        const scaleY = 1.2;
        const scaleX = scaleY * (w / h);
        sprite.scale.set(scaleX, scaleY, 1);
        return sprite;
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
        this._pollTimer = setInterval(() => this._pollLive(), this.pollIntervalMs);
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
