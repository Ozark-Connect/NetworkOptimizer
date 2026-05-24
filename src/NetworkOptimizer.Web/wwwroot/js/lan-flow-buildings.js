// 3D building renderer for the LAN Flow Map.
// Reads pre-projected building/floor/wall geometry from the snapshot and builds
// Three.js meshes: walls colored by material, floor planes, and pitched roofs.

import * as THREE from 'three';

const WALL_HEIGHT_M = 2.44;
const WALL_THICKNESS_M = 0.15;
const FLOOR_OPACITY = 0.25;
const WALL_OPACITY = 0.5;
const ROOF_OPACITY = 0.45;
const ROOF_COLOR = 0x5a6577;
const FLOOR_COLOR = 0x2a3545;
const ROOF_PITCH = 0.22;
const MAX_RIDGE_M = 2.5;
const FALLBACK_WALL_COLOR = '#94a3b8';

export function buildBuildings(snap) {
    const group = new THREE.Group();
    group.name = 'buildings';

    const buildings = snap.buildings;
    if (!buildings || buildings.length === 0) return group;

    const bounds = snap.bounds || { radius: 1.0, anchorCount: 0 };
    if (bounds.anchorCount === 0) return group;

    const sceneRadius = 30.0;
    const spreadFactor = 1.875;
    const scale = (sceneRadius / Math.max(bounds.radius, 1.0)) * spreadFactor;
    const colors = snap.materialColors || {};

    const wallHScene = WALL_HEIGHT_M * scale * 0.8;
    const wallDScene = WALL_THICKNESS_M * scale;

    for (const building of buildings) {
        const bGroup = new THREE.Group();
        bGroup.name = `building-${building.id}`;
        let maxFloorNum = -Infinity;

        for (const floor of building.floors) {
            if (floor.floorNumber > maxFloorNum) maxFloorNum = floor.floorNumber;
            const floorY = floor.z * scale * 0.8;

            _buildFloorPlane(floor, scale, floorY, bGroup);
            _buildWalls(floor, scale, floorY, wallHScene, wallDScene, colors, bGroup);
        }

        const topFloor = building.floors.find(f => f.floorNumber === maxFloorNum);
        if (topFloor) {
            _buildRoof(topFloor, building, scale, wallHScene, bGroup);
        }

        group.add(bGroup);
    }

    return group;
}

// -- coordinate helpers -------------------------------------------------------

function toScene(pt, scale) {
    return { x: -pt.x * scale, z: pt.y * scale };
}

function cornerToScene(meterX, meterY, scale) {
    return { x: -meterX * scale, z: meterY * scale };
}

// -- floor plane (from wall convex hull, not axis-aligned bbox) ---------------

function _buildFloorPlane(floor, scale, floorY, parent) {
    const pts = [];
    for (const wall of floor.walls) {
        for (const pt of wall.points) {
            pts.push(toScene(pt, scale));
        }
    }
    if (pts.length < 3) return;

    const hull = _convexHull(pts);
    if (hull.length < 3) return;

    // Fan triangulation from hull[0]
    const triCount = hull.length - 2;
    const verts = new Float32Array(triCount * 9);
    for (let i = 0; i < triCount; i++) {
        const a = hull[0], b = hull[i + 1], c = hull[i + 2];
        const off = i * 9;
        verts[off]     = a.x; verts[off + 1] = floorY; verts[off + 2] = a.z;
        verts[off + 3] = b.x; verts[off + 4] = floorY; verts[off + 5] = b.z;
        verts[off + 6] = c.x; verts[off + 7] = floorY; verts[off + 8] = c.z;
    }

    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(verts, 3));
    geo.computeVertexNormals();

    const mat = new THREE.MeshStandardMaterial({
        color: FLOOR_COLOR,
        transparent: true,
        opacity: FLOOR_OPACITY,
        depthWrite: false,
        side: THREE.DoubleSide,
    });
    parent.add(new THREE.Mesh(geo, mat));
}

// -- walls --------------------------------------------------------------------

function _buildWalls(floor, scale, floorY, wallH, wallD, colors, parent) {
    const matCache = new Map();

    function getMaterial(matKey) {
        if (matCache.has(matKey)) return matCache.get(matKey);
        const hex = colors[matKey] || FALLBACK_WALL_COLOR;
        const m = new THREE.MeshStandardMaterial({
            color: new THREE.Color(hex),
            transparent: true,
            opacity: WALL_OPACITY,
            depthWrite: false,
            side: THREE.DoubleSide,
            emissive: new THREE.Color(hex),
            emissiveIntensity: 0.05,
        });
        matCache.set(matKey, m);
        return m;
    }

    for (const wall of floor.walls) {
        const pts = wall.points;
        if (!pts || pts.length < 2) continue;

        for (let i = 0; i < pts.length - 1; i++) {
            const a = toScene(pts[i], scale);
            const b = toScene(pts[i + 1], scale);

            const dx = b.x - a.x;
            const dz = b.z - a.z;
            const segLen = Math.sqrt(dx * dx + dz * dz);
            if (segLen < 0.001) continue;

            const angle = Math.atan2(dz, dx);
            const mx = (a.x + b.x) / 2;
            const mz = (a.z + b.z) / 2;

            const segMat = (wall.materials && wall.materials[i]) || wall.material;
            const material = getMaterial(segMat);

            const geo = new THREE.BoxGeometry(segLen, wallH, wallD);
            const mesh = new THREE.Mesh(geo, material);
            mesh.position.set(mx, floorY + wallH / 2, mz);
            mesh.rotation.y = -angle;
            parent.add(mesh);
        }
    }
}

// -- pitched roof -------------------------------------------------------------

function _buildRoof(topFloor, building, scale, wallH, parent) {
    const allPts = [];
    for (const floor of building.floors) {
        for (const wall of floor.walls) {
            for (const pt of wall.points) {
                allPts.push(toScene(pt, scale));
            }
        }
    }
    if (allPts.length < 3) return;

    const hull = _convexHull(allPts);
    if (hull.length < 3) return;

    const obb = _orientedBoundingBox(hull);
    const floorY = topFloor.z * scale * 0.8;
    const eaveY = floorY + wallH;
    const maxRidgeScene = MAX_RIDGE_M * scale * 0.8;
    const ridgeHeight = Math.min(obb.shortLen * ROOF_PITCH, maxRidgeScene);
    const ridgeY = eaveY + ridgeHeight;

    // OBB corners and ridge endpoints
    const { corners, longAxis, shortAxis, center } = obb;
    const halfLong = obb.longLen / 2;
    const halfShort = obb.shortLen / 2;

    // Ridge endpoints (along the long axis, through center)
    const rA = { x: center.x + longAxis.x * halfLong, z: center.z + longAxis.z * halfLong };
    const rB = { x: center.x - longAxis.x * halfLong, z: center.z - longAxis.z * halfLong };

    // Eave corners
    const c0 = { x: rA.x + shortAxis.x * halfShort, z: rA.z + shortAxis.z * halfShort };
    const c1 = { x: rA.x - shortAxis.x * halfShort, z: rA.z - shortAxis.z * halfShort };
    const c2 = { x: rB.x - shortAxis.x * halfShort, z: rB.z - shortAxis.z * halfShort };
    const c3 = { x: rB.x + shortAxis.x * halfShort, z: rB.z + shortAxis.z * halfShort };

    // Overhang: extend eaves slightly past the walls
    const overhang = obb.shortLen * 0.06;
    const oc0 = _extend(c0, center, overhang);
    const oc1 = _extend(c1, center, overhang);
    const oc2 = _extend(c2, center, overhang);
    const oc3 = _extend(c3, center, overhang);
    const orA = _extend(rA, center, overhang);
    const orB = _extend(rB, center, overhang);

    const verts = new Float32Array([
        // Left slope (oc0, oc3, orA-ridge, orB-ridge)
        oc0.x, eaveY, oc0.z,   orA.x, ridgeY, orA.z,   oc3.x, eaveY, oc3.z,
        oc3.x, eaveY, oc3.z,   orA.x, ridgeY, orA.z,   orB.x, ridgeY, orB.z,
        // Right slope (oc1, oc2, orA-ridge, orB-ridge)
        oc1.x, eaveY, oc1.z,   oc2.x, eaveY, oc2.z,    orA.x, ridgeY, orA.z,
        oc2.x, eaveY, oc2.z,   orB.x, ridgeY, orB.z,    orA.x, ridgeY, orA.z,
        // Gable end A (oc0, oc1, orA-ridge)
        oc0.x, eaveY, oc0.z,   oc1.x, eaveY, oc1.z,    orA.x, ridgeY, orA.z,
        // Gable end B (oc3, oc2, orB-ridge)
        oc3.x, eaveY, oc3.z,   orB.x, ridgeY, orB.z,    oc2.x, eaveY, oc2.z,
    ]);

    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(verts, 3));
    geo.computeVertexNormals();

    const mat = new THREE.MeshStandardMaterial({
        color: ROOF_COLOR,
        transparent: true,
        opacity: ROOF_OPACITY,
        depthWrite: false,
        side: THREE.DoubleSide,
        emissive: new THREE.Color(ROOF_COLOR),
        emissiveIntensity: 0.03,
    });

    parent.add(new THREE.Mesh(geo, mat));
}

function _extend(point, center, amount) {
    const dx = point.x - center.x;
    const dz = point.z - center.z;
    const d = Math.sqrt(dx * dx + dz * dz) || 1;
    return {
        x: point.x + (dx / d) * amount,
        z: point.z + (dz / d) * amount,
    };
}

// -- convex hull (Andrew's monotone chain) ------------------------------------

function _convexHull(points) {
    const pts = points.slice().sort((a, b) => a.x - b.x || a.z - b.z);
    if (pts.length <= 2) return pts.slice();

    const cross = (o, a, b) =>
        (a.x - o.x) * (b.z - o.z) - (a.z - o.z) * (b.x - o.x);

    const lower = [];
    for (const p of pts) {
        while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], p) <= 0)
            lower.pop();
        lower.push(p);
    }

    const upper = [];
    for (let i = pts.length - 1; i >= 0; i--) {
        while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], pts[i]) <= 0)
            upper.pop();
        upper.push(pts[i]);
    }

    lower.pop();
    upper.pop();
    return lower.concat(upper);
}

// -- oriented bounding box (minimum-area rectangle) ---------------------------

function _orientedBoundingBox(hull) {
    let bestArea = Infinity;
    let bestResult = null;

    for (let i = 0; i < hull.length; i++) {
        const j = (i + 1) % hull.length;
        const edgeDx = hull[j].x - hull[i].x;
        const edgeDz = hull[j].z - hull[i].z;
        const edgeLen = Math.sqrt(edgeDx * edgeDx + edgeDz * edgeDz);
        if (edgeLen < 1e-9) continue;

        const ax = edgeDx / edgeLen;
        const az = edgeDz / edgeLen;
        const bx = -az;
        const bz = ax;

        let minA = Infinity, maxA = -Infinity;
        let minB = Infinity, maxB = -Infinity;
        for (const p of hull) {
            const projA = p.x * ax + p.z * az;
            const projB = p.x * bx + p.z * bz;
            if (projA < minA) minA = projA;
            if (projA > maxA) maxA = projA;
            if (projB < minB) minB = projB;
            if (projB > maxB) maxB = projB;
        }

        const area = (maxA - minA) * (maxB - minB);
        if (area < bestArea) {
            bestArea = area;
            const lenA = maxA - minA;
            const lenB = maxB - minB;
            const isALong = lenA >= lenB;
            const longLen = isALong ? lenA : lenB;
            const shortLen = isALong ? lenB : lenA;
            const longAxis = isALong ? { x: ax, z: az } : { x: bx, z: bz };
            const shortAxis = isALong ? { x: bx, z: bz } : { x: ax, z: az };
            const midA = (minA + maxA) / 2;
            const midB = (minB + maxB) / 2;
            const cx = midA * ax + midB * bx;
            const cz = midA * az + midB * bz;

            bestResult = {
                longLen,
                shortLen,
                longAxis,
                shortAxis,
                center: { x: cx, z: cz },
                corners: [
                    { x: minA * ax + minB * bx, z: minA * az + minB * bz },
                    { x: maxA * ax + minB * bx, z: maxA * az + minB * bz },
                    { x: maxA * ax + maxB * bx, z: maxA * az + maxB * bz },
                    { x: minA * ax + maxB * bx, z: minA * az + maxB * bz },
                ],
            };
        }
    }

    return bestResult;
}
