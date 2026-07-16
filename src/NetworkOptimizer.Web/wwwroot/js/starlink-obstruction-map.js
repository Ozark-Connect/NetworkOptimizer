// Renders the Starlink obstruction sky map onto a canvas.
// The dish reports a square grid of per-direction SNR samples (0..1, -1 =
// unmeasured) already projected as a disc in the dish reference frame:
// boresight at the center, max_theta_deg at the rim. Clear sky renders in
// the app's teal, obstructions ramp amber -> red, unmeasured stays dark.

const COLORS = {
    ringStroke: 'rgba(55, 65, 81, 0.9)',      // grid line color
    rimStroke: 'rgba(160, 160, 168, 0.35)',
    unmeasured: [22, 22, 24, 255],            // --bg-secondary
    boresight: 'rgba(237, 237, 239, 0.9)',
};

// SNR 0..1 -> RGBA. Obstruction severity uses the app's signal palette:
// >=0.9 clear teal, then emerald/yellow/orange down to red for fully blocked.
function snrColor(v) {
    if (v >= 0.9) {
        // Clear sky: deep teal, brightening slightly with quality
        const t = (v - 0.9) / 0.1;
        return [Math.round(27 + 16 * t), Math.round(138 + 30 * t), Math.round(128 + 26 * t), 255]; // #2ba89a family
    }
    if (v >= 0.65) {
        const t = (v - 0.65) / 0.25;
        return [Math.round(234 - 190 * t), Math.round(179 - 25 * t), Math.round(8 + 100 * t), 255]; // yellow -> teal-green
    }
    if (v >= 0.35) {
        const t = (v - 0.35) / 0.3;
        return [Math.round(249 - 15 * t), Math.round(115 + 64 * t), Math.round(22 - 14 * t), 255]; // orange -> yellow
    }
    const t = Math.max(v, 0) / 0.35;
    return [239, Math.round(68 + 47 * t), Math.round(68 - 46 * t), 255]; // red -> orange
}

/**
 * Draw the obstruction map. data: { rows, cols, snr: number[] }.
 * The canvas is cleared and fully repainted.
 */
export function render(canvas, data) {
    if (!canvas || !data || !data.rows || !data.cols || !data.snr) return;

    const ctx = canvas.getContext('2d');
    const size = canvas.width; // square canvas
    ctx.clearRect(0, 0, size, canvas.height);

    // Paint the grid into an offscreen canvas at native resolution, then
    // scale up with smoothing so patches blend into a soft dome.
    const off = document.createElement('canvas');
    off.width = data.cols;
    off.height = data.rows;
    const offCtx = off.getContext('2d');
    const img = offCtx.createImageData(data.cols, data.rows);

    const cx = (data.cols - 1) / 2;
    const cy = (data.rows - 1) / 2;
    const radius = Math.min(cx, cy);

    for (let r = 0; r < data.rows; r++) {
        for (let c = 0; c < data.cols; c++) {
            const v = data.snr[r * data.cols + c];
            const inside = ((c - cx) ** 2 + (r - cy) ** 2) <= radius * radius;
            const px = (r * data.cols + c) * 4;
            const rgba = (!inside || v < 0) ? COLORS.unmeasured : snrColor(v);
            img.data[px] = rgba[0];
            img.data[px + 1] = rgba[1];
            img.data[px + 2] = rgba[2];
            img.data[px + 3] = inside ? rgba[3] : 0;
        }
    }
    offCtx.putImageData(img, 0, 0);

    // Clip to the dome circle and draw scaled
    const center = size / 2;
    const domeR = size / 2 - 2;
    ctx.save();
    ctx.beginPath();
    ctx.arc(center, center, domeR, 0, Math.PI * 2);
    ctx.clip();
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(off, 0, 0, size, size);
    ctx.restore();

    // Elevation rings (1/3 and 2/3 of the dome) + rim
    ctx.lineWidth = 1;
    ctx.strokeStyle = COLORS.ringStroke;
    for (const f of [1 / 3, 2 / 3]) {
        ctx.beginPath();
        ctx.arc(center, center, domeR * f, 0, Math.PI * 2);
        ctx.stroke();
    }
    ctx.strokeStyle = COLORS.rimStroke;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.arc(center, center, domeR, 0, Math.PI * 2);
    ctx.stroke();

    // Boresight marker
    ctx.fillStyle = COLORS.boresight;
    ctx.beginPath();
    ctx.arc(center, center, 2.5, 0, Math.PI * 2);
    ctx.fill();
}
