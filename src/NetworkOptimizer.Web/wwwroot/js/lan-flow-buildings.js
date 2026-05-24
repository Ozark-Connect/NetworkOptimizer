// 3D building renderer for the LAN Flow Map.
// Reads pre-projected building/floor/wall geometry from the snapshot and builds
// Three.js meshes: walls colored by material, floor planes, and pitched roofs.
// Procedural textures for brick, siding, wood, and concrete.

import * as THREE from 'three';

const WALL_HEIGHT_M = 2.8;
const WALL_THICKNESS_M = 0.15;
const FLOOR_OPACITY = 0.25;
const WALL_OPACITY = 1.0;
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

// Materials that look different on the interior face. Maps to the
// REALISTIC_COLORS key used for the back-face solid color.
const INTERIOR_LOOK = {
    wood_paneling: '#A08060',       // warm wood paneling
    exterior_residential: '#E8E0D8', // drywall
    exterior: '#E8E0D8',
    exterior_commercial: '#D5CEC6',
};

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
// Each canvas represents a fixed real-world tile (tileSizeM). The Three.js
// texture is cached; per-segment materials clone it with repeat set from
// the wall's actual meter dimensions.

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
            _drawLogCabin(canvas, ctx);
            tileSizeM = 1.0;
            break;
        case 'wood_paneling':
            _drawWoodVertical(canvas, ctx);
            tileSizeM = 0.6;
            break;
        default:
            _texCache.set(matKey, null);
            return null;
    }

    // Cache the raw canvas and tile size - NOT the Three.js texture.
    // CanvasTexture.clone() doesn't reliably transfer image data to the GPU,
    // so each wall segment creates a fresh texture from the cached canvas.
    _texCache.set(matKey, { canvas, tileSizeM });
    return _texCache.get(matKey);
}

// Standard US modular brick: 7-5/8" x 2-1/4" (194mm x 57mm) with
// 3/8" (10mm) mortar joints. 512px canvas = 1m tile.
// At 0.512 px/mm: brick = 99px x 29px, mortar = 5px, course = 34px.
// ~5 bricks wide, ~15 courses tall per 1m tile.
function _drawBrick(canvas, ctx) {
    canvas.width = 512;
    canvas.height = 512;
    const brickW = 99;
    const brickH = 29;
    const mortarGap = 5;
    const courseH = brickH + mortarGap;
    const halfBrick = Math.floor((brickW + mortarGap) / 2);
    const brickColors = [
        '#8B4225', '#7E3B20', '#96482A', '#6E3320',
        '#A0502E', '#844028', '#924530', '#7A3822',
    ];

    // Mortar fill
    ctx.fillStyle = '#C0B8A8';
    ctx.fillRect(0, 0, 512, 512);

    let row = 0;
    for (let y = 0; y < 512; y += courseH) {
        const offset = (row % 2) ? halfBrick : 0;
        for (let x = -offset; x < 512; x += brickW + mortarGap) {
            const ci = (row * 7 + Math.floor((x + offset) / 50)) % brickColors.length;
            ctx.fillStyle = brickColors[ci];
            const bx = Math.max(x, 0);
            const bw = Math.min(x + brickW, 512) - bx;
            if (bw <= 0) continue;
            ctx.fillRect(bx, y, bw, brickH);

            // Subtle per-brick shade variation
            ctx.fillStyle = `rgba(0,0,0,${0.02 + ((row * 3 + x) % 5) * 0.012})`;
            ctx.fillRect(bx, y, bw, brickH);

            // Fine surface texture
            for (let t = 0; t < 3; t++) {
                const tx = bx + Math.random() * bw;
                const ty = y + Math.random() * brickH;
                ctx.fillStyle = `rgba(0,0,0,${0.03 + Math.random() * 0.04})`;
                ctx.fillRect(tx, ty, 1 + Math.random() * 3, 1);
            }
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

// Horizontal stacked logs with chinking between courses.
function _drawLogCabin(canvas, ctx) {
    canvas.width = 256;
    canvas.height = 256;
    const logH = 32;
    const chinkH = 4;
    const courseH = logH + chinkH;
    const logColors = ['#6B4226', '#5C3A1E', '#7A4E30', '#634020', '#715038'];

    ctx.fillStyle = '#C8BCA0';
    ctx.fillRect(0, 0, 256, 256);

    let row = 0;
    for (let y = 0; y < 256; y += courseH) {
        const ci = row % logColors.length;
        ctx.fillStyle = logColors[ci];
        ctx.fillRect(0, y, 256, logH);

        // Rounded log shading - highlight on top, shadow on bottom
        const grad = ctx.createLinearGradient(0, y, 0, y + logH);
        grad.addColorStop(0, 'rgba(255,255,255,0.12)');
        grad.addColorStop(0.3, 'rgba(255,255,255,0.05)');
        grad.addColorStop(0.7, 'rgba(0,0,0,0.03)');
        grad.addColorStop(1, 'rgba(0,0,0,0.15)');
        ctx.fillStyle = grad;
        ctx.fillRect(0, y, 256, logH);

        // Horizontal grain lines
        for (let g = 0; g < 3; g++) {
            const gy = y + 6 + Math.random() * (logH - 12);
            ctx.strokeStyle = `rgba(0,0,0,${0.04 + Math.random() * 0.05})`;
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.moveTo(0, gy);
            ctx.lineTo(256, gy + (Math.random() - 0.5) * 2);
            ctx.stroke();
        }

        // Occasional knot
        if (row % 3 === 1) {
            const kx = 60 + (row * 73) % 140;
            const ky = y + logH / 2;
            ctx.fillStyle = 'rgba(60,30,10,0.25)';
            ctx.beginPath();
            ctx.ellipse(kx, ky, 5, 3.5, 0, 0, Math.PI * 2);
            ctx.fill();
        }

        row++;
    }
}

// Horizontal lap siding for residential exteriors - dark warm gray-brown
// with visible wood grain texture and pronounced overlap shadow lines.
function _drawSiding(canvas, ctx) {
    canvas.width = 512;
    canvas.height = 512;
    const boardH = 36;

    // Cool charcoal gray palette matching fiber cement/engineered siding
    const baseColors = [
        [95, 92, 88], [90, 87, 83], [98, 95, 90],
        [88, 85, 81], [93, 90, 86], [96, 93, 88],
    ];

    let row = 0;
    for (let y = 0; y < 512; y += boardH) {
        const [br, bg, bb] = baseColors[row % baseColors.length];
        const shift = ((row * 13) % 7) - 3;
        ctx.fillStyle = `rgb(${br + shift},${bg + shift},${bb + shift})`;
        ctx.fillRect(0, y, 512, boardH);

        // Wood grain texture - horizontal fine lines across each board
        for (let gi = 0; gi < 12; gi++) {
            const gy = y + 3 + (gi / 12) * (boardH - 8) + (Math.random() - 0.5) * 2;
            const darkness = 0.03 + Math.random() * 0.05;
            ctx.strokeStyle = `rgba(30,20,10,${darkness})`;
            ctx.lineWidth = 0.5 + Math.random() * 0.5;
            ctx.beginPath();
            let gx = 0;
            ctx.moveTo(gx, gy);
            // Slight grain wander
            for (let sx = 40; sx <= 512; sx += 40) {
                gx = sx;
                ctx.lineTo(gx, gy + (Math.random() - 0.5) * 1.2);
            }
            ctx.stroke();
        }

        // Wider grain bands
        for (let si = 0; si < 2; si++) {
            const sy = y + 5 + Math.random() * (boardH - 12);
            ctx.fillStyle = `rgba(20,12,5,${0.03 + Math.random() * 0.04})`;
            ctx.fillRect(0, sy, 512, 1.5 + Math.random() * 2);
        }

        // Overlap shadow at bottom - pronounced dark line where boards overlap
        const shadowGrad = ctx.createLinearGradient(0, y + boardH - 5, 0, y + boardH);
        shadowGrad.addColorStop(0, 'rgba(0,0,0,0)');
        shadowGrad.addColorStop(0.4, 'rgba(0,0,0,0.12)');
        shadowGrad.addColorStop(1, 'rgba(0,0,0,0.22)');
        ctx.fillStyle = shadowGrad;
        ctx.fillRect(0, y + boardH - 5, 512, 5);

        // Highlight along top edge where board catches light
        ctx.fillStyle = 'rgba(255,255,255,0.06)';
        ctx.fillRect(0, y, 512, 1);
        ctx.fillStyle = 'rgba(255,255,255,0.03)';
        ctx.fillRect(0, y + 1, 512, 1);

        // Occasional board seam (vertical joint where boards butt together)
        if (row % 2 === 0) {
            const sx = 180 + ((row * 97) % 200);
            ctx.fillStyle = 'rgba(0,0,0,0.1)';
            ctx.fillRect(sx, y + 1, 1, boardH - 3);
        }

        row++;
    }
}

// Weathered gray vertical board siding - exterior look for bare-wood
// structures (sheds, barns, outbuildings). Colors are pushed cool/blue-gray
// to compensate for the scene's ACES tone mapping and warm lighting.
function _drawWoodVertical(canvas, ctx) {
    canvas.width = 512;
    canvas.height = 512;
    const boardW = 62;
    const gapW = 4;

    // Cool dark gap background
    ctx.fillStyle = '#1a1e22';
    ctx.fillRect(0, 0, 512, 512);

    // Cool blue-gray palette - compensates for scene's warm tone mapping
    const baseColors = [
        [125, 130, 138], [118, 123, 132], [130, 135, 142],
        [112, 118, 126], [122, 127, 135], [120, 125, 133],
        [115, 120, 128], [128, 132, 140],
    ];

    let boardIdx = 0;
    for (let x = 0; x < 512; x += boardW + gapW) {
        const [br, bg, bb] = baseColors[boardIdx % baseColors.length];
        // Per-board color variation
        const shift = ((boardIdx * 17) % 11) - 5;
        const r = Math.min(255, Math.max(0, br + shift));
        const g = Math.min(255, Math.max(0, bg + shift));
        const b = Math.min(255, Math.max(0, bb + shift));

        ctx.fillStyle = `rgb(${r},${g},${b})`;
        ctx.fillRect(x, 0, boardW, 512);

        // Vertical grain - cool-toned fine lines with slight wander
        for (let gi = 0; gi < 8; gi++) {
            const gx = x + 3 + (gi / 8) * (boardW - 6) + (Math.random() - 0.5) * 4;
            const darkness = 0.04 + Math.random() * 0.08;
            ctx.strokeStyle = `rgba(20,25,35,${darkness})`;
            ctx.lineWidth = 0.5 + Math.random() * 1;
            ctx.beginPath();
            let cx = gx;
            ctx.moveTo(cx, 0);
            for (let y = 32; y <= 512; y += 32) {
                cx += (Math.random() - 0.5) * 1.5;
                ctx.lineTo(cx, y);
            }
            ctx.stroke();
        }

        // Wider grain bands - cool dark streaks
        for (let si = 0; si < 2; si++) {
            const sx = x + 8 + Math.random() * (boardW - 16);
            const sw = 2 + Math.random() * 4;
            ctx.fillStyle = `rgba(15,20,30,${0.04 + Math.random() * 0.06})`;
            ctx.fillRect(sx, 0, sw, 512);
        }

        // Weathering gradient - darker at bottom, lighter silver at top
        const wGrad = ctx.createLinearGradient(0, 0, 0, 512);
        wGrad.addColorStop(0, 'rgba(180,185,195,0.06)');
        wGrad.addColorStop(0.4, 'rgba(0,0,0,0)');
        wGrad.addColorStop(0.85, 'rgba(0,0,0,0.04)');
        wGrad.addColorStop(1, 'rgba(15,20,25,0.08)');
        ctx.fillStyle = wGrad;
        ctx.fillRect(x, 0, boardW, 512);

        // Knot (one per ~third board)
        if (boardIdx % 3 === 0) {
            const ky = 80 + ((boardIdx * 137) % 300);
            const kx = x + boardW / 2 + (Math.random() - 0.5) * 10;
            const kr = 4 + Math.random() * 3;

            ctx.strokeStyle = 'rgba(30,35,45,0.35)';
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.ellipse(kx, ky, kr, kr * 0.7, 0, 0, Math.PI * 2);
            ctx.stroke();

            ctx.fillStyle = 'rgba(40,45,55,0.25)';
            ctx.beginPath();
            ctx.ellipse(kx, ky, kr - 1, kr * 0.6, 0, 0, Math.PI * 2);
            ctx.fill();

            for (let a = -2; a <= 2; a++) {
                ctx.strokeStyle = 'rgba(20,25,35,0.06)';
                ctx.lineWidth = 0.5;
                ctx.beginPath();
                const offset = (kr + 4 + Math.abs(a) * 3) * (a < 0 ? -1 : 1);
                ctx.moveTo(kx + offset, ky - 30);
                ctx.quadraticCurveTo(kx + offset * 0.3, ky, kx + offset, ky + 30);
                ctx.stroke();
            }
        }

        // Board edge shadow/highlight
        ctx.fillStyle = 'rgba(0,0,0,0.08)';
        ctx.fillRect(x + boardW - 2, 0, 2, 512);
        ctx.fillStyle = 'rgba(200,210,220,0.04)';
        ctx.fillRect(x, 0, 1, 512);

        boardIdx++;
    }
}

// -- wall material factory ----------------------------------------------------
// Textured materials get a cloned texture with repeat scaled to the wall
// segment's real-world dimensions. Solid materials use realistic muted colors.

function _createWallMaterial(matKey, segLenM) {
    const hex = REALISTIC_COLORS[matKey] || '#94a3b8';
    const cached = TEXTURED.has(matKey) ? _getTexCanvas(matKey) : null;

    if (cached) {
        const tex = new THREE.CanvasTexture(cached.canvas);
        tex.wrapS = THREE.RepeatWrapping;
        tex.wrapT = THREE.RepeatWrapping;
        tex.colorSpace = THREE.SRGBColorSpace;
        tex.repeat.set(segLenM / cached.tileSizeM, WALL_HEIGHT_M / cached.tileSizeM);
        return new THREE.MeshStandardMaterial({
            map: tex,
            transparent: WALL_OPACITY < 1.0,
            opacity: WALL_OPACITY,
            depthWrite: WALL_OPACITY >= 1.0,
            side: THREE.DoubleSide,
            roughness: 0.85,
        });
    }

    return new THREE.MeshStandardMaterial({
        color: new THREE.Color(hex),
        transparent: WALL_OPACITY < 1.0,
        opacity: WALL_OPACITY,
        depthWrite: WALL_OPACITY >= 1.0,
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
