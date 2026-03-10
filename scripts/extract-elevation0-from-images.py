"""
Extract Elevation 0 deg antenna pattern data from Ubiquiti reference images.

Uses Elevation 90 deg plots (column 2) as ground truth to calibrate extraction.
The .ant files have el90 data, so we extract el90 from the image, compare to .ant,
and use the match quality to validate center detection, radius, and angular mapping.

Usage:
    python scripts/extract-elevation0-from-images.py <image_path> --db-max 15 --db-min -20 [--debug]

For images with multiple antenna variants (e.g., E7-Audience narrow + wide):
    python scripts/extract-elevation0-from-images.py <image_path> --db-max 10 --variants narrow,wide

To correct center detection offset (positive = move center down on page):
    python scripts/extract-elevation0-from-images.py <image_path> --db-max 10 --cy-shift 15

For images where row detection merges adjacent rows (e.g., E7 Summary with 7 bands):
    python scripts/extract-elevation0-from-images.py <image_path> --db-max 10 --n-bands 7
"""

import sys
import json
import math
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import numpy as np


# ── Color detection ──────────────────────────────────────────────────────────

def is_pattern_line(r, g, b):
    """Blue pattern line: R~11, G~11, B~253."""
    return int(r) < 50 and int(g) < 50 and int(b) > 200


def is_grid_pixel(r, g, b):
    """Grid lines have a distinct blue tint (B significantly > R and G).
    Excludes pure gray pixels from AP product images and text."""
    ri, gi, bi = int(r), int(g), int(b)
    return (130 < bi < 255 and
            bi > ri + 15 and bi > gi + 15 and
            not is_pattern_line(r, g, b))


# ── Plot detection ───────────────────────────────────────────────────────────

def find_row_spans(arr, col_start, col_end, n_expected=None):
    """Find vertical spans containing blue pattern pixels in a column range.

    If n_expected is given, adaptively merges the closest spans until
    exactly n_expected remain. Otherwise uses a 20px gap threshold.
    """
    h = arr.shape[0]

    # Build mask of blue pattern pixels
    mask = np.zeros((h, col_end - col_start), dtype=bool)
    for y in range(h):
        for x in range(col_start, col_end):
            r, g, b = arr[y, x, :3]
            if is_pattern_line(r, g, b):
                mask[y, x - col_start] = True

    # Find row spans with blue pixels
    row_has_blue = mask.any(axis=1)
    spans = []
    in_span = False
    start = 0
    for y in range(h):
        if row_has_blue[y] and not in_span:
            start = y
            in_span = True
        elif not row_has_blue[y] and in_span:
            if y - start > 30:
                spans.append((start, y))
            in_span = False
    if in_span and h - start > 30:
        spans.append((start, h))

    # Merge spans with < 20px gap (within-plot pixel noise)
    merged = [spans[0]] if spans else []
    for s, e in spans[1:]:
        if s - merged[-1][1] < 20:
            merged[-1] = (merged[-1][0], e)
        else:
            merged.append((s, e))

    return merged, mask


def find_grid_center(arr, y_start, y_end, col_start, col_end, rough_cx, rough_cy, pat_r):
    """Find true plot center using grid pixel centroid.

    Grid circles are symmetric around the center, unlike the pattern which
    is asymmetric for directional antennas. The centroid of grid pixels
    gives us the actual polar plot origin.
    """
    h = arr.shape[0]
    margin = int(pat_r * 0.3)
    scan_y0 = max(0, rough_cy - int(pat_r) - margin)
    scan_y1 = min(h, rough_cy + int(pat_r) + margin)
    scan_x0 = max(col_start, rough_cx - int(pat_r) - margin)
    scan_x1 = min(col_end, rough_cx + int(pat_r) + margin)

    grid_xs, grid_ys = [], []
    for y in range(scan_y0, scan_y1):
        for x in range(scan_x0, scan_x1):
            r, g, b = arr[y, x, :3]
            if is_grid_pixel(r, g, b):
                grid_xs.append(x)
                grid_ys.append(y)

    if len(grid_xs) > 100:
        return int(np.mean(grid_xs)), int(np.mean(grid_ys)), len(grid_xs)
    return rough_cx, rough_cy, len(grid_xs)


def find_crosshair_center(arr, approx_cy, col_start, col_end):
    """Find the true polar plot center using the horizontal crosshair line.

    The horizontal crosshair is a dashed/gradient line through the plot center.
    It contains the densest concentration of blue-tinted grid pixels on any row.
    The midpoint of its extent gives center X; the row gives center Y.

    Returns (cx, cy, blue_half_span) or None.
    """
    # Find the row with the most blue-tinted grid pixels near the approximate center.
    # Search ±50px since the blue pixel centroid can be far from the true center
    # (directional patterns extend much further in one direction).
    best_count = 0
    best_y = approx_cy
    for y in range(max(0, approx_cy - 50), min(arr.shape[0], approx_cy + 50)):
        count = 0
        for x in range(col_start, col_end):
            r, g, b = arr[y, x, :3]
            if is_grid_pixel(r, g, b):
                count += 1
        if count > best_count:
            best_count = count
            best_y = y

    if best_count < 50:
        return None

    # Find extent of blue-tinted pixels on the crosshair row
    xs = []
    for x in range(col_start, col_end):
        r, g, b = arr[best_y, x, :3]
        if is_grid_pixel(r, g, b):
            xs.append(x)

    if len(xs) < 20:
        return None

    cx = (min(xs) + max(xs)) / 2
    half_span = (max(xs) - min(xs)) / 2

    return cx, best_y, half_span


def compute_outer_ring_radius(blue_half_span, n_rings):
    """Compute the outer ring radius from the crosshair blue-tinted extent.

    The blue-tinted crosshair gradient extends to the second-to-outermost
    grid ring. The outer ring (at db_max) is one ring-spacing further out:
        outer_r = blue_half_span * (n_rings - 1) / (n_rings - 2)

    For 7 rings (30 dB range, 5 dB spacing): outer_r = half_span * 6/5
    """
    n_intervals = n_rings - 1
    return blue_half_span * n_intervals / (n_intervals - 1)


def find_plots_in_column(arr, col_start, col_end, n_expected=None, n_rings=7):
    """Find polar plot centers in a column range.

    Uses two-step detection:
    1. Horizontal crosshair for center (most reliable)
    2. Distance histogram for outer ring radius
    Falls back to grid centroid + circle fitting if crosshair detection fails.
    """
    h = arr.shape[0]
    merged, mask = find_row_spans(arr, col_start, col_end, n_expected=n_expected)

    plots = []
    for y_start, y_end in merged:
        # Blue pixel bounding box (rough center estimate)
        blue_xs, blue_ys = [], []
        for y in range(y_start, y_end):
            for x in range(col_start, col_end):
                if mask[y, x - col_start]:
                    blue_xs.append(x)
                    blue_ys.append(y)

        if len(blue_xs) < 20:
            continue

        rough_cx = (min(blue_xs) + max(blue_xs)) // 2
        rough_cy = (min(blue_ys) + max(blue_ys)) // 2
        rx = (max(blue_xs) - min(blue_xs)) / 2
        ry = (max(blue_ys) - min(blue_ys)) / 2

        if max(rx, ry) < 30:
            continue

        # Use center of the row span as search target for crosshair
        # (more reliable than blue pixel centroid, which is biased by pattern shape)
        span_center_y = (y_start + y_end) // 2
        ch = find_crosshair_center(arr, span_center_y, col_start, col_end)
        if ch:
            cx, cy, half_span = ch
            cx = int(round(cx))

            # Compute outer ring radius from crosshair extent
            radius = compute_outer_ring_radius(half_span, n_rings)
        else:
            # Fallback: grid centroid
            cx, cy, n_grid = find_grid_center(arr, y_start, y_end, col_start, col_end,
                                               rough_cx, rough_cy, max(rx, ry))
            # Try circle fitting for radius
            fit = fit_outer_ring(arr, cx, cy, max(rx, ry), col_start, col_end)
            if fit:
                cx, cy = int(round(fit[0])), int(round(fit[1]))
                radius = fit[2]
            else:
                radius = detect_grid_boundary(arr, cx, cy, max(rx, ry),
                                               col_start, col_end)

        # Pattern radius: 95th percentile distance from center to blue pixels
        dists = [math.sqrt((bx - cx) ** 2 + (by - cy) ** 2)
                 for bx, by in zip(blue_xs, blue_ys)]
        dists.sort()
        pat_r = dists[int(len(dists) * 0.95)]

        plots.append({
            "cx": cx, "cy": cy,
            "blue_cx": rough_cx, "blue_cy": rough_cy,
            "pattern_radius": pat_r,
            "radius": radius,
            "y_range": (y_start, y_end),
            "n_grid_pixels": 0,
        })

    # Deduplicate: if two plots are within 100px vertically (center-to-center),
    # they're from the same row split by a gap. Keep the one from the larger span.
    if n_expected and len(plots) > n_expected:
        plots.sort(key=lambda p: p["cy"])
        deduped = []
        i = 0
        while i < len(plots):
            if i + 1 < len(plots) and abs(plots[i + 1]["cy"] - plots[i]["cy"]) < 100:
                span_a = plots[i]["y_range"][1] - plots[i]["y_range"][0]
                span_b = plots[i + 1]["y_range"][1] - plots[i + 1]["y_range"][0]
                deduped.append(plots[i] if span_a >= span_b else plots[i + 1])
                i += 2
            else:
                deduped.append(plots[i])
                i += 1
        plots = deduped

    return plots


def detect_grid_boundary(arr, cx, cy, pattern_radius, col_start, col_end):
    """Find outermost grid ring using distance histogram of grid pixels.

    Collects distances from center to all grid pixels, builds a histogram,
    and finds peaks. Grid rings create peaks; scattered label pixels don't.
    The outermost clear peak is the outer grid ring.
    """
    h = arr.shape[0]
    margin = int(pattern_radius * 0.5)
    scan_r = int(pattern_radius) + margin

    # Scan region around center for grid pixels and record their distance
    scan_y0 = max(0, cy - scan_r)
    scan_y1 = min(h, cy + scan_r)
    scan_x0 = max(col_start, cx - scan_r)
    scan_x1 = min(col_end, cx + scan_r)

    dist_hist = [0] * (scan_r + 1)
    for y in range(scan_y0, scan_y1):
        for x in range(scan_x0, scan_x1):
            rv, gv, bv = arr[y, x, :3]
            if is_grid_pixel(rv, gv, bv):
                d = int(math.sqrt((x - cx) ** 2 + (y - cy) ** 2))
                if d <= scan_r:
                    dist_hist[d] += 1

    # Smooth histogram (5px window) to find ring peaks
    smoothed = [0] * len(dist_hist)
    for i in range(2, len(dist_hist) - 2):
        smoothed[i] = sum(dist_hist[i - 2:i + 3]) / 5

    # Find peaks: local maxima above a threshold.
    # Grid rings should have more pixels than inter-ring areas.
    threshold = max(smoothed[10:]) * 0.3 if max(smoothed[10:]) > 0 else 1
    peaks = []
    for r in range(10, len(smoothed) - 3):
        if (smoothed[r] >= threshold and
                smoothed[r] >= smoothed[r - 3] and
                smoothed[r] >= smoothed[r + 3]):
            peaks.append((r, smoothed[r]))

    # Deduplicate peaks within 5px of each other (keep highest)
    deduped = []
    for r, v in peaks:
        if deduped and r - deduped[-1][0] < 5:
            if v > deduped[-1][1]:
                deduped[-1] = (r, v)
        else:
            deduped.append((r, v))

    if deduped:
        # The outermost peak is the outer grid ring.
        # But check: if the outermost peak is much weaker than inner peaks,
        # it might be a label artifact. Use the outermost "strong" peak.
        max_val = max(v for _, v in deduped)
        strong_peaks = [(r, v) for r, v in deduped if v > max_val * 0.2]
        outer_r = max(r for r, v in strong_peaks)
        return outer_r

    return int(pattern_radius)


def _kasa_circle_fit(points):
    """Algebraic circle fit (Kasa method) for a list of (x, y) points.

    Returns (cx, cy, radius).
    """
    xs = np.array([p[0] for p in points], dtype=float)
    ys = np.array([p[1] for p in points], dtype=float)

    A = np.column_stack([xs, ys, np.ones(len(xs))])
    b_vec = xs ** 2 + ys ** 2
    result, _, _, _ = np.linalg.lstsq(A, b_vec, rcond=None)

    fit_cx = result[0] / 2
    fit_cy = result[1] / 2
    fit_r = math.sqrt(abs(result[2] + fit_cx ** 2 + fit_cy ** 2))

    return fit_cx, fit_cy, fit_r


def fit_outer_ring(arr, rough_cx, rough_cy, pattern_radius, col_start, col_end):
    """Find the outer grid ring by scanning from outside inward at many angles.

    The AP product image can occlude inner rings, but the outer ring is always
    visible. Scans from well outside the plot inward, looking for the first
    non-white pixel (brightness-based, since grid rings may be gray without
    blue tint). Uses iterative outlier removal to reject text label hits.

    Returns (fit_cx, fit_cy, fit_radius) or None if insufficient data.
    """
    h, w = arr.shape[:2]
    scan_start = int(pattern_radius) + 30  # well outside plot

    def is_pattern_line_local(r, g, b):
        return int(r) < 50 and int(g) < 50 and int(b) > 200

    # Scan from outside inward at many angles.
    # To distinguish ring pixels (thin line, 1-3px) from text (multi-pixel blocks),
    # we require a "thin line" pattern: the hit pixel should have white within
    # a few pixels on the inner side (ring is thin, text is thick).
    ring_points = []
    for angle_deg in range(0, 360, 2):  # every 2 degrees
        angle_rad = math.radians(angle_deg)
        cos_a = math.cos(angle_rad)
        sin_a = math.sin(angle_rad)

        for r in range(scan_start, 40, -1):
            x = int(rough_cx + r * cos_a)
            y = int(rough_cy + r * sin_a)
            if not (0 <= x < w and 0 <= y < h):
                continue
            rv, gv, bv = arr[y, x, :3]
            brightness = (int(rv) + int(gv) + int(bv)) / 3
            if brightness >= 230 or is_pattern_line_local(rv, gv, bv):
                continue

            # Check that this is a thin line: within 5px inward there should be
            # a white pixel (ring lines are 1-3px wide, text is wider)
            is_thin = False
            for dr in range(1, 6):
                ix = int(rough_cx + (r - dr) * cos_a)
                iy = int(rough_cy + (r - dr) * sin_a)
                if 0 <= ix < w and 0 <= iy < h:
                    irv, igv, ibv = arr[iy, ix, :3]
                    ib = (int(irv) + int(igv) + int(ibv)) / 3
                    if ib > 240:
                        is_thin = True
                        break

            if is_thin:
                ring_points.append((x, y))
                break

    if len(ring_points) < 30:
        return None

    # First pass: use median radius from rough center to reject gross outliers
    radii = [math.sqrt((x - rough_cx) ** 2 + (y - rough_cy) ** 2)
             for x, y in ring_points]
    median_r = sorted(radii)[len(radii) // 2]
    points = [p for p, r in zip(ring_points, radii) if abs(r - median_r) < 10]

    if len(points) < 20:
        return None

    # Iterative circle fit with outlier removal
    for _ in range(3):
        if len(points) < 15:
            break

        fit_cx, fit_cy, fit_r = _kasa_circle_fit(points)

        residuals = []
        for px, py in points:
            d = math.sqrt((px - fit_cx) ** 2 + (py - fit_cy) ** 2)
            residuals.append(abs(d - fit_r))

        median_res = sorted(residuals)[len(residuals) // 2]
        threshold = max(median_res * 2.5, 3.0)
        points = [p for p, res in zip(points, residuals) if res < threshold]

    if len(points) < 15:
        return None

    return _kasa_circle_fit(points)


# ── Pattern extraction ───────────────────────────────────────────────────────

def extract_polar_pattern(arr, cx, cy, outer_radius, db_max, db_range,
                           search_start=None):
    """Extract gain values by ray casting from center outward.

    outer_radius: radius that maps to db_max (the outer grid ring) for dB calculation.
    search_start: outermost radius to scan from (default: outer_radius + 5).
                  Set to pattern_radius + margin to avoid hitting legend markers
                  or other artifacts outside the actual pattern.

    Returns 359 values (0-358 degrees) in absolute dBi.
    Convention: 0 deg at top of polar plot, clockwise.
    """
    h, w = arr.shape[:2]
    db_min = db_max - db_range
    if search_start is None:
        search_start = int(outer_radius) + 5

    gains = []
    for angle_deg in range(359):
        # 0 deg at top (north), clockwise
        angle_rad = math.radians(-angle_deg - 90)  # CCW from 12 o'clock
        cos_a = math.cos(angle_rad)
        sin_a = math.sin(angle_rad)

        # Cast ray from search_start inward, find outermost blue pixel.
        # Use wider perpendicular band near horizontal crosshair angles
        # (85-95° and 265-275°) where the crosshair gradient occludes pattern.
        near_horizontal = ((85 <= angle_deg <= 95) or (265 <= angle_deg <= 275))
        offsets = [-3, -2, -1, 0, 1, 2, 3] if near_horizontal else [-1, 0, 1]

        found_r = 0
        for r in range(search_start, 2, -1):
            hit = False
            for offset in offsets:
                # Perpendicular offset
                x = int(cx + r * cos_a + offset * sin_a)
                y = int(cy + r * sin_a - offset * cos_a)
                if 0 <= x < w and 0 <= y < h:
                    rv, gv, bv = arr[y, x, :3]
                    if is_pattern_line(rv, gv, bv):
                        hit = True
                        break
            if hit:
                found_r = r
                break

        # Map radius to dB (linear: center=db_min, outer=db_max)
        if found_r > 0:
            gain = db_min + (found_r / outer_radius) * db_range
        else:
            gain = db_min

        gains.append(round(gain, 1))

    # Despike: replace single/double-degree nulls caused by ray-casting misses.
    # The pattern line is 1-2px wide; at certain angles the 3px-wide ray can
    # slip through, producing a sudden deep null surrounded by normal values.
    gains = despike(gains)

    return gains


def despike(gains, threshold=5.0):
    """Remove single/double-point spikes from extracted pattern data.

    If a point differs from both neighbors by more than threshold dB,
    If a point differs from both neighbors by more than threshold dB,
    replace it with the average of its neighbors. Run twice to catch
    adjacent spikes (2-degree nulls).
    """
    n = len(gains)
    for _ in range(2):
        smoothed = list(gains)
        for i in range(n):
            prev = gains[(i - 1) % n]
            next_ = gains[(i + 1) % n]
            avg = (prev + next_) / 2
            if gains[i] - avg < -threshold:  # only fix deep nulls, not peaks
                smoothed[i] = round(avg, 1)
        gains = smoothed
    return gains


# ── Calibration ──────────────────────────────────────────────────────────────

def cross_correlate(extracted, reference):
    """Find angular rotation that best aligns extracted with reference.

    Uses Pearson correlation coefficient instead of RMSE to handle cases where
    the extracted dynamic range is compressed (pattern doesn't reach grid edge).
    Returns (best_offset, best_corr, rmse_at_best).
    """
    n = len(extracted)
    best_offset = 0
    best_corr = -2.0

    # Pre-compute reference stats
    ref_mean = sum(reference[:n]) / n
    ref_dev = [b - ref_mean for b in reference[:n]]
    den_b = math.sqrt(sum(d * d for d in ref_dev))
    if den_b == 0:
        return 0, 0.0, 99.0

    for offset in range(n):
        rotated = extracted[offset:] + extracted[:offset]
        a_mean = sum(rotated) / n
        a_dev = [a - a_mean for a in rotated]
        den_a = math.sqrt(sum(d * d for d in a_dev))

        if den_a > 0:
            corr = sum(a * b for a, b in zip(a_dev, ref_dev)) / (den_a * den_b)
            if corr > best_corr:
                best_corr = corr
                best_offset = offset

    # Compute RMSE at best offset for reporting
    rotated = extracted[best_offset:] + extracted[:best_offset]
    rmse = math.sqrt(sum((a - b) ** 2 for a, b in zip(rotated, reference[:n])) / n)

    return best_offset, best_corr, rmse


def validate_with_el90(arr, el0_plots, ant_data, model, bands, db_max, db_range,
                        debug=False):
    """Validate extraction parameters using el90 from column 2 vs .ant reference.

    Extracts el90 from the image using the detected grid_r, compares with .ant
    reference data, and reports match quality. This is purely validation - the
    grid_r and angles come from physical detection in the image, not from fitting.

    Returns (detected_grid_r, el90_plots).
    """
    h, w = arr.shape[:2]
    col2_start = w // 4
    col2_end = w // 2

    print(f"\n  === Validating with Elevation 90 (column 2) ===")

    n_rings = int(db_range / 5) + 1
    el90_plots = find_plots_in_column(arr, col2_start, col2_end, n_rings=n_rings)
    print(f"  Found {len(el90_plots)} el90 plots in column 2")

    if not el90_plots:
        print("  WARNING: No el90 plots found!")
        return None, []

    if len(el90_plots) != len(el0_plots):
        print(f"  WARNING: el90 count ({len(el90_plots)}) != el0 count ({len(el0_plots)})")

    # Map sub-bands to .ant band keys
    band_map = {
        "2.4": "2.4", "2.45": "2.4",
        "5.15": "5", "5.5": "5", "5.85": "5",
        "6.0": "6", "6.5": "6", "6.5b": "6", "7.0": "6",
    }

    # Extract el90 for each band and compare with .ant reference
    for i, band in enumerate(bands):
        if i >= len(el90_plots):
            break

        plot = el90_plots[i]
        cx, cy = plot["cx"], plot["cy"]
        detected_r = plot.get("radius", plot["pattern_radius"])

        ant_band = band_map.get(band, band)
        if model not in ant_data or ant_band not in ant_data[model]:
            print(f"    {band}: no .ant ref for '{ant_band}', skip")
            continue

        ref_el90 = ant_data[model][ant_band].get("elevation", [])
        if not ref_el90:
            continue

        pat_r = plot["pattern_radius"]
        search_r = int(pat_r) + 10

        extracted = extract_polar_pattern(arr, cx, cy, detected_r, db_max, db_range,
                                           search_start=search_r)

        ext_peak_val = max(extracted)
        ext_peak = extracted.index(ext_peak_val)
        ref_peak_val = max(ref_el90) if ref_el90 else 0
        ref_peak = ref_el90.index(ref_peak_val) if ref_el90 else "?"

        # Compute RMSE
        n = min(len(extracted), len(ref_el90))
        rmse = math.sqrt(sum((a - b) ** 2 for a, b in zip(extracted[:n], ref_el90[:n])) / n)

        shift = math.sqrt((cx - plot["blue_cx"]) ** 2 +
                           (cy - plot["blue_cy"]) ** 2)

        print(f"    {band}: center=({cx},{cy}) shift={shift:.0f}px "
              f"det_r={detected_r:.0f}")
        print(f"           ext_peak@{ext_peak}deg ref_peak@{ref_peak}deg "
              f"RMSE={rmse:.1f}dB")

    grid_r = int(el90_plots[0].get("radius", el90_plots[0]["pattern_radius"]))
    print(f"\n  Grid radius (detected): {grid_r}px")

    return grid_r, el90_plots


# ── Debug image ──────────────────────────────────────────────────────────────

def extract_pattern_with_radii(arr, cx, cy, outer_radius, db_max, db_range,
                                search_start=None):
    """Extract pattern AND return the raw found_r for each angle (for visualization)."""
    h, w = arr.shape[:2]
    db_min = db_max - db_range
    if search_start is None:
        search_start = int(outer_radius) + 5
    gains = []
    radii = []

    for angle_deg in range(359):
        angle_rad = math.radians(-angle_deg - 90)  # CCW from 12 o'clock
        cos_a = math.cos(angle_rad)
        sin_a = math.sin(angle_rad)

        near_horizontal = ((85 <= angle_deg <= 95) or (265 <= angle_deg <= 275))
        offsets = [-3, -2, -1, 0, 1, 2, 3] if near_horizontal else [-1, 0, 1]

        found_r = 0
        for r in range(search_start, 2, -1):
            hit = False
            for offset in offsets:
                x = int(cx + r * cos_a + offset * sin_a)
                y = int(cy + r * sin_a - offset * cos_a)
                if 0 <= x < w and 0 <= y < h:
                    rv, gv, bv = arr[y, x, :3]
                    if is_pattern_line(rv, gv, bv):
                        hit = True
                        break
            if hit:
                found_r = r
                break

        if found_r > 0:
            gain = db_min + (found_r / outer_radius) * db_range
        else:
            gain = db_min
        gains.append(round(gain, 1))
        radii.append(found_r)

    gains = despike(gains)
    return gains, radii


def save_debug_image(arr, img, el0_plots, el90_plots, calibrated_radius, bands,
                      db_max, db_range, out_path):
    """Save annotated image with detected centers, radii, and extracted patterns."""
    debug_img = img.copy()
    draw = ImageDraw.Draw(debug_img)

    for plots, col_label, color, dot_color in [
        (el0_plots, "EL0", "red", "yellow"),
        (el90_plots, "EL90", "lime", "magenta"),
    ]:
        for i, p in enumerate(plots):
            cx, cy = p["cx"], p["cy"]
            r = calibrated_radius or p.get("radius", p["pattern_radius"])
            band = bands[i] if i < len(bands) else f"#{i}"

            # Crosshair at grid center (large)
            draw.line([(cx - 12, cy), (cx + 12, cy)], fill=color, width=2)
            draw.line([(cx, cy - 12), (cx, cy + 12)], fill=color, width=2)

            # Circle at grid_r
            draw.ellipse([(cx - r, cy - r), (cx + r, cy + r)],
                         outline=color, width=1)

            # Blue center marker (bounding box center)
            bcx, bcy = p["blue_cx"], p["blue_cy"]
            draw.line([(bcx - 5, bcy), (bcx + 5, bcy)], fill="cyan", width=1)
            draw.line([(bcx, bcy - 5), (bcx, bcy + 5)], fill="cyan", width=1)

            # Extract pattern and draw detected points
            pat_r = p["pattern_radius"]
            sr = max(int(pat_r) + 10, int(r) + 5)
            _, radii = extract_pattern_with_radii(arr, cx, cy, r, db_max, db_range,
                                                   search_start=sr)
            for angle_deg in range(359):
                found_r = radii[angle_deg]
                if found_r > 0:
                    angle_rad = math.radians(-angle_deg - 90)  # CCW from 12 o'clock
                    px = int(cx + found_r * math.cos(angle_rad))
                    py = int(cy + found_r * math.sin(angle_rad))
                    # Draw small dot
                    draw.rectangle([(px, py), (px + 1, py + 1)], fill=dot_color)

            # Label
            label = f"{col_label} {band}"
            draw.text((cx + 15, cy - 15), label, fill=color)

    debug_img.save(out_path)
    print(f"  Debug image: {out_path}")


# ── Band assignment ──────────────────────────────────────────────────────────

def assign_bands(n_plots, filename=""):
    """Assign band labels to plots based on count and filename hints."""
    fname = filename.lower()

    if n_plots == 3:
        # 3 plots: detect from filename
        if "6ghz" in fname or "6 ghz" in fname:
            return ["6.0", "6.5", "7.0"]
        elif "5ghz" in fname or "5 ghz" in fname:
            return ["5.15", "5.5", "5.85"]
        elif "2.4" in fname or "2_4" in fname:
            return ["2.4", "2.4b", "2.4c"]
        return ["2.4", "5", "6"]
    elif n_plots == 8:
        return ["2.4", "5.15", "5.5", "5.85", "6.0", "6.5", "6.5b", "7.0"]
    elif n_plots == 7:
        return ["2.4", "5.15", "5.5", "5.85", "6.0", "6.5", "7.0"]
    elif n_plots == 4:
        return ["2.4", "5.15", "5.5", "5.85"]
    elif n_plots == 2:
        return ["2.4", "5"]
    return [f"band{i}" for i in range(n_plots)]


def extract_model_name(filename):
    """Extract model name from image filename, stripping suffixes."""
    name = filename.replace(" Total", "").replace(" ", "-")
    # Strip -Summary-XGHz suffixes
    import re
    name = re.sub(r'-Summary-\d+(\.\d+)?GHz$', '', name, flags=re.IGNORECASE)
    return name


# ── Per-variant processing ──────────────────────────────────────────────────

def process_variant(arr, plots, bands, model_key, ant_data, db_max, db_range,
                    debug=False, grid_radius_override=None):
    """Extract el0 patterns for a set of plots (one variant).

    Returns dict of {band: gains}.
    """
    if not plots:
        return {}

    print(f"\n  === Extracting Elevation 0 for {model_key} ===")
    print(f"  {len(plots)} plots")

    results = {}
    for i, (plot, band) in enumerate(zip(plots, bands)):
        cx, cy = plot["cx"], plot["cy"]
        pat_r = plot["pattern_radius"]

        # Use per-band radius (from crosshair/histogram detection)
        # or override if specified
        if grid_radius_override:
            use_radius = grid_radius_override
        else:
            use_radius = plot.get("radius", pat_r)

        search_r = max(int(pat_r) + 10, int(use_radius) + 5)
        gains = extract_polar_pattern(arr, cx, cy, use_radius, db_max, db_range,
                                       search_start=search_r)

        # Mirror for "from above" convention
        gains = [gains[0]] + gains[1:][::-1]

        peak_val = max(gains)
        peak_idx = gains.index(peak_val)
        min_val = min(gains)
        print(f"  {band}: center=({cx},{cy}) r={use_radius:.0f} "
              f"peak={peak_val:.1f}dBi@{peak_idx}deg min={min_val:.1f}dBi")

        if debug:
            for d in range(0, 359, 15):
                print(f"    [{d:3d}] = {gains[d]:6.1f} dB")

        results[band] = gains

    # Validation against .ant data
    band_map = {
        "2.4": "2.4", "2.45": "2.4",
        "5.15": "5", "5.5": "5", "5.85": "5",
        "6.0": "6", "6.5": "6", "6.5b": "6", "7.0": "6",
    }

    if model_key in ant_data:
        print(f"\n  === Validation: el0 vs el90 for {model_key} ===")
        for band in results:
            ant_band = band_map.get(band, band)
            if ant_band in ant_data.get(model_key, {}):
                ref_el90 = ant_data[model_key][ant_band].get("elevation", [])
                if ref_el90:
                    el0 = results[band]
                    total = 0
                    close = 0
                    for a, b in zip(el0, ref_el90):
                        if a > -25 and b > -25:
                            total += 1
                            if abs(a - b) < db_range * 0.25:
                                close += 1
                    pct = (close / total * 100) if total > 0 else 0
                    print(f"    {band} -> {ant_band}: {close}/{total} points "
                          f"within 25% ({pct:.0f}%)")
                    for d in [0, 90, 180, 270]:
                        diff = el0[d] - ref_el90[d]
                        print(f"      [{d:3d}] el0={el0[d]:6.1f} el90={ref_el90[d]:6.1f} "
                              f"diff={diff:+.1f}")

    return results


# ── Main ─────────────────────────────────────────────────────────────────────

def main():
    debug = "--debug" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]

    # Parse named arguments
    db_max = None
    db_min = -20.0
    variants = None
    cx_shift = 0
    cy_shift = 0
    n_bands = None
    grid_radius_override = None
    for i, a in enumerate(sys.argv):
        if a == "--db-max" and i + 1 < len(sys.argv):
            db_max = float(sys.argv[i + 1])
        if a == "--db-min" and i + 1 < len(sys.argv):
            db_min = float(sys.argv[i + 1])
        if a == "--variants" and i + 1 < len(sys.argv):
            variants = [v.strip() for v in sys.argv[i + 1].split(",")]
        if a == "--cx-shift" and i + 1 < len(sys.argv):
            cx_shift = int(sys.argv[i + 1])
        if a == "--cy-shift" and i + 1 < len(sys.argv):
            cy_shift = int(sys.argv[i + 1])
        if a == "--n-bands" and i + 1 < len(sys.argv):
            n_bands = int(sys.argv[i + 1])
        if a == "--grid-radius" and i + 1 < len(sys.argv):
            grid_radius_override = int(sys.argv[i + 1])

    if not args:
        print("Usage: python extract-elevation0-from-images.py <image_path> "
              "--db-max <value> [--variants narrow,wide] [--cy-shift N] [--n-bands N] [--db-min -20] [--debug]")
        print("\n  --db-max is REQUIRED. Read it from the outer ring label on the polar plot.")
        print("  Common values: 10 (indoor APs), 15 (outdoor APs)")
        print("\n  --variants: Split rows into named variants (e.g., narrow,wide).")
        print("    Top rows = first variant, bottom rows = second variant.")
        sys.exit(1)

    if db_max is None:
        print("ERROR: --db-max is required. Read the outer ring dBi label from the polar plot image.")
        print("  Common values: 10 (indoor APs like U7-Pro-XGS), 15 (outdoor APs like U7-Outdoor)")
        sys.exit(1)

    db_range = db_max - db_min

    image_path = Path(args[0])
    if not image_path.exists():
        print(f"Error: {image_path} not found")
        sys.exit(1)

    print(f"Processing: {image_path.name}")
    print(f"  dB scale: center={db_min} dBi, outer ring={db_max} dBi, range={db_range} dB")
    if cx_shift or cy_shift:
        print(f"  Center shift: dx={cx_shift}, dy={cy_shift} (positive = right/down on page)")
    if variants:
        print(f"  Variants: {variants}")

    img = Image.open(image_path)
    arr = np.array(img)
    h, w = arr.shape[:2]
    print(f"  Image: {w}x{h}")

    # Number of grid rings (5 dB spacing)
    n_rings = int(db_range / 5) + 1

    # ── Phase 1: Find el0 plots in column 1 ──
    col1_end = w // 4
    el0_plots = find_plots_in_column(arr, 0, col1_end, n_expected=n_bands,
                                      n_rings=n_rings)
    print(f"  Found {len(el0_plots)} el0 plots in column 1")

    if not el0_plots:
        print("  ERROR: No plots found!")
        sys.exit(1)

    # Apply center shifts if specified
    if cx_shift or cy_shift:
        for p in el0_plots:
            p["cx"] += cx_shift
            p["cy"] += cy_shift

    for i, p in enumerate(el0_plots):
        shift_dist = math.sqrt((p["cx"] - p["blue_cx"]) ** 2 +
                                (p["cy"] - p["blue_cy"]) ** 2)
        print(f"    #{i}: grid_center=({p['cx']},{p['cy']}) "
              f"blue_center=({p['blue_cx']},{p['blue_cy']}) "
              f"shift={shift_dist:.0f}px grid_r={p.get('radius', 0):.0f} "
              f"pat_r={p['pattern_radius']:.0f} "
              f"n_grid={p['n_grid_pixels']}")

    # ── Model name ──
    model = extract_model_name(image_path.stem)

    # ── Load .ant reference data ──
    ant_json = None
    search = image_path.parent
    for _ in range(8):
        candidate = search / "src" / "NetworkOptimizer.Web" / "wwwroot" / "data" / "antenna-patterns.json"
        if candidate.exists():
            ant_json = candidate
            break
        search = search.parent

    ant_data = {}
    if ant_json:
        with open(ant_json) as f:
            ant_data = json.load(f)
        print(f"  Loaded .ant data from: {ant_json}")
    else:
        print(f"\n  No antenna-patterns.json found for validation")

    # ── Phase 2: Validate with el90 from column 2 ──
    el90_plots = []
    n_total = len(el0_plots)

    if variants:
        # Split plots evenly across variants
        n_per = n_total // len(variants)
        if n_total % len(variants) != 0:
            print(f"  WARNING: {n_total} plots doesn't divide evenly by "
                  f"{len(variants)} variants ({n_per} each, {n_total % len(variants)} extra)")

        bands_per_variant = assign_bands(n_per, image_path.name)
        # Flat band list for el90 validation (repeated for each variant)
        all_bands = bands_per_variant * len(variants)

        if ant_json:
            # Validate using first variant's model key for el90
            first_key = f"{model}:{variants[0]}"
            _, el90_plots = validate_with_el90(
                arr, el0_plots, ant_data, first_key, all_bands, db_max, db_range, debug)

        # Process each variant
        output = {}
        for vi, variant_name in enumerate(variants):
            start = vi * n_per
            end = start + n_per
            variant_plots = el0_plots[start:end]
            model_key = f"{model}:{variant_name}"

            results = process_variant(
                arr, variant_plots, bands_per_variant, model_key, ant_data,
                db_max, db_range, debug, grid_radius_override)
            output[model_key] = {"elevation_0": results}

        # Save output
        out_path = image_path.with_suffix(".elevation0.json")
        with open(out_path, "w") as f:
            json.dump(output, f, indent=2)
        print(f"\n  Model: {model} (variants: {', '.join(variants)})")
        print(f"  Saved: {out_path}")

    else:
        # Original single-variant path
        bands = assign_bands(n_total, image_path.name)

        if ant_json:
            _, el90_plots = validate_with_el90(
                arr, el0_plots, ant_data, model, bands, db_max, db_range, debug)

        results = process_variant(
            arr, el0_plots, bands, model, ant_data, db_max, db_range, debug,
            grid_radius_override)

        output = {model: {"elevation_0": results}}
        out_path = image_path.with_suffix(".elevation0.json")
        with open(out_path, "w") as f:
            json.dump(output, f, indent=2)
        print(f"\n  Model: {model}")
        print(f"  Saved: {out_path}")

    # ── Save debug image ──
    if debug:
        use_radius = el0_plots[0].get("radius", el0_plots[0]["pattern_radius"])
        all_bands = assign_bands(n_total, image_path.name) if not variants else (
            assign_bands(n_total // len(variants), image_path.name) * len(variants))
        debug_path = image_path.with_suffix(".debug.png")
        save_debug_image(arr, img, el0_plots, el90_plots, use_radius, all_bands,
                          db_max, db_range, debug_path)


if __name__ == "__main__":
    main()
