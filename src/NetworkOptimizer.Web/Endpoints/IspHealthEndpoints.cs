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

            var asns = series.Select(s => new
            {
                asn = s.AsnNumber,
                name = string.IsNullOrEmpty(s.AsnName) ? $"AS{s.AsnNumber}" : s.AsnName,
                points = s.Samples
                    .Where(p => p.RttAvgMs.HasValue)
                    .GroupBy(p => new DateTime(p.Time.Ticks - p.Time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc))
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        time = g.Key.ToString("o"),
                        value = Math.Round(g.Average(p => p.RttAvgMs!.Value), 2)
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
