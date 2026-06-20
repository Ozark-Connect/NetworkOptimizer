using System.Collections.Concurrent;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Owns the lifecycle of the InfluxDB client used by the monitoring subsystem. The client
/// is built lazily from the user-configured connection details persisted in
/// <see cref="MonitoringSettings"/>, and can be reconfigured at runtime via
/// <see cref="ReconfigureAsync"/> when the user updates settings.
///
/// Provides schema-aligned write helpers for the Gate 1 measurements
/// (interface_counters, device_health, latency, wifi_client, sfp, events). Writes are
/// buffered and flushed on a 5 s timer or when the buffer hits the size cap, matching the
/// STM batching pattern.
/// </summary>
public class MonitoringInfluxClient : IAsyncDisposable
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly ILogger<MonitoringInfluxClient> _logger;

    private readonly SemaphoreSlim _configLock = new(1, 1);
    private readonly ConcurrentQueue<BufferedPoint> _writeBuffer = new();
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
    private readonly CancellationTokenSource _timerCts = new();

    private InfluxDBClient? _client;
    private WriteApiAsync? _writeApi;
    private string? _org;
    private string? _bucket;
    private string? _longtermBucket;
    private string? _url;
    private PeriodicTimer? _flushTimer;
    private Task? _flushTask;
    private int _maxBufferSize = 1000;
    private int _flushIntervalSeconds = 5;
    private bool _disposed;
    private bool _initialized;
    private string? _tokenHash;

    public MonitoringInfluxClient(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        ICredentialProtectionService credentialProtection,
        ILogger<MonitoringInfluxClient> logger)
    {
        _dbFactory = dbFactory;
        _credentialProtection = credentialProtection;
        _logger = logger;
    }

    public string? CurrentUrl => _url;
    public bool IsConfigured => _client != null && !string.IsNullOrEmpty(_bucket);

    /// <summary>
    /// Build (or rebuild) the client from current MonitoringSettings. Safe to call repeatedly.
    /// Returns true if a usable client was constructed.
    /// </summary>
    public async Task<bool> ReconfigureAsync(CancellationToken ct = default)
    {
        await _configLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings == null)
            {
                _logger.LogDebug("MonitoringSettings row not yet created; InfluxDB client not configured");
                await DisposeClientAsync();
                return false;
            }

            return await ApplyConfigAsync(settings, ct);
        }
        finally
        {
            _configLock.Release();
        }
    }

    private async Task<bool> ApplyConfigAsync(MonitoringSettings settings, CancellationToken ct)
    {
        var url = settings.InfluxDbUrl?.Trim();
        var token = settings.InfluxDbToken;
        var org = settings.InfluxDbOrg?.Trim();
        var bucket = settings.InfluxDbBucket?.Trim();
        var longterm = settings.InfluxDbLongtermBucket?.Trim();

        if (string.IsNullOrWhiteSpace(url) ||
            string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(org) ||
            string.IsNullOrWhiteSpace(bucket))
        {
            _logger.LogDebug("InfluxDB config incomplete (url/token/org/bucket missing) — client not built");
            await DisposeClientAsync();
            _initialized = false;
            return false;
        }

        string plainToken;
        try
        {
            plainToken = _credentialProtection.Decrypt(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt InfluxDB token");
            await DisposeClientAsync();
            return false;
        }

        var currentTokenHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(plainToken)))[..16];

        if (_initialized &&
            _url == url &&
            _org == org &&
            _bucket == bucket &&
            _longtermBucket == longterm &&
            _tokenHash == currentTokenHash)
        {
            return true;
        }

        await DisposeClientAsync();

        try
        {
            var options = new InfluxDBClientOptions.Builder()
                .Url(url)
                .AuthenticateToken(plainToken)
                .LogLevel(InfluxDB.Client.Core.LogLevel.None)
                .Build();

            _client = new InfluxDBClient(options);
            _writeApi = _client.GetWriteApiAsync();
            _url = url;
            _org = SanitizeFluxString(org);
            _bucket = SanitizeFluxString(bucket);
            _longtermBucket = SanitizeFluxString(string.IsNullOrWhiteSpace(longterm) ? bucket : longterm);
            _tokenHash = currentTokenHash;
            _initialized = true;

            _flushTimer = new PeriodicTimer(TimeSpan.FromSeconds(_flushIntervalSeconds));
            _flushTask = RunFlushLoopAsync(_timerCts.Token);

            _logger.LogInformation(
                "Monitoring InfluxDB client configured (url={Url}, org={Org}, bucket={Bucket}, longterm={Longterm})",
                _url, _org, _bucket, _longtermBucket);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build InfluxDB client");
            await DisposeClientAsync();
            return false;
        }
    }

    /// <summary>
    /// Ping InfluxDB and persist the result to MonitoringSettings for UI display.
    /// </summary>
    public async Task<InfluxHealthResult> CheckHealthAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            var configured = await ReconfigureAsync(ct);
            if (!configured)
            {
                await PersistHealthAsync(false, "InfluxDB connection not configured", ct);
                return new InfluxHealthResult(false, "InfluxDB connection not configured");
            }
        }

        // /ping only validates the server is up. The stored scoped token might be invalid
        // (revoked, or scoped to a bucket the user deleted) and writes will silently fail
        // while the UI keeps saying "Connected". Do a small query against the primary
        // bucket so the health probe actually exercises the credentials + the bucket
        // existence the agent depends on.
        try
        {
            var pinged = await _client!.PingAsync();
            if (!pinged)
            {
                await PersistHealthAsync(false, "InfluxDB ping returned false", ct);
                return new InfluxHealthResult(false, "InfluxDB ping returned false");
            }

            var queryApi = _client.GetQueryApi();

            // Probe the primary bucket
            var flux = $@"from(bucket: ""{_bucket}"") |> range(start: -1m) |> limit(n: 1)";
            await queryApi.QueryAsync(flux, _org, ct);

            // Probe the longterm bucket too - SFP/modem charts query it and users
            // get confused by 500s when only this one is missing.
            if (!string.IsNullOrEmpty(_longtermBucket) && _longtermBucket != _bucket)
            {
                var ltFlux = $@"from(bucket: ""{_longtermBucket}"") |> range(start: -1m) |> limit(n: 1)";
                await queryApi.QueryAsync(ltFlux, _org, ct);
            }

            await PersistHealthAsync(true, null, ct);
            return new InfluxHealthResult(true, null);
        }
        catch (InfluxDB.Client.Core.Exceptions.UnauthorizedException ex)
        {
            // Most common case after the user revokes the token or deletes the buckets
            // the token was scoped to. Surface a specific message; the wizard can be
            // re-run to provision fresh.
            var msg = $"Token is no longer authorized for bucket '{_bucket}'. Re-run InfluxDB setup. ({ex.Message})";
            _logger.LogWarning(ex, "InfluxDB health check: unauthorized");
            await PersistHealthAsync(false, msg, ct);
            return new InfluxHealthResult(false, msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InfluxDB health check failed");
            await PersistHealthAsync(false, ex.Message, ct);
            return new InfluxHealthResult(false, ex.Message);
        }
    }

    private async Task PersistHealthAsync(bool reachable, string? error, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
            if (settings == null) return;
            settings.InfluxDbReachable = reachable;
            settings.LastInfluxDbCheck = DateTime.UtcNow;
            settings.LastInfluxDbError = reachable ? null : Truncate(error, 500);
            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist InfluxDB health state");
        }
    }

    // ---- Schema-aligned write helpers (Gate 1) ----

    public Task WriteInterfaceCountersAsync(
        string deviceMac,
        string ifName,
        string? portId,
        InterfaceDirection direction,
        long bytesIn,
        long bytesOut,
        double? rateInBps,
        double? rateOutBps,
        long? speedBps,
        int operStatus,
        long errorsIn,
        long errorsOut,
        long discardsIn,
        long discardsOut,
        bool hcCounters,
        long? ucastPktsIn,
        long? ucastPktsOut,
        long? mcastPktsIn,
        long? mcastPktsOut,
        long? bcastPktsIn,
        long? bcastPktsOut,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("interface_counters")
            .Tag("device_mac", NormalizeMac(deviceMac))
            .Tag("if_name", ifName)
            .Tag("port_id", portId ?? "")
            .Tag("direction", direction.ToString().ToLowerInvariant())
            .Field("bytes_in", bytesIn)
            .Field("bytes_out", bytesOut)
            .Field("oper_status", operStatus)
            .Field("errors_in", errorsIn)
            .Field("errors_out", errorsOut)
            .Field("discards_in", discardsIn)
            .Field("discards_out", discardsOut)
            .Field("hc_counters", hcCounters)
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (rateInBps.HasValue) point = point.Field("rate_in_bps", rateInBps.Value);
        if (rateOutBps.HasValue) point = point.Field("rate_out_bps", rateOutBps.Value);
        if (speedBps.HasValue) point = point.Field("speed_bps", speedBps.Value);
        if (ucastPktsIn is > 0) point = point.Field("ucast_pkts_in", ucastPktsIn.Value);
        if (ucastPktsOut is > 0) point = point.Field("ucast_pkts_out", ucastPktsOut.Value);
        if (mcastPktsIn is > 0) point = point.Field("mcast_pkts_in", mcastPktsIn.Value);
        if (mcastPktsOut is > 0) point = point.Field("mcast_pkts_out", mcastPktsOut.Value);
        if (bcastPktsIn is > 0) point = point.Field("bcast_pkts_in", bcastPktsIn.Value);
        if (bcastPktsOut is > 0) point = point.Field("bcast_pkts_out", bcastPktsOut.Value);

        Enqueue(point, longterm: false);
        return Task.CompletedTask;
    }

    public Task WriteDeviceHealthAsync(
        string deviceMac,
        string deviceType,
        double? cpuPercent,
        long? memoryTotalKb,
        long? memoryUsedKb,
        double? memoryUsedPercent,
        double? temperatureC,
        long? uptimeSeconds,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("device_health")
            .Tag("device_mac", NormalizeMac(deviceMac))
            .Tag("device_type", deviceType.ToLowerInvariant())
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (cpuPercent.HasValue) point = point.Field("cpu_percent", cpuPercent.Value);
        if (memoryTotalKb.HasValue) point = point.Field("memory_total_kb", memoryTotalKb.Value);
        if (memoryUsedKb.HasValue) point = point.Field("memory_used_kb", memoryUsedKb.Value);
        if (memoryUsedPercent.HasValue) point = point.Field("memory_used_percent", memoryUsedPercent.Value);
        if (temperatureC.HasValue) point = point.Field("temperature_c", temperatureC.Value);
        if (uptimeSeconds.HasValue) point = point.Field("uptime_seconds", uptimeSeconds.Value);

        Enqueue(point, longterm: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Write custom OID values as additional fields on an existing measurement.
    /// Tag set must match the standard write for the same measurement so the
    /// fields land on the same InfluxDB series.
    /// </summary>
    public Task WriteCustomFieldsAsync(
        string measurement,
        string deviceMac,
        Dictionary<string, object> customFields,
        string? deviceType,
        string? ifName,
        string? portId,
        DateTime timestamp)
    {
        if (!IsConfigured || customFields.Count == 0) return Task.CompletedTask;

        var point = PointData.Measurement(measurement)
            .Tag("device_mac", NormalizeMac(deviceMac))
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (!string.IsNullOrEmpty(deviceType))
            point = point.Tag("device_type", deviceType.ToLowerInvariant());
        if (!string.IsNullOrEmpty(ifName))
            point = point.Tag("if_name", ifName);
        if (!string.IsNullOrEmpty(portId))
            point = point.Tag("port_id", portId);
        if (measurement == "interface_counters")
            point = point.Tag("direction", "unknown");

        foreach (var (name, value) in customFields)
        {
            point = value switch
            {
                long l => point.Field(name, l),
                double d => point.Field(name, d),
                string s => point.Field(name, s),
                int i => point.Field(name, (long)i),
                _ => point.Field(name, value.ToString() ?? "")
            };
        }

        Enqueue(point, longterm: false);
        return Task.CompletedTask;
    }

    public Task WriteLatencyAsync(
        string targetId,
        string vantagePoint,
        MonitoringTargetType targetType,
        ProbeMode probeMode,
        double? rttMinMs,
        double? rttAvgMs,
        double? rttMaxMs,
        double? jitterMs,
        double lossPercent,
        bool success,
        int sent,
        int received,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("latency")
            .Tag("target_id", targetId)
            .Tag("vantage_point", vantagePoint)
            .Tag("target_type", targetType.ToString().ToLowerInvariant())
            .Field("loss_percent", lossPercent)
            .Field("success", success)
            // Raw burst counts: sent + received per probe burst. Lets dashboards
            // reconstruct "total probes sent" and verify the burst configuration
            // independent of the loss_percent field (STM parity).
            .Field("sent", sent)
            .Field("received", received)
            .Field("probe_mode", probeMode.ToString().ToLowerInvariant())
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (rttMinMs.HasValue) point = point.Field("rtt_min_ms", rttMinMs.Value);
        if (rttAvgMs.HasValue) point = point.Field("rtt_avg_ms", rttAvgMs.Value);
        if (rttMaxMs.HasValue) point = point.Field("rtt_max_ms", rttMaxMs.Value);
        if (jitterMs.HasValue) point = point.Field("jitter_ms", jitterMs.Value);

        Enqueue(point, longterm: false);
        return Task.CompletedTask;
    }

    public Task WriteWifiClientAsync(
        string apMac,
        string band,
        string clientMac,
        double? signalDbm,
        double? noiseDbm,
        long? txRateKbps,
        long? rxRateKbps,
        int? channel,
        int? channelWidth,
        int? satisfaction,
        int? rssi,
        long? txBytes,
        long? rxBytes,
        double? txThroughputBps,
        double? rxThroughputBps,
        bool? isMlo,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("wifi_client")
            .Tag("device_mac", NormalizeMac(apMac))
            .Tag("band", band.ToLowerInvariant())
            .Field("client_mac", NormalizeMac(clientMac))
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (signalDbm.HasValue) point = point.Field("signal_dbm", signalDbm.Value);
        if (noiseDbm.HasValue) point = point.Field("noise_dbm", noiseDbm.Value);
        if (txRateKbps.HasValue) point = point.Field("tx_rate_kbps", txRateKbps.Value);
        if (rxRateKbps.HasValue) point = point.Field("rx_rate_kbps", rxRateKbps.Value);
        if (channel.HasValue) point = point.Field("channel", channel.Value);
        if (channelWidth.HasValue) point = point.Field("channel_width", channelWidth.Value);
        if (satisfaction.HasValue) point = point.Field("satisfaction", satisfaction.Value);
        if (rssi.HasValue) point = point.Field("rssi", rssi.Value);
        if (txBytes.HasValue) point = point.Field("tx_bytes", txBytes.Value);
        if (rxBytes.HasValue) point = point.Field("rx_bytes", rxBytes.Value);
        if (txThroughputBps.HasValue) point = point.Field("tx_throughput_bps", txThroughputBps.Value);
        if (rxThroughputBps.HasValue) point = point.Field("rx_throughput_bps", rxThroughputBps.Value);
        if (isMlo.HasValue) point = point.Field("is_mlo", isMlo.Value);

        Enqueue(point, longterm: false);
        return Task.CompletedTask;
    }

    public Task WriteWiredClientAsync(
        string switchMac,
        string clientMac,
        double? txThroughputBps,
        double? rxThroughputBps,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("wired_client")
            .Tag("device_mac", NormalizeMac(switchMac))
            .Field("client_mac", NormalizeMac(clientMac))
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (txThroughputBps.HasValue) point = point.Field("tx_throughput_bps", txThroughputBps.Value);
        if (rxThroughputBps.HasValue) point = point.Field("rx_throughput_bps", rxThroughputBps.Value);

        Enqueue(point, longterm: false);
        return Task.CompletedTask;
    }

    public Task WriteSfpAsync(
        string deviceMac,
        string portName,
        double? rxPowerDbm,
        double? txPowerDbm,
        double? txBiasMa,
        double? temperatureC,
        double? voltageV,
        int? sfpLinkSpeedMbps,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("sfp")
            .Tag("device_mac", NormalizeMac(deviceMac))
            .Tag("port_name", portName)
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (rxPowerDbm.HasValue) point = point.Field("rx_power_dbm", rxPowerDbm.Value);
        if (txPowerDbm.HasValue) point = point.Field("tx_power_dbm", txPowerDbm.Value);
        if (txBiasMa.HasValue) point = point.Field("tx_bias_ma", txBiasMa.Value);
        if (temperatureC.HasValue) point = point.Field("temperature_c", temperatureC.Value);
        if (voltageV.HasValue) point = point.Field("voltage_v", voltageV.Value);
        if (sfpLinkSpeedMbps.HasValue) point = point.Field("sfp_link_speed_mbps", sfpLinkSpeedMbps.Value);

        Enqueue(point, longterm: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Write cellular modem signal metrics for time-series charting.
    /// Tags identify the modem; fields capture all available signal/band/carrier data.
    /// Written to the longterm bucket since cellular trends are useful over weeks/months.
    /// </summary>
    public Task WriteCellularAsync(
        string modemId,
        string modemName,
        string provider,
        string? networkMode,
        string? carrier,
        string? bandName,
        int? channel,
        int? bandwidthMhz,
        double? rsrp,
        double? rsrq,
        double? snr,
        double? rssi,
        int? signalQuality,
        int? signalBars,
        bool? isRoaming,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("cellular")
            .Tag("modem_id", modemId)
            .Tag("modem_name", modemName)
            .Tag("provider", provider)
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (!string.IsNullOrEmpty(networkMode)) point = point.Tag("network_mode", networkMode);
        if (!string.IsNullOrEmpty(carrier)) point = point.Field("carrier", carrier);
        if (!string.IsNullOrEmpty(bandName)) point = point.Field("band", bandName);

        if (rsrp.HasValue) point = point.Field("rsrp", rsrp.Value);
        if (rsrq.HasValue) point = point.Field("rsrq", rsrq.Value);
        if (snr.HasValue) point = point.Field("snr", snr.Value);
        if (rssi.HasValue) point = point.Field("rssi", rssi.Value);
        if (signalQuality.HasValue) point = point.Field("signal_quality", signalQuality.Value);
        if (signalBars.HasValue) point = point.Field("signal_bars", signalBars.Value);
        if (channel.HasValue) point = point.Field("channel", channel.Value);
        if (bandwidthMhz.HasValue) point = point.Field("bandwidth_mhz", bandwidthMhz.Value);
        if (isRoaming.HasValue) point = point.Field("roaming", isRoaming.Value);

        Enqueue(point, longterm: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Write cable modem aggregate metrics for time-series charting.
    /// Per-channel detail is NOT written; only averages and error deltas.
    /// </summary>
    public Task WriteCableModemAsync(
        string cmId,
        string cmName,
        double? dsPowerAvgDbmv,
        double? dsSnrAvgDb,
        double? usPowerAvgDbmv,
        int lockedDsChannels,
        int lockedUsChannels,
        long correctablesDelta,
        long uncorrectablesDelta,
        long correctablesTotal,
        long uncorrectablesTotal,
        int channelsWithUncorrectables,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("cable_modem")
            .Tag("cm_id", cmId)
            .Tag("cm_name", cmName)
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (dsPowerAvgDbmv.HasValue) point = point.Field("ds_power_avg_dbmv", dsPowerAvgDbmv.Value);
        if (dsSnrAvgDb.HasValue) point = point.Field("ds_snr_avg_db", dsSnrAvgDb.Value);
        if (usPowerAvgDbmv.HasValue) point = point.Field("us_power_avg_dbmv", usPowerAvgDbmv.Value);
        point = point.Field("locked_ds_channels", lockedDsChannels);
        point = point.Field("locked_us_channels", lockedUsChannels);
        point = point.Field("correctables_delta", correctablesDelta);
        point = point.Field("uncorrectables_delta", uncorrectablesDelta);
        point = point.Field("correctables_total", correctablesTotal);
        point = point.Field("uncorrectables_total", uncorrectablesTotal);
        point = point.Field("channels_with_uncorrectables", channelsWithUncorrectables);

        Enqueue(point, longterm: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Write external ONT DDM metrics for time-series charting.
    /// Same physical measurements as SFP DDM but sourced from the ISP's device.
    /// </summary>
    public Task WriteOntAsync(
        string ontId,
        string ontName,
        double? rxPowerDbm,
        double? txPowerDbm,
        double? temperatureC,
        double? voltageV,
        double? biasMa,
        long? fecErrors,
        long? bipErrors,
        string? ponType,
        string? wavelength,
        string? ponLinkStatus,
        int? bwpSpeedMbps,
        int? sfpLinkSpeedMbps,
        DateTime timestamp,
        long? linkUptimeSeconds = null,
        string? oltVendor = null,
        string? oltModel = null)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("ont")
            .Tag("ont_id", ontId)
            .Tag("ont_name", ontName)
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (!string.IsNullOrEmpty(ponType)) point = point.Tag("pon_type", ponType);
        if (!string.IsNullOrEmpty(wavelength)) point = point.Tag("wavelength", wavelength);
        if (!string.IsNullOrEmpty(oltVendor)) point = point.Tag("olt_vendor", oltVendor);
        if (!string.IsNullOrEmpty(oltModel)) point = point.Tag("olt_model", oltModel);

        if (rxPowerDbm.HasValue) point = point.Field("rx_power_dbm", rxPowerDbm.Value);
        if (txPowerDbm.HasValue) point = point.Field("tx_power_dbm", txPowerDbm.Value);
        if (temperatureC.HasValue) point = point.Field("temperature_c", temperatureC.Value);
        if (voltageV.HasValue) point = point.Field("voltage_v", voltageV.Value);
        if (biasMa.HasValue) point = point.Field("bias_ma", biasMa.Value);
        if (fecErrors.HasValue) point = point.Field("fec_errors", fecErrors.Value);
        if (bipErrors.HasValue) point = point.Field("bip_errors", bipErrors.Value);
        if (!string.IsNullOrEmpty(ponLinkStatus)) point = point.Field("pon_link_status", ponLinkStatus);
        if (bwpSpeedMbps.HasValue) point = point.Field("bwp_speed_mbps", bwpSpeedMbps.Value);
        if (sfpLinkSpeedMbps.HasValue) point = point.Field("sfp_link_speed_mbps", sfpLinkSpeedMbps.Value);
        if (linkUptimeSeconds.HasValue) point = point.Field("link_uptime_seconds", linkUptimeSeconds.Value);

        Enqueue(point, longterm: true);
        return Task.CompletedTask;
    }

    public Task WriteEventAsync(
        string deviceMac,
        string eventType,
        string severity,
        string? detail,
        string? ifName,
        string? oldValue,
        string? newValue,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("events")
            .Tag("device_mac", NormalizeMac(deviceMac))
            .Tag("event_type", eventType)
            .Tag("severity", severity)
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        // Events always have at least one field so InfluxDB accepts them.
        point = point.Field("detail", detail ?? string.Empty);
        if (!string.IsNullOrEmpty(ifName)) point = point.Field("if_name", ifName);
        if (!string.IsNullOrEmpty(oldValue)) point = point.Field("old_value", oldValue);
        if (!string.IsNullOrEmpty(newValue)) point = point.Field("new_value", newValue);

        Enqueue(point, longterm: true);
        return Task.CompletedTask;
    }

    // ---- Read API (Flux queries) ----

    /// <summary>
    /// Per-port time-series of computed rates for one device. Used by the diagnostic
    /// view to plot ingress/egress per ifName over a chosen window. Returns rows
    /// ordered by time.
    /// </summary>
    public async Task<IReadOnlyList<InterfaceRatePoint>> QueryInterfaceRatesAsync(
        string deviceMac,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<InterfaceRatePoint>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);
        var mac = NormalizeMac(deviceMac);
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""interface_counters"")
  |> filter(fn: (r) => r.device_mac == ""{mac}"")
  |> filter(fn: (r) => r._field == ""rate_in_bps"" or r._field == ""rate_out_bps"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new List<InterfaceRatePoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            results.Add(new InterfaceRatePoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                IfName = record.GetValueByKey("if_name") as string ?? "?",
                RateInBps = AsDoubleOrNull(record.GetValueByKey("rate_in_bps")),
                RateOutBps = AsDoubleOrNull(record.GetValueByKey("rate_out_bps"))
            });
        }
        return results;
    }

    /// <summary>
    /// Raw interface rate query for a single device - no aggregateWindow, no pivot.
    /// Returns raw rate_in_bps and rate_out_bps points paired in C#. Much cheaper
    /// than the aggregated variant for short-range playback where data is already
    /// at native 5s cadence.
    /// </summary>
    public async Task<IReadOnlyList<InterfaceRatePoint>> QueryInterfaceRatesRawAsync(
        string deviceMac,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<InterfaceRatePoint>();
        var mac = NormalizeMac(deviceMac);
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""interface_counters"")
  |> filter(fn: (r) => r.device_mac == ""{mac}"")
  |> filter(fn: (r) => r._field == ""rate_in_bps"" or r._field == ""rate_out_bps"")
";
        var rateIn = new Dictionary<(string ifName, long ticks), double>();
        var rateOut = new Dictionary<(string ifName, long ticks), double>();
        var times = new Dictionary<(string ifName, long ticks), DateTime>();

        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var ifName = record.GetValueByKey("if_name") as string ?? "?";
            var time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow);
            var field = record.GetValueByKey("_field") as string;
            var value = AsDoubleOrNull(record.GetValueByKey("_value"));
            if (value == null) continue;

            var key = (ifName, time.Ticks);
            times[key] = time;
            if (field == "rate_in_bps") rateIn[key] = value.Value;
            else if (field == "rate_out_bps") rateOut[key] = value.Value;
        }

        var results = new List<InterfaceRatePoint>(times.Count);
        foreach (var (key, time) in times)
        {
            results.Add(new InterfaceRatePoint
            {
                Time = time,
                IfName = key.ifName,
                RateInBps = rateIn.TryGetValue(key, out var ri) ? ri : null,
                RateOutBps = rateOut.TryGetValue(key, out var ro) ? ro : null,
            });
        }
        return results;
    }

    /// <summary>
    /// Batch variant: fetches interface rates for a set of devices in one query.
    /// Returns results grouped by device MAC for caller-side partitioning.
    /// </summary>
    public async Task<Dictionary<string, List<InterfaceRatePoint>>> QueryBatchInterfaceRatesAsync(
        IEnumerable<string> deviceMacs,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return new Dictionary<string, List<InterfaceRatePoint>>(StringComparer.OrdinalIgnoreCase);
        var macs = deviceMacs.Select(NormalizeMac).Distinct().ToList();
        if (macs.Count == 0) return new Dictionary<string, List<InterfaceRatePoint>>(StringComparer.OrdinalIgnoreCase);

        var window = aggregateWindow ?? PickAggregateWindow(to - from);
        var macFilter = string.Join(" or ", macs.Select(m => $@"r.device_mac == ""{m}"""));
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""interface_counters"")
  |> filter(fn: (r) => {macFilter})
  |> filter(fn: (r) => r._field == ""rate_in_bps"" or r._field == ""rate_out_bps"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new Dictionary<string, List<InterfaceRatePoint>>(StringComparer.OrdinalIgnoreCase);
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var mac = record.GetValueByKey("device_mac") as string ?? "";
            var point = new InterfaceRatePoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                IfName = record.GetValueByKey("if_name") as string ?? "?",
                RateInBps = AsDoubleOrNull(record.GetValueByKey("rate_in_bps")),
                RateOutBps = AsDoubleOrNull(record.GetValueByKey("rate_out_bps"))
            };
            if (!results.TryGetValue(mac, out var list))
            {
                list = new List<InterfaceRatePoint>();
                results[mac] = list;
            }
            list.Add(point);
        }
        return results;
    }

    /// <summary>
    /// Point-in-time snapshot of every interface_counters field for one or more
    /// devices. Returns the most recent point per (device, interface) within a
    /// short window around <paramref name="at"/> (or the latest available when
    /// <paramref name="at"/> is null). Used by the Live View port stats table,
    /// which reads the value at the current map scrubber position.
    /// </summary>
    /// <param name="deviceMacs">Devices to include; null or empty returns all polled devices.</param>
    /// <param name="at">Historic playback instant, or null for the latest sample.</param>
    public async Task<IReadOnlyList<PortStatsPoint>> QueryPortStatsAsync(
        IReadOnlyList<string>? deviceMacs,
        DateTime? at,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<PortStatsPoint>();

        string rangeClause;
        if (at.HasValue)
        {
            // Mirror the historic snapshot window used elsewhere on the Live tab so
            // the table lines up with the map and WAN chart at the same scrub point.
            var center = at.Value.ToUniversalTime();
            rangeClause = $"range(start: {ToFluxInstant(center.AddSeconds(-90))}, stop: {ToFluxInstant(center.AddSeconds(30))})";
        }
        else
        {
            // Wide enough to catch the newest sample on the slowest SNMP tier;
            // last() collapses it to the single most recent point per interface.
            rangeClause = "range(start: -120s)";
        }

        var macFilter = "";
        if (deviceMacs != null && deviceMacs.Count > 0)
        {
            var macs = deviceMacs.Select(NormalizeMac).Distinct().ToList();
            macFilter = "\n  |> filter(fn: (r) => " +
                string.Join(" or ", macs.Select(m => $@"r.device_mac == ""{m}""")) + ")";
        }

        var flux = $@"
from(bucket: ""{_bucket}"")
  |> {rangeClause}
  |> filter(fn: (r) => r._measurement == ""interface_counters""){macFilter}
  |> last()
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var raw = new List<PortStatsPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            raw.Add(new PortStatsPoint
            {
                DeviceMac = record.GetValueByKey("device_mac") as string ?? "",
                IfName = record.GetValueByKey("if_name") as string ?? "",
                PortId = record.GetValueByKey("port_id") as string ?? "",
                OperStatus = AsIntOrNull(record.GetValueByKey("oper_status")),
                SpeedBps = AsLongOrNull(record.GetValueByKey("speed_bps")),
                RateInBps = AsDoubleOrNull(record.GetValueByKey("rate_in_bps")),
                RateOutBps = AsDoubleOrNull(record.GetValueByKey("rate_out_bps")),
                BytesIn = AsLongOrNull(record.GetValueByKey("bytes_in")),
                BytesOut = AsLongOrNull(record.GetValueByKey("bytes_out")),
                UcastPktsIn = AsLongOrNull(record.GetValueByKey("ucast_pkts_in")),
                UcastPktsOut = AsLongOrNull(record.GetValueByKey("ucast_pkts_out")),
                McastPktsIn = AsLongOrNull(record.GetValueByKey("mcast_pkts_in")),
                McastPktsOut = AsLongOrNull(record.GetValueByKey("mcast_pkts_out")),
                BcastPktsIn = AsLongOrNull(record.GetValueByKey("bcast_pkts_in")),
                BcastPktsOut = AsLongOrNull(record.GetValueByKey("bcast_pkts_out")),
                ErrorsIn = AsLongOrNull(record.GetValueByKey("errors_in")),
                ErrorsOut = AsLongOrNull(record.GetValueByKey("errors_out")),
                DiscardsIn = AsLongOrNull(record.GetValueByKey("discards_in")),
                DiscardsOut = AsLongOrNull(record.GetValueByKey("discards_out")),
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
            });
        }

        // Different fields can land on different timestamps - rate_in/out are only
        // written when a rate is computable, so a fresh point may carry oper_status
        // and packet counters while the rates sit on an earlier point. pivot-by-time
        // then splits one interface into several partial rows. Collapse to a single
        // row per (device, interface), coalescing each field from the most recent
        // sample that carries it.
        return raw
            .GroupBy(p => (p.DeviceMac, p.IfName), TupleMacIfComparer)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(p => p.Time).ToList();
                long? FirstLong(Func<PortStatsPoint, long?> sel) => ordered.Select(sel).FirstOrDefault(v => v.HasValue);
                double? FirstDouble(Func<PortStatsPoint, double?> sel) => ordered.Select(sel).FirstOrDefault(v => v.HasValue);
                return new PortStatsPoint
                {
                    DeviceMac = g.Key.DeviceMac,
                    IfName = g.Key.IfName,
                    PortId = ordered.Select(p => p.PortId).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "",
                    OperStatus = ordered.Select(p => p.OperStatus).FirstOrDefault(v => v.HasValue),
                    SpeedBps = FirstLong(p => p.SpeedBps),
                    RateInBps = FirstDouble(p => p.RateInBps),
                    RateOutBps = FirstDouble(p => p.RateOutBps),
                    BytesIn = FirstLong(p => p.BytesIn),
                    BytesOut = FirstLong(p => p.BytesOut),
                    UcastPktsIn = FirstLong(p => p.UcastPktsIn),
                    UcastPktsOut = FirstLong(p => p.UcastPktsOut),
                    McastPktsIn = FirstLong(p => p.McastPktsIn),
                    McastPktsOut = FirstLong(p => p.McastPktsOut),
                    BcastPktsIn = FirstLong(p => p.BcastPktsIn),
                    BcastPktsOut = FirstLong(p => p.BcastPktsOut),
                    ErrorsIn = FirstLong(p => p.ErrorsIn),
                    ErrorsOut = FirstLong(p => p.ErrorsOut),
                    DiscardsIn = FirstLong(p => p.DiscardsIn),
                    DiscardsOut = FirstLong(p => p.DiscardsOut),
                    Time = ordered[0].Time,
                };
            })
            .ToList();
    }

    private static readonly IEqualityComparer<(string DeviceMac, string IfName)> TupleMacIfComparer =
        new MacIfTupleComparer();

    private sealed class MacIfTupleComparer : IEqualityComparer<(string DeviceMac, string IfName)>
    {
        public bool Equals((string DeviceMac, string IfName) x, (string DeviceMac, string IfName) y) =>
            string.Equals(x.DeviceMac, y.DeviceMac, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.IfName, y.IfName, StringComparison.Ordinal);

        public int GetHashCode((string DeviceMac, string IfName) obj) =>
            HashCode.Combine(obj.DeviceMac.ToLowerInvariant(), obj.IfName);
    }

    public async Task<IReadOnlyList<WanRatePoint>> QueryGatewayWanRatesAsync(
        string deviceMac,
        IReadOnlyList<string> wanIfNames,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured || wanIfNames.Count == 0) return Array.Empty<WanRatePoint>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);
        var mac = NormalizeMac(deviceMac);
        var ifFilter = string.Join(" or ", wanIfNames.Select(n =>
            $@"r.if_name == ""{SanitizeFluxString(n)}"""));
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""interface_counters"")
  |> filter(fn: (r) => r.device_mac == ""{mac}"")
  |> filter(fn: (r) => {ifFilter})
  |> filter(fn: (r) => r._field == ""rate_in_bps"" or r._field == ""rate_out_bps"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> group(columns: [""_time"", ""_field""])
  |> sum()
  |> group()
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
  |> sort(columns: [""_time""])
";
        var results = new List<WanRatePoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            // SNMP rate_in_bps on a WAN interface = bytes received from ISP = download.
            // rate_out_bps = bytes sent to ISP = upload.
            results.Add(new WanRatePoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                DownloadBps = AsDoubleOrNull(record.GetValueByKey("rate_in_bps")),
                UploadBps = AsDoubleOrNull(record.GetValueByKey("rate_out_bps"))
            });
        }
        return results;
    }

    public record WanRatePoint
    {
        public required DateTime Time { get; init; }
        public double? DownloadBps { get; init; }
        public double? UploadBps { get; init; }
    }

    /// <summary>Per-device CPU/memory/temperature trace.</summary>
    public async Task<IReadOnlyList<DeviceHealthPoint>> QueryDeviceHealthAsync(
        string deviceMac,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<DeviceHealthPoint>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);
        var mac = NormalizeMac(deviceMac);
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""device_health"")
  |> filter(fn: (r) => r.device_mac == ""{mac}"")
  |> filter(fn: (r) => r._field == ""cpu_percent"" or r._field == ""memory_used_percent"" or r._field == ""temperature_c"" or r._field == ""uptime_seconds"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new List<DeviceHealthPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            results.Add(new DeviceHealthPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                CpuPercent = AsDoubleOrNull(record.GetValueByKey("cpu_percent")),
                MemoryUsedPercent = AsDoubleOrNull(record.GetValueByKey("memory_used_percent")),
                TemperatureC = AsDoubleOrNull(record.GetValueByKey("temperature_c")),
                UptimeSeconds = (long?)AsDoubleOrNull(record.GetValueByKey("uptime_seconds"))
            });
        }
        results.Sort((a, b) => a.Time.CompareTo(b.Time));
        return results;
    }

    /// <summary>
    /// Query custom OID field values from device_health for a specific device.
    /// Returns a time series per field name.
    /// </summary>
    public async Task<Dictionary<string, List<(DateTime Time, double Value)>>> QueryCustomOidFieldsAsync(
        string deviceMac,
        IReadOnlyList<string> fieldNames,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured || fieldNames.Count == 0)
            return new Dictionary<string, List<(DateTime, double)>>();

        var window = aggregateWindow ?? PickAggregateWindow(to - from);
        var mac = NormalizeMac(deviceMac);
        var fieldFilter = string.Join(" or ", fieldNames.Select(f => $"r._field == \"{f}\""));
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""device_health"")
  |> filter(fn: (r) => r.device_mac == ""{mac}"")
  |> filter(fn: (r) => {fieldFilter})
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
";
        var result = new Dictionary<string, List<(DateTime, double)>>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var field = record.GetValueByKey("_field")?.ToString();
            var value = AsDoubleOrNull(record.GetValueByKey("_value"));
            if (field == null || value == null) continue;
            if (!result.TryGetValue(field, out var list))
            {
                list = new List<(DateTime, double)>();
                result[field] = list;
            }
            list.Add((ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow), value.Value));
        }
        foreach (var list in result.Values) list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    /// <summary>Raw device health query - no aggregation, pairs fields in C#.</summary>
    public async Task<IReadOnlyList<DeviceHealthPoint>> QueryDeviceHealthRawAsync(
        string deviceMac, DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<DeviceHealthPoint>();
        var mac = NormalizeMac(deviceMac);
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""device_health"")
  |> filter(fn: (r) => r.device_mac == ""{mac}"")
  |> filter(fn: (r) => r._field == ""cpu_percent"" or r._field == ""memory_used_percent"" or r._field == ""temperature_c"" or r._field == ""uptime_seconds"")
";
        var cpu = new Dictionary<long, double>();
        var mem = new Dictionary<long, double>();
        var temp = new Dictionary<long, double>();
        var uptime = new Dictionary<long, double>();
        var times = new Dictionary<long, DateTime>();

        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow);
            var field = record.GetValueByKey("_field") as string;
            var value = AsDoubleOrNull(record.GetValueByKey("_value"));
            if (value == null) continue;
            var key = time.Ticks;
            times[key] = time;
            if (field == "cpu_percent") cpu[key] = value.Value;
            else if (field == "memory_used_percent") mem[key] = value.Value;
            else if (field == "temperature_c") temp[key] = value.Value;
            else if (field == "uptime_seconds") uptime[key] = value.Value;
        }

        return times.Select(kv => new DeviceHealthPoint
        {
            Time = kv.Value,
            CpuPercent = cpu.TryGetValue(kv.Key, out var c) ? c : null,
            MemoryUsedPercent = mem.TryGetValue(kv.Key, out var m) ? m : null,
            TemperatureC = temp.TryGetValue(kv.Key, out var t) ? t : null,
            UptimeSeconds = uptime.TryGetValue(kv.Key, out var u) ? (long?)u : null,
        }).OrderBy(p => p.Time).ToList();
    }

    /// <summary>Raw latency query by target type - no aggregation, pairs fields in C#.</summary>
    public async Task<IReadOnlyList<LatencyPoint>> QueryLatencyByTargetTypeRawAsync(
        MonitoringTargetType targetType, DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return Array.Empty<LatencyPoint>();
        var typeTag = targetType.ToString().ToLowerInvariant();
        var typeFilter = targetType == MonitoringTargetType.InternetService
            ? @"r.target_type == ""internetservice"" or r.target_type == ""wan"""
            : $@"r.target_type == ""{typeTag}""";
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""latency"")
  |> filter(fn: (r) => {typeFilter})
  |> filter(fn: (r) => r._field == ""rtt_avg_ms"" or r._field == ""loss_percent"")
";
        var rtt = new Dictionary<(string targetId, long ticks), double>();
        var loss = new Dictionary<(string targetId, long ticks), double>();
        var times = new Dictionary<(string targetId, long ticks), DateTime>();

        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var targetId = record.GetValueByKey("target_id") as string;
            if (string.IsNullOrEmpty(targetId)) continue;
            var time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow);
            var field = record.GetValueByKey("_field") as string;
            var value = AsDoubleOrNull(record.GetValueByKey("_value"));
            if (value == null) continue;
            var key = (targetId, time.Ticks);
            times[key] = time;
            if (field == "rtt_avg_ms") rtt[key] = value.Value;
            else if (field == "loss_percent") loss[key] = value.Value;
        }

        return times.Select(kv => new LatencyPoint
        {
            Time = kv.Value,
            RttAvgMs = rtt.TryGetValue(kv.Key, out var r) ? r : null,
            LossPercent = loss.TryGetValue(kv.Key, out var l) ? l : null,
        }).OrderBy(p => p.Time).ToList();
    }

    /// <summary>Time-series of RTT and loss for multiple monitoring targets, keyed by target_id.</summary>
    public async Task<Dictionary<string, List<LatencyPoint>>> QueryLatencyByTargetTypeAsync(
        MonitoringTargetType targetType,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return new Dictionary<string, List<LatencyPoint>>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);
        var typeTag = targetType.ToString().ToLowerInvariant();
        // InternetService replaced Wan — old data has target_type=wan, new has internetservice.
        var typeFilter = targetType == MonitoringTargetType.InternetService
            ? @"r.target_type == ""internetservice"" or r.target_type == ""wan"""
            : $@"r.target_type == ""{typeTag}""";

        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""latency"")
  |> filter(fn: (r) => {typeFilter})
  |> filter(fn: (r) => r._field == ""rtt_avg_ms"" or r._field == ""loss_percent"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new Dictionary<string, List<LatencyPoint>>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var targetId = record.GetValueByKey("target_id") as string;
            if (string.IsNullOrEmpty(targetId)) continue;
            if (!results.TryGetValue(targetId, out var list))
            {
                list = new List<LatencyPoint>();
                results[targetId] = list;
            }
            list.Add(new LatencyPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                RttAvgMs = AsDoubleOrNull(record.GetValueByKey("rtt_avg_ms")),
                LossPercent = AsDoubleOrNull(record.GetValueByKey("loss_percent"))
            });
        }
        // InternetService queries include legacy target_type=wan data; InfluxDB returns
        // each tag series separately so merged lists may not be in chronological order.
        foreach (var list in results.Values)
            list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return results;
    }

    /// <summary>
    /// Like QueryLatencyByTargetTypeAsync but also pivots max RTT and jitter, which the
    /// ISP Health scorer and congestion/step detectors need. Kept separate so existing
    /// chart callers keep the leaner LatencyPoint shape.
    /// </summary>
    public async Task<Dictionary<string, List<LatencySeriesPoint>>> QueryLatencyDetailByTargetTypeAsync(
        MonitoringTargetType targetType,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return new Dictionary<string, List<LatencySeriesPoint>>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);
        var typeTag = targetType.ToString().ToLowerInvariant();
        var typeFilter = targetType == MonitoringTargetType.InternetService
            ? @"r.target_type == ""internetservice"" or r.target_type == ""wan"""
            : $@"r.target_type == ""{typeTag}""";

        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""latency"")
  |> filter(fn: (r) => {typeFilter})
  |> filter(fn: (r) => r._field == ""rtt_avg_ms"" or r._field == ""rtt_max_ms"" or r._field == ""jitter_ms"" or r._field == ""loss_percent"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new Dictionary<string, List<LatencySeriesPoint>>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var targetId = record.GetValueByKey("target_id") as string;
            if (string.IsNullOrEmpty(targetId)) continue;
            if (!results.TryGetValue(targetId, out var list))
            {
                list = new List<LatencySeriesPoint>();
                results[targetId] = list;
            }
            list.Add(new LatencySeriesPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                RttAvgMs = AsDoubleOrNull(record.GetValueByKey("rtt_avg_ms")),
                RttMaxMs = AsDoubleOrNull(record.GetValueByKey("rtt_max_ms")),
                JitterMs = AsDoubleOrNull(record.GetValueByKey("jitter_ms")),
                LossPercent = AsDoubleOrNull(record.GetValueByKey("loss_percent"))
            });
        }
        foreach (var list in results.Values)
            list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return results;
    }

    /// <summary>
    /// Per-window rtt/jitter/loss detail for a single target by its id - used to pull just the LAN
    /// gateway's loss for outage scoping without loading every fabric device's series.
    /// </summary>
    public async Task<List<LatencySeriesPoint>> QueryLatencyDetailByTargetIdAsync(
        string targetId,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return new List<LatencySeriesPoint>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""latency"")
  |> filter(fn: (r) => r.target_id == ""{targetId}"")
  |> filter(fn: (r) => r._field == ""rtt_avg_ms"" or r._field == ""rtt_max_ms"" or r._field == ""jitter_ms"" or r._field == ""loss_percent"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var list = new List<LatencySeriesPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            list.Add(new LatencySeriesPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                RttAvgMs = AsDoubleOrNull(record.GetValueByKey("rtt_avg_ms")),
                RttMaxMs = AsDoubleOrNull(record.GetValueByKey("rtt_max_ms")),
                JitterMs = AsDoubleOrNull(record.GetValueByKey("jitter_ms")),
                LossPercent = AsDoubleOrNull(record.GetValueByKey("loss_percent"))
            });
        }
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    public record LatencySeriesPoint
    {
        public required DateTime Time { get; init; }
        public double? RttAvgMs { get; init; }
        public double? RttMaxMs { get; init; }
        public double? JitterMs { get; init; }
        public double? LossPercent { get; init; }
    }

    /// <summary>Mean RTT and loss across all ISP+Transit targets, aggregated per time window.
    /// Averages each target into a per-window mean first (normalizing uneven probe
    /// intervals), then averages within each target_type, then averages the two category
    /// means - the same weighting as /api/monitoring/live-stats, so the WAN live chart
    /// doesn't jump when its buffer swaps between history and live samples.</summary>
    public async Task<IReadOnlyList<LatencyPoint>> QueryMeanIspTransitLatencyAsync(
        DateTime from,
        DateTime to,
        IReadOnlyList<string>? enabledTargetIds = null,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return Array.Empty<LatencyPoint>();
        var window = aggregateWindow ?? TimeSpan.FromSeconds(
            Math.Max(10, (int)((to - from).TotalSeconds / 150)));
        // Query extra lead-in so every target has probed at least once before the first
        // visible point, priming fill(usePrevious). Without it, the oldest windows
        // average a partial subset of targets and the left edge of the chart skews high
        // or low depending on which targets happen to be missing. Warmup rows are
        // dropped from the results below.
        var warmup = TimeSpan.FromSeconds(60) + window;
        var queryFrom = from - warmup;
        var targetFilter = "";
        if (enabledTargetIds is { Count: > 0 })
        {
            var idFilter = string.Join(" or ", enabledTargetIds.Select(id =>
                $@"r.target_id == ""{SanitizeFluxString(id)}"""));
            targetFilter = $"\n  |> filter(fn: (r) => {idFilter})";
        }
        var flux = $@"
base = from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(queryFrom)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""latency"")
  |> filter(fn: (r) => r.target_type == ""accessisp"" or r.target_type == ""transit""){targetFilter}

rtt = base
  |> filter(fn: (r) => r._field == ""rtt_avg_ms"")
  |> group(columns: [""target_id"", ""target_type""])
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: true)
  |> fill(usePrevious: true)
  |> filter(fn: (r) => exists r._value)
  |> group(columns: [""target_type"", ""_time""])
  |> mean()
  |> group(columns: [""_time""])
  |> mean()
  |> group()
  |> sort(columns: [""_time""])
  |> map(fn: (r) => ({{_time: r._time, _value: r._value, _field: ""rtt_avg_ms""}}))

loss = base
  |> filter(fn: (r) => r._field == ""loss_percent"")
  |> group(columns: [""target_id"", ""target_type""])
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> group(columns: [""target_type"", ""_time""])
  |> mean()
  |> group(columns: [""_time""])
  |> mean()
  |> group()
  |> sort(columns: [""_time""])
  |> map(fn: (r) => ({{_time: r._time, _value: r._value, _field: ""loss_percent""}}))

union(tables: [rtt, loss])
  |> group()
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new List<LatencyPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow);
            if (time < from) continue;
            results.Add(new LatencyPoint
            {
                Time = time,
                RttAvgMs = AsDoubleOrNull(record.GetValueByKey("rtt_avg_ms")),
                LossPercent = AsDoubleOrNull(record.GetValueByKey("loss_percent"))
            });
        }
        return results;
    }

    /// <summary>Time-series of RTT and loss for a single monitoring target.</summary>
    public async Task<IReadOnlyList<LatencyPoint>> QueryLatencyAsync(
        string targetId,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<LatencyPoint>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""latency"")
  |> filter(fn: (r) => r.target_id == ""{targetId}"")
  |> filter(fn: (r) => r._field == ""rtt_avg_ms"" or r._field == ""loss_percent"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new List<LatencyPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            results.Add(new LatencyPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                RttAvgMs = AsDoubleOrNull(record.GetValueByKey("rtt_avg_ms")),
                LossPercent = AsDoubleOrNull(record.GetValueByKey("loss_percent"))
            });
        }
        return results;
    }

    /// <summary>Per-SFP DDM time-series for a set of (device_mac, port_name) pairs.</summary>
    public async Task<Dictionary<string, List<SfpPoint>>> QuerySfpByModulesAsync(
        IReadOnlyList<(string DeviceMac, string PortName)> modules,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured || modules.Count == 0) return new Dictionary<string, List<SfpPoint>>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);

        var macFilter = string.Join(" or ", modules.Select(m =>
            $@"(r.device_mac == ""{NormalizeMac(m.DeviceMac)}"" and r.port_name == ""{SanitizeFluxString(m.PortName)}"")"));

        var flux = $@"
from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""sfp"")
  |> filter(fn: (r) => {macFilter})
  |> filter(fn: (r) => r._field == ""rx_power_dbm"" or r._field == ""tx_power_dbm"" or r._field == ""temperature_c"" or r._field == ""voltage_v"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new Dictionary<string, List<SfpPoint>>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var mac = record.GetValueByKey("device_mac") as string ?? "";
            var port = record.GetValueByKey("port_name") as string ?? "";
            var key = $"{mac}:{port}";
            if (!results.TryGetValue(key, out var list))
            {
                list = new List<SfpPoint>();
                results[key] = list;
            }
            list.Add(new SfpPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                RxPowerDbm = AsDoubleOrNull(record.GetValueByKey("rx_power_dbm")),
                TxPowerDbm = AsDoubleOrNull(record.GetValueByKey("tx_power_dbm")),
                TemperatureC = AsDoubleOrNull(record.GetValueByKey("temperature_c")),
                VoltageV = AsDoubleOrNull(record.GetValueByKey("voltage_v"))
            });
        }
        return results;
    }

    /// <summary>
    /// Query cellular modem signal metrics over time. Groups by modem_id + network_mode
    /// so LTE and NR5G are separate series (important for NSA dual-connectivity).
    /// Reads from the longterm bucket.
    /// </summary>
    public async Task<Dictionary<string, List<CellularPoint>>> QueryCellularAsync(
        DateTime from,
        DateTime to,
        string? modemId = null,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return new Dictionary<string, List<CellularPoint>>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);

        var modemFilter = !string.IsNullOrEmpty(modemId)
            ? $@"|> filter(fn: (r) => r.modem_id == ""{SanitizeFluxString(modemId)}"")"
            : "";

        var flux = $@"
from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""cellular"")
  {modemFilter}
  |> filter(fn: (r) => r._field == ""rsrp"" or r._field == ""rsrq"" or r._field == ""snr"" or r._field == ""rssi"" or r._field == ""signal_quality"" or r._field == ""band"" or r._field == ""carrier"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: last, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new Dictionary<string, List<CellularPoint>>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var modemKey = record.GetValueByKey("modem_id") as string ?? "";
            var mode = record.GetValueByKey("network_mode") as string;
            var key = string.IsNullOrEmpty(mode) ? modemKey : $"{modemKey}:{mode}";

            if (!results.TryGetValue(key, out var list))
            {
                list = new List<CellularPoint>();
                results[key] = list;
            }
            list.Add(new CellularPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                Rsrp = AsDoubleOrNull(record.GetValueByKey("rsrp")),
                Rsrq = AsDoubleOrNull(record.GetValueByKey("rsrq")),
                Snr = AsDoubleOrNull(record.GetValueByKey("snr")),
                Rssi = AsDoubleOrNull(record.GetValueByKey("rssi")),
                SignalQuality = AsIntOrNull(record.GetValueByKey("signal_quality")),
                NetworkMode = mode,
                Band = record.GetValueByKey("band") as string,
                Carrier = record.GetValueByKey("carrier") as string,
            });
        }
        return results;
    }

    /// <summary>
    /// Query cable modem aggregate metrics over a time range.
    /// Returns dict keyed by cm_id.
    /// </summary>
    public async Task<Dictionary<string, List<CmPoint>>> QueryCableModemAsync(
        DateTime from,
        DateTime to,
        string? cmId = null,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return new Dictionary<string, List<CmPoint>>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);

        var cmFilter = !string.IsNullOrEmpty(cmId)
            ? $@"|> filter(fn: (r) => r.cm_id == ""{SanitizeFluxString(cmId)}"")"
            : "";

        var winDur = ToFluxDuration(window);
        var flux = $@"
gauges = from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""cable_modem"")
  {cmFilter}
  |> filter(fn: (r) => r._field == ""ds_power_avg_dbmv"" or r._field == ""ds_snr_avg_db"" or r._field == ""us_power_avg_dbmv"" or r._field == ""locked_ds_channels"" or r._field == ""locked_us_channels"")
  |> aggregateWindow(every: {winDur}, fn: last, createEmpty: false)

deltas = from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""cable_modem"")
  {cmFilter}
  |> filter(fn: (r) => r._field == ""correctables_delta"" or r._field == ""uncorrectables_delta"")
  |> aggregateWindow(every: {winDur}, fn: sum, createEmpty: false)

union(tables: [gauges, deltas])
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new Dictionary<string, List<CmPoint>>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var key = record.GetValueByKey("cm_id") as string ?? "unknown";
            if (!results.TryGetValue(key, out var list))
            {
                list = new List<CmPoint>();
                results[key] = list;
            }
            list.Add(new CmPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                DsPowerAvgDbmv = AsDoubleOrNull(record.GetValueByKey("ds_power_avg_dbmv")),
                DsSnrAvgDb = AsDoubleOrNull(record.GetValueByKey("ds_snr_avg_db")),
                UsPowerAvgDbmv = AsDoubleOrNull(record.GetValueByKey("us_power_avg_dbmv")),
                LockedDsChannels = AsIntOrNull(record.GetValueByKey("locked_ds_channels")),
                LockedUsChannels = AsIntOrNull(record.GetValueByKey("locked_us_channels")),
                CorrDelta = AsLongOrNull(record.GetValueByKey("correctables_delta")),
                UncorrDelta = AsLongOrNull(record.GetValueByKey("uncorrectables_delta")),
            });
        }
        return results;
    }

    /// <summary>
    /// Query external ONT metrics over a time range.
    /// Returns dict keyed by ont_id.
    /// </summary>
    public async Task<Dictionary<string, List<OntPoint>>> QueryOntAsync(
        DateTime from,
        DateTime to,
        string? ontId = null,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return new Dictionary<string, List<OntPoint>>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);

        var ontFilter = !string.IsNullOrEmpty(ontId)
            ? $@"|> filter(fn: (r) => r.ont_id == ""{SanitizeFluxString(ontId)}"")"
            : "";

        var flux = $@"
from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""ont"")
  {ontFilter}
  |> filter(fn: (r) => r._field == ""rx_power_dbm"" or r._field == ""tx_power_dbm"" or r._field == ""temperature_c"" or r._field == ""voltage_v"" or r._field == ""bias_ma"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: last, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new Dictionary<string, List<OntPoint>>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var key = record.GetValueByKey("ont_id") as string ?? "unknown";
            if (!results.TryGetValue(key, out var list))
            {
                list = new List<OntPoint>();
                results[key] = list;
            }
            list.Add(new OntPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                RxPowerDbm = AsDoubleOrNull(record.GetValueByKey("rx_power_dbm")),
                TxPowerDbm = AsDoubleOrNull(record.GetValueByKey("tx_power_dbm")),
                TemperatureC = AsDoubleOrNull(record.GetValueByKey("temperature_c")),
                VoltageV = AsDoubleOrNull(record.GetValueByKey("voltage_v")),
                BiasMa = AsDoubleOrNull(record.GetValueByKey("bias_ma")),
            });
        }
        return results;
    }

    /// <summary>
    /// Historical WiFi client snapshots for timeline mode on the 3D map. Filter by
    /// AP MAC (tag), optionally by band (tag) and by client MAC (field). Returns
    /// rows ordered by time.
    /// </summary>
    public record ClientThroughputPoint
    {
        public DateTime Time { get; init; }
        public string? ClientMac { get; init; }
        public double? TxThroughputBps { get; init; }
        public double? RxThroughputBps { get; init; }
        /// <summary>Connection stats - populated for wifi_client only; null for wired_client.</summary>
        public int? SignalDbm { get; init; }
        public long? TxRateKbps { get; init; }
        public long? RxRateKbps { get; init; }
        /// <summary>Raw band tag ("2.4ghz"/"5ghz"/"6ghz"); caller normalizes for display.</summary>
        public string? Band { get; init; }
        /// <summary>AP the client was associated with (device_mac tag).</summary>
        public string? ApMac { get; init; }
    }

    public async Task<IReadOnlyList<ClientThroughputPoint>> QueryAllClientThroughputAsync(
        string measurement,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<ClientThroughputPoint>();
        // signal_dbm / tx_rate_kbps / rx_rate_kbps only exist on wifi_client; harmless to
        // request for wired_client (no rows match, columns come back absent -> null). band
        // and device_mac are tags and survive the pivot as columns.
        var flux = $@"from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""{measurement}"")
  |> filter(fn: (r) => r._field == ""tx_throughput_bps"" or r._field == ""rx_throughput_bps"" or r._field == ""client_mac"" or r._field == ""signal_dbm"" or r._field == ""tx_rate_kbps"" or r._field == ""rx_rate_kbps"")
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
  |> filter(fn: (r) => (exists r.tx_throughput_bps and r.tx_throughput_bps > 0.0) or (exists r.rx_throughput_bps and r.rx_throughput_bps > 0.0))";

        var results = new List<ClientThroughputPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            results.Add(new ClientThroughputPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                ClientMac = record.GetValueByKey("client_mac") as string,
                TxThroughputBps = AsDoubleOrNull(record.GetValueByKey("tx_throughput_bps")),
                RxThroughputBps = AsDoubleOrNull(record.GetValueByKey("rx_throughput_bps")),
                SignalDbm = (int?)AsDoubleOrNull(record.GetValueByKey("signal_dbm")),
                TxRateKbps = (long?)AsDoubleOrNull(record.GetValueByKey("tx_rate_kbps")),
                RxRateKbps = (long?)AsDoubleOrNull(record.GetValueByKey("rx_rate_kbps")),
                Band = record.GetValueByKey("band") as string,
                ApMac = record.GetValueByKey("device_mac") as string,
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<ClientThroughputPoint>> QueryClientThroughputAsync(
        string measurement,
        string clientMac,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<ClientThroughputPoint>();
        var mac = NormalizeMac(clientMac);
        var flux = $@"from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""{measurement}"")
  |> filter(fn: (r) => r._field == ""tx_throughput_bps"" or r._field == ""rx_throughput_bps"" or r._field == ""client_mac"")
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
  |> filter(fn: (r) => r.client_mac == ""{mac}"")";

        var results = new List<ClientThroughputPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            results.Add(new ClientThroughputPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                TxThroughputBps = AsDoubleOrNull(record.GetValueByKey("tx_throughput_bps")),
                RxThroughputBps = AsDoubleOrNull(record.GetValueByKey("rx_throughput_bps")),
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<WifiClientHistoryPoint>> QueryWifiClientHistoryAsync(
        string apMac,
        string? band,
        string? clientMac,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<WifiClientHistoryPoint>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);
        var ap = NormalizeMac(apMac);

        var fluxBuilder = new System.Text.StringBuilder();
        fluxBuilder.AppendLine($@"from(bucket: ""{_bucket}"")");
        fluxBuilder.AppendLine($@"  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})");
        fluxBuilder.AppendLine($@"  |> filter(fn: (r) => r._measurement == ""wifi_client"")");
        fluxBuilder.AppendLine($@"  |> filter(fn: (r) => r.device_mac == ""{ap}"")");
        if (!string.IsNullOrEmpty(band))
            fluxBuilder.AppendLine($@"  |> filter(fn: (r) => r.band == ""{band.ToLowerInvariant()}"")");
        // The fields we want pivoted into one row per timestamp.
        fluxBuilder.AppendLine(@"  |> filter(fn: (r) => r._field == ""signal_dbm"" or r._field == ""tx_throughput_bps"" or r._field == ""rx_throughput_bps"" or r._field == ""tx_rate_kbps"" or r._field == ""rx_rate_kbps"" or r._field == ""client_mac"")");
        fluxBuilder.AppendLine($@"  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: last, createEmpty: false)");
        fluxBuilder.AppendLine(@"  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")");
        // client_mac is a field (cardinality control), so post-filter after pivot.
        if (!string.IsNullOrEmpty(clientMac))
        {
            var c = NormalizeMac(clientMac);
            fluxBuilder.AppendLine($@"  |> filter(fn: (r) => r.client_mac == ""{c}"")");
        }

        var results = new List<WifiClientHistoryPoint>();
        await foreach (var record in QueryFluxAsync(fluxBuilder.ToString(), ct))
        {
            results.Add(new WifiClientHistoryPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                Band = record.GetValueByKey("band") as string ?? string.Empty,
                ClientMac = record.GetValueByKey("client_mac") as string,
                SignalDbm = AsDoubleOrNull(record.GetValueByKey("signal_dbm")),
                TxThroughputBps = AsDoubleOrNull(record.GetValueByKey("tx_throughput_bps")),
                RxThroughputBps = AsDoubleOrNull(record.GetValueByKey("rx_throughput_bps")),
                TxRateKbps = (long?)AsDoubleOrNull(record.GetValueByKey("tx_rate_kbps")),
                RxRateKbps = (long?)AsDoubleOrNull(record.GetValueByKey("rx_rate_kbps"))
            });
        }
        return results;
    }

    private async IAsyncEnumerable<InfluxDB.Client.Core.Flux.Domain.FluxRecord> QueryFluxAsync(
        string flux,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_client == null || string.IsNullOrEmpty(_org)) yield break;

        List<InfluxDB.Client.Core.Flux.Domain.FluxTable> tables;
        try
        {
            var queryApi = _client.GetQueryApi();
            tables = await queryApi.QueryAsync(flux, _org, ct);
        }
        catch (Exception ex) when (
            ex is InfluxDB.Client.Core.Exceptions.NotFoundException
            or InfluxDB.Client.Core.Exceptions.BadRequestException
            or InfluxDB.Client.Core.Exceptions.UnauthorizedException)
        {
            _logger.LogWarning("InfluxDB query failed ({Error}) — check Settings > Monitoring", ex.Message);
            _ = PersistHealthAsync(false, ex.Message, CancellationToken.None);
            yield break;
        }

        foreach (var table in tables)
            foreach (var record in table.Records)
                yield return record;
    }

    private static TimeSpan PickAggregateWindow(TimeSpan range)
    {
        // Target ~150 data points regardless of range. Floor at 5 s so short ranges
        // don't produce sub-second windows that InfluxDB can't aggregate meaningfully.
        const int targetPoints = 150;
        var windowSeconds = Math.Max(5, (int)(range.TotalSeconds / targetPoints));
        return TimeSpan.FromSeconds(windowSeconds);
    }

    private static string ToFluxInstant(DateTime t) =>
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string ToFluxDuration(TimeSpan window)
    {
        if (window.TotalDays >= 1) return $"{(int)window.TotalDays}d";
        if (window.TotalHours >= 1) return $"{(int)window.TotalHours}h";
        if (window.TotalMinutes >= 1) return $"{(int)window.TotalMinutes}m";
        return $"{Math.Max(1, (int)window.TotalSeconds)}s";
    }

    private static string SanitizeFluxString(string value) =>
        value.Replace("\"", "").Replace("\\", "").Replace(")", "").Replace("|>", "");

    private static DateTime ToUtc(DateTime t) =>
        t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);

    private static double? AsDoubleOrNull(object? v) => v switch
    {
        null => null,
        double d => d,
        float f => f,
        int i => (double)i,
        long l => (double)l,
        decimal m => (double)m,
        _ => double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null
    };

    private static int? AsIntOrNull(object? v) => v switch
    {
        null => null,
        int i => i,
        long l => (int)l,
        double d => (int)d,
        _ => int.TryParse(v.ToString(), out var parsed) ? parsed : null
    };

    private static long? AsLongOrNull(object? v) => v switch
    {
        null => null,
        long l => l,
        int i => i,
        double d => (long)d,
        _ => long.TryParse(v.ToString(), out var parsed) ? parsed : null
    };

    /// <summary>
    /// Find the most recent packet loss event across ISP, Transit, and Internet targets
    /// (checked in that priority order). Returns the timestamp and target type of the
    /// first match with loss > 1%.
    /// </summary>
    public async Task<RecentLossEvent?> FindRecentLossEventAsync(
        DateTime? before = null, DateTime? after = null,
        IReadOnlyCollection<string>? enabledTargetIds = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return null;

        var rangeStart = after ?? DateTime.UtcNow.AddDays(-30);
        var rangeStop = before ?? DateTime.UtcNow;
        var sortDesc = !after.HasValue;

        var targetFilter = "";
        if (enabledTargetIds != null && enabledTargetIds.Count > 0)
        {
            var conditions = string.Join(" or ", enabledTargetIds.Select(id => $"r.target_id == \"{id}\""));
            targetFilter = $"\n  |> filter(fn: (r) => {conditions})";
        }

        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(rangeStart)}, stop: {ToFluxInstant(rangeStop)})
  |> filter(fn: (r) => r._measurement == ""latency"")
  |> filter(fn: (r) => r.target_type == ""accessisp"" or r.target_type == ""transit"" or r.target_type == ""internetservice"" or r.target_type == ""wan""){targetFilter}
  |> filter(fn: (r) => r._field == ""loss_percent"")
  |> aggregateWindow(every: 1m, fn: mean, createEmpty: false)
  |> filter(fn: (r) => r._value > 1.0)
  |> group()
  |> sort(columns: [""_time""], desc: {(sortDesc ? "true" : "false")})
  |> limit(n: 1)
";
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow);
            var targetId = record.GetValueByKey("target_id") as string;
            var targetType = record.GetValueByKey("target_type") as string ?? "internetservice";
            var loss = AsDoubleOrNull(record.GetValueByKey("_value"));
            return new RecentLossEvent
            {
                Timestamp = time,
                TargetType = targetType,
                TargetId = targetId,
                LossPercent = loss ?? 0
            };
        }
        return null;
    }

    /// <summary>
    /// Like FindRecentLossEventAsync but only loss minutes that coincided with the
    /// WAN being loaded (either direction at or above its loaded-threshold rate).
    /// Loss under load is the SQM/bufferbloat signal; idle loss points at the
    /// physical layer instead.
    /// </summary>
    public async Task<RecentLossEvent?> FindRecentLoadedLossEventAsync(
        string gatewayMac,
        IReadOnlyList<string> wanIfNames,
        double loadedDownBpsThreshold,
        double loadedUpBpsThreshold,
        DateTime? before = null, DateTime? after = null,
        IReadOnlyCollection<string>? enabledTargetIds = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured || wanIfNames.Count == 0) return null;

        var rangeStart = after ?? DateTime.UtcNow.AddDays(-30);
        var rangeStop = before ?? DateTime.UtcNow;
        var sortDesc = !after.HasValue;

        var targetFilter = "";
        if (enabledTargetIds != null && enabledTargetIds.Count > 0)
        {
            var conditions = string.Join(" or ", enabledTargetIds.Select(id => $"r.target_id == \"{id}\""));
            targetFilter = $"\n  |> filter(fn: (r) => {conditions})";
        }
        var mac = NormalizeMac(gatewayMac);
        var ifFilter = string.Join(" or ", wanIfNames.Select(n => $@"r.if_name == ""{SanitizeFluxString(n)}"""));

        var flux = $@"
import ""join""
loss = from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(rangeStart)}, stop: {ToFluxInstant(rangeStop)})
  |> filter(fn: (r) => r._measurement == ""latency"")
  |> filter(fn: (r) => r.target_type == ""accessisp"" or r.target_type == ""transit"" or r.target_type == ""internetservice"" or r.target_type == ""wan""){targetFilter}
  |> filter(fn: (r) => r._field == ""loss_percent"")
  |> aggregateWindow(every: 1m, fn: mean, createEmpty: false)
  |> filter(fn: (r) => r._value > 1.0)
  |> group()
  |> keep(columns: [""_time"", ""_value"", ""target_id"", ""target_type""])

load = from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(rangeStart)}, stop: {ToFluxInstant(rangeStop)})
  |> filter(fn: (r) => r._measurement == ""interface_counters"")
  |> filter(fn: (r) => r.device_mac == ""{mac}"")
  |> filter(fn: (r) => {ifFilter})
  |> filter(fn: (r) => r._field == ""rate_in_bps"" or r._field == ""rate_out_bps"")
  |> aggregateWindow(every: 1m, fn: max, createEmpty: false)
  |> filter(fn: (r) => (r._field == ""rate_in_bps"" and r._value >= {loadedDownBpsThreshold.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}.0)
      or (r._field == ""rate_out_bps"" and r._value >= {loadedUpBpsThreshold.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}.0))
  |> group()
  |> keep(columns: [""_time""])
  |> unique(column: ""_time"")

join.inner(left: loss, right: load, on: (l, r) => l._time == r._time, as: (l, r) => l)
  |> sort(columns: [""_time""], desc: {(sortDesc ? "true" : "false")})
  |> limit(n: 1)
";
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow);
            var targetId = record.GetValueByKey("target_id") as string;
            var targetType = record.GetValueByKey("target_type") as string ?? "internetservice";
            var loss = AsDoubleOrNull(record.GetValueByKey("_value"));
            return new RecentLossEvent
            {
                Timestamp = time,
                TargetType = targetType,
                TargetId = targetId,
                LossPercent = loss ?? 0
            };
        }
        return null;
    }

    /// <summary>
    /// Find the most recent SFP anomaly: temperature above PON threshold (75 C) or
    /// RX power below PON threshold (-25 dBm). Scans the last 7 days.
    /// </summary>
    public async Task<RecentSfpAnomaly?> FindRecentSfpAnomalyAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return null;

        var lookback = DateTime.UtcNow.AddDays(-7);

        var flux = $@"
from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(lookback)})
  |> filter(fn: (r) => r._measurement == ""sfp"")
  |> filter(fn: (r) => r._field == ""temp_c"" or r._field == ""rx_power_dbm"")
  |> filter(fn: (r) => (r._field == ""temp_c"" and r._value > 75.0) or (r._field == ""rx_power_dbm"" and r._value < -25.0))
  |> sort(columns: [""_time""], desc: true)
  |> limit(n: 1)
";
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow);
            var field = record.GetValueByKey("_field") as string;
            var value = AsDoubleOrNull(record.GetValueByKey("_value"));
            var deviceMac = record.GetValueByKey("device_mac") as string;
            var portName = record.GetValueByKey("port_name") as string;
            return new RecentSfpAnomaly
            {
                Timestamp = time,
                Metric = field == "temp_c" ? "temperature" : "rx_power",
                Value = value ?? 0,
                DeviceMac = deviceMac,
                PortName = portName
            };
        }
        return null;
    }

    public record RecentLossEvent
    {
        public required DateTime Timestamp { get; init; }
        public required string TargetType { get; init; }
        public string? TargetId { get; init; }
        public double LossPercent { get; init; }
    }

    public record RecentSfpAnomaly
    {
        public required DateTime Timestamp { get; init; }
        public required string Metric { get; init; }
        public double Value { get; init; }
        public string? DeviceMac { get; init; }
        public string? PortName { get; init; }
    }

    public record InterfaceRatePoint
    {
        public required DateTime Time { get; init; }
        public required string IfName { get; init; }
        public double? RateInBps { get; init; }
        public double? RateOutBps { get; init; }
    }

    /// <summary>
    /// Full set of interface_counters fields for a single port at one instant.
    /// Packet-counter fields (ucast/mcast/bcast) are nullable so the table renders
    /// gracefully on data written before those fields were collected.
    /// </summary>
    public record PortStatsPoint
    {
        public required DateTime Time { get; init; }
        public required string DeviceMac { get; init; }
        public required string IfName { get; init; }
        public string PortId { get; init; } = "";
        public int? OperStatus { get; init; }
        public long? SpeedBps { get; init; }
        public double? RateInBps { get; init; }
        public double? RateOutBps { get; init; }
        public long? BytesIn { get; init; }
        public long? BytesOut { get; init; }
        public long? UcastPktsIn { get; init; }
        public long? UcastPktsOut { get; init; }
        public long? McastPktsIn { get; init; }
        public long? McastPktsOut { get; init; }
        public long? BcastPktsIn { get; init; }
        public long? BcastPktsOut { get; init; }
        public long? ErrorsIn { get; init; }
        public long? ErrorsOut { get; init; }
        public long? DiscardsIn { get; init; }
        public long? DiscardsOut { get; init; }
    }

    public record DeviceHealthPoint
    {
        public required DateTime Time { get; init; }
        public double? CpuPercent { get; init; }
        public double? MemoryUsedPercent { get; init; }
        public double? TemperatureC { get; init; }
        public long? UptimeSeconds { get; init; }
    }

    public record LatencyPoint
    {
        public required DateTime Time { get; init; }
        public double? RttAvgMs { get; init; }
        public double? LossPercent { get; init; }
    }

    public record SfpPoint
    {
        public required DateTime Time { get; init; }
        public double? RxPowerDbm { get; init; }
        public double? TxPowerDbm { get; init; }
        public double? TemperatureC { get; init; }
        public double? VoltageV { get; init; }
    }

    public record CellularPoint
    {
        public required DateTime Time { get; init; }
        public double? Rsrp { get; init; }
        public double? Rsrq { get; init; }
        public double? Snr { get; init; }
        public double? Rssi { get; init; }
        public int? SignalQuality { get; init; }
        public string? NetworkMode { get; init; }
        public string? Band { get; init; }
        public string? Carrier { get; init; }
    }

    public record CmPoint
    {
        public required DateTime Time { get; init; }
        public double? DsPowerAvgDbmv { get; init; }
        public double? DsSnrAvgDb { get; init; }
        public double? UsPowerAvgDbmv { get; init; }
        public int? LockedDsChannels { get; init; }
        public int? LockedUsChannels { get; init; }
        public long? CorrDelta { get; init; }
        public long? UncorrDelta { get; init; }
    }

    public record OntPoint
    {
        public required DateTime Time { get; init; }
        public double? RxPowerDbm { get; init; }
        public double? TxPowerDbm { get; init; }
        public double? TemperatureC { get; init; }
        public double? VoltageV { get; init; }
        public double? BiasMa { get; init; }
    }

    /// <summary>
    /// Single historical wifi_client sample. PHY rate fields are CAPACITY; throughput
    /// fields are MEASURED. See WifiClientLiveSnapshot for the same distinction in the
    /// live in-memory cache.
    /// </summary>
    public record WifiClientHistoryPoint
    {
        public required DateTime Time { get; init; }
        public required string Band { get; init; }
        public string? ClientMac { get; init; }
        public double? SignalDbm { get; init; }
        public double? TxThroughputBps { get; init; }
        public double? RxThroughputBps { get; init; }
        public long? TxRateKbps { get; init; }
        public long? RxRateKbps { get; init; }
    }

    // ---- Buffer + flush ----

    private void Enqueue(PointData point, bool longterm)
    {
        _writeBuffer.Enqueue(new BufferedPoint(point, longterm));
        if (_writeBuffer.Count >= _maxBufferSize)
        {
            _ = FlushAsync();
        }
    }

    public async Task FlushAsync()
    {
        if (!IsConfigured) return;
        if (!await _flushSemaphore.WaitAsync(0)) return;
        try
        {
            var primary = new List<PointData>();
            var longterm = new List<PointData>();
            while (_writeBuffer.TryDequeue(out var buffered))
            {
                if (buffered.Longterm) longterm.Add(buffered.Point);
                else primary.Add(buffered.Point);
            }

            if (primary.Count > 0 && _writeApi != null && !string.IsNullOrEmpty(_bucket))
            {
                await _writeApi.WritePointsAsync(primary, _bucket, _org);
            }
            if (longterm.Count > 0 && _writeApi != null && !string.IsNullOrEmpty(_longtermBucket))
            {
                await _writeApi.WritePointsAsync(longterm, _longtermBucket, _org);
            }

            if (primary.Count + longterm.Count > 0)
            {
                _logger.LogDebug(
                    "Flushed {Primary} points to {Bucket}, {Longterm} to {LongtermBucket}",
                    primary.Count, _bucket, longterm.Count, _longtermBucket);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush monitoring points to InfluxDB — points dropped");
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

    private async Task RunFlushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_flushTimer != null && await _flushTimer.WaitForNextTickAsync(ct))
            {
                if (!_writeBuffer.IsEmpty)
                    await FlushAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitoring flush loop crashed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _timerCts.CancelAsync(); } catch { }
        if (_flushTask != null)
        {
            try { await _flushTask; } catch { }
        }
        // Intentionally do NOT flush remaining buffered writes. The buffer contains
        // latency probes from the last ~5s that timed out during shutdown — flushing
        // them writes artificial 100% loss to InfluxDB.
        await DisposeClientAsync();
        _timerCts.Dispose();
        _flushSemaphore.Dispose();
        _configLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task DisposeClientAsync()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        if (_flushTask != null)
        {
            try { await _flushTask; } catch { }
            _flushTask = null;
        }
        _client?.Dispose();
        _client = null;
        _writeApi = null;
    }

    private static string NormalizeMac(string mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace('-', ':');

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private readonly record struct BufferedPoint(PointData Point, bool Longterm);
}

public record InfluxHealthResult(bool Reachable, string? Error);
