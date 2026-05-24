// 3D building renderer for the LAN Flow Map.
// Reads pre-projected building/floor/wall geometry from the snapshot and builds
// Three.js meshes: walls colored by material, floor planes, and pitched roofs.
// Procedural textures for brick, siding, wood, and concrete.

import * as THREE from 'three';

const WALL_HEIGHT_M = 2.8;
const WALL_THICKNESS_M = 0.15;
const FLOOR_OPACITY = 0.25;
const WALL_OPACITY = 0.5;
const ROOF_OPACITY = 0.45;
const ROOF_COLOR = 0x5a6577;
const FLOOR_COLOR = 0x2a3545;
const ROOF_PITCH = 0.28;
const MAX_RIDGE_M = 3.0;

// Realistic colors for 3D rendering - muted real-world tones instead of
// the bright signal-map palette from MaterialAttenuation.MaterialColors.
const REALISTIC_COLORS = {
    drywall:              '#E8E0D8',
    drywall_heavy:        '#D5CEC6',
    wood:                 '#7A5C3A',
    wood_paneling:        '#A08060',
    glass:                '#A8C8E0',
    glass_thin:           '#BDD8EC',
    brick:                '#8B4225',
    concrete:             '#8A8A8A',
    metal:                '#9A9A9A',
    door_wood:            '#5C3A1E',
    door_metal:           '#707070',
    door_glass:           '#9AB8D0',
    window_1_pane:        '#9AB8D8',
    window_2_pane:        '#88A8C8',
    window_3_pane:        '#7898B8',
    exterior:             '#C0B49C',
    exterior_residential: '#C0B49C',
    exterior_commercial:  '#707068',
    floor_wood:           '#6B5540',
    floor_concrete:       '#8A8A8A',
};

// Materials that get procedural textures
const TEXTURED = new Set([
    'brick', 'concrete', 'exterior_commercial',
    'exterior_residential', 'exterior',
    'wood', 'wood_paneling',
]);

const _texCache = new Map();

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
            _buildWalls(floor, scale, floorY, wallHScene, wallDScene, bGroup);
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

// -- procedural textures ------------------------------------------------------
// Each canvas represents a fixed real-world tile (tileSizeM). The raw canvas
// is cached; each wall segment creates a fresh CanvasTexture from it with
// repeat set from the wall's actual meter dimensions.

function _getTexCanvas(matKey) {
    if (_texCache.has(matKey)) return _texCache.get(matKey);
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d');
    let tileSizeM = 1.0;

    switch (matKey) {
        case 'brick':
            _drawBrick(canvas, ctx);
            tileSizeM = 1.0;
            break;
        case 'concrete':
        case 'exterior_commercial':
            _drawConcrete(canvas, ctx, matKey);
            tileSizeM = 1.5;
            break;
        case 'exterior_residential':
        case 'exterior':
            _drawSiding(canvas, ctx);
            tileSizeM = 1.0;
            break;
        case 'wood':
        case 'wood_paneling':
            _drawWoodVertical(canvas, ctx);
            tileSizeM = 0.6;
            break;
        default:
            _texCache.set(matKey, null);
            return null;
    }

    _texCache.set(matKey, { canvas, tileSizeM });
    return _texCache.get(matKey);
}

// Create a fresh Three.js texture from a cached canvas with segment-specific repeat.
function _makeTexture(cached, segLenM) {
    const tex = new THREE.CanvasTexture(cached.canvas);
    tex.wrapS = THREE.RepeatWrapping;
    tex.wrapT = THREE.RepeatWrapping;
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.repeat.set(segLenM / cached.tileSizeM, WALL_HEIGHT_M / cached.tileSizeM);
    return tex;
}

// Standard US brick: 7-5/8" x 2-1/4" with 3/8" mortar joints.
// Canvas represents 1m x 1m tile (~5 bricks wide, ~15 courses tall).
function _drawBrick(canvas, ctx) {
    canvas.width = 256;
    canvas.height = 256;
    const mortarColor = '#C0B8A8';
    const brickH = 17;
    const mortarGap = 3;
    const courseH = brickH + mortarGap;
    const brickColors = ['#8B4225', '#7E3B20', '#96482A', '#6E3320', '#A0502E', '#844028'];

    ctx.fillStyle = mortarColor;
    ctx.fillRect(0, 0, 256, 256);

    let row = 0;
    for (let y = 0; y < 256; y += courseH) {
        const offset = (row % 2) ? 32 : 0;
        const brickW = 52;
        for (let x = -offset; x < 256; x += brickW + mortarGap) {
            const ci = (row * 7 + Math.floor(x / 30)) % brickColors.length;
            ctx.fillStyle = brickColors[ci];
            const bx = Math.max(x, 0);
            const bw = Math.min(x + brickW, 256) - bx;
            if (bw <= 0) continue;
            ctx.fillRect(bx, y, bw, brickH);
            // Subtle shade variation
            ctx.fillStyle = `rgba(0,0,0,${0.02 + ((row * 3 + x) % 5) * 0.015})`;
            ctx.fillRect(bx, y, bw, brickH);
        }
        row++;
    }
}

function _drawConcrete(canvas, ctx, matKey) {
    canvas.width = 256;
    canvas.height = 256;
    const base = matKey === 'exterior_commercial' ? '#707068' : '#8A8A8A';
    ctx.fillStyle = base;
    ctx.fillRect(0, 0, 256, 256);
    // Subtle noise
    for (let i = 0; i < 800; i++) {
        const x = Math.random() * 256;
        const y = Math.random() * 256;
        const s = 1 + Math.random() * 3;
        ctx.fillStyle = `rgba(${Math.random() > 0.5 ? '255,255,255' : '0,0,0'},${0.03 + Math.random() * 0.04})`;
        ctx.fillRect(x, y, s, s);
    }
    // Form lines (horizontal joints in poured/block concrete)
    ctx.strokeStyle = 'rgba(0,0,0,0.08)';
    ctx.lineWidth = 1;
    for (let y = 64; y < 256; y += 64) {
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(256, y);
        ctx.stroke();
    }
}

function _drawSiding(canvas, ctx) {
    canvas.width = 256;
    canvas.height = 256;
    const boardH = 18;
    const colors = ['#C0B49C', '#B8AC94', '#C4B8A0', '#BCAE96'];

    for (let y = 0; y < 256; y += boardH) {
        const ci = Math.floor(y / boardH) % colors.length;
        ctx.fillStyle = colors[ci];
        ctx.fillRect(0, y, 256, boardH - 1);
        // Shadow line at bottom of each board
        ctx.fillStyle = 'rgba(0,0,0,0.12)';
        ctx.fillRect(0, y + boardH - 2, 256, 2);
        // Highlight at top
        ctx.fillStyle = 'rgba(255,255,255,0.06)';
        ctx.fillRect(0, y, 256, 1);
    }
}

// Weathered gray vertical board siding - exterior look for bare-wood structures
// (sheds, barns, outbuildings with no insulation/drywall).
function _drawWoodVertical(canvas, ctx) {
    canvas.width = 256;
    canvas.height = 256;
    const boardW = 28;
    const gapW = 2;
    const baseColors = ['#8A8580', '#7E7A75', '#908880', '#7A7670', '#858078'];

    ctx.fillStyle = '#1a1a1a';
    ctx.fillRect(0, 0, 256, 256);

    for (let x = 0; x < 256; x += boardW + gapW) {
        const ci = Math.floor(x / (boardW + gapW)) % baseColors.length;
        ctx.fillStyle = baseColors[ci];
        ctx.fillRect(x, 0, boardW, 256);
        // Vertical grain lines
        for (let g = 0; g < 4; g++) {
            const gx = x + 4 + Math.random() * (boardW - 8);
            ctx.strokeStyle = `rgba(0,0,0,${0.06 + Math.random() * 0.06})`;
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.moveTo(gx, 0);
            ctx.lineTo(gx + (Math.random() - 0.5) * 2, 256);
            ctx.stroke();
        }
        // Subtle weathering
        ctx.fillStyle = `rgba(0,0,0,${0.02 + Math.random() * 0.03})`;
        ctx.fillRect(x, 0, boardW, 256);
    }
}

// -- wall material factory ----------------------------------------------------
// Textured materials get a cloned texture with repeat scaled to the wall
// segment's real-world dimensions. Solid materials use realistic muted colors.

function _createWallMaterial(matKey, segLenM) {
    const hex = REALISTIC_COLORS[matKey] || '#94a3b8';
    const cached = TEXTURED.has(matKey) ? _getTexCanvas(matKey) : null;

    if (cached) {
        return new THREE.MeshStandardMaterial({
            map: _makeTexture(cached, segLenM),
            transparent: true,
            opacity: WALL_OPACITY,
            depthWrite: false,
            side: THREE.DoubleSide,
            roughness: 0.85,
        });
    }

    return new THREE.MeshStandardMaterial({
        color: new THREE.Color(hex),
        transparent: true,
        opacity: WALL_OPACITY,
        depthWrite: false,
        side: THREE.DoubleSide,
        emissive: new THREE.Color(hex),
        emissiveIntensity: 0.05,
        roughness: 0.7,
    });
}

// -- floor plane (convex hull of wall points, not axis-aligned bbox) ----------

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

function _buildWalls(floor, scale, floorY, wallH, wallD, parent) {
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

            // Real-world segment length from meter-space points
            const mDx = pts[i + 1].x - pts[i].x;
            const mDy = pts[i + 1].y - pts[i].y;
            const segLenM = Math.sqrt(mDx * mDx + mDy * mDy);

            const angle = Math.atan2(dz, dx);
            const mx = (a.x + b.x) / 2;
            const mz = (a.z + b.z) / 2;

            const segMat = (wall.materials && wall.materials[i]) || wall.material;
            const material = _createWallMaterial(segMat, segLenM);

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

    const { longAxis, shortAxis, center } = obb;
    const halfLong = obb.longLen / 2;
    const halfShort = obb.shortLen / 2;

    const rA = { x: center.x + longAxis.x * halfLong, z: center.z + longAxis.z * halfLong };
    const rB = { x: center.x - longAxis.x * halfLong, z: center.z - longAxis.z * halfLong };

    const c0 = { x: rA.x + shortAxis.x * halfShort, z: rA.z + shortAxis.z * halfShort };
    const c1 = { x: rA.x - shortAxis.x * halfShort, z: rA.z - shortAxis.z * halfShort };
    const c2 = { x: rB.x - shortAxis.x * halfShort, z: rB.z - shortAxis.z * halfShort };
    const c3 = { x: rB.x + shortAxis.x * halfShort, z: rB.z + shortAxis.z * halfShort };

    const overhang = obb.shortLen * 0.06;
    const oc0 = _extend(c0, center, overhang);
    const oc1 = _extend(c1, center, overhang);
    const oc2 = _extend(c2, center, overhang);
    const oc3 = _extend(c3, center, overhang);
    const orA = _extend(rA, center, overhang);
    const orB = _extend(rB, center, overhang);

    const verts = new Float32Array([
        oc0.x, eaveY, oc0.z,   orA.x, ridgeY, orA.z,   oc3.x, eaveY, oc3.z,
        oc3.x, eaveY, oc3.z,   orA.x, ridgeY, orA.z,   orB.x, ridgeY, orB.z,
        oc1.x, eaveY, oc1.z,   oc2.x, eaveY, oc2.z,    orA.x, ridgeY, orA.z,
        oc2.x, eaveY, oc2.z,   orB.x, ridgeY, orB.z,    orA.x, ridgeY, orA.z,
        oc0.x, eaveY, oc0.z,   oc1.x, eaveY, oc1.z,    orA.x, ridgeY, orA.z,
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
