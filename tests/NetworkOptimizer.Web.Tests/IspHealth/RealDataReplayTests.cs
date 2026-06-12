using System.Globalization;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;
using Xunit.Abstractions;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Replay harness for tuning the congestion and step-change detectors against real
/// exported InfluxDB latency data. The exports stay outside the repo; point the
/// ISP_HEALTH_REPLAY_DIR environment variable at a directory of CSV files exported
/// with columns _time, target_id, target_type, jitter_ms, loss_percent, rtt_avg_ms,
/// rtt_max_ms (Flux pivot output). Without the variable the tests no-op, so CI and
/// other machines are unaffected.
/// </summary>
public class RealDataReplayTests
{
    private readonly ITestOutputHelper _output;

    public RealDataReplayTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string? ReplayDir => Environment.GetEnvironmentVariable("ISP_HEALTH_REPLAY_DIR");

    [Fact]
    public void Replay_step_change_detection()
    {
        if (ReplayDir == null) return;
        foreach (var file in Directory.GetFiles(ReplayDir, "shift-*.csv"))
        {
            _output.WriteLine($"=== {Path.GetFileName(file)} ===");
            var allSeries = LoadSeries(file);
            var events = StepChangeDetector.Detect(allSeries, new IspHealthOptions());
            Print(events, "default");
            events = StepChangeDetector.Detect(allSeries, TunedOptions());
            Print(events, "tuned");
        }
    }

    [Fact]
    public void Replay_congestion_detection()
    {
        if (ReplayDir == null) return;
        foreach (var file in Directory.GetFiles(ReplayDir, "congestion-*.csv"))
        {
            _output.WriteLine($"=== {Path.GetFileName(file)} ===");
            var allSeries = LoadSeries(file);
            foreach (var label in new[] { "default", "tuned" })
            {
                var options = label == "default" ? new IspHealthOptions() : TunedOptions();
                var events = CongestionDetector.Detect(allSeries, options);
                _output.WriteLine($"-- {label}: {events.Count} events");
                foreach (var e in events)
                {
                    _output.WriteLine(
                        $"  {e.Start:MM-dd HH:mm} -> {e.End:HH:mm} ({e.Duration.TotalMinutes:0} min) " +
                        $"{(e.IsShared ? "SHARED " : "")}{string.Join("+", e.AsnNames)} " +
                        $"rtt {e.BaselineRttMs:0.#} -> {e.PeakRttMs:0.#} ms, jitter {e.BaselineJitterMs:0.#} -> {e.PeakJitterMs:0.#} ms");
                }
            }
        }
    }

    /// <summary>Candidate tuning to compare against defaults during replay.</summary>
    private static IspHealthOptions TunedOptions() => new()
    {
        StepMinDeltaMs = 2.0,
        StepMinRelativeChange = 0.15,
        CongestionRttMinDeltaMs = 2.0
    };

    private void Print(List<PathShiftEvent> events, string label)
    {
        _output.WriteLine($"-- {label}: {events.Count} events");
        foreach (var e in events)
        {
            _output.WriteLine(
                $"  {e.Time:MM-dd HH:mm} {e.AsnName ?? e.TargetId} {e.BeforeMedianMs:0.##} -> {e.AfterMedianMs:0.##} ms " +
                $"({(e.DeltaMs >= 0 ? "+" : "")}{e.DeltaMs:0.##}) correlated={e.CorrelatedTargetCount}");
        }
    }

    /// <summary>Parses Flux pivot CSV into one AsnSeries per target.</summary>
    internal static List<AsnSeries> LoadSeries(string csvPath)
    {
        var byTarget = new Dictionary<string, List<LatencySample>>();
        int timeIdx = -1, targetIdx = -1, jitterIdx = -1, lossIdx = -1, rttIdx = -1, rttMaxIdx = -1;

        foreach (var line in File.ReadLines(csvPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split(',');
            if (line.Contains("_time") && line.Contains("target_id"))
            {
                timeIdx = Array.IndexOf(cols, "_time");
                targetIdx = Array.IndexOf(cols, "target_id");
                jitterIdx = Array.IndexOf(cols, "jitter_ms");
                lossIdx = Array.IndexOf(cols, "loss_percent");
                rttIdx = Array.IndexOf(cols, "rtt_avg_ms");
                rttMaxIdx = Array.IndexOf(cols, "rtt_max_ms");
                continue;
            }
            if (timeIdx < 0 || cols.Length <= Math.Max(rttIdx, rttMaxIdx)) continue;
            if (!DateTime.TryParse(cols[timeIdx], CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)) continue;

            var target = cols[targetIdx];
            if (!byTarget.TryGetValue(target, out var list))
            {
                list = new List<LatencySample>();
                byTarget[target] = list;
            }
            list.Add(new LatencySample(time,
                ParseDouble(cols, rttIdx), ParseDouble(cols, rttMaxIdx),
                ParseDouble(cols, jitterIdx), ParseDouble(cols, lossIdx)));
        }

        var asn = 64500;
        return byTarget
            .OrderBy(kv => kv.Key)
            .Select(kv => new AsnSeries
            {
                AsnNumber = asn++,
                AsnName = kv.Key,
                TargetIds = { kv.Key },
                Samples = kv.Value.OrderBy(s => s.Time).ToList()
            })
            .ToList();
    }

    private static double? ParseDouble(string[] cols, int idx)
    {
        if (idx < 0 || idx >= cols.Length) return null;
        return double.TryParse(cols[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
