"""
Extract Elevation 0 deg antenna pattern data from Ubiquiti reference images.

Ubiquiti .ant files only include the Elevation 90 deg cut. This script digitizes
the Elevation 0 deg polar plots from Ubiquiti's published reference images to get
the missing data.

Usage:
    python scripts/extract-elevation0-from-images.py <image_path> --db-max 15 --db-min -20 [--debug]
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


def is_grid_pixel(r, g, b):
    """Grid lines are lighter blue-gray, NOT the dark pattern line."""
    return (130 < int(b) < 240 and int(r) < 200 and int(g) < 200 and
            not is_pattern_line(r, g, b) and
            abs(int(r) - int(g)) < 40)


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

        # Pattern radius (blue pixel extent) - used only as starting point
        rx = (max(blue_xs) - min(blue_xs)) / 2
        ry = (max(blue_ys) - min(blue_ys)) / 2
        pattern_radius = max(rx, ry)

        if pattern_radius < 30:
            continue

        plots.append({"cx": cx, "cy": cy, "pattern_radius": pattern_radius})

    # Detect grid boundary for each plot, then use the median for all
    # (all plots in the same image have identical grid size)
    grid_radii = []
    for p in plots:
        gr = detect_grid_boundary(arr, p["cx"], p["cy"], p["pattern_radius"])
        grid_radii.append(gr)

    if grid_radii:
        median_gr = sorted(grid_radii)[len(grid_radii) // 2]
        for p in plots:
            p["radius"] = median_gr

    return plots


def detect_grid_boundary(arr, cx, cy, pattern_radius):
    """Find the outermost grid ring radius by scanning outward from center.

    Stays strictly within the first column (width/4) to avoid picking up
    grid lines from adjacent Elevation 90 or Azimuth plots.
    """
    h, w = arr.shape[:2]
    col_end = w // 4  # first column boundary
    max_scan = int(pattern_radius * 3)

    boundary_radii = []
    for angle in range(0, 360, 5):
        angle_rad = math.radians(angle - 90)
        cos_a = math.cos(angle_rad)
        sin_a = math.sin(angle_rad)

        outermost_grid_r = 0
        for r in range(5, max_scan):
            x = int(cx + r * cos_a)
            y = int(cy + r * sin_a)
            # Stay within first column and image bounds
            if 0 <= x < col_end and 0 <= y < h:
                rv, gv, bv = arr[y, x, :3]
                if is_grid_pixel(rv, gv, bv):
                    outermost_grid_r = r
            else:
                break  # hit column or image boundary

        if outermost_grid_r > 0:
            boundary_radii.append(outermost_grid_r)

    if not boundary_radii:
        return pattern_radius  # fallback

    # Use 90th percentile - some directions are cut short by labels or row gaps
    boundary_radii.sort()
    idx = int(len(boundary_radii) * 0.9)
    return boundary_radii[min(idx, len(boundary_radii) - 1)]


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

    # Parse --db-max and --db-min
    db_max = 10.0
    db_min = -20.0
    for i, a in enumerate(sys.argv):
        if a == "--db-max" and i + 1 < len(sys.argv):
            db_max = float(sys.argv[i + 1])
        if a == "--db-min" and i + 1 < len(sys.argv):
            db_min = float(sys.argv[i + 1])

    db_range = db_max - db_min

    if not args:
        print("Usage: python extract-elevation0-from-images.py <image_path> --db-max 15 --db-min -20 [--debug]")
        sys.exit(1)

    image_path = Path(args[0])
    if not image_path.exists():
        print(f"Error: {image_path} not found")
        sys.exit(1)

    print(f"Processing: {image_path.name}")
    print(f"  dB scale: center={db_min} dBi, outer ring={db_max} dBi, range={db_range} dB")

    img = Image.open(image_path)
    arr = np.array(img)
    print(f"  Image: {img.size[0]}x{img.size[1]}")

    plots = find_elevation0_plots(arr)
    print(f"  Found {len(plots)} plots")

    if not plots:
        print("  ERROR: No plots found!")
        sys.exit(1)

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
        cx, cy = plot["cx"], plot["cy"]
        grid_r = plot["radius"]
        pat_r = plot["pattern_radius"]
        gains = extract_polar_pattern(arr, cx, cy, grid_r, db_max, db_range)

        peak_idx = gains.index(0.0)
        min_val = min(gains)
        print(f"  {band}: center=({cx},{cy}) grid_r={grid_r:.0f} pat_r={pat_r:.0f} peak@{peak_idx}deg min={min_val:.1f}dB")

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
                            print(f"    {band}: el0[0]={new_el[0]:.1f} vs el90[0]={ant_el[0]:.1f}  "
                                  f"el0[90]={new_el[90]:.1f} vs el90[90]={ant_el[90]:.1f}  "
                                  f"el0[180]={new_el[180]:.1f} vs el90[180]={ant_el[180]:.1f}")
                        break

    output = {model: {"elevation_0": results}}
    out_path = image_path.with_suffix(".elevation0.json")
    with open(out_path, "w") as f:
        json.dump(output, f, indent=2)
    print(f"\n  Model: {model}")
    print(f"  Saved: {out_path}")


if __name__ == "__main__":
    main()
