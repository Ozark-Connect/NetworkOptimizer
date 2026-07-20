using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;
using Xunit.Abstractions;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Isolates the jitter-scoring lever (P95 -> P90 -> P50) over real exported latency data, to see
/// where a lower percentile's relief lands. Reuses <see cref="RealDataReplayTests.LoadSeries"/> and
/// the exact floor-relative jitter curve from IspHealthScorer.ScoreJitterVsFloor. Per target it
/// reports the raw jitter percentile and the resulting 0-100 jitter sub-score; the path jitter floor
/// tracks the chosen percentile (as ComputeJitterFloor does in production), so numerator and floor
/// move together. Gated on ISP_HEALTH_REPLAY_DIR - a no-op in CI and on other machines.
/// </summary>
public class JitterPercentileReplayTests
{
    private readonly ITestOutputHelper _output;

    public JitterPercentileReplayTests(ITestOutputHelper output) => _output = output;

    private static string? ReplayDir => Environment.GetEnvironmentVariable("ISP_HEALTH_REPLAY_DIR");

    // Neutral (no per-tech band) floor-relative jitter curve, verbatim from IspHealthScorer.ScoreJitterVsFloor.
    private static readonly IspHealthOptions Opt = new();

    private static double JitterScore(double jitterMs, double floorMs)
    {
        var f = Math.Clamp(floorMs, Opt.JitterFloorMinMs, Opt.JitterFloorMaxMs);
        return ScoreCurve.Interpolate(jitterMs,
            (f, 100), (1.25 * f, 96), (1.5 * f, 91), (2.0 * f, 70), (5.0, 22), (12.0, 0));
    }

    // Matches SeriesStats.Percentile (linear, rank = p*(n-1)); inlined so the test needs no internals.
    private static double Percentile(IReadOnlyList<double> sortedAsc, double p)
    {
        var rank = p * (sortedAsc.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return lo == hi ? sortedAsc[lo] : sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * (rank - lo);
    }

    private static string Band(double score) =>
        score >= 90 ? "Excellent" : score >= 75 ? "Good" : score >= 60 ? "Fair" : "Poor";

    private static string Signed(double d) => (d >= 0 ? "+" : "-") + Math.Abs(d).ToString("0.0");

    [Fact]
    public void Replay_jitter_percentile_comparison()
    {
        if (ReplayDir == null) return;
        foreach (var file in Directory.GetFiles(ReplayDir, "*.csv", SearchOption.AllDirectories).OrderBy(f => f))
        {
            var header = File.ReadLines(file).FirstOrDefault(l => l.Contains("_time") && l.Contains("target_id"));
            if (header == null || !header.Contains("jitter_ms")) continue; // only pivoted exports carry jitter

            var series = RealDataReplayTests.LoadSeries(file);
            var targets = series
                .Select(s => (s.AsnName, Jit: s.Samples
                    .Select(x => x.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value)
                    .OrderBy(v => v).ToList()))
                .Where(t => t.Jit.Count >= 20)
                .Select(t => (t.AsnName, t.Jit, P50: Percentile(t.Jit, 0.50), P90: Percentile(t.Jit, 0.90), P95: Percentile(t.Jit, 0.95)))
                .ToList();
            if (targets.Count == 0) continue;

            // Floor tracks the percentile, exactly as ComputeJitterFloor -> ScoringJitterOf does.
            double floor95 = targets.Min(t => t.P95), floor90 = targets.Min(t => t.P90), floor50 = targets.Min(t => t.P50);

            var rows = targets.Select(t => new
            {
                t.AsnName, t.Jit.Count, t.P50, t.P90, t.P95,
                S95 = JitterScore(t.P95, floor95),
                S90 = JitterScore(t.P90, floor90),
                S50 = JitterScore(t.P50, floor50)
            }).OrderByDescending(r => r.S90 - r.S95).ToList();

            _output.WriteLine($"=== {Path.GetFileName(file)}  ({rows.Count} targets w/ jitter) ===");
            _output.WriteLine($"Path jitter floor:  P95={floor95:0.00}ms  P90={floor90:0.00}ms  P50={floor50:0.00}ms");
            _output.WriteLine($"{"target",-30} {"n",5} {"P50",6} {"P90",6} {"P95",6} | {"jScore95",8} {"jScore90",8} {"jScore50",8} | {"dP90",6} {"dP50",6}");
            foreach (var r in rows)
            {
                _output.WriteLine(
                    $"{Trunc(r.AsnName, 30),-30} {r.Count,5} {r.P50,6:0.00} {r.P90,6:0.00} {r.P95,6:0.00} | " +
                    $"{r.S95,8:0.0} {r.S90,8:0.0} {r.S50,8:0.0} | {Signed(r.S90 - r.S95),6} {Signed(r.S50 - r.S95),6}");
            }

            int up90 = rows.Count(r => Band(r.S90) != Band(r.S95) && r.S90 > r.S95);
            int down90 = rows.Count(r => Band(r.S90) != Band(r.S95) && r.S90 < r.S95);
            int up50 = rows.Count(r => Band(r.S50) != Band(r.S95) && r.S50 > r.S95);
            int down50 = rows.Count(r => Band(r.S50) != Band(r.S95) && r.S50 < r.S95);
            _output.WriteLine($"Mean jitter score:  P95={rows.Average(r => r.S95):0.0}  P90={rows.Average(r => r.S90):0.0}  P50={rows.Average(r => r.S50):0.0}");
            _output.WriteLine($"Band shifts vs P95 (Excellent>=90/Good>=75/Fair>=60/Poor):  P90: +{up90} / -{down90}   P50: +{up50} / -{down50}");
            _output.WriteLine($"Biggest P90 mover: {rows.First().AsnName} {rows.First().S95:0.0} -> {rows.First().S90:0.0}  (P95 jit {rows.First().P95:0.00} -> P90 {rows.First().P90:0.00} ms)");
            _output.WriteLine($"Jitter is AsnJitterWeight={Opt.AsnJitterWeight:0.##} of the ASN quality blend, so +D jitter pts => ~+{Opt.AsnJitterWeight:0.##}*D on the grade (below the reach ceiling).");
            _output.WriteLine("");
        }
    }

    private static string Trunc(string? s, int n) => (s ?? "") is { } v && v.Length > n ? v[..n] : s ?? "";
}
