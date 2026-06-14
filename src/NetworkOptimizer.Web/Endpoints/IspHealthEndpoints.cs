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
            IspHealthService ispHealth,
            CancellationToken ct) =>
        {
            var (series, report) = await ispHealth.GetAsnChartDataAsync(ct);

            var asnBuckets = series.Select(s => new
            {
                asn = s.AsnNumber,
                name = string.IsNullOrEmpty(s.AsnName) ? $"AS{s.AsnNumber}" : s.AsnName,
                buckets = s.Samples
                    .Where(p => p.RttAvgMs.HasValue)
                    .GroupBy(p => new DateTime(p.Time.Ticks - p.Time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc))
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
