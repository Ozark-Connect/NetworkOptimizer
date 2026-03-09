"""
Extract Elevation 0 deg antenna pattern data from Ubiquiti reference images.

Ubiquiti .ant files only include the Elevation 90 deg cut. This script digitizes
the Elevation 0 deg polar plots from Ubiquiti's published reference images to get
the missing data.

Usage:
    python scripts/extract-elevation0-from-images.py <image_path> [--db-max 10] [--debug]
"""

import sys
import json
import math
from pathlib import Path
from PIL import Image
import numpy as np


# Blue pattern line: R~11, G~11, B~253
def is_pattern_line(r, g, b):
    return int(r) < 50 and int(g) < 50 and int(b) > 200


def find_elevation0_plots(arr):
    """Find Elevation 0 deg polar plot centers in the first column."""
    h, w = arr.shape[:2]
    col_end = w // 4

    # Build a mask of blue pattern pixels in the first column
    mask = np.zeros((h, col_end), dtype=bool)
    for y in range(h):
        for x in range(col_end):
            r, g, b = arr[y, x, :3]
            if is_pattern_line(r, g, b):
                mask[y, x] = True

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
            if y - start > 30:  # minimum plot height
                spans.append((start, y))
            in_span = False
    if in_span and h - start > 30:
        spans.append((start, h))

    # Merge spans that are very close (< 100px gap)
    merged = [spans[0]] if spans else []
    for s, e in spans[1:]:
        if s - merged[-1][1] < 100:
            merged[-1] = (merged[-1][0], e)
        else:
            merged.append((s, e))

    plots = []
    for y_start, y_end in merged:
        # Get blue pixels in this span
        blue_xs = []
        blue_ys = []
        for y in range(y_start, y_end):
            for x in range(col_end):
                if mask[y, x]:
                    blue_xs.append(x)
                    blue_ys.append(y)

        if len(blue_xs) < 20:
            continue

        cx = (min(blue_xs) + max(blue_xs)) // 2
        cy = (min(blue_ys) + max(blue_ys)) // 2

        # Radius = half the max extent
        rx = (max(blue_xs) - min(blue_xs)) / 2
        ry = (max(blue_ys) - min(blue_ys)) / 2
        radius = max(rx, ry)

        if radius < 50:  # skip tiny false detections
            continue

        plots.append({"cx": cx, "cy": cy, "radius": radius})

    return plots


def extract_polar_pattern(arr, cx, cy, outer_radius, db_max, db_range):
    """Extract gain values by ray casting from center outward.

    Returns 359 values (0-358 degrees), normalized to peak = 0 dB.
    Matches the elevation array format in antenna-patterns.json.
    """
    h, w = arr.shape[:2]
    db_min = db_max - db_range

    gains = []
    for angle_deg in range(359):
        # Polar plots: 0 deg at top, clockwise
        angle_rad = math.radians(angle_deg - 90)
        cos_a = math.cos(angle_rad)
        sin_a = math.sin(angle_rad)

        # Cast ray from outer edge inward, find outermost blue pixel
        # Check a 3px wide band for each radius to avoid missing the line
        found_r = 0
        for r in range(int(outer_radius) + 5, 2, -1):
            hit = False
            for offset in [-1, 0, 1]:
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

    # Normalize to peak = 0 dB
    peak = max(gains)
    gains = [round(g - peak, 1) for g in gains]

    return gains


def main():
    debug = "--debug" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]

    # Parse --db-max
    db_max = 10.0
    for i, a in enumerate(sys.argv):
        if a == "--db-max" and i + 1 < len(sys.argv):
            db_max = float(sys.argv[i + 1])

    if not args:
        print("Usage: python extract-elevation0-from-images.py <image_path> [--db-max 10] [--debug]")
        sys.exit(1)

    image_path = Path(args[0])
    if not image_path.exists():
        print(f"Error: {image_path} not found")
        sys.exit(1)

    print(f"Processing: {image_path.name}")
    print(f"  dB max (outer ring): {db_max} dBi")

    img = Image.open(image_path)
    arr = np.array(img)
    print(f"  Image: {img.size[0]}x{img.size[1]}")

    plots = find_elevation0_plots(arr)
    print(f"  Found {len(plots)} plots")

    # Count grid rings to determine dB range
    # Use the first valid plot
    if not plots:
        print("  ERROR: No plots found!")
        sys.exit(1)

    # Detect grid rings on the first large plot
    p = plots[0]
    db_range = detect_db_range(arr, p["cx"], p["cy"], p["radius"])
    print(f"  dB range: {db_range} dB (center = {db_max - db_range} dBi)")

    # Band labels based on count
    n = len(plots)
    if n == 8:
        bands = ["2.4", "5.15", "5.5", "5.85", "6.0", "6.5", "6.5b", "7.0"]
    elif n == 7:
        bands = ["2.4", "5.15", "5.5", "5.85", "6.0", "6.5", "7.0"]
    elif n == 4:
        bands = ["2.4", "5.15", "5.5", "5.85"]
    elif n == 3:
        bands = ["2.4", "5", "6"]
    elif n == 2:
        bands = ["2.4", "5"]
    else:
        bands = [f"band{i}" for i in range(n)]

    results = {}
    for i, (plot, band) in enumerate(zip(plots, bands)):
        cx, cy, radius = plot["cx"], plot["cy"], plot["radius"]
        gains = extract_polar_pattern(arr, cx, cy, radius, db_max, db_range)

        peak_idx = gains.index(0.0)
        min_val = min(gains)
        print(f"  {band}: center=({cx},{cy}) r={radius:.0f} peak@{peak_idx}deg min={min_val:.1f}dB")

        if debug:
            for d in range(0, 359, 15):
                print(f"    [{d:3d}] = {gains[d]:6.1f} dB")

        results[band] = gains

    # Compare with existing .ant data if available
    ant_json = image_path.parent.parent.parent / "src" / "NetworkOptimizer.Web" / "wwwroot" / "data" / "antenna-patterns.json"
    model = image_path.stem.replace(" Total", "").replace(" ", "-")

    if ant_json.exists():
        with open(ant_json) as f:
            existing = json.load(f)
        if model in existing:
            print(f"\n  Comparison with existing .ant elevation (Elevation 90 deg cut):")
            for band in results:
                ant_band = band.replace(".15", "").replace(".5", "").replace(".85", "")
                # Try exact band match first, then simplified
                for try_band in [band, ant_band]:
                    if try_band in existing[model]:
                        ant_el = existing[model][try_band].get("elevation", [])
                        if ant_el:
                            new_el = results[band]
                            # Compare at key angles
                            print(f"    {band}: new_el0[0]={new_el[0]:.1f} vs ant_el90[0]={ant_el[0]:.1f}  "
                                  f"new[90]={new_el[90]:.1f} vs ant[90]={ant_el[90]:.1f}  "
                                  f"new[180]={new_el[180]:.1f} vs ant[180]={ant_el[180]:.1f}")
                        break

    output = {model: {"elevation_0": results}}
    out_path = image_path.with_suffix(".elevation0.json")
    with open(out_path, "w") as f:
        json.dump(output, f, indent=2)
    print(f"\n  Model: {model}")
    print(f"  Saved: {out_path}")


def detect_db_range(arr, cx, cy, outer_radius):
    """Count grid rings along multiple directions to determine dB range."""
    h, w = arr.shape[:2]
    ring_counts = []

    # Check along 4 directions (right, down, left, up)
    for angle in [0, 90, 180, 270]:
        angle_rad = math.radians(angle - 90)
        cos_a = math.cos(angle_rad)
        sin_a = math.sin(angle_rad)

        crossings = 0
        was_grid = False
        for r in range(5, int(outer_radius)):
            x = int(cx + r * cos_a)
            y = int(cy + r * sin_a)
            if 0 <= x < w and 0 <= y < h:
                rv, gv, bv = arr[y, x, :3]
                # Grid lines: lighter blue/gray, NOT the dark pattern line
                is_grid = (130 < int(bv) < 240 and int(rv) < 200 and int(gv) < 200 and
                           not is_pattern_line(rv, gv, bv) and
                           abs(int(rv) - int(gv)) < 40)  # grid is blue-gray, R~G
                if is_grid and not was_grid:
                    crossings += 1
                was_grid = is_grid

        ring_counts.append(crossings)

    # Use the median count
    median_rings = sorted(ring_counts)[len(ring_counts) // 2]
    if median_rings < 3:
        median_rings = 4  # default: 4 rings = 20 dB

    return median_rings * 5  # 5 dBi per ring


if __name__ == "__main__":
    main()
