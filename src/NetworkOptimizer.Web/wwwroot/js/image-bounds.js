// The opaque part of a loaded image, for logos drawn by their visible shape: an ISP logo's PNG
// pads a wide mark with transparent rows, which would otherwise sit as a gap under it.

const ALPHA_MIN = 16;

/**
 * The bounding box of an image's opaque pixels, {x, y, w, h} in image pixels. The whole image
 * when it cannot be read (a cross-origin image taints the canvas) or has no opaque pixel.
 */
export function opaqueBounds(img) {
    const w = img.naturalWidth || img.width, h = img.naturalHeight || img.height;
    const whole = { x: 0, y: 0, w, h };
    if (!w || !h) return whole;
    try {
        const c = document.createElement('canvas');
        c.width = w; c.height = h;
        const ctx = c.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(img, 0, 0);
        const data = ctx.getImageData(0, 0, w, h).data;
        let x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                if (data[(y * w + x) * 4 + 3] < ALPHA_MIN) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        }
        return x1 < 0 ? whole : { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
    } catch {
        return whole;
    }
}

/**
 * The size a logo's opaque box draws at in a `size` x `size` slot: as tall as the slot, and a wide
 * mark may run up to `maxWide` times the slot's width so it keeps some height.
 */
export function logoFit(box, size, maxWide = 1.25) {
    const s = Math.min(size / box.h, (size * maxWide) / box.w);
    return { w: box.w * s, h: box.h * s };
}
