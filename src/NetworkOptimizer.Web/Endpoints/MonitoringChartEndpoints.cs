using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

public static class MonitoringChartEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/live-stats", async (
            MonitoringLiveStats liveStats,
            UniFiConnectionService connectionService,
            CancellationToken ct) =>
        {
            string? gatewayMac = null;
            List<string>? wanIfNames = null;
            try
            {
                var devices = await connectionService.GetDiscoveredDevicesAsync(ct);
                var gw = devices?.FirstOrDefault(d =>
                    d.Type == DeviceType.Gateway || d.HardwareType == DeviceType.Gateway);
                gatewayMac = gw?.Mac?.Replace("-", ":").ToLowerInvariant();
                wanIfNames = gw?.WanInterfaceNames;
            }
            catch { }

            double wanDown = 0, wanUp = 0;
            DateTime? sampleTime = null;
            if (gatewayMac != null && wanIfNames != null)
            {
                foreach (var ifName in wanIfNames)
                {
                    var rate = liveStats.GetPortRate(gatewayMac, ifName);
                    if (rate == null) continue;
                    wanDown += rate.UpBps;
                    wanUp += rate.DownBps;
                    if (sampleTime == null || rate.LastUpdate > sampleTime)
                        sampleTime = rate.LastUpdate;
                }
            }

            var targets = await liveStats.GetIspTransitTargetsAsync(ct);

            var ispRtts = new List<double>();
            var ispLosses = new List<double>();
            var transitRtts = new List<double>();
            var transitLosses = new List<double>();

            foreach (var t in targets)
            {
                var st = liveStats.GetTargetStats(t.TargetId);
                if (st == null) continue;

                if (t.TargetType == MonitoringTargetType.AccessIsp)
                {
                    if (st.RttAvgMs != null) ispRtts.Add(st.RttAvgMs.Value);
                    ispLosses.Add(st.LossPercent);
                }
                else
                {
                    if (st.RttAvgMs != null) transitRtts.Add(st.RttAvgMs.Value);
                    transitLosses.Add(st.LossPercent);
                }
            }

            var ispRtt = ispRtts.Count > 0 ? ispRtts.Average() : (double?)null;
            var ispLoss = ispLosses.Count > 0 ? ispLosses.Average() : 0.0;
            var transitRtt = transitRtts.Count > 0 ? transitRtts.Average() : (double?)null;
            var transitLoss = transitLosses.Count > 0 ? transitLosses.Average() : 0.0;

            double? meanRtt = null;
            if (ispRtt != null && transitRtt != null)
                meanRtt = (ispRtt.Value + transitRtt.Value) / 2;
            else meanRtt = ispRtt ?? transitRtt;

            double meanLoss = 0;
            if (ispRtt != null && transitRtt != null)
                meanLoss = (ispLoss + transitLoss) / 2;
            else if (ispRtt != null) meanLoss = ispLoss;
            else if (transitRtts.Count > 0) meanLoss = transitLoss;

            return Results.Ok(new
            {
                downloadBps = wanDown,
                uploadBps = wanUp,
                rttMs = meanRtt,
                lossPercent = meanLoss,
                // SNMP sample timestamp (max LastUpdate across WAN ports) so the
                // live chart can dedupe polls that land on the same sample.
                sampleTime = sampleTime?.ToString("o"),
            });
        });

        app.MapGet("/api/monitoring/wan-live-chart-data", async (
            MonitoringInfluxClient influx,
            MonitoringLiveStats liveStats,
            UniFiConnectionService connectionService,
            DateTime? from,
            DateTime? to,
            CancellationToken ct) =>
        {
            DateTime queryFrom, queryTo;
            if (from.HasValue && to.HasValue)
            {
                queryFrom = from.Value.ToUniversalTime();
                queryTo = to.Value.ToUniversalTime();
            }
            else
            {
                queryTo = DateTime.UtcNow;
                queryFrom = queryTo.AddMinutes(-5);
            }

            string? gatewayMac = null;
            List<string>? wanIfNames = null;
            try
            {
                var devices = await connectionService.GetDiscoveredDevicesAsync(ct);
                var gw = devices?.FirstOrDefault(d =>
                    d.Type == DeviceType.Gateway || d.HardwareType == DeviceType.Gateway);
                gatewayMac = gw?.Mac;
                wanIfNames = gw?.WanInterfaceNames;
            }
            catch { }

            var wanTask = !string.IsNullOrEmpty(gatewayMac) && wanIfNames?.Count > 0
                ? influx.QueryGatewayWanRatesAsync(gatewayMac, wanIfNames, queryFrom, queryTo, ct: ct)
                : Task.FromResult<IReadOnlyList<MonitoringInfluxClient.WanRatePoint>>(Array.Empty<MonitoringInfluxClient.WanRatePoint>());
            var targets = await liveStats.GetIspTransitTargetsAsync(ct);
            var targetIds = targets.Select(t => t.TargetId).ToList();
            var rttTask = influx.QueryMeanIspTransitLatencyAsync(queryFrom, queryTo, targetIds, ct: ct);

            await Task.WhenAll(wanTask, rttTask);

            var wanData = await wanTask;
            var rttData = await rttTask;

            var rttByTime = new Dictionary<long, MonitoringInfluxClient.LatencyPoint>();
            foreach (var p in rttData)
            {
                var bucket = p.Time.Ticks / (TimeSpan.TicksPerSecond * 5) * (TimeSpan.TicksPerSecond * 5);
                rttByTime[bucket] = p;
            }
            MonitoringInfluxClient.LatencyPoint? lastRtt = null;

            var points = wanData.Select(w =>
            {
                var bucket = w.Time.Ticks / (TimeSpan.TicksPerSecond * 5) * (TimeSpan.TicksPerSecond * 5);
                if (rttByTime.TryGetValue(bucket, out var rtt)) lastRtt = rtt;

                return new
                {
                    time = w.Time.ToString("o"),
                    downloadBps = w.DownloadBps,
                    uploadBps = w.UploadBps,
                    rttMs = lastRtt?.RttAvgMs,
                    lossPercent = lastRtt?.LossPercent,
                };
            });

            return Results.Ok(new { points });
        });

        app.MapGet("/api/monitoring/chart-data", async (
            MonitoringInfluxClient influx,
            IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
            string? category,
            int? rangeHours,
            DateTime? from,
            DateTime? to,
            CancellationToken ct) =>
        {
            var targetType = category switch
            {
                "AccessIsp" => MonitoringTargetType.AccessIsp,
                "Transit" => MonitoringTargetType.Transit,
                "InternetService" => MonitoringTargetType.InternetService,
                _ => MonitoringTargetType.Fabric
            };
            DateTime queryFrom, queryTo;
            if (from.HasValue && to.HasValue)
            {
                queryFrom = from.Value.ToUniversalTime();
                queryTo = to.Value.ToUniversalTime();
            }
            else
            {
                var hours = rangeHours ?? 1;
                queryTo = DateTime.UtcNow;
                queryFrom = hours == 0 ? queryTo.AddMinutes(-15) : queryTo.AddHours(-hours);
            }

            // Target names come from SQLite; time-series data from InfluxDB via
            // the target_type tag (indexed, ~10ms) instead of contains() on
            // target_id set (full scan, ~400ms+).
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == targetType && t.Enabled)
                .OrderBy(t => t.Name)
                .Select(t => new { t.TargetId, t.Name })
                .ToListAsync(ct);

            if (targets.Count == 0)
                return Results.Ok(new { targets = Array.Empty<object>() });

            var targetLookup = targets.ToDictionary(t => t.TargetId, t => t.Name);
            var data = await influx.QueryLatencyByTargetTypeAsync(targetType, queryFrom, queryTo, ct: ct);

            var result = targets.Select(t =>
            {
                data.TryGetValue(t.TargetId, out var points);
                var pts = points ?? new List<MonitoringInfluxClient.LatencyPoint>();
                return new
                {
                    targetId = t.TargetId,
                    name = t.Name,
                    rtt = pts.Select(p => new { time = p.Time.ToString("o"), value = p.RttAvgMs }),
                    loss = pts.Select(p => new { time = p.Time.ToString("o"), value = p.LossPercent }),
                };
            });

            return Results.Ok(new { targets = result });
        });

        app.MapGet("/api/monitoring/wan-rate-chart", async (
            MonitoringInfluxClient influx,
            UniFiConnectionService connectionService,
            int? rangeHours,
            DateTime? from,
            DateTime? to,
            CancellationToken ct) =>
        {
            DateTime queryFrom, queryTo;
            if (from.HasValue && to.HasValue)
            {
                queryFrom = from.Value.ToUniversalTime();
                queryTo = to.Value.ToUniversalTime();
            }
            else
            {
                var hours = rangeHours ?? 1;
                queryTo = DateTime.UtcNow;
                queryFrom = hours == 0 ? queryTo.AddMinutes(-15) : queryTo.AddHours(-hours);
            }

            string? gatewayMac = null;
            List<string>? wanIfNames = null;
            try
            {
                var devices = await connectionService.GetDiscoveredDevicesAsync(ct);
                var gw = devices?.FirstOrDefault(d =>
                    d.Type == NetworkOptimizer.Core.Enums.DeviceType.Gateway
                    || d.HardwareType == NetworkOptimizer.Core.Enums.DeviceType.Gateway);
                gatewayMac = gw?.Mac;
                wanIfNames = gw?.WanInterfaceNames;
            }
            catch { }

            if (string.IsNullOrEmpty(gatewayMac) || wanIfNames == null || wanIfNames.Count == 0)
                return Results.Ok(new { download = Array.Empty<object>(), upload = Array.Empty<object>() });

            var data = await influx.QueryGatewayWanRatesAsync(gatewayMac, wanIfNames, queryFrom, queryTo, ct: ct);

            return Results.Ok(new
            {
                download = data.Select(p => new { time = p.Time.ToString("o"), value = p.DownloadBps }),
                upload = data.Select(p => new { time = p.Time.ToString("o"), value = p.UploadBps })
            });
        });
    }
}
