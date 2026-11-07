using System.Globalization;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
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

    /// <summary>
    /// Runs Path Localization's degradation trigger over every exported target, all fed in time order as
    /// the episode watcher feeds them, and prints each degraded run with its RTT, loss, and jitter against
    /// baseline, and whether it reads as a route change: <see cref="PathShiftMatch.IsStep"/>, and no other
    /// target queued while it ran (<see cref="TargetDegradation.QueuedSince"/>). Congestion exports should
    /// print congestion; shift exports, steps.
    /// </summary>
    [Fact]
    public void Replay_path_degradation_step_or_congestion()
    {
        if (ReplayDir == null) return;
        var options = new IspHealthOptions();
        foreach (var file in Directory.GetFiles(ReplayDir, "*.csv"))
        {
            var allSeries = LoadSeries(file);
            if (allSeries.Count == 0 || allSeries.All(s => s.Samples.All(x => x.JitterMs == null))) continue;
            _output.WriteLine($"=== {Path.GetFileName(file)} ===");
            var trackers = allSeries.ToDictionary(s => s.TargetIds[0], s => new TargetDegradation(s.TargetIds[0], options));
            var open = new Dictionary<string, (DateTime Since, double? BaseRtt, double? BaseJitter)>();
            void Report(string id, DateTime end)
            {
                var t = trackers[id];
                var (since, baseRtt, baseJitter) = open[id];
                var levels = new EpisodeTargetLevels(id, baseRtt, t.PeakRttMs, t.PeakLossPct, baseJitter, t.JitterP95Since(since));
                var step = PathShiftMatch.IsStep(levels, options);
                var queued = trackers.Any(o => o.Key != id && o.Value.QueuedSince(since));
                _output.WriteLine(
                    $"  {id} {since:MM-dd HH:mm} -> {end:HH:mm} rtt {baseRtt:0.#} -> {t.PeakRttMs:0.#} ms, " +
                    $"jitter {baseJitter:0.##} -> p95 {t.JitterP95Since(since):0.##} ms, loss {t.PeakLossPct:0.#}%" +
                    $"{(queued ? ", another target queued" : "")} => {(step && !queued ? "STEP" : "congestion")}");
                open.Remove(id);
            }
            var samples = allSeries.SelectMany(s => s.Samples.Select(x => (Id: s.TargetIds[0], Sample: x))).OrderBy(x => x.Sample.Time);
            foreach (var (id, s) in samples)
            {
                var tracker = trackers[id];
                // Pooled loss reads the same at any probe count; 100 per point keeps the probe floor met.
                var received = 100 - (int)Math.Round(s.LossPercent ?? 0);
                tracker.Record(s.Time, s.RttAvgMs, 100, received, () => tracker.Degraded, s.JitterMs);
                if (tracker.Degraded && !open.ContainsKey(id))
                    open[id] = (tracker.DegradedSince!.Value, tracker.BaselineRttMs, tracker.BaselineJitterMs);
                else if (!tracker.Degraded && open.ContainsKey(id))
                    Report(id, s.Time);
            }
            foreach (var id in open.Keys.ToList()) Report(id, allSeries.Max(s => s.Samples[^1].Time));
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
