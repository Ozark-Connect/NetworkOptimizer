namespace NetworkOptimizer.Web.Services.InnerSpace;

/// <summary>What the scan of one plan adds beyond InnerSpace's shapes.</summary>
public sealed record ScanInfo(List<ScannedOpening> Openings, int? Story);

/// <summary>
/// Turns an InnerSpace project into building templates in local meters. Pure: the caller fetches
/// the project, the floor image sizes and the scan archives.
/// </summary>
public static class InnerSpaceMapper
{
    // An opening lies on a wall when both ends are this close to the wall's line (meters).
    private const double OpeningOnWallTolerance = 0.15;
    // A scanned window sits about half a wall thickness off the centerline; allow a full thickness.
    private const double ScannedOpeningTolerance = 0.3;
    // Wall endpoints closer than this join into one polyline.
    private const double JoinTolerance = 0.02;
    private const string ScanDefault = "\u0000scan-default";

    /// <summary>
    /// Builds the templates. <paramref name="imageSizes"/> is each plan's floor image size in pixels
    /// (InnerSpace's <c>floor_plans</c>); <paramref name="scans"/> holds the RoomPlan data of scanned
    /// plans; <paramref name="siteExterior"/> is the outline material for unedited scanned walls.
    /// </summary>
    public static List<ImportTemplate> Build(
        InnerSpaceProject project,
        IReadOnlyDictionary<string, (double Width, double Height)> imageSizes,
        IReadOnlyDictionary<string, ScanInfo> scans,
        string siteExterior)
    {
        var shapes = InnerSpaceShapes.From(project.Shapes);
        var materials = new InnerSpaceMaterials(project.WallTypes);
        var products = project.Products.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());

        var plans = project.Plans.Where(p => !p.Type.Equals("sample", StringComparison.OrdinalIgnoreCase)).ToList();
        var inBuilding = plans.Where(p => p.Ordering > 0).OrderBy(p => p.Ordering).ToList();
        var refPpm = inBuilding.Count > 0 ? Ppm(shapes, inBuilding[0].Id) : null;

        var floors = new Dictionary<string, ImportFloor>();
        var skipped = new List<string>();
        foreach (var plan in plans)
        {
            var floor = BuildFloor(plan, shapes, materials, products, imageSizes, scans, siteExterior,
                plan.Ordering > 0 ? refPpm : null);
            if (floor == null) skipped.Add(plan.Title);
            else floors[plan.Id] = floor;
        }

        var templates = new List<ImportTemplate>();
        var building = inBuilding.Where(p => floors.ContainsKey(p.Id)).ToList();
        if (building.Count > 0) templates.AddRange(Group(building, floors, shapes, scans));
        foreach (var plan in plans.Where(p => p.Ordering <= 0 && floors.ContainsKey(p.Id)))
            templates.AddRange(Group(new List<InnerSpacePlan> { plan }, floors, shapes, scans));

        MarkEarlierImports(templates);
        if (skipped.Count > 0 && templates.Count > 0)
            templates[0].Warnings.Add($"Skipped (no scale set in InnerSpace): {string.Join(", ", skipped)}");
        return templates;
    }

    /// <summary>
    /// Shape units per meter from the plan's scale line. The author's line wins over InnerSpace's
    /// placeholder: on Design Center plans the two disagree by 3x or more.
    /// </summary>
    public static double? Ppm(InnerSpaceShapes shapes, string planId)
    {
        var line = shapes.Scales.Where(s => s.PlanId == planId && s.Position.Count == 2 && s.Scale > 0)
            .OrderBy(s => s.DefaultScale).FirstOrDefault();
        if (line == null) return null;
        var len = Math.Sqrt(Math.Pow(line.Position[1].X - line.Position[0].X, 2) + Math.Pow(line.Position[1].Y - line.Position[0].Y, 2));
        return len > 0 ? len / line.Scale!.Value : null;
    }

    private static ImportFloor? BuildFloor(
        InnerSpacePlan plan, InnerSpaceShapes shapes, InnerSpaceMaterials materials,
        Dictionary<string, InnerSpaceProduct> products,
        IReadOnlyDictionary<string, (double Width, double Height)> imageSizes,
        IReadOnlyDictionary<string, ScanInfo> scans, string siteExterior, double? refPpm)
    {
        var ppm = Ppm(shapes, plan.Id);
        if (ppm == null) return null;
        var map = shapes.Maps.FirstOrDefault(m => m.PlanId == plan.Id);
        var isScan = !string.IsNullOrEmpty(map?.UrlModel);
        var scale = shapes.Scales.Where(s => s.PlanId == plan.Id).OrderBy(s => s.DefaultScale).FirstOrDefault();
        scans.TryGetValue(plan.Id, out var scan);

        var floor = new ImportFloor
        {
            SourcePlanId = plan.Id,
            Label = string.IsNullOrWhiteSpace(plan.Title) ? "Imported floor" : plan.Title.Trim(),
            FloorNumber = plan.Ordering > 0 ? plan.Ordering : (scan?.Story ?? 0) + 1,
            HeightM = scale?.Height > 0 ? scale.Height : null,
            FloorMaterial = plan.Attenuation is > 0 ? InnerSpaceMaterials.NearestFloor(plan.Attenuation.Value) : null,
        };

        Vec2 P(InnerSpacePoint p) => new(p.X / ppm.Value, p.Y / ppm.Value);

        // 1. Resolve every segment.
        var hosts = new List<Segment>();
        var openings = new List<Segment>();
        foreach (var w in shapes.Walls.Where(w => w.PlanId == plan.Id && w.Position.Count >= 2))
        {
            var r = materials.Resolve(w.Variant, w.WallTypeId, isScan);
            var seg = new Segment(P(w.Position[0]), P(w.Position[1]), r.IsScanDefault ? ScanDefault : r.Material, r.Span);
            if (seg.Length < 1e-3) continue;
            (InnerSpaceMaterials.IsOpening(seg.Material) ? openings : hosts).Add(seg);
        }

        // 2. Cut drawn openings into their host walls, then scanned ones where nothing was drawn.
        var loose = new List<Segment>();
        foreach (var o in openings)
            if (!CutInto(hosts, o.A, o.B, o.Material, o.Span, OpeningOnWallTolerance, requireEnds: true))
                loose.Add(o);
        if (scan != null)
        {
            foreach (var s in scan.Openings)
            {
                var material = s.IsDoor ? "door_wood" : "window_2_pane";
                CutScanned(hosts, s, material);
            }
        }

        var pieces = hosts.SelectMany(h => h.Pieces()).Concat(loose).ToList();

        // 3. Each separate structure's outline is the outer face of its wall network, written as one
        // closed polyline (the editor's outline shape); the rest chain as interior walls. Drawn plans
        // meet outer walls at T-junctions, so chaining alone never closes their outline.
        var (outer, interior) = OuterFaces(pieces);
        floor.Walls.AddRange(outer);
        floor.Walls.AddRange(Chain(interior));
        foreach (var l in outer) floor.Footprints.Add(l.Points.ToList());

        // A hand-traced outline often misses closing by a gap. Its walls' hull still stands in as the
        // footprint for splitting buildings and snapping; exterior defaults below use real loops only.
        if (outer.Count == 0)
        {
            var hull = PlanGeometry.ConvexHull(floor.Walls.SelectMany(w => w.Points));
            if (hull.Count >= 3 && PlanGeometry.Area(hull) > 1)
            {
                hull.Add(hull[0]);
                floor.Footprints.Add(hull);
            }
        }

        // 4. Unedited scanned walls: the outline takes the site's exterior, the rest drywall.
        foreach (var w in floor.Walls)
        {
            var onOutline = outer.Contains(w) || (outer.Count == 0 && isScan);
            for (var i = 0; i < w.Materials.Count; i++)
                if (w.Materials[i] == ScanDefault)
                    w.Materials[i] = onOutline ? siteExterior : "drywall";
        }

        // 5. APs. An adopted AP on a placeholder of the same model replaces it.
        var aps = new List<ImportAp>();
        foreach (var d in shapes.Devices.Where(d => d.PlanId == plan.Id && d.Position.Count > 0))
        {
            if (!products.TryGetValue(d.ProductId, out var product) || !IsAccessPoint(product)) continue;
            var pos = P(d.Position[0]);
            aps.Add(new ImportAp
            {
                Name = string.IsNullOrWhiteSpace(d.Title) ? product.Sku : d.Title.Trim(),
                Model = product.Sku,
                Mac = product.IsManaged ? NormalizeMac(d.Meta?.Mac) : null,
                MountType = NormalizeMount(d.Mount),
                AntennaMode = NormalizeAntenna(d.Antenna),
                Position = pos,
                HeightM = d.Position[0].Z is > 0 ? d.Position[0].Z / ppm.Value : null,
                OrientationDeg = Azimuth(d.Rotation?.Pov),
            });
        }
        floor.Aps.AddRange(aps.Where(a => a.Mac != null || !aps.Any(m => m.Mac != null
            && m.Model.Equals(a.Model, StringComparison.OrdinalIgnoreCase)
            && (m.Position - a.Position).Length < 0.5)));

        // 6. The floor image: InnerSpace centers it on the shape origin.
        if (map?.UrlImage != null && imageSizes.TryGetValue(plan.Id, out var size) && size.Width > 0 && size.Height > 0)
        {
            var sx = map.ImageScale?.X is > 0 ? map.ImageScale.X : 1;
            var sy = map.ImageScale?.Y is > 0 ? map.ImageScale.Y : 1;
            floor.Image = new ImportImage
            {
                SourceUrl = map.UrlImage,
                Center = new Vec2((map.ImageOffset?.X ?? 0) / ppm.Value, (map.ImageOffset?.Y ?? 0) / ppm.Value),
                WidthM = size.Width * sx / ppm.Value,
                HeightM = size.Height * sy / ppm.Value,
                RotationDeg = -map.ImageRotation * 180 / Math.PI,
            };
        }

        // 7. A building floor sits where InnerSpace aligned it over the first floor.
        if (plan.Ordering > 0 && refPpm != null && map?.Position is { Count: > 0 })
        {
            var offset = new Vec2(map.Position[0].X / refPpm.Value, map.Position[0].Y / refPpm.Value);
            Transform(floor, map.Rotation, offset);
        }
        return floor;
    }

    private static bool IsAccessPoint(InnerSpaceProduct p) =>
        string.Equals(p.Subcategory, "access_point", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(p.Category, "wifi", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(p.Subcategory));

    private static string? NormalizeMount(string? mount) => mount?.Trim().ToLowerInvariant() switch
    {
        "ceiling" => "ceiling",
        "wall" => "wall",
        "floor" or "rack" => "desktop",
        _ => null,
    };

    // InnerSpace also has builtIn, dual, stub, terminal, highGain and reflector modes; those keep the
    // model's default pattern rather than naming one our antenna patterns do not have.
    private static string? NormalizeAntenna(string? antenna) => antenna?.Trim().ToLowerInvariant() switch
    {
        "omni" or "narrow" or "wide" or "panel" => antenna.Trim().ToLowerInvariant(),
        _ => null,
    };

    private static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : null;
    }

    /// <summary>
    /// Clockwise degrees from plan-up. A wall-mounted or tilted AP faces where its antenna boresight
    /// (local +Z, out of the face) points once rotated, read in the plan. An AP lying flat on a
    /// ceiling or floor has a vertical boresight and no facing from it, so it keeps InnerSpace's
    /// integration azimuth: the Z angle of the quaternion (three.js XYZ order) less 90.
    /// </summary>
    public static int Azimuth(InnerSpaceQuaternion? q)
    {
        if (q == null) return 0;

        // v' = v + w t + q × t, with t = 2 (q × v), for v = (0, 0, 1).
        double tx = 2 * q.Y, ty = -2 * q.X, tz = 0;
        var bx = q.W * tx + (q.Y * tz - q.Z * ty);
        var by = q.W * ty + (q.Z * tx - q.X * tz);
        if (Math.Sqrt(bx * bx + by * by) > 0.5)
        {
            // Plan y is up (north at rotation 0): compass = atan2(east, north).
            var compass = Math.Atan2(bx, by) * 180 / Math.PI;
            return (int)Math.Round((compass % 360 + 360) % 360) % 360;
        }

        var m12 = 2 * (q.X * q.Y - q.W * q.Z);
        var m11 = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        var z = Math.Atan2(-m12, m11) * 180 / Math.PI;
        var deg = z - 90;
        return (int)Math.Round(((deg % 360) + 360) % 360) % 360;
    }

    private static void Transform(ImportFloor floor, double rotationRad, Vec2 offset)
    {
        Vec2 T(Vec2 p) => p.Rotate(rotationRad) + offset;
        foreach (var w in floor.Walls)
            for (var i = 0; i < w.Points.Count; i++) w.Points[i] = T(w.Points[i]);
        foreach (var f in floor.Footprints)
            for (var i = 0; i < f.Count; i++) f[i] = T(f[i]);
        var deg = rotationRad * 180 / Math.PI;
        foreach (var a in floor.Aps)
        {
            a.Position = T(a.Position);
            a.OrientationDeg = (int)Math.Round(((a.OrientationDeg - deg) % 360 + 360) % 360) % 360;
        }
        if (floor.Image != null)
        {
            floor.Image.Center = T(floor.Image.Center);
            floor.Image.RotationDeg -= deg;
        }
    }

    // ---- Openings ----

    private sealed class Segment
    {
        public Segment(Vec2 a, Vec2 b, string material, WallSpan? span)
        {
            A = a; B = b; Material = material; Span = span;
        }

        public Vec2 A { get; }
        public Vec2 B { get; }
        public string Material { get; }
        public WallSpan? Span { get; }
        public double Length => (B - A).Length;
        public List<(double T0, double T1, string Material, WallSpan? Span)> Cuts { get; } = new();

        public bool Overlaps(double t0, double t1) => Cuts.Any(c => t0 < c.T1 && t1 > c.T0);

        /// <summary>The host split at its cuts, in order.</summary>
        public IEnumerable<Segment> Pieces()
        {
            var t = 0.0;
            Vec2 At(double u) => A + (B - A) * u;
            foreach (var c in Cuts.OrderBy(c => c.T0))
            {
                if (c.T0 > t + 1e-6) yield return new Segment(At(t), At(c.T0), Material, Span);
                yield return new Segment(At(c.T0), At(c.T1), c.Material, c.Span);
                t = c.T1;
            }
            if (t < 1 - 1e-6) yield return new Segment(At(t), B, Material, Span);
        }
    }

    private static bool CutInto(List<Segment> hosts, Vec2 a, Vec2 b, string material, WallSpan? span,
        double tolerance, bool requireEnds)
    {
        foreach (var h in hosts.OrderBy(h => PlanGeometry.DistanceToSegment(h.A, h.B, (a + b) * 0.5)))
        {
            if (PlanGeometry.DistanceToLine(h.A, h.B, a) > tolerance || PlanGeometry.DistanceToLine(h.A, h.B, b) > tolerance)
                continue;
            var t0 = PlanGeometry.ProjectT(h.A, h.B, a);
            var t1 = PlanGeometry.ProjectT(h.A, h.B, b);
            if (t0 > t1) (t0, t1) = (t1, t0);
            if (requireEnds && (t0 < -0.02 || t1 > 1.02)) continue;
            t0 = Math.Clamp(t0, 0, 1);
            t1 = Math.Clamp(t1, 0, 1);
            if (t1 - t0 < 1e-4 || h.Overlaps(t0, t1)) continue;
            h.Cuts.Add((t0, t1, material, span));
            return true;
        }
        return false;
    }

    private static void CutScanned(List<Segment> hosts, ScannedOpening s, string material)
    {
        var host = hosts.Where(h => PlanGeometry.DistanceToSegment(h.A, h.B, s.Center) <= ScannedOpeningTolerance)
            .OrderBy(h => PlanGeometry.DistanceToSegment(h.A, h.B, s.Center)).FirstOrDefault();
        if (host == null || host.Length < 1e-3) return;
        var tc = PlanGeometry.ProjectT(host.A, host.B, s.Center);
        var half = s.WidthM / 2 / host.Length;
        var t0 = Math.Clamp(tc - half, 0, 1);
        var t1 = Math.Clamp(tc + half, 0, 1);
        if (t1 - t0 < 1e-4 || host.Overlaps(t0, t1)) return; // a drawn opening there wins
        host.Cuts.Add((t0, t1, material, new WallSpan(Math.Round(s.SillM, 2), Math.Round(s.HeadM, 2))));
    }

    // ---- Outlines ----

    // A wall end this close to another wall joins it there (a T-junction).
    private const double JunctionTolerance = 0.05;

    /// <summary>
    /// The outer face of each connected wall network as a closed polyline, and every other segment.
    /// Walls are split where another wall ends on them, then each network is walked from its leftmost
    /// node taking the rightmost turn at every junction, which traces the outside. Walls walked both
    /// ways (dangling stubs) stay interior. A network with no enclosing cycle yields no outline.
    /// </summary>
    private static (List<ImportPolyline> Outlines, List<Segment> Interior) OuterFaces(List<Segment> segments)
    {
        // Split at junctions.
        var ends = segments.SelectMany(s => new[] { s.A, s.B }).ToList();
        var split = new List<Segment>();
        foreach (var s in segments)
        {
            var len = s.Length;
            if (len < 1e-3) continue;
            var ts = ends
                .Where(p => PlanGeometry.DistanceToLine(s.A, s.B, p) <= JunctionTolerance)
                .Select(p => PlanGeometry.ProjectT(s.A, s.B, p))
                .Where(t => t * len > JunctionTolerance && (1 - t) * len > JunctionTolerance)
                .Distinct().OrderBy(t => t).ToList();
            var pts = new List<Vec2> { s.A };
            pts.AddRange(ts.Select(t => s.A + (s.B - s.A) * t));
            pts.Add(s.B);
            for (var i = 0; i < pts.Count - 1; i++) split.Add(new Segment(pts[i], pts[i + 1], s.Material, s.Span));
        }

        // Nodes and edges.
        var nodes = new List<Vec2>();
        int Node(Vec2 p)
        {
            for (var i = 0; i < nodes.Count; i++)
                if ((nodes[i] - p).Length <= JunctionTolerance) return i;
            nodes.Add(p);
            return nodes.Count - 1;
        }
        var edges = new Dictionary<(int, int), Segment>();
        foreach (var s in split)
        {
            int a = Node(s.A), b = Node(s.B);
            if (a != b) edges.TryAdd((Math.Min(a, b), Math.Max(a, b)), s);
        }
        var adj = new Dictionary<int, List<int>>();
        foreach (var (a, b) in edges.Keys)
        {
            (adj.TryGetValue(a, out var la) ? la : adj[a] = new()).Add(b);
            (adj.TryGetValue(b, out var lb) ? lb : adj[b] = new()).Add(a);
        }

        var outlines = new List<ImportPolyline>();
        var onOutline = new HashSet<(int, int)>();
        var seen = new HashSet<int>();
        foreach (var root in adj.Keys)
        {
            if (!seen.Add(root)) continue;
            var component = new List<int> { root };
            for (var q = 0; q < component.Count; q++)
                foreach (var n in adj[component[q]])
                    if (seen.Add(n)) component.Add(n);

            var walk = WalkOuter(component, nodes, adj);
            if (walk == null) continue;
            var line = new ImportPolyline();
            line.Points.Add(nodes[walk[0].From]);
            foreach (var (from, to) in walk)
            {
                var seg = edges[(Math.Min(from, to), Math.Max(from, to))];
                line.Points.Add(nodes[to]);
                line.Materials.Add(seg.Material);
                line.Spans.Add(seg.Span);
                onOutline.Add((Math.Min(from, to), Math.Max(from, to)));
            }
            if (!line.IsClosed || PlanGeometry.Area(line.Points) < 1) continue;
            outlines.Add(line);
        }

        var interior = edges.Where(e => !onOutline.Contains(e.Key))
            .Select(e => new Segment(nodes[e.Key.Item1], nodes[e.Key.Item2], e.Value.Material, e.Value.Span))
            .ToList();
        return (outlines, interior);
    }

    /// <summary>The outside of one network as directed edges, spikes removed; null without an enclosing cycle.</summary>
    private static List<(int From, int To)>? WalkOuter(List<int> component, List<Vec2> nodes, Dictionary<int, List<int>> adj)
    {
        if (component.Count < 3) return null;
        var start = component.OrderBy(i => nodes[i].X).ThenBy(i => nodes[i].Y).First();
        double Angle(Vec2 v) => Math.Atan2(v.Y, v.X);
        int Next(int cur, Vec2 back) => adj[cur].OrderBy(n =>
        {
            var a = (Angle(nodes[n] - nodes[cur]) - Angle(back)) % (2 * Math.PI);
            if (a < 0) a += 2 * Math.PI;
            return a < 1e-9 ? 2 * Math.PI : a; // straight back only at a dead end
        }).First();

        var walked = new List<(int From, int To)>();
        var curNode = start;
        var backDir = new Vec2(-1, 0); // arrived from outside, to the west
        var limit = 4 * adj.Sum(kv => kv.Value.Count) + 8;
        while (walked.Count < limit)
        {
            var nxt = Next(curNode, backDir);
            if (walked.Count > 0 && (curNode, nxt) == walked[0]) break;
            walked.Add((curNode, nxt));
            backDir = nodes[curNode] - nodes[nxt];
            curNode = nxt;
        }
        if (walked.Count >= limit) return null;

        // A wall walked out and back is a stub hanging off the outline, not part of it.
        var directed = walked.ToHashSet();
        var outline = walked.Where(e => !directed.Contains((e.To, e.From))).ToList();
        return outline.Count >= 3 ? outline : null;
    }

    // ---- Polylines ----

    private static List<ImportPolyline> Chain(List<Segment> segments)
    {
        var unused = new List<Segment>(segments);
        var result = new List<ImportPolyline>();
        bool Near(Vec2 p, Vec2 q) => (p - q).Length <= JoinTolerance;

        while (unused.Count > 0)
        {
            var first = unused[0];
            unused.RemoveAt(0);
            var pts = new LinkedList<Vec2>(new[] { first.A, first.B });
            var mats = new LinkedList<(string M, WallSpan? S)>(new[] { (first.Material, first.Span) });

            var grew = true;
            while (grew)
            {
                grew = false;
                for (var i = 0; i < unused.Count; i++)
                {
                    var s = unused[i];
                    var tail = pts.Last!.Value;
                    var head = pts.First!.Value;
                    if (Near(pts.First.Value, pts.Last.Value) && pts.Count > 2) break; // closed
                    if (Near(tail, s.A)) { pts.AddLast(s.B); mats.AddLast((s.Material, s.Span)); }
                    else if (Near(tail, s.B)) { pts.AddLast(s.A); mats.AddLast((s.Material, s.Span)); }
                    else if (Near(head, s.B)) { pts.AddFirst(s.A); mats.AddFirst((s.Material, s.Span)); }
                    else if (Near(head, s.A)) { pts.AddFirst(s.B); mats.AddFirst((s.Material, s.Span)); }
                    else continue;
                    unused.RemoveAt(i);
                    grew = true;
                    break;
                }
            }

            var line = new ImportPolyline();
            line.Points.AddRange(pts);
            if (line.Points.Count > 2 && Near(line.Points[0], line.Points[^1]))
                line.Points[^1] = line.Points[0];
            foreach (var (m, s) in mats) { line.Materials.Add(m); line.Spans.Add(s); }
            result.Add(line);
        }
        return result;
    }

    // ---- Buildings ----

    private sealed class Cluster
    {
        public List<(ImportFloor Floor, List<Vec2> Footprint)> Parts { get; } = new();
        public (Vec2 Min, Vec2 Max) Top => PlanGeometry.Bounds(Parts[^1].Footprint);
    }

    /// <summary>
    /// Groups a stack (or one canvas) into buildings by footprint. Separate outlines on one floor are
    /// separate buildings; an outline overlapping one on the floor below joins that building, at any
    /// size. Aligned floors with one outline each are therefore one building.
    /// </summary>
    private static IEnumerable<ImportTemplate> Group(List<InnerSpacePlan> plans, Dictionary<string, ImportFloor> floors,
        InnerSpaceShapes shapes, IReadOnlyDictionary<string, ScanInfo> scans)
    {
        var stack = plans.Select(p => floors[p.Id]).OrderBy(f => f.FloorNumber).ToList();
        var clusters = new List<Cluster>();
        foreach (var floor in stack)
        {
            foreach (var fp in floor.Footprints.OrderByDescending(f => PlanGeometry.Area(f)))
            {
                var box = PlanGeometry.Bounds(fp);
                var area = Area(box);
                // Only a footprint on a lower floor can continue a building; two on one floor are two buildings.
                // Overlap decides, never size: an upper floor bigger than the one below is still above it.
                var match = clusters
                    .Where(c => c.Parts[^1].Floor.FloorNumber < floor.FloorNumber)
                    .Select(c => (c, overlap: PlanGeometry.OverlapArea(c.Top, box)))
                    .Where(x => x.overlap >= 0.5 * Math.Min(area, Area(x.c.Top)))
                    .OrderByDescending(x => x.overlap).Select(x => x.c).FirstOrDefault();
                if (match == null) { match = new Cluster(); clusters.Add(match); }
                match.Parts.Add((floor, fp));
            }
        }

        var source = plans.Any(p => !string.IsNullOrEmpty(p.DcSourceProjectId)) ? ImportSource.DesignCenter
            : plans.Any(p => shapes.Maps.Any(m => m.PlanId == p.Id && !string.IsNullOrEmpty(m.UrlModel))) ? ImportSource.Scan
            : ImportSource.Drawn;
        var name = plans.Count == 1 ? stack[0].Label : $"{stack[0].Label} ({plans.Count} floors)";
        var baseId = "innerspace:" + string.Join("+", plans.Select(p => p.Id));
        var dcId = plans.Select(p => p.DcSourceProjectId).FirstOrDefault(d => !string.IsNullOrEmpty(d));
        var created = plans.Max(p => p.CreatedAt);
        var loc = plans.Select(p => p.Location).FirstOrDefault(l => l != null);
        (double, double)? location = loc == null ? null : (loc.Latitude, loc.Longitude);

        if (clusters.Count <= 1)
        {
            var t = new ImportTemplate
            {
                Id = baseId, Name = name, Source = source, DcSourceProjectId = dcId, CreatedAt = created, Location = location,
            };
            t.Floors.AddRange(stack);
            if (clusters.Count == 0 && stack.Any(f => f.Walls.Count > 0))
                t.Warnings.Add("No closed outline found: placement cannot snap to an existing building.");
            Recenter(t);
            yield return t;
            yield break;
        }

        // Partition everything first, on copies, so recentering one part never moves another.
        var parts = new List<ImportTemplate>();
        var biggest = clusters.OrderByDescending(c => c.Parts.Count).First();
        for (var i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            var t = new ImportTemplate
            {
                Id = $"{baseId}#{i + 1}", Name = $"{name} - Part {i + 1}", Source = source, DcSourceProjectId = dcId,
                CreatedAt = created, Location = location, WasSplit = true, GroupId = baseId,
            };
            foreach (var floor in stack)
            {
                if (floor.Footprints.Count == 0)
                {
                    // An image-only floor has nothing to split by: it stays whole with the largest building.
                    if (cluster == biggest) t.Floors.Add(Copy(floor, floor.Walls, floor.Aps, floor.Footprints, floor.Image));
                    continue;
                }
                var mine = cluster.Parts.Where(p => p.Floor == floor).Select(p => p.Footprint).ToList();
                if (mine.Count == 0) continue;
                var others = clusters.Where(c => c != cluster).SelectMany(c => c.Parts.Where(p => p.Floor == floor).Select(p => p.Footprint)).ToList();
                bool Mine(Vec2 p) => Owner(p, mine, others);
                t.Floors.Add(Copy(floor,
                    floor.Walls.Where(w => Mine(PlanGeometry.Centroid(w.Points))),
                    floor.Aps.Where(a => Mine(a.Position)), mine, image: null));
            }
            parts.Add(t);
        }

        foreach (var t in parts)
        {
            // Renumber so this building's lowest floor is the editor's first floor (1).
            var lowest = t.Floors.Min(f => f.FloorNumber);
            foreach (var f in t.Floors) f.FloorNumber -= lowest - 1;
            t.Warnings.Add("Split from a plan holding several buildings; the floor image is not imported for split parts.");
            Recenter(t);
            yield return t;
        }

        // The unsplit alternative, behind Import as one building. Built last: the parts above are
        // copies, so recentering the original floors here moves nothing of theirs.
        var whole = new ImportTemplate
        {
            Id = baseId, Name = name, Source = source, DcSourceProjectId = dcId, CreatedAt = created, Location = location,
            GroupId = baseId, IsWhole = true,
        };
        whole.Floors.AddRange(stack);
        Recenter(whole);
        yield return whole;
    }

    private static ImportFloor Copy(ImportFloor floor, IEnumerable<ImportPolyline> walls, IEnumerable<ImportAp> aps,
        IEnumerable<List<Vec2>> footprints, ImportImage? image)
    {
        var copy = new ImportFloor
        {
            SourcePlanId = floor.SourcePlanId, Label = floor.Label, FloorNumber = floor.FloorNumber,
            HeightM = floor.HeightM, FloorMaterial = floor.FloorMaterial,
        };
        foreach (var w in walls)
        {
            var line = new ImportPolyline();
            line.Points.AddRange(w.Points);
            line.Materials.AddRange(w.Materials);
            line.Spans.AddRange(w.Spans);
            copy.Walls.Add(line);
        }
        foreach (var a in aps)
            copy.Aps.Add(new ImportAp
            {
                Name = a.Name, Model = a.Model, Mac = a.Mac, MountType = a.MountType, AntennaMode = a.AntennaMode,
                Position = a.Position, HeightM = a.HeightM, OrientationDeg = a.OrientationDeg,
            });
        foreach (var f in footprints) copy.Footprints.Add(f.ToList());
        if (image != null)
            copy.Image = new ImportImage
            {
                SourceUrl = image.SourceUrl, Center = image.Center, WidthM = image.WidthM, HeightM = image.HeightM,
                RotationDeg = image.RotationDeg,
            };
        return copy;
    }

    private static double Area((Vec2 Min, Vec2 Max) b) => Math.Max(0, b.Max.X - b.Min.X) * Math.Max(0, b.Max.Y - b.Min.Y);

    /// <summary>True when <paramref name="p"/> belongs to one of <paramref name="mine"/> rather than <paramref name="others"/>.</summary>
    private static bool Owner(Vec2 p, List<List<Vec2>> mine, List<List<Vec2>> others)
    {
        if (mine.Any(f => PlanGeometry.Contains(f, p))) return true;
        if (others.Any(f => PlanGeometry.Contains(f, p))) return false;
        var dMine = mine.Min(f => PlanGeometry.MeanDistanceToEdges(new[] { p }, f));
        var dOther = others.Count == 0 ? double.MaxValue : others.Min(f => PlanGeometry.MeanDistanceToEdges(new[] { p }, f));
        return dMine <= dOther;
    }

    /// <summary>Moves the template so the center of everything on it is the origin.</summary>
    private static void Recenter(ImportTemplate t)
    {
        var pts = t.Floors.SelectMany(f => f.Footprints.SelectMany(x => x)).ToList();
        if (pts.Count == 0)
            pts = t.Floors.SelectMany(f => f.Walls.SelectMany(w => w.Points).Concat(f.Aps.Select(a => a.Position))).ToList();
        if (pts.Count == 0) return;
        var (min, max) = PlanGeometry.Bounds(pts);
        var c = new Vec2((min.X + max.X) / 2, (min.Y + max.Y) / 2);
        foreach (var f in t.Floors)
        {
            foreach (var w in f.Walls) for (var i = 0; i < w.Points.Count; i++) w.Points[i] -= c;
            foreach (var fp in f.Footprints) for (var i = 0; i < fp.Count; i++) fp[i] -= c;
            foreach (var a in f.Aps) a.Position -= c;
            if (f.Image != null) f.Image.Center -= c;
        }
    }

    private static void MarkEarlierImports(List<ImportTemplate> templates)
    {
        foreach (var g in templates.Where(t => t.DcSourceProjectId != null && !t.WasSplit).GroupBy(t => t.DcSourceProjectId))
        {
            var newest = g.OrderByDescending(t => t.CreatedAt ?? DateTime.MinValue).First();
            foreach (var t in g) t.IsEarlierImport = t != newest;
        }
        // A split plan's parts follow their whole.
        foreach (var whole in templates.Where(t => t.IsWhole))
            foreach (var part in templates.Where(t => t.WasSplit && t.GroupId == whole.GroupId))
                part.IsEarlierImport = whole.IsEarlierImport;
    }
}
