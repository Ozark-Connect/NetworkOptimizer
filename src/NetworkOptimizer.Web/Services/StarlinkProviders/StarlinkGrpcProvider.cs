using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.StarlinkProviders.Proto;

namespace NetworkOptimizer.Web.Services.StarlinkProviders;

/// <summary>
/// Polls a Starlink user terminal over its local gRPC API (192.168.100.1:9200
/// on a stock install). The API is unversioned and community-documented, so
/// parsing is defensive: the proto is a trimmed field-number-exact subset and
/// unknown fields from newer firmware are skipped by protobuf.
/// Instances are per site (like the cellular providers), so per-config delta
/// state keyed by configuration ID cannot collide across sites.
/// </summary>
public class StarlinkGrpcProvider : IStarlinkProvider
{
    /// <summary>Default gRPC port on the dish.</summary>
    public const int DefaultPort = 9200;

    // GPS epoch (1980-01-06) to Unix epoch offset, minus current leap seconds.
    // Outage timestamps in get_history are GPS-epoch nanoseconds.
    private const long GpsToUnixSeconds = 315964800 - 18;

    private readonly ILogger<StarlinkGrpcProvider> _logger;

    // History ring buffers are 1 Hz with a monotonic sample counter; remember
    // the counter and poll time per config so aggregates cover exactly the
    // samples since the previous poll (the CM correctables-delta pattern).
    private readonly ConcurrentDictionary<int, ulong> _lastHistoryCounter = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastPollUtc = new();

    public StarlinkGrpcProvider(ILogger<StarlinkGrpcProvider> logger)
    {
        _logger = logger;
    }

    public string ProviderKey => "starlink-grpc";

    public string DisplayName => "Starlink (local gRPC)";

    public async Task<StarlinkStats?> PollAsync(
        StarlinkPollContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var channel = CreateChannel(context);
            var client = new Device.DeviceClient(channel);

            var statusResp = await CallAsync(client, new Request { GetStatus = new GetStatusRequest() }, cancellationToken);
            if (statusResp?.ResponseCase != Response.ResponseOneofCase.DishGetStatus)
            {
                _logger.LogWarning("Starlink {Name} ({Host}): status poll returned {Case}",
                    context.Name, context.ConfiguredHost ?? context.Host, statusResp?.ResponseCase);
                return null;
            }

            var status = statusResp.DishGetStatus;
            var stats = MapStatus(status);

            try
            {
                var historyResp = await CallAsync(client, new Request { GetHistory = new GetHistoryRequest() }, cancellationToken);
                if (historyResp?.ResponseCase == Response.ResponseOneofCase.DishGetHistory)
                    ApplyHistory(stats, historyResp.DishGetHistory, context.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Starlink {Name}: history fetch failed; status-only poll", context.Name);
            }

            try
            {
                var diagResp = await CallAsync(client, new Request { GetDiagnostics = new GetDiagnosticsRequest() }, cancellationToken);
                if (diagResp?.ResponseCase == Response.ResponseOneofCase.DishGetDiagnostics)
                    ApplyDiagnostics(stats, diagResp.DishGetDiagnostics);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Starlink {Name}: diagnostics fetch failed; skipping self-test result", context.Name);
            }

            _lastPollUtc[context.Id] = stats.Timestamp;
            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Starlink {Name} ({Host}): poll failed",
                context.Name, context.ConfiguredHost ?? context.Host);
            return null;
        }
    }

    public async Task<StarlinkObstructionMap?> GetObstructionMapAsync(
        StarlinkPollContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var channel = CreateChannel(context);
            var client = new Device.DeviceClient(channel);

            var resp = await CallAsync(client, new Request { DishGetObstructionMap = new DishGetObstructionMapRequest() }, cancellationToken);
            if (resp?.ResponseCase != Response.ResponseOneofCase.DishGetObstructionMap)
                return null;

            var map = resp.DishGetObstructionMap;
            if (map.NumRows == 0 || map.NumCols == 0 || map.Snr.Count != map.NumRows * map.NumCols)
            {
                _logger.LogDebug("Starlink {Name}: obstruction map malformed ({Rows}x{Cols}, {Samples} samples)",
                    context.Name, map.NumRows, map.NumCols, map.Snr.Count);
                return null;
            }

            return new StarlinkObstructionMap
            {
                Timestamp = DateTime.UtcNow,
                NumRows = (int)map.NumRows,
                NumCols = (int)map.NumCols,
                Snr = map.Snr.ToArray(),
                MaxThetaDeg = map.MaxThetaDeg,
                ReferenceFrame = map.MapReferenceFrame.ToString(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Starlink {Name} ({Host}): obstruction map fetch failed",
                context.Name, context.ConfiguredHost ?? context.Host);
            return null;
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        StarlinkPollContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var channel = CreateChannel(context);
            var client = new Device.DeviceClient(channel);

            var resp = await CallAsync(client, new Request { GetDeviceInfo = new GetDeviceInfoRequest() }, cancellationToken);
            if (resp?.ResponseCase != Response.ResponseOneofCase.GetDeviceInfo)
                return (false, "Dish answered, but not with device info - is this a Starlink terminal?");

            var info = resp.GetDeviceInfo.DeviceInfo;
            return (true, $"Connected: {info?.HardwareVersion} running {info?.SoftwareVersion}");
        }
        catch (RpcException ex)
        {
            return (false, $"gRPC error: {ex.Status.StatusCode} - {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private static GrpcChannel CreateChannel(StarlinkPollContext context)
    {
        var port = context.Port > 0 ? context.Port : DefaultPort;
        return GrpcChannel.ForAddress($"http://{context.Host}:{port}");
    }

    private static async Task<Response?> CallAsync(
        Device.DeviceClient client, Request request, CancellationToken ct)
    {
        return await client.HandleAsync(
            request,
            deadline: DateTime.UtcNow.AddSeconds(10),
            cancellationToken: ct);
    }

    private static StarlinkStats MapStatus(DishGetStatusResponse status)
    {
        var stats = new StarlinkStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceId = NullIfEmpty(status.DeviceInfo?.Id),
            HardwareVersion = NullIfEmpty(status.DeviceInfo?.HardwareVersion),
            SoftwareVersion = NullIfEmpty(status.DeviceInfo?.SoftwareVersion),
            CountryCode = NullIfEmpty(status.DeviceInfo?.CountryCode),
            BootCount = status.DeviceInfo?.Bootcount,
            UptimeSeconds = (long?)status.DeviceState?.UptimeS,
            EthSpeedMbps = status.EthSpeedMbps > 0 ? status.EthSpeedMbps : null,
            IsSnrAboveNoiseFloor = status.IsSnrAboveNoiseFloor,
            IsSnrPersistentlyLow = status.IsSnrPersistentlyLow,
            MobilityClass = status.MobilityClass.ToString(),
            ClassOfService = status.ClassOfService.ToString(),
            SoftwareUpdateState = status.SoftwareUpdateState.ToString(),
            DisablementCode = status.DisablementCode.ToString(),
            DownlinkRestrictedReason = status.DlBandwidthRestrictedReason.ToString(),
            UplinkRestrictedReason = status.UlBandwidthRestrictedReason.ToString(),
        };

        if (status.ObstructionStats != null)
        {
            var o = status.ObstructionStats;
            stats.FractionObstructed = FiniteOrNull(o.FractionObstructed);
            stats.CurrentlyObstructed = o.CurrentlyObstructed;
            stats.ObstructionValidSeconds = FiniteOrNull(o.ValidS);
            if (o.AvgProlongedObstructionValid)
                stats.AvgProlongedObstructionDurationSeconds = FiniteOrNull(o.AvgProlongedObstructionDurationS);
        }

        if (status.GpsStats != null)
        {
            stats.GpsValid = status.GpsStats.GpsValid;
            stats.GpsSatellites = (int)status.GpsStats.GpsSats;
        }

        if (status.AlignmentStats != null)
        {
            var a = status.AlignmentStats;
            stats.TiltAngleDeg = FiniteOrNull(a.TiltAngleDeg);
            stats.BoresightAzimuthDeg = FiniteOrNull(a.BoresightAzimuthDeg);
            stats.BoresightElevationDeg = FiniteOrNull(a.BoresightElevationDeg);
            stats.DesiredBoresightAzimuthDeg = FiniteOrNull(a.DesiredBoresightAzimuthDeg);
            stats.DesiredBoresightElevationDeg = FiniteOrNull(a.DesiredBoresightElevationDeg);
            stats.AttitudeUncertaintyDeg = FiniteOrNull(a.AttitudeUncertaintyDeg);
        }

        if (status.Alerts != null)
        {
            // Walk the DishAlerts descriptor so alerts added by newer firmware
            // (and declared in the proto) surface without a mapping table.
            foreach (var field in DishAlerts.Descriptor.Fields.InFieldNumberOrder())
            {
                if (field.FieldType == Google.Protobuf.Reflection.FieldType.Bool &&
                    field.Accessor.GetValue(status.Alerts) is true)
                {
                    stats.ActiveAlerts.Add(field.Name);
                }
            }
        }

        return stats;
    }

    private void ApplyHistory(StarlinkStats stats, DishGetHistoryResponse history, int configId)
    {
        var counter = history.Current;
        var bufferLen = Math.Max(history.PopPingDropRate.Count, history.PowerIn.Count);
        if (counter == 0 || bufferLen == 0) return;

        // Number of new 1 Hz samples since the previous poll, capped at the
        // ring buffer length. First poll (or counter reset after reboot)
        // aggregates over the full buffer.
        long window = bufferLen;
        if (_lastHistoryCounter.TryGetValue(configId, out var prev) && counter > prev)
            window = Math.Min((long)(counter - prev), bufferLen);
        window = Math.Min(window, (long)counter);
        _lastHistoryCounter[configId] = counter;

        AggregateRing(history.PopPingDropRate, counter, window,
            out var dropAvg, out var dropMax, out _);
        stats.PingDropRateAvg = dropAvg;
        stats.PingDropRateMax = dropMax;

        AggregateRing(history.PowerIn, counter, window,
            out var powerAvg, out var powerMax, out var powerLatest);
        stats.PowerInAvgWatts = powerAvg;
        stats.PowerInMaxWatts = powerMax;
        stats.PowerInWatts = powerLatest;

        if (history.Outages.Count > 0)
        {
            var windowStart = stats.Timestamp.AddSeconds(-window);
            foreach (var outage in history.Outages)
            {
                var startUtc = GpsNsToUtc(outage.StartTimestampNs);
                var durationS = outage.DurationNs / 1e9;

                if (stats.LastOutageAt is null || startUtc > stats.LastOutageAt)
                {
                    stats.LastOutageAt = startUtc;
                    stats.LastOutageCause = outage.Cause.ToString();
                    stats.LastOutageDurationSeconds = durationS;
                }

                if (startUtc >= windowStart)
                {
                    stats.OutageCountDelta++;
                    stats.OutageSecondsDelta += durationS;
                }
            }
        }
    }

    private static void ApplyDiagnostics(StarlinkStats stats, DishGetDiagnosticsResponse diag)
    {
        if (diag.HardwareSelfTest != DishGetDiagnosticsResponse.Types.TestResult.NoResult)
        {
            stats.HardwareSelfTest = diag.HardwareSelfTest.ToString();
            foreach (var code in diag.HardwareSelfTestCodes)
                stats.HardwareSelfTestCodes.Add(code.ToString());
        }
    }

    /// <summary>
    /// Aggregate the newest <paramref name="window"/> samples of a 1 Hz ring
    /// buffer whose write position is <paramref name="counter"/> % length.
    /// </summary>
    private static void AggregateRing(
        IReadOnlyList<float> ring, ulong counter, long window,
        out double? avg, out double? max, out double? latest)
    {
        avg = null;
        max = null;
        latest = null;
        if (ring.Count == 0 || window <= 0) return;

        double sum = 0;
        double maxSeen = double.MinValue;
        long counted = 0;
        for (long k = 0; k < Math.Min(window, ring.Count); k++)
        {
            var idx = (long)((counter - 1 - (ulong)k) % (ulong)ring.Count);
            var v = ring[(int)idx];
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            if (k == 0) latest = v;
            sum += v;
            if (v > maxSeen) maxSeen = v;
            counted++;
        }

        if (counted == 0) return;
        avg = sum / counted;
        max = maxSeen;
    }

    private static DateTime GpsNsToUtc(long gpsNs) =>
        DateTimeOffset.FromUnixTimeSeconds(gpsNs / 1_000_000_000 + GpsToUnixSeconds).UtcDateTime;

    private static double? FiniteOrNull(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) ? null : value;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
