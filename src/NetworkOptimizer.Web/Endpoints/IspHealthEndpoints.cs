using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Chart data for the ISP Health tab's ES-module chart. The Blazor panel itself
/// reads IspHealthService directly; this exists only for JS-fetched series.
/// </summary>
public static class IspHealthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/isp-health/asn-series", async (
            DateTime? from,
            DateTime? to,
            IspHealthService ispHealth,
            CancellationToken ct) =>
        {
            // from/to (the tab's date/time filter) make the chart follow a custom window off
            // the 48 h cache; absent, it serves the cached 48 h report.
            var (series, report) = await ispHealth.GetAsnChartDataAsync(from, to, ct);

            // Cap the chart payload only for long windows: bucket toward a target point count,
            // but never finer than per-minute. Anything <= ~50 h stays at the prior per-minute
            // density (48 h ~ 2880 points/line); a 30-day view coarsens to ~3000 instead of ~21k.
            // Detectors still run on the full-resolution samples; this is display only.
            const int ChartTargetPoints = 3000;
            var spanTicks = from.HasValue && to.HasValue ? (to.Value - from.Value).Ticks
                : report != null ? (report.WindowEnd - report.WindowStart).Ticks
                : TimeSpan.TicksPerDay * 2;
            var bucketTicks = Math.Max(TimeSpan.TicksPerMinute, spanTicks / ChartTargetPoints);

            var asnBuckets = series.Select(s => new
            {
                asn = s.AsnNumber,
                name = string.IsNullOrEmpty(s.AsnName) ? $"AS{s.AsnNumber}" : s.AsnName,
                buckets = s.Samples
                    .Where(p => p.RttAvgMs.HasValue)
                    .GroupBy(p => new DateTime(p.Time.Ticks - p.Time.Ticks % bucketTicks, DateTimeKind.Utc))
                    .ToDictionary(g => g.Key, g => Math.Round(g.Average(p => p.RttAvgMs!.Value), 2))
            }).ToList();

            var allTimes = asnBuckets
                .SelectMany(a => a.buckets.Keys)
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            var asns = asnBuckets.Select(a => new
            {
                a.asn,
                a.name,
                points = allTimes.Select(t => new
                {
                    time = t.ToString("o"),
                    value = a.buckets.TryGetValue(t, out var v) ? (double?)v : null
                })
            });

            var events = new List<object>();
            if (report != null)
            {
                events.AddRange(report.CongestionEvents.Select(e => (object)new
                {
                    type = "congestion",
                    start = e.Start.ToString("o"),
                    end = e.End.ToString("o"),
                    label = e.IsShared ? "Shared congestion" : "Congestion",
                    shared = e.IsShared
                }));
                events.AddRange(report.PathShifts.Select(e => (object)new
                {
                    type = "path-shift",
                    start = e.Time.ToString("o"),
                    end = (string?)null,
                    label = $"Path shift {(e.DeltaMs >= 0 ? "+" : "")}{e.DeltaMs:0.#} ms",
                    shared = false
                }));
            }

            return Results.Ok(new { asns, events });
        });
    }
}
