using System.IO.Compression;
using System.Text.Json;

namespace NetworkOptimizer.Web.Services.InnerSpace;

/// <summary>A window or door measured by the scan, in the plan's meter frame (InnerSpace shape frame / ppm).</summary>
public sealed record ScannedOpening(Vec2 Center, double WidthM, double SillM, double HeadM, bool IsDoor);

/// <summary>
/// Reads the windows and doors InnerSpace drops from a LiDAR scan. They exist only in the RoomPlan
/// capture inside the scan's zip; its frame maps to the plan through the glTF model's AlignNode.
/// </summary>
public static class RoomPlanWindows
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Openings from <c>scan.zip</c> and <c>scan.gltf</c>. Returns empty when either is missing or
    /// unreadable: scanned openings are a bonus, never a reason to fail an import.
    /// </summary>
    public static List<ScannedOpening> Read(byte[]? zip, string? gltfJson) => ReadScan(zip, gltfJson).Openings;

    /// <summary>The scan's openings and its story (floor level), as far as the archive holds them.</summary>
    public static ScanInfo ReadScan(byte[]? zip, string? gltfJson)
    {
        var align = AlignMatrix(gltfJson);
        var capture = Capture(zip);
        if (capture == null) return new ScanInfo(new(), null);
        return new ScanInfo(align == null ? new() : FromCapture(capture, align), capture.Story);
    }

    /// <summary>Openings from an already parsed capture, mapped through a column-major AlignNode matrix.</summary>
    public static List<ScannedOpening> FromCapture(RoomPlanCapture capture, IReadOnlyList<double> align)
    {
        var floorY = capture.Floors.FirstOrDefault(f => f.Transform.Count == 16)?.Transform[13]
            ?? capture.Walls.Where(w => w.Transform.Count == 16 && w.Dimensions.Count >= 2)
                .Select(w => w.Transform[13] - w.Dimensions[1] / 2).DefaultIfEmpty(0).Min();

        var result = new List<ScannedOpening>();
        void Add(IEnumerable<RoomPlanSurface> items, bool isDoor)
        {
            foreach (var w in items)
            {
                if (w.Transform.Count != 16 || w.Dimensions.Count < 2) continue;
                var (x, z) = Apply(align, w.Transform[12], w.Transform[14]);
                var h = w.Dimensions[1];
                var cy = w.Transform[13] - floorY;
                result.Add(new ScannedOpening(new Vec2(x, -z), w.Dimensions[0],
                    Math.Max(0, cy - h / 2), cy + h / 2, isDoor));
            }
        }
        Add(capture.Windows, false);
        Add(capture.Doors, true);
        return result;
    }

    private static (double X, double Z) Apply(IReadOnlyList<double> m, double x, double z) =>
        (m[0] * x + m[8] * z + m[12], m[2] * x + m[10] * z + m[14]);

    private static IReadOnlyList<double>? AlignMatrix(string? gltfJson)
    {
        if (string.IsNullOrEmpty(gltfJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(gltfJson);
            if (!doc.RootElement.TryGetProperty("nodes", out var nodes)) return null;
            foreach (var n in nodes.EnumerateArray())
            {
                if (n.TryGetProperty("name", out var name) && name.GetString() == "AlignNode"
                    && n.TryGetProperty("matrix", out var m) && m.GetArrayLength() == 16)
                    return m.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            }
            // No AlignNode: the capture frame is already the plan frame.
            return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RoomPlanCapture? Capture(byte[]? zip)
    {
        if (zip == null || zip.Length == 0) return null;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("draft.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Length > 20_000_000) return null;
            using var s = entry.Open();
            return JsonSerializer.Deserialize<RoomPlanCapture>(s, Opts);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return null;
        }
    }
}
