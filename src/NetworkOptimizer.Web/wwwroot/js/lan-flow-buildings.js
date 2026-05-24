// 3D building renderer for the LAN Flow Map.
// Reads pre-projected building/floor/wall geometry from the snapshot and builds
// Three.js meshes: walls colored by material, floor planes, and pitched roofs.
// Procedural textures for brick, siding, wood, and concrete.

import * as THREE from 'three';

const WALL_HEIGHT_M = 2.9;
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
    'glass', 'glass_thin',
]);

// Materials drawn per-segment (no tiling). Each segment gets a fresh canvas
// sized to its real-world proportions.
const PER_SEGMENT = new Set([
    'window_1_pane', 'window_2_pane', 'window_3_pane',
    'door_wood', 'door_metal', 'door_glass',
]);

// Materials that look different on the interior face. Value is the
// interior texture key used by _getTexCanvas / _getInteriorTexCanvas.
const INTERIOR_LOOK = {
    wood_paneling: 'interior_wood',
    exterior_residential: 'interior_drywall',
    exterior: 'interior_drywall',
    exterior_commercial: 'interior_drywall',
};

const _interiorTexCache = new Map();

// Flat surround colors for the wall area around doors/windows. Approximates
// what the adjacent wall material looks like as a solid fill.
const SURROUND_COLORS = {
    exterior_residential: '#707072',
    exterior:             '#707072',
    exterior_commercial:  '#686868',
    wood_paneling:        '#757880',
    wood:                 '#6B4226',
    brick:                '#8B4225',
    concrete:             '#808080',
    drywall:              '#D8D2C8',
    drywall_heavy:        '#CCC6BC',
    metal:                '#888888',
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

        // Determine winding direction from the longest wall loop to decide
        // which BoxGeometry face (+Z or -Z) is the exterior.
        const winding = _detectWinding(building, scale);

        for (const floor of building.floors) {
            if (floor.floorNumber > maxFloorNum) maxFloorNum = floor.floorNumber;
            const floorY = floor.z * scale * 0.8;

            _buildFloorPlane(floor, scale, floorY, bGroup);
            _buildWalls(floor, scale, floorY, wallHScene, wallDScene, winding, bGroup);
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

// Compute signed area of the longest wall loop to determine winding direction.
// Returns 1 if the exterior face is +Z (group 4), -1 if it's -Z (group 5).
function _detectWinding(building, scale) {
    // Find the longest closed wall (most points) as the representative outline
    let bestWall = null;
    let bestLen = 0;
    for (const floor of building.floors) {
        for (const wall of floor.walls) {
            if (wall.points.length > bestLen) {
                bestLen = wall.points.length;
                bestWall = wall;
            }
        }
    }
    if (!bestWall || bestWall.points.length < 3) return 1;

    // Shoelace formula for signed area in scene space
    const pts = bestWall.points.map(p => toScene(p, scale));
    let area = 0;
    for (let i = 0; i < pts.length; i++) {
        const j = (i + 1) % pts.length;
        area += pts[i].x * pts[j].z - pts[j].x * pts[i].z;
    }
    // Negative signed area = clockwise in XZ plane = +Z face points outward
    return area < 0 ? 1 : -1;
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
        case 'glass':
        case 'glass_thin':
            _drawGlassWall(canvas, ctx, matKey === 'glass_thin');
            tileSizeM = 1.0;
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

// Horizontal lap siding for residential exteriors - warm greige (gray-beige)
// engineered wood siding with visible grain texture, pronounced overlap shadows,
// and staggered vertical butt joints. Modeled after fiber cement/LP SmartSide.
function _drawSiding(canvas, ctx) {
    canvas.width = 512;
    canvas.height = 512;
    const boardH = 40;

    // Cool-shifted greige to survive ACES tone mapping without going warm/wood
    const baseR = 112, baseG = 112, baseB = 115;

    let row = 0;
    for (let y = 0; y < 512; y += boardH) {
        // Very subtle per-board variation
        const shift = ((row * 11) % 5) - 2;
        ctx.fillStyle = `rgb(${baseR + shift},${baseG + shift},${baseB + shift})`;
        ctx.fillRect(0, y, 512, boardH);

        // Horizontal wood grain showing through - the defining texture of
        // engineered wood siding vs vinyl
        for (let gi = 0; gi < 14; gi++) {
            const gy = y + 2 + (gi / 14) * (boardH - 6) + (Math.random() - 0.5) * 1.5;
            const darkness = 0.03 + Math.random() * 0.06;
            ctx.strokeStyle = `rgba(30,30,35,${darkness})`;
            ctx.lineWidth = 0.5 + Math.random() * 0.8;
            ctx.beginPath();
            let gx = 0;
            ctx.moveTo(gx, gy);
            for (let sx = 30; sx <= 512; sx += 30) {
                gx = sx;
                ctx.lineTo(gx, gy + (Math.random() - 0.5) * 0.8);
            }
            ctx.stroke();
        }

        // Wider grain bands - slightly darker sweeps across the board
        for (let si = 0; si < 3; si++) {
            const sy = y + 4 + Math.random() * (boardH - 10);
            ctx.fillStyle = `rgba(25,25,30,${0.025 + Math.random() * 0.035})`;
            ctx.fillRect(0, sy, 512, 1 + Math.random() * 2.5);
        }

        // Pronounced overlap shadow at bottom - crisp dark line
        const shadowGrad = ctx.createLinearGradient(0, y + boardH - 6, 0, y + boardH);
        shadowGrad.addColorStop(0, 'rgba(0,0,0,0)');
        shadowGrad.addColorStop(0.3, 'rgba(0,0,0,0.08)');
        shadowGrad.addColorStop(0.7, 'rgba(0,0,0,0.18)');
        shadowGrad.addColorStop(1, 'rgba(0,0,0,0.28)');
        ctx.fillStyle = shadowGrad;
        ctx.fillRect(0, y + boardH - 6, 512, 6);

        // Light catch along top edge
        ctx.fillStyle = 'rgba(255,255,255,0.07)';
        ctx.fillRect(0, y, 512, 1);
        ctx.fillStyle = 'rgba(255,255,255,0.03)';
        ctx.fillRect(0, y + 1, 512, 1);

        // Staggered vertical butt joints where board ends meet
        if (row % 3 !== 2) {
            const sx = 120 + ((row * 97) % 280);
            ctx.fillStyle = 'rgba(0,0,0,0.09)';
            ctx.fillRect(sx, y + 1, 1, boardH - 4);
            ctx.fillStyle = 'rgba(255,255,255,0.03)';
            ctx.fillRect(sx + 1, y + 1, 1, boardH - 4);
        }

        row++;
    }
}

// Painted gray vertical board siding - uniform paint color with subtle wood
// grain texture showing through. Clean flat boards, no exposed knots.
// Colors pushed cool/blue-gray to compensate for scene's warm tone mapping.
function _drawWoodVertical(canvas, ctx) {
    canvas.width = 512;
    canvas.height = 512;
    const boardW = 62;
    const gapW = 4;

    // Dark gap between boards
    ctx.fillStyle = '#181c20';
    ctx.fillRect(0, 0, 512, 512);

    // Uniform painted gray - slight per-board variation like real paint
    const baseR = 118, baseG = 123, baseB = 132;

    let boardIdx = 0;
    for (let x = 0; x < 512; x += boardW + gapW) {
        // Very subtle per-board shift (paint absorption varies slightly)
        const shift = ((boardIdx * 7) % 5) - 2;
        ctx.fillStyle = `rgb(${baseR + shift},${baseG + shift},${baseB + shift})`;
        ctx.fillRect(x, 0, boardW, 512);

        // Subtle grain showing through paint - very faint vertical lines
        for (let gi = 0; gi < 6; gi++) {
            const gx = x + 4 + (gi / 6) * (boardW - 8) + (Math.random() - 0.5) * 3;
            ctx.strokeStyle = `rgba(20,25,35,${0.02 + Math.random() * 0.03})`;
            ctx.lineWidth = 0.5 + Math.random() * 0.5;
            ctx.beginPath();
            let cx = gx;
            ctx.moveTo(cx, 0);
            for (let y = 40; y <= 512; y += 40) {
                cx += (Math.random() - 0.5) * 1;
                ctx.lineTo(cx, y);
            }
            ctx.stroke();
        }

        // Board channel shadow on right edge
        ctx.fillStyle = 'rgba(0,0,0,0.12)';
        ctx.fillRect(x + boardW - 2, 0, 2, 512);

        // Slight highlight on left edge where board catches light
        ctx.fillStyle = 'rgba(180,190,200,0.04)';
        ctx.fillRect(x + 1, 0, 1, 512);

        boardIdx++;
    }
}

// -- glass walls --------------------------------------------------------------
// Full glass curtain wall or storefront glazing. Thin dark frame grid with
// large glass panels.
function _drawGlassWall(canvas, ctx, thin) {
    canvas.width = 256;
    canvas.height = 256;
    const frameColor = thin ? '#505058' : '#3A3A3E';
    const frameW = thin ? 3 : 5;

    // Glass base with sky reflection
    const glassGrad = ctx.createLinearGradient(0, 0, 256, 256);
    glassGrad.addColorStop(0, thin ? '#A0C0D8' : '#88AACC');
    glassGrad.addColorStop(0.5, thin ? '#90B0C8' : '#7898B8');
    glassGrad.addColorStop(1, thin ? '#80A8C0' : '#6888A8');
    ctx.fillStyle = glassGrad;
    ctx.fillRect(0, 0, 256, 256);

    // Reflection
    ctx.fillStyle = 'rgba(255,255,255,0.08)';
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.lineTo(120, 0);
    ctx.lineTo(0, 160);
    ctx.closePath();
    ctx.fill();

    // Grid frame
    ctx.fillStyle = frameColor;
    for (let x = 0; x < 256; x += 128) {
        ctx.fillRect(x - frameW / 2, 0, frameW, 256);
    }
    for (let y = 0; y < 256; y += 128) {
        ctx.fillRect(0, y - frameW / 2, 256, frameW);
    }
}

// -- doors and windows (per-segment, not tiled) -------------------------------
// These draw a single unit sized to the actual wall segment dimensions.
// The adjacent wall's texture is tiled as the background so the material
// pattern (siding, brick, etc.) flows continuously through the segment.

// Tiles the adjacent wall texture across the canvas as background, or
// falls back to a solid color if no texture is available.
function _fillWallBackground(ctx, w, h, widthM, heightM, bgTex, fallback) {
    if (bgTex && bgTex.canvas) {
        const tileW = w * (bgTex.tileSizeM / widthM);
        const tileH = h * (bgTex.tileSizeM / heightM);
        for (let ty = 0; ty < h; ty += tileH) {
            for (let tx = 0; tx < w; tx += tileW) {
                ctx.drawImage(bgTex.canvas, tx, ty, tileW, tileH);
            }
        }
    } else {
        ctx.fillStyle = fallback || '#6E6E72';
        ctx.fillRect(0, 0, w, h);
    }
}

// Residential window proportioned to segment width. Wider segments get taller
// windows, narrow segments get squarer windows.
function _drawWindow(canvas, ctx, panes, widthM, heightM, bgTex, fallback) {
    const w = 256, h = Math.min(2048, Math.round(256 * (heightM / widthM)));
    canvas.width = w;
    canvas.height = h;

    // Tile adjacent wall texture as continuous background
    _fillWallBackground(ctx, w, h, widthM, heightM, bgTex, fallback);

    // Window proportions: width fills ~80% of segment. Height scales with
    // width but stays proportionate - not floor to ceiling.
    const winW = w * 0.8;
    const winH = h * 0.38 + Math.min(winW * 0.4, h * 0.2);
    const winL = (w - winW) / 2;
    // Vertically center the window in the upper portion of the wall
    const winT = (h - winH) * 0.35;

    const trimW = Math.max(3, w * 0.025);
    const frameW = Math.max(4, w * 0.04);

    // White trim casing
    ctx.fillStyle = '#D8D8D8';
    ctx.fillRect(winL - trimW, winT - trimW, winW + trimW * 2, winH + trimW * 2);

    // Dark frame
    ctx.fillStyle = '#3A3A3E';
    ctx.fillRect(winL, winT, winW, winH);

    // Glass area
    const gL = winL + frameW, gT = winT + frameW;
    const gW = winW - frameW * 2, gH = winH - frameW * 2;

    const glassGrad = ctx.createLinearGradient(gL, gT, gL + gW, gT + gH);
    glassGrad.addColorStop(0, '#8AAEC8');
    glassGrad.addColorStop(0.3, '#7BA0BC');
    glassGrad.addColorStop(0.7, '#6890AE');
    glassGrad.addColorStop(1, '#5A82A0');
    ctx.fillStyle = glassGrad;
    ctx.fillRect(gL, gT, gW, gH);

    // Reflection highlight
    ctx.fillStyle = 'rgba(255,255,255,0.12)';
    ctx.beginPath();
    ctx.moveTo(gL, gT);
    ctx.lineTo(gL + gW * 0.35, gT);
    ctx.lineTo(gL, gT + gH * 0.45);
    ctx.closePath();
    ctx.fill();

    // Mullions
    ctx.fillStyle = '#3A3A3E';
    const mullW = Math.max(2, frameW * 0.6);
    if (panes >= 2) {
        ctx.fillRect(gL, gT + gH / 2 - mullW / 2, gW, mullW);
    }
    if (panes >= 3) {
        ctx.fillRect(gL + gW / 2 - mullW / 2, gT, mullW, gH);
    }

    // Sill
    ctx.fillStyle = '#C0C0C0';
    ctx.fillRect(winL - 3, winT + winH + trimW, winW + 6, Math.max(3, h * 0.012));
}

// Raised-panel residential wood door - sits in the lower portion of the wall.
function _drawDoorWood(canvas, ctx, widthM, heightM, bgTex, fallback) {
    const w = 256, h = Math.min(2048, Math.round(256 * (heightM / widthM)));
    canvas.width = w;
    canvas.height = h;

    _fillWallBackground(ctx, w, h, widthM, heightM, bgTex, fallback);

    // Standard US door: 6'8" (2.032 m) height, fixed regardless of width.
    const doorW = w * 0.85;
    const doorH = h * (2.032 / heightM);
    const doorL = (w - doorW) / 2;
    const doorB = h * 0.97;
    const doorT = doorB - doorH;

    const trim = Math.max(3, w * 0.03);

    // Trim casing
    ctx.fillStyle = '#D8D8D8';
    ctx.fillRect(doorL - trim, doorT - trim, doorW + trim * 2, doorH + trim);

    // Door face
    ctx.fillStyle = '#5C4030';
    ctx.fillRect(doorL, doorT, doorW, doorH);

    // 6-panel layout (2 cols x 3 rows)
    const pad = doorW * 0.07;
    const gapX = doorW * 0.04, gapY = doorH * 0.02;
    const pw = (doorW - pad * 2 - gapX) / 2;
    const ph = (doorH - pad * 2 - gapY * 2) / 3;

    for (let row = 0; row < 3; row++) {
        for (let col = 0; col < 2; col++) {
            const px = doorL + pad + col * (pw + gapX);
            const py = doorT + pad + row * (ph + gapY);
            ctx.fillStyle = 'rgba(0,0,0,0.15)';
            ctx.fillRect(px, py, pw, ph);
            ctx.fillStyle = '#6B4E3A';
            ctx.fillRect(px + 3, py + 3, pw - 6, ph - 6);
            ctx.fillStyle = 'rgba(255,255,255,0.06)';
            ctx.fillRect(px + 3, py + 3, pw - 6, 2);
        }
    }

    // Handle
    ctx.fillStyle = '#B0A898';
    const handleY = doorT + doorH * 0.52;
    ctx.fillRect(doorL + doorW - doorW * 0.14, handleY, doorW * 0.04, doorH * 0.04);
}

// Steel entry door with half-lite window.
function _drawDoorMetal(canvas, ctx, widthM, heightM, bgTex, fallback) {
    const w = 256, h = Math.min(2048, Math.round(256 * (heightM / widthM)));
    canvas.width = w;
    canvas.height = h;

    _fillWallBackground(ctx, w, h, widthM, heightM, bgTex, fallback);

    const doorW = w * 0.85;
    const doorH = h * (2.032 / heightM);
    const doorL = (w - doorW) / 2;
    const doorB = h * 0.97;
    const doorT = doorB - doorH;
    const trim = Math.max(3, w * 0.03);

    ctx.fillStyle = '#D8D8D8';
    ctx.fillRect(doorL - trim, doorT - trim, doorW + trim * 2, doorH + trim);

    ctx.fillStyle = '#6A6A6E';
    ctx.fillRect(doorL, doorT, doorW, doorH);

    // Embossed panel lines
    ctx.strokeStyle = 'rgba(0,0,0,0.1)';
    ctx.lineWidth = 1;
    ctx.strokeRect(doorL + doorW * 0.08, doorT + doorH * 0.05, doorW * 0.84, doorH * 0.25);
    ctx.strokeRect(doorL + doorW * 0.08, doorT + doorH * 0.35, doorW * 0.84, doorH * 0.58);

    // Half-lite window
    const liteL = doorL + doorW * 0.2, liteT = doorT + doorH * 0.07;
    const liteW = doorW * 0.6, liteH = doorH * 0.18;
    ctx.fillStyle = '#3A3A3E';
    ctx.fillRect(liteL, liteT, liteW, liteH);
    const lGrad = ctx.createLinearGradient(liteL, liteT, liteL + liteW, liteT + liteH);
    lGrad.addColorStop(0, '#7898B0');
    lGrad.addColorStop(1, '#5A7A92');
    ctx.fillStyle = lGrad;
    ctx.fillRect(liteL + 3, liteT + 3, liteW - 6, liteH - 6);

    // Handle
    ctx.fillStyle = '#B0A898';
    ctx.fillRect(doorL + doorW * 0.82, doorT + doorH * 0.52, doorW * 0.04, doorH * 0.04);
}

// Glass/French door - mostly glass with grid.
function _drawDoorGlass(canvas, ctx, widthM, heightM, bgTex, fallback) {
    const w = 256, h = Math.min(2048, Math.round(256 * (heightM / widthM)));
    canvas.width = w;
    canvas.height = h;

    _fillWallBackground(ctx, w, h, widthM, heightM, bgTex, fallback);

    const doorW = w * 0.85;
    const doorH = h * (2.032 / heightM);
    const doorL = (w - doorW) / 2;
    const doorB = h * 0.97;
    const doorT = doorB - doorH;
    const trim = Math.max(3, w * 0.03);

    ctx.fillStyle = '#D8D8D8';
    ctx.fillRect(doorL - trim, doorT - trim, doorW + trim * 2, doorH + trim);

    ctx.fillStyle = '#3A3A3E';
    ctx.fillRect(doorL, doorT, doorW, doorH);

    // Glass area (leave bottom rail ~15%)
    const railH = doorH * 0.12;
    const gL = doorL + doorW * 0.04, gT = doorT + doorW * 0.04;
    const gW = doorW * 0.92, gH = doorH - railH - doorW * 0.08;

    const gGrad = ctx.createLinearGradient(gL, gT, gL + gW, gT + gH);
    gGrad.addColorStop(0, '#8AAEC8');
    gGrad.addColorStop(0.4, '#7898B0');
    gGrad.addColorStop(1, '#6088A0');
    ctx.fillStyle = gGrad;
    ctx.fillRect(gL, gT, gW, gH);

    // Reflection
    ctx.fillStyle = 'rgba(255,255,255,0.1)';
    ctx.beginPath();
    ctx.moveTo(gL, gT);
    ctx.lineTo(gL + gW * 0.3, gT);
    ctx.lineTo(gL, gT + gH * 0.4);
    ctx.closePath();
    ctx.fill();

    // French door grid
    const mullW = Math.max(2, doorW * 0.02);
    ctx.fillStyle = '#3A3A3E';
    ctx.fillRect(gL + gW / 2 - mullW / 2, gT, mullW, gH);
    for (let r = 1; r < 3; r++) {
        ctx.fillRect(gL, gT + (gH / 3) * r - mullW / 2, gW, mullW);
    }

    // Bottom rail
    ctx.fillStyle = '#4A4A4E';
    ctx.fillRect(doorL, doorT + doorH - railH, doorW, railH);

    // Handle
    ctx.fillStyle = '#B0A898';
    ctx.fillRect(doorL + doorW * 0.82, doorT + doorH * 0.48, doorW * 0.03, doorH * 0.05);
}

// -- interior textures --------------------------------------------------------
// Cached separately from exterior textures. Used for the back face of walls
// that look different inside vs outside.

function _getInteriorTexCanvas(interiorKey) {
    if (_interiorTexCache.has(interiorKey)) return _interiorTexCache.get(interiorKey);
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d');
    let tileSizeM = 1.0;

    switch (interiorKey) {
        case 'interior_wood':
            _drawWoodPanelingInterior(canvas, ctx);
            tileSizeM = 0.6;
            break;
        case 'interior_drywall':
            _drawDrywallInterior(canvas, ctx);
            tileSizeM = 1.0;
            break;
        default:
            _interiorTexCache.set(interiorKey, null);
            return null;
    }

    _interiorTexCache.set(interiorKey, { canvas, tileSizeM });
    return _interiorTexCache.get(interiorKey);
}

// Warm wood paneling with grain and knots - interior face of wood_paneling walls.
function _drawWoodPanelingInterior(canvas, ctx) {
    canvas.width = 512;
    canvas.height = 512;
    const boardW = 62;
    const gapW = 3;

    ctx.fillStyle = '#1a1510';
    ctx.fillRect(0, 0, 512, 512);

    const baseColors = [
        [155, 120, 85], [140, 108, 78], [160, 125, 88],
        [135, 105, 75], [148, 115, 82], [145, 112, 80],
    ];

    let boardIdx = 0;
    for (let x = 0; x < 512; x += boardW + gapW) {
        const [br, bg, bb] = baseColors[boardIdx % baseColors.length];
        const shift = ((boardIdx * 13) % 7) - 3;
        ctx.fillStyle = `rgb(${br + shift},${bg + shift},${bb + shift})`;
        ctx.fillRect(x, 0, boardW, 512);

        // Vertical grain lines
        for (let gi = 0; gi < 6; gi++) {
            const gx = x + 3 + (gi / 6) * (boardW - 6) + (Math.random() - 0.5) * 4;
            ctx.strokeStyle = `rgba(40,25,10,${0.04 + Math.random() * 0.06})`;
            ctx.lineWidth = 0.5 + Math.random() * 0.8;
            ctx.beginPath();
            let cx = gx;
            ctx.moveTo(cx, 0);
            for (let y = 32; y <= 512; y += 32) {
                cx += (Math.random() - 0.5) * 1.5;
                ctx.lineTo(cx, y);
            }
            ctx.stroke();
        }

        // Wider grain bands
        for (let si = 0; si < 2; si++) {
            const sx = x + 8 + Math.random() * (boardW - 16);
            ctx.fillStyle = `rgba(30,18,8,${0.04 + Math.random() * 0.05})`;
            ctx.fillRect(sx, 0, 2 + Math.random() * 3, 512);
        }

        // Knot on every third board
        if (boardIdx % 3 === 0) {
            const ky = 80 + ((boardIdx * 137) % 300);
            const kx = x + boardW / 2 + (Math.random() - 0.5) * 10;
            const kr = 4 + Math.random() * 3;
            ctx.strokeStyle = 'rgba(60,35,15,0.35)';
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.ellipse(kx, ky, kr, kr * 0.7, 0, 0, Math.PI * 2);
            ctx.stroke();
            ctx.fillStyle = 'rgba(80,50,25,0.25)';
            ctx.beginPath();
            ctx.ellipse(kx, ky, kr - 1, kr * 0.6, 0, 0, Math.PI * 2);
            ctx.fill();
        }

        ctx.fillStyle = 'rgba(0,0,0,0.06)';
        ctx.fillRect(x + boardW - 1, 0, 1, 512);

        boardIdx++;
    }
}

// Painted drywall - interior face of exterior walls. Off-white with subtle
// roller texture.
function _drawDrywallInterior(canvas, ctx) {
    canvas.width = 256;
    canvas.height = 256;
    ctx.fillStyle = '#E2DBD2';
    ctx.fillRect(0, 0, 256, 256);

    // Subtle roller texture - tiny stipple noise
    for (let i = 0; i < 600; i++) {
        const x = Math.random() * 256;
        const y = Math.random() * 256;
        const s = 0.5 + Math.random() * 1.5;
        ctx.fillStyle = `rgba(${Math.random() > 0.5 ? '255,255,255' : '0,0,0'},${0.01 + Math.random() * 0.02})`;
        ctx.fillRect(x, y, s, s);
    }
}

// -- wall material factory ----------------------------------------------------
// Textured materials get a fresh texture from cached canvas with repeat scaled
// to the wall's real-world dimensions. Solid materials use realistic muted colors.
// Materials in INTERIOR_LOOK return [exteriorMat, interiorMat] for two-sided rendering.

function _createWallMaterial(matKey, segLenM, winding, adjacentMat) {
    const hex = REALISTIC_COLORS[matKey] || '#94a3b8';

    // Per-segment materials (doors, windows) - drawn fresh with real dimensions,
    // no tiling. The adjacent wall texture is tiled as the background so the
    // siding/brick pattern flows continuously through the opening.
    if (PER_SEGMENT.has(matKey)) {
        const canvas = document.createElement('canvas');
        const ctx = canvas.getContext('2d');
        // Get the adjacent wall's texture canvas to tile as background
        const bgTex = TEXTURED.has(adjacentMat) ? _getTexCanvas(adjacentMat) : null;
        const fallback = SURROUND_COLORS[adjacentMat] || '#6E6E72';
        const drawFn = {
            window_1_pane: (c, x) => _drawWindow(c, x, 1, segLenM, WALL_HEIGHT_M, bgTex, fallback),
            window_2_pane: (c, x) => _drawWindow(c, x, 2, segLenM, WALL_HEIGHT_M, bgTex, fallback),
            window_3_pane: (c, x) => _drawWindow(c, x, 3, segLenM, WALL_HEIGHT_M, bgTex, fallback),
            door_wood: (c, x) => _drawDoorWood(c, x, segLenM, WALL_HEIGHT_M, bgTex, fallback),
            door_metal: (c, x) => _drawDoorMetal(c, x, segLenM, WALL_HEIGHT_M, bgTex, fallback),
            door_glass: (c, x) => _drawDoorGlass(c, x, segLenM, WALL_HEIGHT_M, bgTex, fallback),
        }[matKey];
        if (drawFn) drawFn(canvas, ctx);
        const tex = new THREE.CanvasTexture(canvas);
        tex.colorSpace = THREE.SRGBColorSpace;
        const exteriorMat = new THREE.MeshStandardMaterial({
            map: tex,
            transparent: WALL_OPACITY < 1.0,
            opacity: WALL_OPACITY,
            depthWrite: WALL_OPACITY >= 1.0,
            roughness: 0.85,
        });

        // If the adjacent wall has an interior look, build a second canvas
        // with the interior background for the back face.
        const intKey = INTERIOR_LOOK[adjacentMat];
        if (intKey) {
            const intCanvas = document.createElement('canvas');
            const intCtx = intCanvas.getContext('2d');
            const intBg = _getInteriorTexCanvas(intKey);
            const intFallback = SURROUND_COLORS.drywall || '#D8D2C8';
            if (drawFn === null) return exteriorMat;
            // Re-draw the same door/window but with interior background
            const intDrawFn = {
                window_1_pane: (c, x) => _drawWindow(c, x, 1, segLenM, WALL_HEIGHT_M, intBg, intFallback),
                window_2_pane: (c, x) => _drawWindow(c, x, 2, segLenM, WALL_HEIGHT_M, intBg, intFallback),
                window_3_pane: (c, x) => _drawWindow(c, x, 3, segLenM, WALL_HEIGHT_M, intBg, intFallback),
                door_wood: (c, x) => _drawDoorWood(c, x, segLenM, WALL_HEIGHT_M, intBg, intFallback),
                door_metal: (c, x) => _drawDoorMetal(c, x, segLenM, WALL_HEIGHT_M, intBg, intFallback),
                door_glass: (c, x) => _drawDoorGlass(c, x, segLenM, WALL_HEIGHT_M, intBg, intFallback),
            }[matKey];
            if (intDrawFn) intDrawFn(intCanvas, intCtx);
            const intTex = new THREE.CanvasTexture(intCanvas);
            intTex.colorSpace = THREE.SRGBColorSpace;
            const interiorMat = new THREE.MeshStandardMaterial({
                map: intTex,
                transparent: WALL_OPACITY < 1.0,
                opacity: WALL_OPACITY,
                depthWrite: WALL_OPACITY >= 1.0,
                roughness: 0.85,
            });
            const face4 = winding > 0 ? exteriorMat : interiorMat;
            const face5 = winding > 0 ? interiorMat : exteriorMat;
            return [exteriorMat, exteriorMat, exteriorMat, exteriorMat, face4, face5];
        }

        exteriorMat.side = THREE.DoubleSide;
        return exteriorMat;
    }

    const cached = TEXTURED.has(matKey) ? _getTexCanvas(matKey) : null;

    // Build the exterior (primary) material
    let exteriorMat;
    if (cached) {
        const tex = new THREE.CanvasTexture(cached.canvas);
        tex.wrapS = THREE.RepeatWrapping;
        tex.wrapT = THREE.RepeatWrapping;
        tex.colorSpace = THREE.SRGBColorSpace;
        tex.repeat.set(segLenM / cached.tileSizeM, WALL_HEIGHT_M / cached.tileSizeM);
        exteriorMat = new THREE.MeshStandardMaterial({
            map: tex,
            transparent: WALL_OPACITY < 1.0,
            opacity: WALL_OPACITY,
            depthWrite: WALL_OPACITY >= 1.0,
            side: THREE.FrontSide,
            roughness: 0.85,
        });
    } else {
        exteriorMat = new THREE.MeshStandardMaterial({
            color: new THREE.Color(hex),
            transparent: WALL_OPACITY < 1.0,
            opacity: WALL_OPACITY,
            depthWrite: WALL_OPACITY >= 1.0,
            side: THREE.FrontSide,
            emissive: new THREE.Color(hex),
            emissiveIntensity: 0.05,
            roughness: 0.7,
        });
    }

    // If this material has a different interior look, return a 6-material array
    // for BoxGeometry face groups: [+X, -X, +Y, -Y, +Z front, -Z back].
    // Front face (+Z, group 4) gets the exterior, back face (-Z, group 5) gets interior.
    const interiorKey = INTERIOR_LOOK[matKey];
    if (interiorKey) {
        const intCached = _getInteriorTexCanvas(interiorKey);
        let interiorMat;
        if (intCached) {
            const tex = new THREE.CanvasTexture(intCached.canvas);
            tex.wrapS = THREE.RepeatWrapping;
            tex.wrapT = THREE.RepeatWrapping;
            tex.colorSpace = THREE.SRGBColorSpace;
            tex.repeat.set(segLenM / intCached.tileSizeM, WALL_HEIGHT_M / intCached.tileSizeM);
            interiorMat = new THREE.MeshStandardMaterial({
                map: tex,
                transparent: WALL_OPACITY < 1.0,
                opacity: WALL_OPACITY,
                depthWrite: WALL_OPACITY >= 1.0,
                roughness: 0.85,
            });
        } else {
            interiorMat = exteriorMat;
        }
        // BoxGeometry groups: [+X, -X, +Y, -Y, +Z(front), -Z(back)]
        // Winding determines which face is exterior: 1 = +Z is exterior, -1 = -Z is exterior
        const face4 = winding > 0 ? exteriorMat : interiorMat;
        const face5 = winding > 0 ? interiorMat : exteriorMat;
        return [exteriorMat, exteriorMat, exteriorMat, exteriorMat, face4, face5];
    }

    return exteriorMat;
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

function _buildWalls(floor, scale, floorY, wallH, wallD, winding, parent) {
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
            // For doors/windows, find the adjacent wall material for the surround
            let adjacentMat = wall.material;
            if (PER_SEGMENT.has(segMat) && wall.materials) {
                // Look at previous then next segment for a non-door/window material
                for (const adj of [i - 1, i + 1]) {
                    if (adj >= 0 && adj < wall.materials.length) {
                        const m = wall.materials[adj] || wall.material;
                        if (!PER_SEGMENT.has(m)) { adjacentMat = m; break; }
                    }
                }
            }
            // Returns a single material or a 6-element array for per-face rendering
            const material = _createWallMaterial(segMat, segLenM, winding, adjacentMat);
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
