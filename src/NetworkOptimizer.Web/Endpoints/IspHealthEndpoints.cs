using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Chart data for the ISP Health tab's ES-module chart, plus the PDF export. The Blazor
/// panel itself reads IspHealthService directly; these exist for JS-fetched series and for
/// a download the browser has to fetch as a file.
/// </summary>
public static class IspHealthEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the whole group carries authorization metadata, which is what
        // architecture test A1 checks. The policy short-circuits when the install has
        // authentication disabled (GlobalRoleHandler).
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        // Full ISP Health report for the selected window as a PDF. from/to mirror the tab's
        // date/time filter: both present computes that window (served from the same
        // custom-window cache the tab uses, so exporting what's on screen is a cache hit),
        // absent exports the live cached report. The rendering is a pure projection of the
        // scored report - the export can never disagree with the tab.
        group.MapGet("/api/monitoring/isp-health/pdf", async (
            DateTime? from,
            DateTime? to,
            string? wan,
            IspHealthRegistry ispHealthRegistry,
            SiteContextService siteContext,
            SiteManagementService siteManagement,
            CancellationToken ct) =>
        {
            // wan (a UniFi wan key) exports a non-primary WAN's report; absent = primary,
            // exactly as before.
            var ispHealth = ispHealthRegistry.GetFor(siteContext.Slug, wan);
            var report = from.HasValue && to.HasValue
                ? await ispHealth.GetReportForWindowAsync(from.Value, to.Value, ct: ct)
                : await ispHealth.GetReportAsync(ct: ct);

            if (report == null)
                return Results.NotFound(new { error = "ISP Health has no report to export yet" });

            // Only a secondary site has a name worth putting in the title; the default site
            // is just "this install" to its operator.
            string? siteName = null;
            if (!siteContext.IsDefault)
            {
                var sites = await siteManagement.GetSitesAsync();
                siteName = sites.FirstOrDefault(s => s.Slug == siteContext.Slug)?.Name ?? siteContext.Slug;
            }

            var pdfBytes = new IspHealthPdfGenerator().GenerateReportBytes(report, siteName);
            var sitePart = siteContext.IsDefault ? "" : $"_{siteContext.Slug}";
            var fileName = $"ISPHealth{sitePart}_{report.WindowEnd.ToLocalTime():yyyyMMdd-HHmm}.pdf";
            return Results.File(pdfBytes, "application/pdf", fileName);
        });

        group.MapGet("/api/monitoring/isp-health/asn-series", async (
            DateTime? from,
            DateTime? to,
            string? wan,
            IspHealthRegistry ispHealthRegistry,
            SiteContextService siteContext,
            CancellationToken ct) =>
        {
            // from/to (the tab's date/time filter) make the chart follow a custom window off
            // the 48 h cache; absent, it serves the cached 48 h report. wan (a UniFi wan key)
            // serves a non-primary WAN's instance; absent = primary, exactly as before.
            var ispHealth = ispHealthRegistry.GetFor(siteContext.Slug, wan);
            var (series, report) = await ispHealth.GetAsnChartDataAsync(from, to, ct);

            // Cap the chart payload only for long windows: bucket toward a target point count,
            // but never finer than per-minute. The floor is the polling cadence, so anything up to
            // ~17 h keeps every sample; past that the buckets widen to hold the line near the
            // target (48 h ~ 2.9 min buckets, 30 days ~ 43 min) rather than shipping ~21k points.
            // Detectors still run on the full-resolution samples; this is display only.
            const int ChartTargetPoints = 1000;
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
                events.AddRange(report.PathShifts.Select(e => (object)(e.IsUnreachable
                    ? new
                    {
                        type = "unreachable",
                        start = e.Time.ToString("o"),
                        end = e.UnreachableEnd?.ToString("o"),
                        label = $"{(string.IsNullOrEmpty(e.AsnName) ? "Transit" : e.AsnName)} unreachable",
                        shared = false
                    }
                    : new
                    {
                        type = "path-shift",
                        start = e.Time.ToString("o"),
                        end = (string?)null,
                        label = $"Path shift {(e.DeltaMs >= 0 ? "+" : "")}{e.DeltaMs:0.#} ms",
                        shared = false
                    })));
            }

            return Results.Ok(new { asns, events });
        });
    }
}
