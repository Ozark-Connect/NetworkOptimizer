using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Endpoints;

public static class MonitoringChartEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the whole group carries authorization metadata, which is what
        // architecture test A1 checks. The policy short-circuits when the install has
        // authentication disabled (GlobalRoleHandler).
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        group.MapGet("/api/monitoring/live-stats", async (
            MonitoringLiveStats liveStats,
            UniFiConnectionService connectionService,
            NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDb,
            SiteContextService siteContext,
            string? wan,
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

            // The live tick has to answer for the same WAN the caller is charting. Without this it
            // served the primary's counters to every caller, so a chart backfilled with one WAN's
            // history then grew a live edge of the primary's traffic - the two halves of the same
            // line describing different connections. Absent means the primary, exactly as before.
            if (!string.IsNullOrEmpty(wan))
            {
                var group2 = NetworkOptimizer.UniFi.GatewayWanHelper.WanNetworkGroupFromKey(wan);
                string? scopedCounter = null;
                try
                {
                    scopedCounter = (await connectionService.GetWanInterfacesForGroupAsync(group2, ct))?.CounterIfName;
                }
                catch { }
                if (string.IsNullOrEmpty(scopedCounter))
                {
                    try
                    {
                        await using var db = siteDb.CreateForSite(siteContext.Slug, siteContext.IsDefault);
                        var profile = await db.WanProfiles.AsNoTracking()
                            .FirstOrDefaultAsync(w => w.WanNetworkgroup == group2, ct);
                        scopedCounter = profile?.CounterInterface;
                        if (string.IsNullOrEmpty(gatewayMac) && profile?.GatewayMac != null)
                            gatewayMac = profile.GatewayMac.Replace("-", ":").ToLowerInvariant();
                    }
                    catch { }
                }
                // Empty rather than the primary's: a WAN with no recorded counter has no live
                // answer, and borrowing one would draw another WAN's traffic under its name.
                wanIfNames = string.IsNullOrEmpty(scopedCounter)
                    ? new List<string>()
                    : new List<string> { scopedCounter! };
            }

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

            // Scoped to the same WAN as the rates above, or the chart's RTT and loss lines would
            // be the site's while its throughput was one WAN's - and a WAN with no targets of its
            // own would show the primary's latency as if it were its own.
            var isPrimaryWan = string.IsNullOrEmpty(wan)
                || string.Equals(NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(wan!),
                    NetworkOptimizer.UniFi.GatewayWanHelper.DefaultWanKey, StringComparison.OrdinalIgnoreCase);
            var (meanRtt, meanLoss) = await liveStats.GetMeanIspTransitLiveAsync(ct, wan, isPrimaryWan);

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

        group.MapGet("/api/monitoring/wan-live-chart-data", async (
            MonitoringInfluxClient influx,
            MonitoringLiveStats liveStats,
            UniFiConnectionService connectionService,
            NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDb,
            SiteContextService siteContext,
            ILoggerFactory loggerFactory,
            DateTime? from,
            DateTime? to,
            string? wan,
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

            // The stored rates are keyed on gateway MAC + counter interface, and both normally come
            // from the console - so an offline site skipped the query entirely and read as having no
            // history, when the history is sitting in InfluxDB. Fall back to the remembered WAN
            // profile, which records exactly that pair.
            //
            // ONLY while the console is down, so nothing about the online path changes: a connected
            // console that returns no gateway still yields an empty series as before, and eth0,
            // eth6.100 and ppp0 keep resolving live. CounterInterface, not the data path - a VLAN
            // sub-interface's counters double, which is why the two are stored apart.
            // A named WAN replaces the primary-only list with THAT WAN's counter interface: live
            // from the console, else the WAN's own remembered profile. Never a fallback to another
            // WAN - an empty series is the honest answer for a WAN nothing has recorded, where
            // borrowing the primary's would draw someone else's traffic under this WAN's name.
            if (!string.IsNullOrEmpty(wan))
            {
                var group = NetworkOptimizer.UniFi.GatewayWanHelper.WanNetworkGroupFromKey(wan);
                string? scopedCounter = null;
                try
                {
                    var ifaces = await connectionService.GetWanInterfacesForGroupAsync(group, ct);
                    scopedCounter = ifaces?.CounterIfName;
                }
                catch { }
                try
                {
                    await using var db = siteDb.CreateForSite(siteContext.Slug, siteContext.IsDefault);
                    var profile = await db.WanProfiles.AsNoTracking()
                        .FirstOrDefaultAsync(w => w.WanNetworkgroup == group, ct);
                    scopedCounter ??= profile?.CounterInterface;
                    if (string.IsNullOrEmpty(gatewayMac) && profile?.GatewayMac != null)
                        gatewayMac = profile.GatewayMac.Replace("-", ":").ToLowerInvariant();
                }
                catch { }
                wanIfNames = string.IsNullOrEmpty(scopedCounter)
                    ? new List<string>()
                    : new List<string> { scopedCounter! };
            }
            else if ((string.IsNullOrEmpty(gatewayMac) || wanIfNames is not { Count: > 0 })
                && !connectionService.IsConnected)
            {
                try
                {
                    await using var db = siteDb.CreateForSite(siteContext.Slug, siteContext.IsDefault);
                    var profile = await db.WanProfiles.AsNoTracking()
                        .Where(w => w.GatewayMac != null && w.CounterInterface != null)
                        .OrderBy(w => w.WanNetworkgroup)
                        .ThenByDescending(w => w.UpdatedAt)
                        .FirstOrDefaultAsync(ct);
                    if (profile != null)
                    {
                        // Assign rather than ??=: the entry condition allows an EMPTY string, which
                        // ??= would keep. Normalized to match the other reader and the writer.
                        gatewayMac = profile.GatewayMac!.Replace("-", ":").ToLowerInvariant();
                        wanIfNames = new List<string> { profile.CounterInterface! };
                    }
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger("MonitoringChartEndpoints").LogDebug(ex,
                        "Could not read the remembered WAN profile; the chart falls back to an empty series");
                }
            }

            // Bucket to the site's own sample interval. The chart is only as fine as the data
            // behind it, and averaging several samples into one bucket throws away resolution a
            // faster-polling site is paying for. Read from this site's settings, not assumed.
            var sampleIntervalSeconds = 5;
            try
            {
                await using var sdb = siteDb.CreateForSite(siteContext.Slug, siteContext.IsDefault);
                var ms = await sdb.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
                if (ms != null) sampleIntervalSeconds = Math.Max(1, ms.FastPollIntervalSeconds);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("MonitoringChartEndpoints").LogDebug(ex,
                    "Could not read the sample interval; the chart buckets at the default");
            }

            var wanTask = !string.IsNullOrEmpty(gatewayMac) && wanIfNames?.Count > 0
                ? influx.QueryGatewayWanRatesAsync(gatewayMac, wanIfNames, queryFrom, queryTo,
                    sampleIntervalSeconds: sampleIntervalSeconds, ct: ct)
                : Task.FromResult<IReadOnlyList<MonitoringInfluxClient.WanRatePoint>>(Array.Empty<MonitoringInfluxClient.WanRatePoint>());
            // Scoped like the rates above: the backfilled RTT and loss have to belong to the WAN
            // being charted, or a secondary WAN's history is drawn with the primary's latency -
            // the same borrowing the live tick did, just arriving as history instead.
            var targets = await liveStats.GetIspTransitTargetsAsync(ct);
            // Points are scoped as well as targets. Selecting the right target ids is not enough on
            // its own: one host reachable from two WANs is probed under each, and a row that has
            // moved between contexts keeps its older points under the tag they were written with -
            // so a read by id alone returns another WAN's readings too, which is a speed test on one
            // WAN showing up as a latency spike on another's chart. Same scope the ISP Health
            // reports use, built by the same helper.
            // Same rule as the live tick: no WAN named means the primary, never every WAN.
            MonitoringInfluxClient.LatencyWanScope? latencyScope = null;
            {
                var wanKey = string.IsNullOrEmpty(wan)
                    ? NetworkOptimizer.UniFi.GatewayWanHelper.DefaultWanKey
                    : NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(wan!);
                var wanIsPrimary = string.Equals(wanKey,
                    NetworkOptimizer.UniFi.GatewayWanHelper.DefaultWanKey, StringComparison.OrdinalIgnoreCase);
                targets = targets.Where(t => NetworkOptimizer.Storage.Models.MonitoringTarget.IsUnpinned(t.WanInterface)
                    ? wanIsPrimary
                    : string.Equals(NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(t.WanInterface!),
                        wanKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                try
                {
                    await using var scopeDb = siteDb.CreateForSite(siteContext.Slug, siteContext.IsDefault);
                    var contexts = await scopeDb.WanContexts.AsNoTracking().ToListAsync(ct);
                    latencyScope = NetworkOptimizer.Web.Services.Monitoring.IspHealth.IspHealthService
                        .BuildWanScope(contexts, wanKey, wanIsPrimary);
                }
                catch
                {
                    // Unreadable contexts: fall back to the id-only read rather than an empty chart.
                }
            }
            var targetIds = targets.Select(t => t.TargetId).ToList();
            // No targets on this WAN means no latency history for it - an empty query would read
            // as the site's, so it is skipped and the series stays empty.
            var rttTask = targetIds.Count > 0
                ? influx.QueryMeanIspTransitLatencyAsync(queryFrom, queryTo, targetIds, wanScope: latencyScope, ct: ct)
                : Task.FromResult<IReadOnlyList<MonitoringInfluxClient.LatencyPoint>>(Array.Empty<MonitoringInfluxClient.LatencyPoint>());

            await Task.WhenAll(wanTask, rttTask);

            var wanData = await wanTask;
            var rttData = await rttTask;

            // As-of merge: each WAN point adopts the newest latency point at or
            // before its own timestamp. The previous exact-bucket join silently
            // DROPPED latency points whenever the SNMP tier skipped the matching
            // 5s window - and SNMP polls get delayed exactly under load, so loss
            // spikes vanished from the chart precisely when they mattered.
            var rttSorted = rttData.OrderBy(p => p.Time).ToList();
            var wanSorted = wanData.OrderBy(w => w.Time).ToList();

            // Rows come from the throughput series, so a span with no throughput point had no row
            // at all - and took its latency and loss down with it. The gateway's SNMP counters are
            // collected by whoever collects for the site, and on a site that leaves collection to
            // the server there is nothing to buffer them: a server restart leaves a real hole in
            // throughput while the agent's probe results replay into it perfectly. The chart drew
            // the hole across every series, so backfilled latency and loss were invisible.
            //
            // Latency points landing in such a hole get a row of their own, with no throughput on
            // it. Only in a hole: a point with throughput either side of it within a couple of
            // sample intervals still rides that throughput point, so ordinary operation keeps
            // exactly the rows it had and the throughput line does not turn dotted. Both series
            // are already in hand - this adds no query.
            var holeTolerance = TimeSpan.FromSeconds(Math.Max(sampleIntervalSeconds * 2, 10));
            var orphanTimes = new List<DateTime>();
            if (wanSorted.Count == 0)
            {
                orphanTimes.AddRange(rttSorted.Select(p => p.Time));
            }
            else
            {
                var wi = 0;
                foreach (var p in rttSorted)
                {
                    while (wi + 1 < wanSorted.Count && wanSorted[wi + 1].Time <= p.Time) wi++;
                    var nearest = (p.Time - wanSorted[wi].Time).Duration();
                    if (wi + 1 < wanSorted.Count)
                    {
                        var next = (wanSorted[wi + 1].Time - p.Time).Duration();
                        if (next < nearest) nearest = next;
                    }
                    if (nearest > holeTolerance) orphanTimes.Add(p.Time);
                }
            }

            var rows = wanSorted
                .Select(w => (Time: w.Time, Down: (double?)w.DownloadBps, Up: (double?)w.UploadBps))
                .Concat(orphanTimes.Select(t => (Time: t, Down: (double?)null, Up: (double?)null)))
                .OrderBy(r => r.Time)
                .ToList();

            var ri = 0;
            MonitoringInfluxClient.LatencyPoint? lastRtt = null;

            var points = rows.Select(r =>
            {
                while (ri < rttSorted.Count && rttSorted[ri].Time <= r.Time)
                    lastRtt = rttSorted[ri++];

                return new
                {
                    time = r.Time.ToString("o"),
                    downloadBps = r.Down,
                    uploadBps = r.Up,
                    rttMs = lastRtt?.RttAvgMs,
                    lossPercent = lastRtt?.LossPercent,
                };
            }).ToList();

            // The client polls live samples on its own timer; handing it the interval lets that
            // timer track the site rather than a constant that was right for a 5s tier.
            return Results.Ok(new { points, sampleIntervalSeconds });
        });

        group.MapGet("/api/monitoring/chart-data", async (
            MonitoringInfluxClient influx,
            SiteDbContextFactory siteDbFactory,
            SiteContextService siteContext,
            ILoggerFactory loggerFactory,
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
                "Custom" => MonitoringTargetType.Custom,
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
            await using var db = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);
            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == targetType && t.Enabled
                    && (t.AsnNumber == null || !WellKnownAsns.NonTransitInfrastructure.Contains(t.AsnNumber.Value)))
                .OrderBy(t => t.Name)
                .Select(t => new { t.TargetId, t.Name, t.AutoLabel, t.WanInterface, t.Address, t.TargetType, t.IsLocal })
                .ToListAsync(ct);

            if (targets.Count == 0)
                return Results.Ok(new { targets = Array.Empty<object>() });

            var data = await influx.QueryLatencyByTargetTypeAsync(targetType, queryFrom, queryTo, ct: ct);

            var result = targets.Select(t =>
            {
                data.TryGetValue(t.TargetId, out var points);
                var pts = points ?? new List<MonitoringInfluxClient.LatencyPoint>();
                return new
                {
                    targetId = t.TargetId,
                    name = t.Name,
                    // Role label ("gateway"/"switch"/"ap"/...) so the LAN flaky detector can
                    // identify the gateway target and mask out gateway-outage windows.
                    autoLabel = t.AutoLabel,
                    // WAN ownership and address, so the chart's WAN filter can scope series
                    // client-side and pair the same host's per-WAN twins.
                    wanInterface = t.WanInterface,
                    address = t.Address,
                    // Whether this sits on the local network, decided server-side from the resolved
                    // answer rather than by re-testing the address in JS - one rule, one place, and
                    // a hostname can only be answered here.
                    isLan = NetworkOptimizer.Web.Services.Monitoring.LocalTargetResolver.IsLocal(
                        t.TargetType, t.Address, t.IsLocal, t.WanInterface),
                    rtt = pts.Select(p => new { time = p.Time.ToString("o"), value = p.RttAvgMs }),
                    loss = pts.Select(p => new { time = p.Time.ToString("o"), value = p.LossPercent }),
                };
            });

            // Chart hover sync compares the first and last instant each chart holds, so those are
            // what a "why is it not syncing" investigation needs from this side. Extents only -
            // no target names or addresses.
            var allPts = data.Values.SelectMany(p => p).ToList();
            loggerFactory.CreateLogger("MonitoringChartEndpoints").LogDebug(
                "chart-data: category={Category} targets={Targets} points={Points} first={First:o} last={Last:o} window={From:o}..{To:o}",
                category ?? "Fabric", targets.Count, allPts.Count,
                allPts.Count > 0 ? allPts.Min(p => p.Time) : (DateTime?)null,
                allPts.Count > 0 ? allPts.Max(p => p.Time) : (DateTime?)null,
                queryFrom, queryTo);

            return Results.Ok(new { targets = result });
        });

        group.MapGet("/api/monitoring/wan-rate-chart", async (
            MonitoringInfluxClient influx,
            UniFiConnectionService connectionService,
            SiteDbContextFactory siteDbFactory,
            SiteContextService siteContext,
            ILoggerFactory loggerFactory,
            int? rangeHours,
            DateTime? from,
            DateTime? to,
            string? wan,
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

            // Explicit WAN (a UniFi wan key like "wan2", from the chart's WAN filter): that WAN's
            // own counter interface - live, then its remembered profile row - replaces the default
            // primary/active-uplink resolution above. Never a cross-WAN fallback: an unresolvable
            // WAN returns an empty series rather than another WAN's throughput.
            if (!string.IsNullOrWhiteSpace(wan))
            {
                var wanGroup = NetworkOptimizer.UniFi.GatewayWanHelper.WanNetworkGroupFromKey(wan.Trim());
                string? counter = null;
                try
                {
                    counter = (await connectionService.GetWanInterfacesForGroupAsync(wanGroup, ct))?.CounterIfName;
                }
                catch { }
                if (string.IsNullOrEmpty(counter) || string.IsNullOrEmpty(gatewayMac))
                {
                    try
                    {
                        await using var wdb = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);
                        var profile = await wdb.WanProfiles.AsNoTracking()
                            .Where(w => w.WanNetworkgroup == wanGroup)
                            .OrderByDescending(w => w.UpdatedAt)
                            .FirstOrDefaultAsync(ct);
                        counter ??= profile?.CounterInterface;
                        gatewayMac = string.IsNullOrEmpty(gatewayMac) ? profile?.GatewayMac : gatewayMac;
                    }
                    catch { }
                }
                wanIfNames = string.IsNullOrEmpty(counter) ? null : new List<string> { counter! };
            }

            if (string.IsNullOrEmpty(gatewayMac) || wanIfNames == null || wanIfNames.Count == 0)
                return Results.Ok(new { download = Array.Empty<object>(), upload = Array.Empty<object>() });

            var data = await influx.QueryGatewayWanRatesAsync(gatewayMac, wanIfNames, queryFrom, queryTo, ct: ct);

            // The other half of the sync comparison - read these against the chart-data line for
            // the same moment to see whether the two queries actually cover the same span.
            loggerFactory.CreateLogger("MonitoringChartEndpoints").LogDebug(
                "wan-rate-chart: wan={Wan} points={Points} first={First:o} last={Last:o} window={From:o}..{To:o}",
                wan ?? "(default)", data.Count,
                data.Count > 0 ? data.Min(p => p.Time) : (DateTime?)null,
                data.Count > 0 ? data.Max(p => p.Time) : (DateTime?)null,
                queryFrom, queryTo);

            return Results.Ok(new
            {
                download = data.Select(p => new { time = p.Time.ToString("o"), value = p.DownloadBps }),
                upload = data.Select(p => new { time = p.Time.ToString("o"), value = p.UploadBps })
            });
        });
    }
}
