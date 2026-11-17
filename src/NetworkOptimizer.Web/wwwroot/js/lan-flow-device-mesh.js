// 3D device bodies for the LAN Flow Map, built from the same icons the 2D map draws.
// An icon is a product face shot with an alpha channel. Its outline is extruded into a slab
// (the icon on the face, the body colour on the sides and back) or, for a round body,
// revolved around the vertical axis.
//
// Local frame of every geometry: the icon plane is XY, icon-up is +Y, the face looks along +Z,
// and the longest side of the device is 1.

import * as THREE from 'three';

/** Slab thickness as a share of the device's longest side. */
export const DEPTH = { accessPoint: 0.20, wallPlate: 0.14, chassis: 0.60, panel: 0.24, client: 0.10, clientMid: 0.30 };

// Width in texels of the solid body-colour strip beside the icon. Sides and back sample it.
const STRIP = 8;

const _cache = new Map();   // key -> { state: 'loading' | 'ready' | 'failed', promise, entry }

/** The icon file of a network device model ("USW Pro 24 PoE" -> usw-pro-24-poe.png), as the 2D map resolves it. */
export function deviceIconUrl(model) {
    return model ? `/images/devices/${model.toLowerCase().replace(/ /g, '-')}.png` : null;
}

/** Whether a model's body is round (a cylinder or capsule), so its outline is revolved, not extruded. */
export function isRoundBody(model) {
    const m = (model || '').toLowerCase().replace(/ /g, '-');
    return /mesh(?!-pro)|^udr|^udm$|flexhd/.test(m);
}

/** Whether a model is a flat panel whose icon is its broad face (the U-LTE and U5G cellular modems), not a chassis front. */
export function isPanelModel(model) {
    return /^(u-?lte|u5g)/.test((model || '').toLowerCase().replace(/ /g, '-'));
}

/** Whether a model is a slim plate (the UDB Device Bridges): the map counts it as a switch, but it is no chassis. */
export function isSlimModel(model) {
    return /^udb/.test((model || '').toLowerCase().replace(/ /g, '-'));
}

// Desktop gateways and switches whose front face is as wide and thin as a rack unit's.
const COMPACT_MODEL = /^(ucg|uxg-(fiber|lite|max)|usg$|usg-3p|uck|udb|usw-(flex|lite|ultra)|usw-(pro|enterprise|pro-xg)-8|us-8|usw-industrial)/;

/**
 * A network device's real longest side in metres, estimated: a face at least four times wider
 * than tall is a rack-width chassis (0.44) unless the model is a compact desktop one, and
 * anything else is hand-sized (0.2). Nothing goes below hand-sized on purpose: a palm-sized
 * device drawn in true proportion is too small to find (TJ, 2026-11-17).
 */
export function estimateRealSizeM(entry, model) {
    const m = (model || '').toLowerCase().replace(/ /g, '-');
    const size = entry.bounds.getSize(new THREE.Vector3());
    return size.x >= size.y * 4 && !COMPACT_MODEL.test(m) ? 0.44 : 0.2;
}

/** Whether a model stands upright on a desk (in-wall and wall models on their stand). Mirrors PropagationService.IsStandMountModel. */
export function isStandMountModel(model) {
    const m = (model || '').toLowerCase().replace(/ /g, '-').replace(/-b$/, '');
    return m.includes('-iw') || m.includes('-wall');
}

/**
 * The cached body for an icon, or null while it loads or when it cannot be built.
 * @param {string} url icon URL (same-origin: the outline is read from its pixels)
 * @param {{round?: boolean, depth?: number}} shape
 */
export function getBody(url, shape) {
    const c = _cache.get(_key(url, shape));
    return c?.state === 'ready' ? c.entry : null;
}

/** Loads and builds the body for an icon. Resolves null when the icon is missing or has no usable outline. */
export function loadBody(url, shape) {
    const key = _key(url, shape);
    let c = _cache.get(key);
    if (!c) {
        c = { state: 'loading', entry: null, promise: null };
        c.promise = _loadImage(url)
            .then(img => {
                c.entry = _build(img, shape);
                c.state = c.entry ? 'ready' : 'failed';
                return c.entry;
            })
            .catch(() => { c.state = 'failed'; return null; });
        _cache.set(key, c);
    }
    return c.promise;
}

/**
 * A mesh for a cached body. Geometry and texture are shared; the material is the caller's own,
 * so opacity and glow stay per node.
 */
export function makeBodyMesh(entry, emissiveIntensity) {
    const material = new THREE.MeshStandardMaterial({
        // Most icons are white hardware. At full white the map's bloom washes the face out.
        color: 0xcccccc,
        map: entry.texture,
        emissiveMap: entry.texture,
        emissive: 0xffffff,
        emissiveIntensity,
        roughness: 0.6,
        metalness: 0.1,
    });
    return new THREE.Mesh(entry.geometry, material);
}

/**
 * Orientation of a body with a saved heading. headingDeg is a compass bearing (0 = north,
 * clockwise) and always points away from the face of the device.
 *   ceiling: face down, icon-up along the heading.
 *   wall, or a stand-mount model on a desk: upright, face along the heading.
 *   desktop: face up, icon-up along the heading.
 * A round body stands upright on every mount.
 */
export function poseQuaternion(mount, headingDeg, { round = false, stand = false } = {}) {
    const t = headingDeg * Math.PI / 180;
    // The map lays out x = -east, z = north.
    const h = new THREE.Vector3(-Math.sin(t), 0, Math.cos(t));
    const up = new THREE.Vector3(0, 1, 0);
    let normal, iconUp;
    if (round || mount === 'wall' || (mount === 'desktop' && stand)) { normal = h; iconUp = up; }
    else if (mount === 'desktop') { normal = up; iconUp = h; }
    else { normal = up.clone().negate(); iconUp = h; }
    const x = new THREE.Vector3().crossVectors(iconUp, normal);
    return new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(x, iconUp, normal));
}

function _key(url, shape) {
    return `${url}|${shape?.round ? 'round' : (shape?.depth ?? DEPTH.client)}`;
}

function _loadImage(url) {
    return new Promise((resolve, reject) => {
        const img = new Image();
        img.onload = () => resolve(img);
        img.onerror = reject;
        img.src = url;
    });
}

function _build(img, shape) {
    const w = img.naturalWidth, h = img.naturalHeight;
    if (!w || !h) return null;
    const canvas = document.createElement('canvas');
    canvas.width = w + STRIP;
    canvas.height = h;
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    ctx.drawImage(img, 0, 0);
    const data = ctx.getImageData(0, 0, w, h).data;

    // Outline: the left and right extent of the opaque pixels on each row. Device outlines are
    // row-convex enough for this; a gap between legs or antennas fills in.
    const rows = [];
    let x0 = w, x1 = 0, y0 = h, y1 = 0;
    let sr = 0, sg = 0, sb = 0, sn = 0;
    for (let y = 0; y < h; y++) {
        let l = -1, r = -1;
        for (let x = 0; x < w; x++) {
            const i = (y * w + x) * 4;
            if (data[i + 3] > 96) { if (l < 0) l = x; r = x; }
            if (data[i + 3] > 200) { sr += data[i]; sg += data[i + 1]; sb += data[i + 2]; sn++; }
        }
        if (l >= 0) {
            rows.push({ y, l, r: r + 1 });
            x0 = Math.min(x0, l); x1 = Math.max(x1, r + 1); y0 = Math.min(y0, y); y1 = Math.max(y1, y + 1);
        }
    }
    if (rows.length < 4 || sn === 0 || x1 - x0 < 4) return null;

    // Texture: the icon over its mean body colour (so the soft edge pixels blend into the body,
    // not into black), plus a solid strip of that colour for the sides and back.
    const body = `rgb(${Math.round(sr / sn)},${Math.round(sg / sn)},${Math.round(sb / sn)})`;
    ctx.globalCompositeOperation = 'destination-over';
    ctx.fillStyle = body;
    ctx.fillRect(0, 0, w + STRIP, h);
    ctx.globalCompositeOperation = 'source-over';
    const texture = new THREE.CanvasTexture(canvas);
    texture.colorSpace = THREE.SRGBColorSpace;
    texture.anisotropy = 4;

    const s = { rows: rows.filter((_, i) => i % 2 === 0 || i === rows.length - 1), w, h, cx: (x0 + x1) / 2, cy: (y0 + y1) / 2, size: Math.max(x1 - x0, y1 - y0) };
    const geometry = shape?.round ? _lathe(s) : _slab(s, shape?.depth ?? DEPTH.client);

    geometry.computeBoundingBox();
    return { geometry, texture, bounds: geometry.boundingBox.clone() };
}

// UV of a point on the icon plane; `plain` sends it to the body-colour strip instead.
function _setUv(uv, i, s, x, y, plain) {
    if (plain) {
        uv[i * 2] = (s.w + STRIP / 2) / (s.w + STRIP);
        uv[i * 2 + 1] = 0.5;
    } else {
        uv[i * 2] = (x * s.size + s.cx) / (s.w + STRIP);
        uv[i * 2 + 1] = 1 - (s.cy - y * s.size) / s.h;
    }
}

// A zero-length normal becomes NaN in the lit shader, and the map's bloom pass smears one NaN
// pixel into a screen-sized dark block. Never leave one: a zero-area triangle still covers a
// pixel at some camera angles.
function _fixNormals(geo, fallback) {
    const n = geo.attributes.normal;
    for (let i = 0; i < n.count; i++) {
        const len = Math.hypot(n.getX(i), n.getY(i), n.getZ(i));
        if (!(len > 0.5)) { const f = fallback(i); n.setXYZ(i, f[0], f[1], f[2]); }
    }
}

function _slab(s, depth) {
    const X = x => (x - s.cx) / s.size, Y = y => (s.cy - y) / s.size;
    const ring = [];
    s.rows.forEach((r, i) => ring.push([X(r.l), i === 0 ? Y(r.y) : Y(r.y + 0.5)]));
    for (let i = s.rows.length - 1; i >= 0; i--) ring.push([X(s.rows[i].r), Y(s.rows[i].y + 0.5)]);
    // Drop points on a straight run. The triangulator turns them into zero-area triangles.
    const pts = [];
    for (let i = 0; i < ring.length; i++) {
        const a = pts.length ? pts[pts.length - 1] : ring[ring.length - 1], b = ring[i], c = ring[(i + 1) % ring.length];
        if (Math.abs((b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0])) > 1e-9) pts.push(b);
    }
    const shape = new THREE.Shape();
    pts.forEach((p, i) => i === 0 ? shape.moveTo(p[0], p[1]) : shape.lineTo(p[0], p[1]));

    const bevel = Math.min(0.02, depth / 4);
    const inner = depth - bevel * 2;
    const geo = new THREE.ExtrudeGeometry(shape, {
        depth: inner, bevelEnabled: true, bevelSize: bevel, bevelThickness: bevel, bevelSegments: 2,
    });
    geo.translate(0, 0, -inner / 2);

    // Group 0 is the two caps, group 1 the bevel and side walls. Only the front cap shows the icon.
    const pos = geo.attributes.position;
    const uv = new Float32Array(pos.count * 2);
    const caps = geo.groups.find(g => g.materialIndex === 0);
    for (let i = 0; i < pos.count; i++) {
        const front = caps && i >= caps.start && i < caps.start + caps.count && pos.getZ(i) > 0;
        _setUv(uv, i, s, pos.getX(i), pos.getY(i), !front);
    }
    geo.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    geo.clearGroups();
    geo.computeVertexNormals();
    _fixNormals(geo, i => [0, 0, pos.getZ(i) < 0 ? -1 : 1]);
    return geo;
}

function _lathe(s) {
    const Y = y => (s.cy - y) / s.size;
    const pts = [new THREE.Vector2(0, Y(s.rows[0].y))];
    for (const r of s.rows) pts.push(new THREE.Vector2((r.r - r.l) / 2 / s.size, Y(r.y + 0.5)));
    pts.push(new THREE.Vector2(0, Y(s.rows[s.rows.length - 1].y + 1)));
    const geo = new THREE.LatheGeometry(pts.reverse(), 40);

    // The icon is projected straight through, pulled 4% toward the axis so the rim samples
    // solid pixels. A round body reads the same from the front and the back.
    const pos = geo.attributes.position;
    const uv = new Float32Array(pos.count * 2);
    for (let i = 0; i < pos.count; i++) _setUv(uv, i, s, pos.getX(i) * 0.96, pos.getY(i), false);
    geo.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    geo.computeVertexNormals();
    // The first and last column's pole vertices sit only in zero-area triangles.
    _fixNormals(geo, i => [0, pos.getY(i) < 0 ? -1 : 1, 0]);
    return geo;
}
