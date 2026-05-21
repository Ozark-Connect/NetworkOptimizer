using System.Collections.Concurrent;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

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

        // Avoid pointless reconnects if nothing changed
        if (_initialized &&
            _url == url &&
            _org == org &&
            _bucket == bucket &&
            _longtermBucket == longterm)
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
            _org = org;
            _bucket = bucket;
            _longtermBucket = string.IsNullOrWhiteSpace(longterm) ? bucket : longterm;
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

        try
        {
            var ok = await _client!.PingAsync();
            var err = ok ? null : "InfluxDB ping returned false";
            await PersistHealthAsync(ok, err, ct);
            return new InfluxHealthResult(ok, err);
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
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("interface_counters")
            .Tag("device_mac", NormalizeMac(deviceMac))
            .Tag("if_name", ifName)
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
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("latency")
            .Tag("target_id", targetId)
            .Tag("vantage_point", vantagePoint)
            .Tag("target_type", targetType.ToString().ToLowerInvariant())
            .Field("loss_percent", lossPercent)
            .Field("success", success)
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

    public Task WriteSfpAsync(
        string deviceMac,
        string portName,
        double? rxPowerDbm,
        double? txPowerDbm,
        double? txBiasMa,
        double? temperatureC,
        double? voltageV,
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
            _logger.LogWarning(ex, "Failed to flush monitoring points to InfluxDB");
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
        if (!_writeBuffer.IsEmpty) await FlushAsync();
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
