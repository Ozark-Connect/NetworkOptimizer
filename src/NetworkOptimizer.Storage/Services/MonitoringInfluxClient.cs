using System.Collections.Concurrent;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
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
    // Direct HTTP client for the high-volume raw-CSV reads (see QueryRawLinesAsync). Built and torn
    // down alongside _client from the same settings; holds the token only in its Authorization
    // header, the same in-memory exposure as the InfluxDB client itself.
    private HttpClient? _rawQueryHttp;
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

    private readonly SiteDbContextFactory? _siteDbFactory;
    private readonly string? _siteSlug;

    /// <param name="siteSlug">
    /// Non-default site whose database holds this client's MonitoringSettings
    /// (URL, token, slug-prefixed bucket names). Null/empty = the default
    /// site, configured from the main database as before.
    /// </param>
    public MonitoringInfluxClient(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        ICredentialProtectionService credentialProtection,
        ILogger<MonitoringInfluxClient> logger,
        SiteDbContextFactory? siteDbFactory = null,
        string? siteSlug = null)
    {
        _dbFactory = dbFactory;
        _credentialProtection = credentialProtection;
        _logger = logger;
        _siteDbFactory = siteDbFactory;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? null : siteSlug;
    }

    /// <summary>Context for the database holding this client's settings row.</summary>
    private async Task<NetworkOptimizerDbContext> CreateSettingsContextAsync(CancellationToken ct)
    {
        if (_siteSlug != null && _siteDbFactory != null)
            return _siteDbFactory.CreateForSite(_siteSlug, isDefault: false);
        return await _dbFactory.CreateDbContextAsync(ct);
    }

    public string? CurrentUrl => _url;
    public bool IsConfigured => _client != null && !string.IsNullOrEmpty(_bucket);

    /// <summary>The primary bucket this client actually reads/writes, after resolution
    /// (slug-prefixed for non-default sites; collisions with main are corrected). Null
    /// until configured. Use this for display so the UI matches the real target bucket
    /// rather than the raw stored value.</summary>
    public string? PrimaryBucket => _bucket;

    /// <summary>The long-term bucket this client actually reads/writes, after resolution.</summary>
    public string? LongtermBucket => _longtermBucket;

    /// <summary>
    /// Build (or rebuild) the client from current MonitoringSettings. Safe to call repeatedly.
    /// Returns true if a usable client was constructed.
    /// </summary>
    public async Task<bool> ReconfigureAsync(CancellationToken ct = default)
    {
        await _configLock.WaitAsync(ct);
        try
        {
            await using var db = await CreateSettingsContextAsync(ct);
            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);

            // One InfluxDB server per NO server, many buckets. A non-default site NEVER
            // holds its own connection: server, org, and token always come from the main
            // site (whose org-scoped token can write every site's buckets). A site only
            // chooses its bucket NAMES. Resolving them here - the single point every read
            // and write is configured from - means no DB/scripted/edited state can ever
            // point a site's client at main's buckets.
            if (_siteSlug != null)
            {
                await using var mainDb = await _dbFactory.CreateDbContextAsync(ct);
                var main = await mainDb.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
                if (!HasInfluxConfig(main))
                {
                    _logger.LogDebug("Main site has no shared InfluxDB connection yet; site {Slug} client not configured", _siteSlug);
                    await DisposeClientAsync();
                    _initialized = false;
                    return false;
                }

                var derived = new MonitoringSettings
                {
                    InfluxDbUrl = main!.InfluxDbUrl,
                    InfluxDbToken = main.InfluxDbToken,
                    InfluxDbOrg = main.InfluxDbOrg,
                    InfluxDbBucket = ResolveSiteBucket(settings?.InfluxDbBucket, main),
                    InfluxDbLongtermBucket = ResolveSiteLongtermBucket(settings?.InfluxDbLongtermBucket, main)
                };
                return await ApplyConfigAsync(derived, ct);
            }

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

    private static bool HasInfluxConfig(MonitoringSettings? settings) =>
        settings != null
        && !string.IsNullOrWhiteSpace(settings.InfluxDbUrl)
        && !string.IsNullOrWhiteSpace(settings.InfluxDbToken)
        && !string.IsNullOrWhiteSpace(settings.InfluxDbOrg)
        && !string.IsNullOrWhiteSpace(settings.InfluxDbBucket);

    /// <summary>
    /// Resolves a non-default site's primary bucket. A custom name from the site's own
    /// row is honored (non-standard names are fine), EXCEPT a name that collides with
    /// either of main's buckets - that is corrected to the slug-prefixed default so a
    /// site's series can never be written into main's data. This is the hard floor
    /// against writing into MAIN; collisions with OTHER sites are rejected at set-time
    /// in the UI, where the full set of known buckets is available.
    /// </summary>
    private string ResolveSiteBucket(string? siteBucket, MonitoringSettings main)
    {
        var fallback = $"{_siteSlug}-{main.InfluxDbBucket}";
        var name = siteBucket?.Trim();
        return string.IsNullOrEmpty(name) || CollidesWithMain(name, main) ? fallback : name;
    }

    private string ResolveSiteLongtermBucket(string? siteLongterm, MonitoringSettings main)
    {
        var fallback = string.IsNullOrWhiteSpace(main.InfluxDbLongtermBucket)
            ? string.Empty
            : $"{_siteSlug}-{main.InfluxDbLongtermBucket}";
        var name = siteLongterm?.Trim();
        return string.IsNullOrEmpty(name) || CollidesWithMain(name, main) ? fallback : name;
    }

    /// <summary>True when a proposed site bucket name equals one of main's bucket names.</summary>
    private static bool CollidesWithMain(string name, MonitoringSettings main) =>
        string.Equals(name, main.InfluxDbBucket?.Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, main.InfluxDbLongtermBucket?.Trim(), StringComparison.OrdinalIgnoreCase);

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
                // The client default query timeout (~10 s) cancels heavy reads (e.g. a 48 h ISP Health
                // window on a slow spinning-disk NAS with a Comcast-style hop-heavy path - see #941)
                // before they finish, surfacing as a TaskCanceledException that reads as a hard failure.
                // Raise it well past ISP Health's own per-compute budget so that budget is the single
                // authority on when to give up (and fall back to a shorter window), not the transport.
                .TimeOut(TimeSpan.FromSeconds(60))
                .Build();

            _client = new InfluxDBClient(options);
            _writeApi = _client.GetWriteApiAsync();

            // Sibling client for the streamed raw-CSV reads. Same wire behavior as the InfluxDB
            // client's transport (RestSharp defaults to requesting compressed responses) and the
            // same 60 s timeout - which here bounds only the wait for response headers, so the
            // body read stays governed by the caller's token (ISP Health's compute budget),
            // exactly the "budget is the single authority" intent of the 60 s bump above.
            _rawQueryHttp = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            {
                BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/"),
                Timeout = TimeSpan.FromSeconds(60)
            };
            _rawQueryHttp.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Token", plainToken);
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
            await using var db = await CreateSettingsContextAsync(ct);
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
        DateTime timestamp,
        string? wanContext = null)
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
        // Multi-WAN context tag, emitted only for non-default contexts so the
        // schema stays additive-only: single-WAN installs never see it. The value
        // is the context's UniFi WAN key where it has one (WanContext.InfluxWanTag),
        // so renaming a context does not orphan its own history under the old tag.
        if (!string.IsNullOrEmpty(wanContext)) point = point.Tag("wan", wanContext);

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
        DateTime timestamp,
        int? port = null,
        string? clientIp = null,
        string? clientName = null)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("wired_client")
            .Tag("device_mac", NormalizeMac(switchMac))
            .Field("client_mac", NormalizeMac(clientMac))
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        // Port tag (additive, absent on pre-2.0 points): lets playback reconstruct
        // which client sat on which port at a given instant (the Port Statistics
        // Client column). Series growth is bounded by physical ports in use per
        // device. client_ip / client_name are fields (no cardinality impact) so a
        // historical scrub can label the client without consulting the live client
        // registry, whose names may have drifted since.
        if (port is > 0) point = point.Tag("port", port.Value.ToString());
        if (!string.IsNullOrEmpty(clientIp)) point = point.Field("client_ip", clientIp);
        if (!string.IsNullOrEmpty(clientName)) point = point.Field("client_name", clientName);

        if (txThroughputBps.HasValue) point = point.Field("tx_throughput_bps", txThroughputBps.Value);
        if (rxThroughputBps.HasValue) point = point.Field("rx_throughput_bps", rxThroughputBps.Value);

        Enqueue(point, longterm: false);
        return Task.CompletedTask;
    }

    /// <summary>A wired client resolved to a device port at a playback instant.</summary>
    public class WiredPortClientPoint
    {
        public string DeviceMac { get; init; } = "";
        public int Port { get; init; }
        public string ClientMac { get; init; } = "";
        public string? ClientIp { get; init; }
        public string? ClientName { get; init; }
    }

    /// <summary>
    /// The single wired client on each (device, port) around a historic instant,
    /// from port-tagged <c>wired_client</c> points - the playback counterpart of
    /// the live GetPortClient map. Ports that saw more than one distinct client
    /// MAC in the window are omitted, matching the live map's rule of never
    /// labelling an uplink/trunk with an arbitrary client. Points written before
    /// the port tag existed (pre-2.0) carry no tag and simply yield no rows.
    /// </summary>
    public async Task<IReadOnlyList<WiredPortClientPoint>> QueryWiredPortClientsAsync(
        IReadOnlyList<string>? deviceMacs,
        DateTime at,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<WiredPortClientPoint>();

        // Same snapshot window as QueryPortStatsAsync so the Client column lines up
        // with the counters shown at the same scrub point.
        var center = at.ToUniversalTime();
        var rangeClause = $"range(start: {ToFluxInstant(center.AddSeconds(-90))}, stop: {ToFluxInstant(center.AddSeconds(30))})";

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
  |> filter(fn: (r) => r._measurement == ""wired_client"")
  |> filter(fn: (r) => exists r.port)
  |> filter(fn: (r) => r._field == ""client_mac"" or r._field == ""client_ip"" or r._field == ""client_name""){macFilter}
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var rows = new List<(string DeviceMac, int Port, DateTime Time, string ClientMac, string? Ip, string? Name)>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            // Normalize on read so grouping (and the caller's dictionary key) can
            // never split one device across case variants, matching the sibling
            // QueryPortStatsAsync's case-insensitive grouping.
            var deviceMac = NormalizeMac(record.GetValueByKey("device_mac") as string ?? "");
            var clientMac = record.GetValueByKey("client_mac") as string ?? "";
            if (deviceMac.Length == 0 || clientMac.Length == 0) continue;
            if (!int.TryParse(record.GetValueByKey("port") as string, out var port) || port <= 0) continue;
            rows.Add((deviceMac, port, ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                clientMac,
                record.GetValueByKey("client_ip") as string,
                record.GetValueByKey("client_name") as string));
        }

        var result = new List<WiredPortClientPoint>();
        foreach (var group in rows.GroupBy(r => (r.DeviceMac, r.Port)))
        {
            if (group.Select(r => r.ClientMac).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                continue; // multiple clients in the window - trunk/uplink, never label
            // Nearest to the scrub instant, matching the counter selection in
            // QueryPortStatsAsync (name/IP can change across the window).
            var nearest = group.OrderBy(r => Math.Abs((r.Time - center).Ticks)).First();
            result.Add(new WiredPortClientPoint
            {
                DeviceMac = nearest.DeviceMac,
                Port = nearest.Port,
                ClientMac = nearest.ClientMac,
                ClientIp = nearest.Ip,
                ClientName = nearest.Name,
            });
        }
        return result;
    }

    /// <summary>The latest persisted DDM reading of one SFP port (see <see cref="QueryLatestSfpAsync"/>).</summary>
    public class SfpLatestPoint
    {
        public string DeviceMac { get; init; } = "";
        public string PortName { get; init; } = "";
        public double? RxPowerDbm { get; init; }
        public double? TxPowerDbm { get; init; }
        public double? TxBiasMa { get; init; }
        public double? TemperatureC { get; init; }
        public double? VoltageV { get; init; }
        public DateTime Time { get; init; }
    }

    /// <summary>
    /// Latest persisted DDM reading per (device, port) from the <c>sfp</c>
    /// measurement, used to warm the live SFP cache after a restart so the
    /// Optical tables aren't blank until the slow tier's first cycle (up to
    /// several minutes on agent-backed sites, whose first tick usually fires
    /// before the tunnel console reconnects). Window bounded to recent history:
    /// a module that stopped reporting long ago should not resurrect as "live".
    /// </summary>
    public async Task<IReadOnlyList<SfpLatestPoint>> QueryLatestSfpAsync(CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrEmpty(_longtermBucket)) return Array.Empty<SfpLatestPoint>();

        var flux = $@"
from(bucket: ""{_longtermBucket}"")
  |> range(start: -6h)
  |> filter(fn: (r) => r._measurement == ""sfp"")
  |> last()
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new List<SfpLatestPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var deviceMac = record.GetValueByKey("device_mac") as string ?? "";
            var portName = record.GetValueByKey("port_name") as string ?? "";
            if (deviceMac.Length == 0 || portName.Length == 0) continue;
            results.Add(new SfpLatestPoint
            {
                DeviceMac = deviceMac,
                PortName = portName,
                RxPowerDbm = AsDoubleOrNull(record.GetValueByKey("rx_power_dbm")),
                TxPowerDbm = AsDoubleOrNull(record.GetValueByKey("tx_power_dbm")),
                TxBiasMa = AsDoubleOrNull(record.GetValueByKey("tx_bias_ma")),
                TemperatureC = AsDoubleOrNull(record.GetValueByKey("temperature_c")),
                VoltageV = AsDoubleOrNull(record.GetValueByKey("voltage_v")),
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
            });
        }
        return results;
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
    /// Write supplemental PON-layer stats onto the sfp measurement, merging with the
    /// DDM point CollectSfpForDevice writes for the same module and timestamp (same
    /// series + timestamp = field union in InfluxDB). pon_link_status / fec_errors /
    /// bip_errors reuse the ont measurement's field names and encodings so display
    /// and alert logic can treat SFP-slot and standalone ONTs uniformly.
    /// </summary>
    public Task WriteSfpPonAsync(
        string deviceMac,
        string portName,
        PonSupplementalStats stats,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("sfp")
            .Tag("device_mac", NormalizeMac(deviceMac))
            .Tag("port_name", portName)
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (!string.IsNullOrEmpty(stats.PonLinkStatus)) point = point.Field("pon_link_status", stats.PonLinkStatus);
        if (!string.IsNullOrEmpty(stats.PonLinkStatusPrev)) point = point.Field("pon_link_status_prev", stats.PonLinkStatusPrev);
        if (stats.PloamElapsedMs.HasValue) point = point.Field("ploam_elapsed_ms", stats.PloamElapsedMs.Value);
        if (stats.GtcDsState.HasValue) point = point.Field("gtc_ds_state", stats.GtcDsState.Value);
        if (stats.OnuId.HasValue) point = point.Field("onu_id", stats.OnuId.Value);
        if (stats.DsFecEnabled.HasValue) point = point.Field("ds_fec_enabled", stats.DsFecEnabled.Value);
        if (stats.UsFecEnabled.HasValue) point = point.Field("us_fec_enabled", stats.UsFecEnabled.Value);
        if (stats.OnuResponseTime.HasValue) point = point.Field("onu_response_time", stats.OnuResponseTime.Value);
        if (stats.BipErrors.HasValue) point = point.Field("bip_errors", stats.BipErrors.Value);
        if (stats.FecErrors.HasValue) point = point.Field("fec_errors", stats.FecErrors.Value);
        if (stats.FecCorrectedWords.HasValue) point = point.Field("fec_corrected_words", stats.FecCorrectedWords.Value);
        if (stats.HecCorrected.HasValue) point = point.Field("hec_corrected", stats.HecCorrected.Value);
        if (stats.HecUncorrected.HasValue) point = point.Field("hec_uncorrected", stats.HecUncorrected.Value);
        if (stats.BwmapCorrected.HasValue) point = point.Field("bwmap_corrected", stats.BwmapCorrected.Value);
        if (stats.BwmapUncorrected.HasValue) point = point.Field("bwmap_uncorrected", stats.BwmapUncorrected.Value);
        if (stats.GemTxFrames.HasValue) point = point.Field("gem_tx_frames", stats.GemTxFrames.Value);
        if (stats.GemTxIdleFrames.HasValue) point = point.Field("gem_tx_idle_frames", stats.GemTxIdleFrames.Value);
        if (stats.GemRxFrames.HasValue) point = point.Field("gem_rx_frames", stats.GemRxFrames.Value);
        if (stats.GemRxDropped.HasValue) point = point.Field("gem_rx_dropped", stats.GemRxDropped.Value);
        if (stats.AllocTotal.HasValue) point = point.Field("alloc_total", stats.AllocTotal.Value);
        if (stats.AllocLost.HasValue) point = point.Field("alloc_lost", stats.AllocLost.Value);
        if (stats.GpePonIngressDiscard.HasValue) point = point.Field("gpe_pon_ingress_discard", stats.GpePonIngressDiscard.Value);
        if (stats.GpePonEgressDiscard.HasValue) point = point.Field("gpe_pon_egress_discard", stats.GpePonEgressDiscard.Value);
        if (stats.GpePonLearningDiscard.HasValue) point = point.Field("gpe_pon_learning_discard", stats.GpePonLearningDiscard.Value);
        if (stats.GpeLanIngressDiscard.HasValue) point = point.Field("gpe_lan_ingress_discard", stats.GpeLanIngressDiscard.Value);
        if (stats.GpeLanEgressDiscard.HasValue) point = point.Field("gpe_lan_egress_discard", stats.GpeLanEgressDiscard.Value);
        if (stats.GpeLanLearningDiscard.HasValue) point = point.Field("gpe_lan_learning_discard", stats.GpeLanLearningDiscard.Value);
        if (stats.LanLinkStatus.HasValue) point = point.Field("lan_link_status", stats.LanLinkStatus.Value);
        if (stats.LanTxFrames.HasValue) point = point.Field("lan_tx_frames", stats.LanTxFrames.Value);
        if (stats.LanRxFrames.HasValue) point = point.Field("lan_rx_frames", stats.LanRxFrames.Value);
        if (stats.LanTxDropEvents.HasValue) point = point.Field("lan_tx_drop_events", stats.LanTxDropEvents.Value);
        if (stats.LanRxFcsErrors.HasValue) point = point.Field("lan_rx_fcs_err", stats.LanRxFcsErrors.Value);
        if (stats.LanBufferOverflow.HasValue) point = point.Field("lan_buffer_overflow", stats.LanBufferOverflow.Value);
        if (stats.SfpUptimeS.HasValue) point = point.Field("sfp_uptime_s", stats.SfpUptimeS.Value);

        Enqueue(point, longterm: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Write cellular modem signal metrics for time-series charting.
    /// Tags identify the modem; fields capture all available signal/band/carrier
    /// data plus the serving cell identity.
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
        DateTime timestamp,
        int? timingAdvanceUs = null,
        int? cellId = null,
        int? tac = null,
        int? neighborCount = null,
        bool? nsaAvailable = null)
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

        // Cell identity as fields, never tags: a handover would otherwise open a new series.
        if (timingAdvanceUs.HasValue) point = point.Field("timing_advance", timingAdvanceUs.Value);
        if (cellId.HasValue) point = point.Field("cell_id", cellId.Value);
        if (tac.HasValue) point = point.Field("tac", tac.Value);
        if (neighborCount.HasValue) point = point.Field("neighbor_count", neighborCount.Value);

        // Whether the serving cell offers EN-DC. Charting it dates the moment the anchor
        // went bad, rather than leaving it to be noticed days later.
        if (nsaAvailable.HasValue) point = point.Field("nsa_available", nsaAvailable.Value);

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

    /// <summary>
    /// Write Starlink terminal health metrics for time-series charting.
    /// Link latency/throughput are intentionally absent (the monitoring
    /// pipeline measures those); these are dish-only signals.
    /// </summary>
    public Task WriteStarlinkAsync(
        string starlinkId,
        string starlinkName,
        double? powerInW,
        double? powerInAvgW,
        double? powerInMaxW,
        double? pingDropRateAvg,
        double? pingDropRateMax,
        double? fractionObstructed,
        bool? currentlyObstructed,
        int? ethSpeedMbps,
        long? uptimeS,
        int? gpsSats,
        bool? gpsValid,
        double? tiltAngleDeg,
        double? alignmentOffsetDeg,
        double? attitudeUncertaintyDeg,
        int outageCountDelta,
        double outageSecondsDelta,
        int alertCount,
        string? alerts,
        bool? snrPersistentlyLow,
        string? softwareUpdateState,
        string? disablementCode,
        string? dlRestrictedReason,
        string? ulRestrictedReason,
        string? hardwareSelfTest,
        string? classOfService,
        string? mobilityClass,
        DateTime timestamp)
    {
        if (!IsConfigured) return Task.CompletedTask;
        var point = PointData.Measurement("starlink")
            .Tag("starlink_id", starlinkId)
            .Tag("starlink_name", starlinkName)
            .Timestamp(timestamp.ToUniversalTime(), WritePrecision.Ns);

        if (powerInW.HasValue) point = point.Field("power_in_w", powerInW.Value);
        if (powerInAvgW.HasValue) point = point.Field("power_in_avg_w", powerInAvgW.Value);
        if (powerInMaxW.HasValue) point = point.Field("power_in_max_w", powerInMaxW.Value);
        if (pingDropRateAvg.HasValue) point = point.Field("ping_drop_rate_avg", pingDropRateAvg.Value);
        if (pingDropRateMax.HasValue) point = point.Field("ping_drop_rate_max", pingDropRateMax.Value);
        if (fractionObstructed.HasValue) point = point.Field("fraction_obstructed", fractionObstructed.Value);
        if (currentlyObstructed.HasValue) point = point.Field("currently_obstructed", currentlyObstructed.Value);
        if (ethSpeedMbps.HasValue) point = point.Field("eth_speed_mbps", ethSpeedMbps.Value);
        if (uptimeS.HasValue) point = point.Field("uptime_s", uptimeS.Value);
        if (gpsSats.HasValue) point = point.Field("gps_sats", gpsSats.Value);
        if (gpsValid.HasValue) point = point.Field("gps_valid", gpsValid.Value);
        if (tiltAngleDeg.HasValue) point = point.Field("tilt_angle_deg", tiltAngleDeg.Value);
        if (alignmentOffsetDeg.HasValue) point = point.Field("alignment_offset_deg", alignmentOffsetDeg.Value);
        if (attitudeUncertaintyDeg.HasValue) point = point.Field("attitude_uncertainty_deg", attitudeUncertaintyDeg.Value);
        point = point.Field("outage_count_delta", outageCountDelta);
        point = point.Field("outage_seconds_delta", outageSecondsDelta);
        point = point.Field("alert_count", alertCount);
        if (!string.IsNullOrEmpty(alerts)) point = point.Field("alerts", alerts);
        if (snrPersistentlyLow.HasValue) point = point.Field("snr_persistently_low", snrPersistentlyLow.Value);
        if (!string.IsNullOrEmpty(softwareUpdateState)) point = point.Field("software_update_state", softwareUpdateState);
        if (!string.IsNullOrEmpty(disablementCode)) point = point.Field("disablement_code", disablementCode);
        if (!string.IsNullOrEmpty(dlRestrictedReason)) point = point.Field("dl_restricted_reason", dlRestrictedReason);
        if (!string.IsNullOrEmpty(ulRestrictedReason)) point = point.Field("ul_restricted_reason", ulRestrictedReason);
        if (!string.IsNullOrEmpty(hardwareSelfTest)) point = point.Field("hardware_self_test", hardwareSelfTest);
        if (!string.IsNullOrEmpty(classOfService)) point = point.Field("class_of_service", classOfService);
        if (!string.IsNullOrEmpty(mobilityClass)) point = point.Field("mobility_class", mobilityClass);

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

    /// <summary>
    /// Event type tag used for device reboot records on the <c>events</c> measurement.
    /// </summary>
    public const string DeviceRebootEventType = "device_reboot";

    /// <summary>
    /// Record why a device rebooted, timestamped at the instant the device came up so the
    /// record sits at the start of the current boot. Written to the long-term bucket because
    /// a reason has to outlive the device's uptime, which can run for months.
    /// </summary>
    /// <param name="deviceMac">Device MAC.</param>
    /// <param name="deviceType">Device type, matching the <c>device_health</c> tag values.</param>
    /// <param name="category">Machine-readable classification (see RebootCategory).</param>
    /// <param name="summary">Short user-facing reason.</param>
    /// <param name="detail">Evidence behind the call.</param>
    /// <param name="source">Which evidence source produced it.</param>
    /// <param name="bootedAt">When the current boot started.</param>
    /// <param name="firmwareVersion">
    /// Firmware the device reported on this boot. Persisted so the next reboot can be compared
    /// against it even if the server restarted in between, which is what lets an upgrade be
    /// recognised from the UniFi device data alone.
    /// </param>
    public Task WriteDeviceRebootAsync(
        string deviceMac,
        string deviceType,
        string category,
        string summary,
        string? detail,
        string source,
        DateTime bootedAt,
        string? firmwareVersion = null,
        int classifierVersion = 0)
    {
        if (!IsConfigured) return Task.CompletedTask;

        var point = PointData.Measurement("events")
            .Tag("device_mac", NormalizeMac(deviceMac))
            .Tag("event_type", DeviceRebootEventType)
            .Tag("severity", "info")
            .Tag("device_type", deviceType.ToLowerInvariant())
            .Timestamp(bootedAt.ToUniversalTime(), WritePrecision.Ns)
            .Field("detail", detail ?? string.Empty)
            .Field("reason_category", category)
            .Field("reason_summary", summary)
            .Field("reason_source", source);

        if (!string.IsNullOrWhiteSpace(firmwareVersion))
            point = point.Field("firmware_version", firmwareVersion);
        if (classifierVersion > 0)
            point = point.Field("classifier_version", classifierVersion);

        Enqueue(point, longterm: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A persisted device reboot record.
    /// </summary>
    public class DeviceRebootPoint
    {
        /// <summary>Device MAC (normalized).</summary>
        public string DeviceMac { get; init; } = "";
        /// <summary>Machine-readable classification.</summary>
        public string Category { get; init; } = "";
        /// <summary>Short user-facing reason.</summary>
        public string Summary { get; init; } = "";
        /// <summary>Evidence behind the call.</summary>
        public string? Detail { get; init; }
        /// <summary>Which evidence source produced it.</summary>
        public string Source { get; init; } = "";
        /// <summary>When the boot this record describes started.</summary>
        public DateTime BootedAt { get; init; }
        /// <summary>Firmware the device reported on that boot, when it was recorded.</summary>
        public string? FirmwareVersion { get; init; }
        /// <summary>Version of the rules that produced this reason; 0 for records written before versioning.</summary>
        public int ClassifierVersion { get; init; }
    }

    /// <summary>
    /// Latest reboot record per device, used to warm the in-memory cache after a restart and
    /// to decide which devices still need probing for their current boot.
    /// </summary>
    /// <param name="lookback">How far back to look. Devices up longer than this need a re-probe.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<DeviceRebootPoint>> QueryLatestDeviceRebootsAsync(
        TimeSpan lookback,
        CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrEmpty(_longtermBucket))
            return Array.Empty<DeviceRebootPoint>();

        var flux = $@"
from(bucket: ""{_longtermBucket}"")
  |> range(start: -{(int)Math.Max(1, lookback.TotalHours)}h)
  |> filter(fn: (r) => r._measurement == ""events"")
  |> filter(fn: (r) => r.event_type == ""{DeviceRebootEventType}"")
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
  |> group(columns: [""device_mac""])
  |> sort(columns: [""_time""])
  |> last(column: ""_time"")
";
        var results = new List<DeviceRebootPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var deviceMac = record.GetValueByKey("device_mac") as string ?? "";
            if (deviceMac.Length == 0) continue;

            results.Add(new DeviceRebootPoint
            {
                DeviceMac = deviceMac,
                Category = record.GetValueByKey("reason_category") as string ?? "",
                Summary = record.GetValueByKey("reason_summary") as string ?? "",
                Detail = record.GetValueByKey("detail") as string,
                Source = record.GetValueByKey("reason_source") as string ?? "",
                FirmwareVersion = record.GetValueByKey("firmware_version") as string,
                ClassifierVersion = (int)(AsDoubleOrNull(record.GetValueByKey("classifier_version")) ?? 0),
                BootedAt = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
            });
        }
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        results.Sort((a, b) => a.BootedAt.CompareTo(b.BootedAt));

        return results;
    }

    /// <summary>
    /// Every reboot record whose boot instant falls inside a window, for all devices.
    ///
    /// The sibling <see cref="QueryLatestDeviceRebootsAsync"/> collapses to one row per device
    /// because it only cares about the boot each device is running now. Charting wants the
    /// opposite: every boot in the window, so a device that restarted three times shows three
    /// marks. Absolute range bounds rather than a lookback, since the chart window can be a
    /// historic one that does not end at now.
    /// </summary>
    /// <param name="from">Window start.</param>
    /// <param name="to">Window end.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<DeviceRebootPoint>> QueryDeviceRebootsInRangeAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrEmpty(_longtermBucket))
            return Array.Empty<DeviceRebootPoint>();

        var flux = $@"
from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""events"")
  |> filter(fn: (r) => r.event_type == ""{DeviceRebootEventType}"")
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new List<DeviceRebootPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var deviceMac = record.GetValueByKey("device_mac") as string ?? "";
            if (deviceMac.Length == 0) continue;

            results.Add(new DeviceRebootPoint
            {
                DeviceMac = deviceMac,
                Category = record.GetValueByKey("reason_category") as string ?? "",
                Summary = record.GetValueByKey("reason_summary") as string ?? "",
                Detail = record.GetValueByKey("detail") as string,
                Source = record.GetValueByKey("reason_source") as string ?? "",
                FirmwareVersion = record.GetValueByKey("firmware_version") as string,
                ClassifierVersion = (int)(AsDoubleOrNull(record.GetValueByKey("classifier_version")) ?? 0),
                BootedAt = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
            });
        }
        // Same pivot caveat as the sibling query: the Flux result is not globally ordered.
        results.Sort((a, b) => a.BootedAt.CompareTo(b.BootedAt));

        return results;
    }

    // ---- Read API (Flux queries) ----

    /// <summary>
    /// Total throughput per device per window - every interface and both directions summed - for
    /// any number of devices in ONE query. The caller gets what it would have got by summing
    /// <see cref="QueryInterfaceRatesAsync"/> rows itself, without a round trip per device and
    /// without carrying every interface's row across the wire.
    /// </summary>
    /// <remarks>
    /// The summing is two aggregateWindow passes, not one: the first takes each interface's mean
    /// over the window, the second adds those means together. Do not collapse it to a single
    /// pass - summing raw samples is a different number whenever interfaces report at different
    /// rates, and grouping on _time instead builds one Flux table per bucket, which measured 8x
    /// slower than this. Filtering by interface has to stay a plain tag regex for the same reason:
    /// anything Influx cannot push down - a map(), or a conditional over two tags - is evaluated
    /// row by row and costs two orders of magnitude more than the whole query.
    ///
    /// <paramref name="from"/> and <paramref name="to"/> must be aligned to
    /// <paramref name="aggregateWindow"/> or the two passes disagree on where windows begin and
    /// the totals land in neighboring buckets.
    /// </remarks>
    /// <param name="deviceMacs">Devices to total. An empty list returns nothing.</param>
    /// <param name="from">Window start (UTC), aligned to <paramref name="aggregateWindow"/>.</param>
    /// <param name="to">Window end (UTC), aligned to <paramref name="aggregateWindow"/>.</param>
    /// <param name="aggregateWindow">Bucket size.</param>
    /// <param name="wiredOnly">
    /// Restrict to copper and SFP interfaces, by the raw port name where there is one and the
    /// interface name otherwise - if_name carries the user's alias once a port has been renamed.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<DeviceRateTotalPoint>> QueryDeviceRateTotalsAsync(
        IReadOnlyCollection<string> deviceMacs,
        DateTime from,
        DateTime to,
        TimeSpan aggregateWindow,
        bool wiredOnly = false,
        CancellationToken ct = default)
    {
        if (!IsConfigured || deviceMacs.Count == 0 || to <= from) return Array.Empty<DeviceRateTotalPoint>();

        var macs = string.Join(" or ", deviceMacs
            .Select(NormalizeMac)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => $@"r.device_mac == ""{m}"""));

        // A plain disjunction of tag regexes, NEVER the if/else that expresses port_id-then-if_name
        // precedence exactly - the conditional runs per row (82 s vs 0.26 s; see remarks). The two
        // disagree only where a non-eth/sfp port has been renamed to eth*/sfp*, which no site has done.
        const string wiredPrefix = @"/^(?i:eth|sfp)/";
        var interfaceFilter = wiredOnly
            ? $@"  |> filter(fn: (r) => r.port_id =~ {wiredPrefix} or r.if_name =~ {wiredPrefix})
"
            : "";

        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""interface_counters"")
  |> filter(fn: (r) => {macs})
  |> filter(fn: (r) => r._field == ""rate_in_bps"" or r._field == ""rate_out_bps"")
{interfaceFilter}  |> aggregateWindow(every: {ToFluxDuration(aggregateWindow)}, fn: mean, createEmpty: false)
  |> group(columns: [""device_mac""])
  |> aggregateWindow(every: {ToFluxDuration(aggregateWindow)}, fn: sum, createEmpty: false, timeSrc: ""_start"")
";

        var results = new List<DeviceRateTotalPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var mac = record.GetValueByKey("device_mac") as string;
            if (string.IsNullOrEmpty(mac)) continue;
            results.Add(new DeviceRateTotalPoint
            {
                DeviceMac = mac,
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                Bps = AsDoubleOrNull(record.GetValueByKey("_value")) ?? 0,
            });
        }

        results.Sort((a, b) => a.Time.CompareTo(b.Time));
        return results;
    }

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
                PortId = record.GetValueByKey("port_id") as string,
                RateInBps = AsDoubleOrNull(record.GetValueByKey("rate_in_bps")),
                RateOutBps = AsDoubleOrNull(record.GetValueByKey("rate_out_bps"))
            });
        }
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        results.Sort((a, b) => a.Time.CompareTo(b.Time));

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
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        results.Sort((a, b) => a.Time.CompareTo(b.Time));

        return results;
    }

    /// <summary>
    /// Earliest interface_counters point in the primary bucket - the effective floor for
    /// historic playback. first() runs per series (pushed down, cheap), then min picks the
    /// oldest across series. Returns null when unconfigured or no data exists yet.
    /// </summary>
    public async Task<DateTime?> QueryEarliestInterfaceDataAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: -90d)
  |> filter(fn: (r) => r._measurement == ""interface_counters"")
  |> filter(fn: (r) => r._field == ""rate_in_bps"")
  |> first()
  |> group()
  |> min(column: ""_time"")
";
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var time = record.GetTimeInDateTime();
            if (time != null) return ToUtc(time.Value);
        }
        return null;
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
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        foreach (var series in results.Values)
            series.Sort((a, b) => a.Time.CompareTo(b.Time));

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
        var lastClause = "\n  |> last()";
        if (at.HasValue)
        {
            // Same fetch window as the historic snapshot used elsewhere on the Live
            // tab. No last() here: last() would take the newest sample in the window
            // (up to 30 s AFTER the scrub instant), putting the table ahead of the
            // map and stat cards, which pick the sample NEAREST the instant. Fetch
            // every sample and let the coalescing below pick the nearest one.
            var center = at.Value.ToUniversalTime();
            rangeClause = $"range(start: {ToFluxInstant(center.AddSeconds(-90))}, stop: {ToFluxInstant(center.AddSeconds(30))})";
            lastClause = "";
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
  |> filter(fn: (r) => r._measurement == ""interface_counters""){macFilter}{lastClause}
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
        // row per (device, interface), coalescing each field from the best sample
        // that carries it: nearest to the scrub instant in historic mode (keeping
        // the table in lockstep with the map, which picks nearest), most recent
        // otherwise.
        var atUtc = at?.ToUniversalTime();
        return raw
            .GroupBy(p => (p.DeviceMac, p.IfName), TupleMacIfComparer)
            .Select(g =>
            {
                var ordered = atUtc.HasValue
                    ? g.OrderBy(p => Math.Abs((p.Time - atUtc.Value).Ticks)).ToList()
                    : g.OrderByDescending(p => p.Time).ToList();
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

    /// <summary>
    /// Gateway WAN throughput from the SNMP interface counters, for the interface(s) named.
    ///
    /// CONTRACT: passing more than one interface SUMS them into a single combined series
    /// (grouped per (_time, _field)). That is correct for exactly ONE caller class - the
    /// all-WAN usage fingerprint, which asks "was the user doing anything on any WAN" - and
    /// wrong for every load/utilization computation, because a summed multi-WAN series divided
    /// by one WAN's plan speeds silently understates or overstates load (and ISP Health's
    /// packet-loss ceiling scales with load QUADRATICALLY, so the damage compounds). Per-WAN
    /// load callers must resolve the one counter interface of the WAN they are pairing with
    /// plan speeds and pass exactly that. Callers that intend the sum must say so via
    /// <paramref name="sumAcrossInterfaces"/>; a multi-interface call without it asserts in
    /// debug builds and logs a warning in release (behavior is unchanged so an existing
    /// caller cannot break, but the mispairing is named at the choke point).
    /// </summary>
    public async Task<IReadOnlyList<WanRatePoint>> QueryGatewayWanRatesAsync(
        string deviceMac,
        IReadOnlyList<string> wanIfNames,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        int sampleIntervalSeconds = 5,
        bool sumAcrossInterfaces = false,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured || wanIfNames.Count == 0) return Array.Empty<WanRatePoint>();
        if (wanIfNames.Count > 1 && !sumAcrossInterfaces)
        {
            System.Diagnostics.Debug.Assert(false,
                "QueryGatewayWanRatesAsync sums multiple interfaces into one series; that is only " +
                "valid for the all-WAN usage fingerprint. Pass sumAcrossInterfaces: true if the sum " +
                "is intended, or resolve the single counter interface of the WAN being measured.");
            _logger.LogWarning(
                "QueryGatewayWanRatesAsync called with {Count} interfaces without sumAcrossInterfaces; " +
                "the result is a summed multi-interface series ({IfNames})",
                wanIfNames.Count, string.Join(",", wanIfNames));
        }
        var window = aggregateWindow ?? PickAggregateWindow(to - from, sampleIntervalSeconds);
        var mac = NormalizeMac(deviceMac);
        var ifFilter = string.Join(" or ", wanIfNames.Select(n =>
            $@"r.if_name == ""{SanitizeFluxString(n)}"""));
        // Summing across interfaces groups by (_time, _field), which on a long window at a fine
        // aggregate means one server-side group per sample - measured at 6657ms against 490ms without,
        // for 148k samples over 30 days. With a single WAN there is nothing to sum: the stage returns
        // its one input value unchanged, so skipping it is identical output, not an approximation.
        // Several interfaces still take the summing path, where it earns its cost.
        var multiWanSum = wanIfNames.Count > 1
            ? @"
  |> group(columns: [""_time"", ""_field""])
  |> sum()
  |> group()"
            : string.Empty;
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""interface_counters"")
  |> filter(fn: (r) => r.device_mac == ""{mac}"")
  |> filter(fn: (r) => {ifFilter})
  |> filter(fn: (r) => r._field == ""rate_in_bps"" or r._field == ""rate_out_bps"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false){multiWanSum}
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
  |> sort(columns: [""_time""])
";
        // Raw CSV rather than the client's FluxRecord model, same as the latency-detail reads: ISP
        // Health holds this series fine-grained however long the window is, so a month reads six
        // figures of rows and the per-record boxing costs multiples of the query itself. Parsed
        // line-by-line as the response streams in - never materialized as one large string.
        var parser = new WanRatesCsvParser();
        await QueryRawLinesAsync(flux, parser.ProcessLine, ct);
        var results = parser.Finish();
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        results.Sort((a, b) => a.Time.CompareTo(b.Time));

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
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
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
    /// <param name="wanScope">Which WAN's points count; null reads every WAN, as it always did.</param>
    public async Task<Dictionary<string, List<LatencyPoint>>> QueryLatencyByTargetTypeAsync(
        MonitoringTargetType targetType,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        LatencyWanScope? wanScope = null,
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
  |> filter(fn: (r) => {typeFilter}){BuildWanScopeFilter(wanScope)}
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
    /// Which WAN's latency series a group-level (target_type) read should return, expressed
    /// against the Influx <c>wan</c> tag. The tag is ABSENT on every point the primary path
    /// writes (single-WAN installs never emit it - additive-only schema), and carries
    /// <c>WanContext.InfluxWanTag</c> (the UniFi wan key, e.g. "wan2") on points probed
    /// through a WAN context. Null scope = no wan filter, today's behavior for every
    /// non-ISP-Health caller.
    /// </summary>
    /// <param name="IncludeUntagged">Include points with NO wan tag (the primary path's points).</param>
    /// <param name="WanTags">Tag values to include (a scoped WAN's key, plus any context display
    /// names that tagged its points before the stable-key tagging landed).</param>
    public sealed record LatencyWanScope(bool IncludeUntagged, IReadOnlyList<string> WanTags)
    {
        /// <summary>Scope for the primary WAN: untagged points, plus any contexts bound to it.</summary>
        public static LatencyWanScope Primary(IReadOnlyList<string>? primaryContextTags = null) =>
            new(true, primaryContextTags ?? Array.Empty<string>());

        /// <summary>Scope for a non-primary WAN: only points tagged with its wan-key/context tags.</summary>
        public static LatencyWanScope ForWan(IReadOnlyList<string> wanTags) => new(false, wanTags);
    }

    /// <summary>
    /// The Flux filter stage for a <see cref="LatencyWanScope"/>, or "" for no filter.
    ///
    /// Filter shape is deliberate - keep it a plain predicate the storage engine can push down:
    /// - Primary with no contexts: <c>not exists r.wan</c>. Tag ABSENCE, not empty-string - a
    ///   series that never wrote the tag has no "wan" column at all, so <c>r.wan == ""</c> would
    ///   match nothing (the comparison against the missing column is null and the row is dropped).
    /// - Non-primary: plain <c>r.wan == "..."</c> equality chain (indexed tag equality; series
    ///   without the tag simply never match). No regex, no client-side post-filtering.
    /// - Primary with contexts bound to the primary WAN (rare): the OR of both shapes, so a
    ///   primary probed both untagged (server default route) and through a primary context keeps
    ///   all its points.
    /// Do not "simplify" the absence check into an equality against "" - it changes matches, and
    /// the mixed OR shape is only emitted when primary-WAN contexts actually exist.
    /// </summary>
    internal static string BuildWanScopeFilter(LatencyWanScope? scope)
    {
        if (scope == null) return string.Empty;
        var clauses = new List<string>();
        if (scope.IncludeUntagged) clauses.Add("not exists r.wan");
        clauses.AddRange(scope.WanTags
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.Ordinal)
            .Select(t => $@"r.wan == ""{SanitizeFluxString(t)}"""));
        if (clauses.Count == 0)
            // A tags-only scope with no usable tag values can match nothing; emit an
            // always-false predicate rather than silently returning every WAN's data.
            return "\n  |> filter(fn: (r) => exists r.wan and not exists r.wan)";
        return $"\n  |> filter(fn: (r) => {string.Join(" or ", clauses)})";
    }

    /// <summary>
    /// Like QueryLatencyByTargetTypeAsync but also pivots max RTT and jitter, which the
    /// ISP Health scorer and congestion/step detectors need. Kept separate so existing
    /// chart callers keep the leaner LatencyPoint shape. <paramref name="wanScope"/>
    /// restricts the read to one WAN's series via the <c>wan</c> tag (see
    /// <see cref="LatencyWanScope"/>); null keeps today's unscoped read.
    /// </summary>
    public async Task<Dictionary<string, List<LatencySeriesPoint>>> QueryLatencyDetailByTargetTypeAsync(
        MonitoringTargetType targetType,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        LatencyWanScope? wanScope = null,
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
  |> filter(fn: (r) => {typeFilter}){BuildWanScopeFilter(wanScope)}
  |> filter(fn: (r) => r._field == ""rtt_avg_ms"" or r._field == ""rtt_max_ms"" or r._field == ""jitter_ms"" or r._field == ""loss_percent"")
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        // Per-query timing: the aggregate figure said the reads cost ~6.7s in-process while the same
        // queries run in ~1s from a shell on the same box, and neither the parser nor the CPU accounts
        // for the gap. Split it per query so one slow read cannot hide behind the others.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var parser = new LatencyDetailCsvParser();
        await QueryRawLinesAsync(flux, parser.ProcessLine, ct);
        var result = parser.Finish();
        _logger.LogDebug("Influx latency-detail read: {Type} in {Ms}ms, {Targets} target(s), {Points} point(s), window {Window}",
            targetType, sw.ElapsedMilliseconds, result.Count, result.Sum(kv => kv.Value.Count), ToFluxDuration(window));
        return result;
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
        var idParser = new LatencyDetailCsvParser();
        await QueryRawLinesAsync(flux, idParser.ProcessLine, ct);
        var byTarget = idParser.Finish();
        // Single target filter, but flatten defensively in case the pivot emits more than one key.
        var list = byTarget.Values.SelectMany(x => x).ToList();
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
    /// <param name="wanScope">
    /// Which WAN's points count. Filtering by target id alone is not enough: a host reachable from
    /// two WANs is probed under each, and a row that has changed context keeps its older points
    /// under the tag they were written with - so one id can hold more than one WAN's readings, and
    /// an unscoped read draws another WAN's loss on this one's chart.
    /// </param>
    public async Task<IReadOnlyList<LatencyPoint>> QueryMeanIspTransitLatencyAsync(
        DateTime from,
        DateTime to,
        IReadOnlyList<string>? enabledTargetIds = null,
        TimeSpan? aggregateWindow = null,
        LatencyWanScope? wanScope = null,
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
  |> filter(fn: (r) => r.target_type == ""accessisp"" or r.target_type == ""transit""){targetFilter}{BuildWanScopeFilter(wanScope)}

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
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: true)
  |> fill(usePrevious: true)
  |> filter(fn: (r) => exists r._value)
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
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        results.Sort((a, b) => a.Time.CompareTo(b.Time));

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
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        results.Sort((a, b) => a.Time.CompareTo(b.Time));

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
        // TimeSpan.Zero requests RAW (un-aggregated) points. ISP Health needs this so DDM read
        // artifacts (a single glitchy sample where RX dives and temperature jumps) stay isolated for
        // rejection - mean aggregation would smear a glitch into a bucket that defeats the filter.
        var raw = aggregateWindow == TimeSpan.Zero;
        var window = (aggregateWindow is null or { Ticks: 0 }) ? PickAggregateWindow(to - from) : aggregateWindow.Value;

        var macFilter = string.Join(" or ", modules.Select(m =>
            $@"(r.device_mac == ""{NormalizeMac(m.DeviceMac)}"" and r.port_name == ""{SanitizeFluxString(m.PortName)}"")"));

        var aggregateLine = raw ? "" : $@"  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: mean, createEmpty: false)
";
        var flux = $@"
from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""sfp"")
  |> filter(fn: (r) => {macFilter})
  |> filter(fn: (r) => r._field == ""rx_power_dbm"" or r._field == ""tx_power_dbm"" or r._field == ""temperature_c"" or r._field == ""voltage_v"")
{aggregateLine}  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
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

        // Order by time, because the Flux result is NOT globally ordered. pivot emits a separate
        // table whenever the row's field set differs, and those tables arrive after the main one - so
        // an interval where the module reported temperature and voltage but no optical power (an ONT
        // during an outage does exactly this) comes back at the END. The chart then drew forward to
        // now and jumped back to the outage, and only on temperature, since the trailing rows carry
        // no rx/tx to plot. QuerySfpPonByModulesAsync is already safe because BuildPonSeries orders
        // its own output.
        foreach (var list in results.Values)
            list.Sort((a, b) => a.Time.CompareTo(b.Time));

        return results;
    }

    /// <summary>One point of supplemental PON-layer stats on the sfp measurement (attached ONT config).
    /// Counters are cumulative; callers derive per-interval deltas.</summary>
    public class SfpPonPoint
    {
        public DateTime Time { get; set; }
        public string? PonLinkStatus { get; set; }
        public string? PonLinkStatusPrev { get; set; }
        public long? OnuId { get; set; }
        public long? DsFecEnabled { get; set; }
        public long? UsFecEnabled { get; set; }
        public long? OnuResponseTime { get; set; }
        public long? SfpUptimeS { get; set; }
        public long? BipErrors { get; set; }
        public long? FecErrors { get; set; }
        public long? FecCorrectedWords { get; set; }
        public long? HecUncorrected { get; set; }
        public long? GemTxFrames { get; set; }
        public long? GemTxIdleFrames { get; set; }
        public long? GemRxFrames { get; set; }
        public long? GemRxDropped { get; set; }
        public long? AllocLost { get; set; }
        public long? LanRxFcsErrors { get; set; }
        public long? LanTxDropEvents { get; set; }
        public long? LanBufferOverflow { get; set; }
    }

    /// <summary>
    /// Supplemental PON-layer time-series for a set of (device_mac, port_name) pairs.
    /// Only modules with an attached ONT config have these fields; others return no rows.
    /// Aggregates with fn: last (counters are cumulative - mean would distort deltas).
    /// </summary>
    public async Task<Dictionary<string, List<SfpPonPoint>>> QuerySfpPonByModulesAsync(
        IReadOnlyList<(string DeviceMac, string PortName)> modules,
        DateTime from,
        DateTime to,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured || modules.Count == 0) return new Dictionary<string, List<SfpPonPoint>>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);

        var macFilter = string.Join(" or ", modules.Select(m =>
            $@"(r.device_mac == ""{NormalizeMac(m.DeviceMac)}"" and r.port_name == ""{SanitizeFluxString(m.PortName)}"")"));

        var fields = new[]
        {
            "pon_link_status", "pon_link_status_prev", "onu_id", "ds_fec_enabled", "us_fec_enabled",
            "onu_response_time", "sfp_uptime_s", "bip_errors", "fec_errors", "fec_corrected_words",
            "hec_uncorrected", "gem_tx_frames", "gem_tx_idle_frames", "gem_rx_frames", "gem_rx_dropped",
            "alloc_lost", "lan_rx_fcs_err", "lan_tx_drop_events", "lan_buffer_overflow",
        };
        var fieldFilter = string.Join(" or ", fields.Select(f => $@"r._field == ""{f}"""));

        var flux = $@"
from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""sfp"")
  |> filter(fn: (r) => {macFilter})
  |> filter(fn: (r) => {fieldFilter})
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: last, createEmpty: false)
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new Dictionary<string, List<SfpPonPoint>>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var mac = record.GetValueByKey("device_mac") as string ?? "";
            var port = record.GetValueByKey("port_name") as string ?? "";
            var key = $"{mac}:{port}";
            if (!results.TryGetValue(key, out var list))
            {
                list = new List<SfpPonPoint>();
                results[key] = list;
            }
            list.Add(new SfpPonPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                PonLinkStatus = record.GetValueByKey("pon_link_status") as string,
                PonLinkStatusPrev = record.GetValueByKey("pon_link_status_prev") as string,
                OnuId = AsLongOrNull(record.GetValueByKey("onu_id")),
                DsFecEnabled = AsLongOrNull(record.GetValueByKey("ds_fec_enabled")),
                UsFecEnabled = AsLongOrNull(record.GetValueByKey("us_fec_enabled")),
                OnuResponseTime = AsLongOrNull(record.GetValueByKey("onu_response_time")),
                SfpUptimeS = AsLongOrNull(record.GetValueByKey("sfp_uptime_s")),
                BipErrors = AsLongOrNull(record.GetValueByKey("bip_errors")),
                FecErrors = AsLongOrNull(record.GetValueByKey("fec_errors")),
                FecCorrectedWords = AsLongOrNull(record.GetValueByKey("fec_corrected_words")),
                HecUncorrected = AsLongOrNull(record.GetValueByKey("hec_uncorrected")),
                GemTxFrames = AsLongOrNull(record.GetValueByKey("gem_tx_frames")),
                GemTxIdleFrames = AsLongOrNull(record.GetValueByKey("gem_tx_idle_frames")),
                GemRxFrames = AsLongOrNull(record.GetValueByKey("gem_rx_frames")),
                GemRxDropped = AsLongOrNull(record.GetValueByKey("gem_rx_dropped")),
                AllocLost = AsLongOrNull(record.GetValueByKey("alloc_lost")),
                LanRxFcsErrors = AsLongOrNull(record.GetValueByKey("lan_rx_fcs_err")),
                LanTxDropEvents = AsLongOrNull(record.GetValueByKey("lan_tx_drop_events")),
                LanBufferOverflow = AsLongOrNull(record.GetValueByKey("lan_buffer_overflow")),
            });
        }
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        foreach (var series in results.Values)
            series.Sort((a, b) => a.Time.CompareTo(b.Time));

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
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        foreach (var series in results.Values)
            series.Sort((a, b) => a.Time.CompareTo(b.Time));

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
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        foreach (var series in results.Values)
            series.Sort((a, b) => a.Time.CompareTo(b.Time));

        return results;
    }

    /// <summary>
    /// Query Starlink terminal metrics over a time range.
    /// Returns dict keyed by starlink_id.
    /// </summary>
    public async Task<Dictionary<string, List<StarlinkPoint>>> QueryStarlinkAsync(
        DateTime from,
        DateTime to,
        string? starlinkId = null,
        TimeSpan? aggregateWindow = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured) return new Dictionary<string, List<StarlinkPoint>>();
        var window = aggregateWindow ?? PickAggregateWindow(to - from);

        var idFilter = !string.IsNullOrEmpty(starlinkId)
            ? $@"|> filter(fn: (r) => r.starlink_id == ""{SanitizeFluxString(starlinkId)}"")"
            : "";

        var winDur = ToFluxDuration(window);
        var flux = $@"
gauges = from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""starlink"")
  {idFilter}
  |> filter(fn: (r) => r._field == ""power_in_avg_w"" or r._field == ""ping_drop_rate_avg"" or r._field == ""fraction_obstructed"" or r._field == ""eth_speed_mbps"" or r._field == ""uptime_s"" or r._field == ""gps_sats"" or r._field == ""alignment_offset_deg"" or r._field == ""alert_count"")
  |> aggregateWindow(every: {winDur}, fn: last, createEmpty: false)

peaks = from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""starlink"")
  {idFilter}
  |> filter(fn: (r) => r._field == ""power_in_max_w"" or r._field == ""ping_drop_rate_max"")
  |> aggregateWindow(every: {winDur}, fn: max, createEmpty: false)

deltas = from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""starlink"")
  {idFilter}
  |> filter(fn: (r) => r._field == ""outage_count_delta"" or r._field == ""outage_seconds_delta"")
  |> aggregateWindow(every: {winDur}, fn: sum, createEmpty: false)

union(tables: [gauges, peaks, deltas])
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new Dictionary<string, List<StarlinkPoint>>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var key = record.GetValueByKey("starlink_id") as string ?? "unknown";
            if (!results.TryGetValue(key, out var list))
            {
                list = new List<StarlinkPoint>();
                results[key] = list;
            }
            list.Add(new StarlinkPoint
            {
                Time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                PowerInAvgW = AsDoubleOrNull(record.GetValueByKey("power_in_avg_w")),
                PowerInMaxW = AsDoubleOrNull(record.GetValueByKey("power_in_max_w")),
                PingDropRateAvg = AsDoubleOrNull(record.GetValueByKey("ping_drop_rate_avg")),
                PingDropRateMax = AsDoubleOrNull(record.GetValueByKey("ping_drop_rate_max")),
                FractionObstructed = AsDoubleOrNull(record.GetValueByKey("fraction_obstructed")),
                EthSpeedMbps = AsIntOrNull(record.GetValueByKey("eth_speed_mbps")),
                UptimeS = AsLongOrNull(record.GetValueByKey("uptime_s")),
                GpsSats = AsIntOrNull(record.GetValueByKey("gps_sats")),
                AlignmentOffsetDeg = AsDoubleOrNull(record.GetValueByKey("alignment_offset_deg")),
                OutageCountDelta = AsLongOrNull(record.GetValueByKey("outage_count_delta")),
                OutageSecondsDelta = AsDoubleOrNull(record.GetValueByKey("outage_seconds_delta")),
                AlertCount = AsIntOrNull(record.GetValueByKey("alert_count")),
            });
        }
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        foreach (var series in results.Values)
            series.Sort((a, b) => a.Time.CompareTo(b.Time));

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
        // TimeSpan.Zero requests RAW points so ISP Health gets per-poll FEC/BIP deltas comparable to
        // the alert spike threshold (aggregation would conflate several polls into one bucket).
        var raw = aggregateWindow == TimeSpan.Zero;
        var window = (aggregateWindow is null or { Ticks: 0 }) ? PickAggregateWindow(to - from) : aggregateWindow.Value;

        var ontFilter = !string.IsNullOrEmpty(ontId)
            ? $@"|> filter(fn: (r) => r.ont_id == ""{SanitizeFluxString(ontId)}"")"
            : "";

        var aggregateLine = raw ? "" : $@"  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: last, createEmpty: false)
";
        var flux = $@"
from(bucket: ""{_longtermBucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""ont"")
  {ontFilter}
  |> filter(fn: (r) => r._field == ""rx_power_dbm"" or r._field == ""tx_power_dbm"" or r._field == ""temperature_c"" or r._field == ""voltage_v"" or r._field == ""bias_ma"" or r._field == ""fec_errors"" or r._field == ""bip_errors"" or r._field == ""pon_link_status"")
{aggregateLine}  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
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
                FecErrors = AsLongOrNull(record.GetValueByKey("fec_errors")),
                BipErrors = AsLongOrNull(record.GetValueByKey("bip_errors")),
                PonLinkStatus = record.GetValueByKey("pon_link_status") as string,
            });
        }
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        foreach (var series in results.Values)
            series.Sort((a, b) => a.Time.CompareTo(b.Time));

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
        /// <summary>
        /// Device the client was attached to at this instant (device_mac tag): the AP for
        /// wifi_client, the switch for wired_client. Named for its original wireless use.
        /// </summary>
        public string? ApMac { get; init; }

        /// <summary>Switch port the client was on (port tag); wired_client only.</summary>
        public int? Port { get; init; }

        /// <summary>Client name as recorded at this instant; wired_client only.</summary>
        public string? ClientName { get; init; }
    }

    public async Task<IReadOnlyList<ClientThroughputPoint>> QueryAllClientThroughputAsync(
        string measurement,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<ClientThroughputPoint>();
        // signal_dbm / tx_rate_kbps / rx_rate_kbps only exist on wifi_client, and client_name
        // only on wired_client; harmless to request either way (no rows match, columns come back
        // absent -> null). band, device_mac and port are tags and survive the pivot as columns.
        var flux = $@"from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""{measurement}"")
  |> filter(fn: (r) => r._field == ""tx_throughput_bps"" or r._field == ""rx_throughput_bps"" or r._field == ""client_mac"" or r._field == ""signal_dbm"" or r._field == ""tx_rate_kbps"" or r._field == ""rx_rate_kbps"" or r._field == ""client_name"")
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
                Port = (int?)AsDoubleOrNull(record.GetValueByKey("port")),
                ClientName = record.GetValueByKey("client_name") as string,
            });
        }
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        results.Sort((a, b) => a.Time.CompareTo(b.Time));

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
        // Order on assembly: the Flux result is NOT globally ordered. pivot emits a separate table
        // whenever a row's field set differs, and those tables arrive after the main one - so an
        // interval where a device reported some fields but not others comes back at the END.
        results.Sort((a, b) => a.Time.CompareTo(b.Time));

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

    /// <summary>
    /// Bucket size for a range: about 150 points, floored so a short range cannot ask for buckets
    /// finer than the data actually has.
    ///
    /// The floor defaults to 5 s, which was the sample interval when this was written. A caller
    /// that knows its site's real interval passes it instead - otherwise a site polling faster has
    /// the extra resolution averaged straight back out, and one polling slower gets buckets with
    /// nothing in them.
    /// </summary>
    private static TimeSpan PickAggregateWindow(TimeSpan range, int floorSeconds = 5)
    {
        const int targetPoints = 150;
        var floor = Math.Max(1, floorSeconds);
        var windowSeconds = Math.Max(floor, (int)(range.TotalSeconds / targetPoints));
        return TimeSpan.FromSeconds(windowSeconds);
    }

    private static string ToFluxInstant(DateTime t) =>
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    /// <summary>
    /// The window as a Flux duration, exactly. Rendering it in whole units truncated anything that was
    /// not a round number of them - a computed 103.97s asked Influx for "1m", so the query ran at 60s
    /// and returned 1.7x the points the caller had sized for, and 90s likewise became "1m". Every
    /// aggregateWindow in this file goes through here, so the drift was silent and app-wide. Seconds
    /// are a valid Flux duration at any magnitude, so emitting them keeps the requested window and the
    /// executed window the same thing.
    /// </summary>
    private static string ToFluxDuration(TimeSpan window) =>
        $"{Math.Max(1, (long)Math.Round(window.TotalSeconds))}s";

    private static string SanitizeFluxString(string value) =>
        value.Replace("\"", "").Replace("\\", "").Replace(")", "").Replace("|>", "").Replace("${", "");

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

    /// <summary>
    /// Runs a Flux query over a directly-streamed HTTP response and feeds each raw annotated-CSV
    /// line to <paramref name="onLine"/> as it comes off the wire. Used for the high-volume
    /// latency-detail and WAN-rate reads, where the InfluxDB client is the dominant cost rather
    /// than the server: its FluxRecord model boxes every value into a per-record dictionary, its
    /// string-returning QueryRawAsync accumulates every line and joins them into one tens-of-MB
    /// large-object-heap string, and either way its transport (RestSharp with the default
    /// ResponseContentRead) buffers the ENTIRE response body in memory before the first line is
    /// handed over - so a month-long read paid the payload several times over in allocations and
    /// could not start parsing until the last byte arrived. This path sends the same query and
    /// annotated-CSV dialect (<see cref="BuildRawQueryBody"/>) with ResponseHeadersRead and parses
    /// incrementally, so the only per-row allocations left are the result points themselves.
    /// Query failures are mapped through the client's own <c>HttpException.Create</c>, keeping the
    /// exception types (and this method's not-found/bad-request/unauthorized handling) identical
    /// to the buffered path it replaces; same error handling as QueryFluxAsync.
    /// </summary>
    private async Task QueryRawLinesAsync(string flux, CsvLineSink onLine, CancellationToken ct)
    {
        if (_rawQueryHttp == null || string.IsNullOrEmpty(_org)) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "api/v2/query?org=" + Uri.EscapeDataString(_org));
            request.Content = new StringContent(BuildRawQueryBody(flux),
                System.Text.Encoding.UTF8, "application/json");
            using var response = await _rawQueryHttp
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw InfluxDB.Client.Core.Exceptions.HttpException.Create(errorBody,
                    (IEnumerable<RestSharp.HeaderParameter>?)null, response.ReasonPhrase, response.StatusCode);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await FeedStreamLinesAsync(stream, onLine, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is InfluxDB.Client.Core.Exceptions.NotFoundException
            or InfluxDB.Client.Core.Exceptions.BadRequestException
            or InfluxDB.Client.Core.Exceptions.UnauthorizedException)
        {
            _logger.LogWarning("InfluxDB query failed ({Error}) — check Settings > Monitoring", ex.Message);
            _ = PersistHealthAsync(false, ex.Message, CancellationToken.None);
        }
    }

    /// <summary>
    /// JSON body for the raw /api/v2/query POST: the same Flux text and the same annotated-CSV
    /// dialect (group/datatype/default annotations, header row, comma delimiter, '#' comments,
    /// default RFC3339 timestamps) the InfluxDB client sent for these reads, so the response
    /// format - and therefore the parsed result - is unchanged.
    /// </summary>
    private static string BuildRawQueryBody(string flux) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            query = flux,
            type = "flux",
            dialect = new
            {
                header = true,
                delimiter = ",",
                annotations = new[] { "group", "datatype", "default" },
                commentPrefix = "#"
            }
        });

    /// <summary>
    /// Reads a UTF-8 CSV response stream and feeds each line to <paramref name="sink"/> with no
    /// per-line string allocation, splitting on LF exactly as <see cref="FeedCsvLines"/> splits the
    /// buffered string (the parsers strip the trailing CR of InfluxDB's CRLF terminators), so the
    /// streamed and string entry points deliver an identical line sequence.
    /// </summary>
    private static async Task FeedStreamLinesAsync(Stream stream, CsvLineSink sink, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var pool = System.Buffers.ArrayPool<char>.Shared;
        var buf = pool.Rent(64 * 1024);
        try
        {
            var len = 0;
            while (true)
            {
                if (len == buf.Length)
                {
                    // A single line larger than the buffer (never expected for this data): grow.
                    var bigger = pool.Rent(buf.Length * 2);
                    Array.Copy(buf, bigger, len);
                    pool.Return(buf);
                    buf = bigger;
                }
                var read = await reader.ReadAsync(buf.AsMemory(len, buf.Length - len), ct).ConfigureAwait(false);
                if (read == 0) break;
                len += read;

                var start = 0;
                while (true)
                {
                    var nl = buf.AsSpan(start, len - start).IndexOf('\n');
                    if (nl < 0) break;
                    sink(buf.AsSpan(start, nl));
                    start += nl + 1;
                }
                if (start > 0)
                {
                    Array.Copy(buf, start, buf, 0, len - start);
                    len -= start;
                }
            }
            if (len > 0) sink(buf.AsSpan(0, len));
        }
        finally
        {
            pool.Return(buf);
        }
    }

    private delegate void CsvLineSink(ReadOnlySpan<char> line);

    /// <summary>
    /// Splits an in-memory CSV string into lines (LF or CRLF; the parsers strip a trailing CR
    /// themselves) and feeds them to <paramref name="sink"/> - the string-input counterpart of
    /// <see cref="QueryRawLinesAsync"/>, so the string-based parse entry points and the streamed
    /// query path run the exact same per-line logic.
    /// </summary>
    private static void FeedCsvLines(string csv, CsvLineSink sink)
    {
        var span = csv.AsSpan();
        int pos = 0;
        while (pos < span.Length)
        {
            var nl = span.Slice(pos).IndexOf('\n');
            var line = nl < 0 ? span.Slice(pos) : span.Slice(pos, nl);
            pos = nl < 0 ? span.Length : pos + nl + 1;
            sink(line);
        }
    }

    /// <summary>
    /// Parses the annotated-CSV result of the WAN rate pivot query (columns _time, rate_in_bps,
    /// rate_out_bps) into points, on the same span-based basis as <see cref="ParseLatencyDetailCsv"/>
    /// and for the same reason: ISP Health holds the rate series at a fine interval however long the
    /// window is - it is the only signal that can tell sustained load from a spike - so a month-long
    /// window reads six figures of rows here, and the client's FluxRecord model costs more per record
    /// than the query costs in total.
    /// </summary>
    internal static List<WanRatePoint> ParseWanRatesCsv(string csv)
    {
        var parser = new WanRatesCsvParser();
        if (!string.IsNullOrEmpty(csv)) FeedCsvLines(csv, parser.ProcessLine);
        return parser.Finish();
    }

    /// <summary>
    /// Incremental line-by-line form of <see cref="ParseWanRatesCsv"/>, fed straight from
    /// <see cref="QueryRawLinesAsync"/> so a month-long rate read is parsed as it streams in
    /// instead of being materialized as one large string first. The per-line logic is the
    /// string parser's, verbatim; <see cref="ParseWanRatesCsv"/> delegates here so the two
    /// entry points can never drift.
    /// </summary>
    internal sealed class WanRatesCsvParser
    {
        private readonly List<WanRatePoint> _result = new();
        private int _iTime = -1, _iIn = -1, _iOut = -1;
        private bool _expectHeader = true;

        public void ProcessLine(ReadOnlySpan<char> line)
        {
            if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Slice(0, line.Length - 1);
            if (line.IsEmpty) return;
            if (line[0] == '#') { _expectHeader = true; return; }

            if (_expectHeader)
            {
                // Re-read on every header: pivot emits a fresh table whenever the field set differs,
                // so a run where one direction went missing has its own column order.
                _iTime = _iIn = _iOut = -1;
                int col = 0, p = 0;
                while (true)
                {
                    var comma = line.Slice(p).IndexOf(',');
                    var cell = comma < 0 ? line.Slice(p) : line.Slice(p, comma);
                    if (cell.SequenceEqual("_time")) _iTime = col;
                    else if (cell.SequenceEqual("rate_in_bps")) _iIn = col;
                    else if (cell.SequenceEqual("rate_out_bps")) _iOut = col;
                    col++;
                    if (comma < 0) break;
                    p += comma + 1;
                }
                _expectHeader = false;
                return;
            }

            if (_iTime < 0) return;

            ReadOnlySpan<char> tTime = default, tIn = default, tOut = default;
            int c = 0, q = 0;
            while (true)
            {
                var comma = line.Slice(q).IndexOf(',');
                var cell = comma < 0 ? line.Slice(q) : line.Slice(q, comma);
                if (c == _iTime) tTime = cell;
                else if (c == _iIn) tIn = cell;
                else if (c == _iOut) tOut = cell;
                c++;
                if (comma < 0) break;
                q += comma + 1;
            }

            if (tTime.IsEmpty) return;
            if (!DateTime.TryParse(tTime, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var time))
                return;

            // rate_in_bps on a WAN interface = bytes received from the ISP = download.
            _result.Add(new WanRatePoint
            {
                Time = DateTime.SpecifyKind(time.ToUniversalTime(), DateTimeKind.Utc),
                DownloadBps = ParseDoubleOrNull(tIn),
                UploadBps = ParseDoubleOrNull(tOut)
            });
        }

        /// <summary>Returns the accumulated points in arrival order (callers sort, as before).</summary>
        public List<WanRatePoint> Finish() => _result;
    }

    /// <summary>
    /// Parses the annotated-CSV result of a latency-detail pivot query (columns target_id, _time,
    /// rtt_avg_ms, rtt_max_ms, jitter_ms, loss_percent) directly into per-target point lists. Span-
    /// based: the only allocations are the per-target list, the distinct target_id keys, and the
    /// points themselves - no FluxRecord, no boxing, no intermediate copy. Annotation rows (#...) are
    /// skipped and the column header is re-read whenever Flux emits one (default annotated dialect),
    /// so column order is never assumed. Tag/field values in this measurement never contain commas,
    /// so a plain comma split is safe.
    /// </summary>
    internal static Dictionary<string, List<LatencySeriesPoint>> ParseLatencyDetailCsv(string csv)
    {
        var parser = new LatencyDetailCsvParser();
        if (!string.IsNullOrEmpty(csv)) FeedCsvLines(csv, parser.ProcessLine);
        return parser.Finish();
    }

    /// <summary>
    /// Incremental line-by-line form of <see cref="ParseLatencyDetailCsv"/>, fed straight from
    /// <see cref="QueryRawLinesAsync"/> so a month-long latency read is parsed as it streams in
    /// instead of being materialized as one large string first. The per-line logic is the string
    /// parser's, verbatim; <see cref="ParseLatencyDetailCsv"/> delegates here so the two entry
    /// points can never drift.
    /// </summary>
    internal sealed class LatencyDetailCsvParser
    {
        private readonly Dictionary<string, List<LatencySeriesPoint>> _result = new();
        private int _iTime = -1, _iTarget = -1, _iRtt = -1, _iRttMax = -1, _iJitter = -1, _iLoss = -1;
        private bool _expectHeader = true; // first non-# line is a header (annotated dialect re-arms this after each #-block)
        private string? _lastKey;
        private List<LatencySeriesPoint>? _lastList;

        public void ProcessLine(ReadOnlySpan<char> line)
        {
            if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Slice(0, line.Length - 1);
            if (line.IsEmpty) return;
            if (line[0] == '#') { _expectHeader = true; return; }

            if (_expectHeader)
            {
                _iTime = _iTarget = _iRtt = _iRttMax = _iJitter = _iLoss = -1;
                int col = 0, p = 0;
                while (true)
                {
                    var comma = line.Slice(p).IndexOf(',');
                    var cell = comma < 0 ? line.Slice(p) : line.Slice(p, comma);
                    if (cell.SequenceEqual("_time")) _iTime = col;
                    else if (cell.SequenceEqual("target_id")) _iTarget = col;
                    else if (cell.SequenceEqual("rtt_avg_ms")) _iRtt = col;
                    else if (cell.SequenceEqual("rtt_max_ms")) _iRttMax = col;
                    else if (cell.SequenceEqual("jitter_ms")) _iJitter = col;
                    else if (cell.SequenceEqual("loss_percent")) _iLoss = col;
                    col++;
                    if (comma < 0) break;
                    p += comma + 1;
                }
                _expectHeader = false;
                return;
            }

            if (_iTime < 0 || _iTarget < 0) return;

            ReadOnlySpan<char> tTime = default, tTarget = default, tRtt = default, tRttMax = default, tJitter = default, tLoss = default;
            int c = 0, q = 0;
            while (true)
            {
                var comma = line.Slice(q).IndexOf(',');
                var cell = comma < 0 ? line.Slice(q) : line.Slice(q, comma);
                if (c == _iTime) tTime = cell;
                else if (c == _iTarget) tTarget = cell;
                else if (c == _iRtt) tRtt = cell;
                else if (c == _iRttMax) tRttMax = cell;
                else if (c == _iJitter) tJitter = cell;
                else if (c == _iLoss) tLoss = cell;
                c++;
                if (comma < 0) break;
                q += comma + 1;
            }

            if (tTarget.IsEmpty || tTime.IsEmpty) return;
            if (!DateTime.TryParse(tTime, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var time))
                return;

            // Rows are grouped by target, so reuse the key/list across a run rather than re-allocating.
            List<LatencySeriesPoint>? list;
            if (_lastKey != null && tTarget.SequenceEqual(_lastKey))
            {
                list = _lastList;
            }
            else
            {
                var key = tTarget.ToString();
                if (!_result.TryGetValue(key, out list)) { list = new List<LatencySeriesPoint>(); _result[key] = list; }
                _lastKey = key;
                _lastList = list;
            }

            list!.Add(new LatencySeriesPoint
            {
                Time = ToUtc(time),
                RttAvgMs = ParseDoubleOrNull(tRtt),
                RttMaxMs = ParseDoubleOrNull(tRttMax),
                JitterMs = ParseDoubleOrNull(tJitter),
                LossPercent = ParseDoubleOrNull(tLoss)
            });
        }

        /// <summary>Sorts each target's points by time and returns the accumulated result.</summary>
        public Dictionary<string, List<LatencySeriesPoint>> Finish()
        {
            foreach (var list in _result.Values)
                list.Sort((a, b) => a.Time.CompareTo(b.Time));
            return _result;
        }
    }

    private static double? ParseDoubleOrNull(ReadOnlySpan<char> s) =>
        s.IsEmpty ? null
        : double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
        : null;

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

        var targetFilter = "";
        if (enabledTargetIds != null && enabledTargetIds.Count > 0)
        {
            var conditions = string.Join(" or ", enabledTargetIds.Select(id => $"r.target_id == \"{id}\""));
            targetFilter = $"\n  |> filter(fn: (r) => {conditions})";
        }

        // Pull every qualifying loss minute in the window time-ascending, then coalesce into
        // events in memory. The caller bounds the window to just before/after the current event
        // (via before/after), so the boundary event we want is fully inside the range.
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(rangeStart)}, stop: {ToFluxInstant(rangeStop)})
  |> filter(fn: (r) => r._measurement == ""latency"")
  |> filter(fn: (r) => r.target_type == ""accessisp"" or r.target_type == ""transit"" or r.target_type == ""internetservice"" or r.target_type == ""wan""){targetFilter}
  |> filter(fn: (r) => r._field == ""loss_percent"")
  |> aggregateWindow(every: 1m, fn: mean, createEmpty: false)
  |> filter(fn: (r) => r._value > 1.0)
  |> group()
  |> sort(columns: [""_time""], desc: false)
";
        var minutes = new List<LossMinute>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow);
            var targetId = record.GetValueByKey("target_id") as string;
            var targetType = record.GetValueByKey("target_type") as string ?? "internetservice";
            var loss = AsDoubleOrNull(record.GetValueByKey("_value")) ?? 0;
            minutes.Add(new LossMinute(time, loss, targetId, targetType));
        }
        return SelectBoundaryEvent(minutes, pickEarliest: after.HasValue);
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
  |> sort(columns: [""_time""], desc: false)
";
        var minutes = new List<LossMinute>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var time = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow);
            var targetId = record.GetValueByKey("target_id") as string;
            var targetType = record.GetValueByKey("target_type") as string ?? "internetservice";
            var loss = AsDoubleOrNull(record.GetValueByKey("_value")) ?? 0;
            minutes.Add(new LossMinute(time, loss, targetId, targetType));
        }
        return SelectBoundaryEvent(minutes, pickEarliest: after.HasValue);
    }

    /// <summary>
    /// Pooled per-sample mean loss_percent across an explicit set of targets over a window - the same
    /// per-sample mean ISP Health's loss factors compute. The caller passes the exact loss-pool target
    /// IDs (see <c>IspHealthService.GetLossPoolTargetIdsAsync</c>) so the Investigate highlight reads
    /// the very pool the score is graded on. The loss event finders report a single target's PEAK-loss
    /// minute to center the chart on the worst point; this is the pooled average over the coalesced
    /// event window instead. Null when the set is empty or no loss samples fall in the window.
    /// </summary>
    public async Task<double?> QueryMeanLossAcrossTargetsAsync(
        IReadOnlyCollection<string> targetIds,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        if (!IsConfigured) await ReconfigureAsync(ct);
        if (!IsConfigured || targetIds.Count == 0) return null;
        var idFilter = string.Join(" or ", targetIds.Select(id => $@"r.target_id == ""{SanitizeFluxString(id)}"""));
        // group() collapses every target's per-sample loss into one table so mean() is the pooled
        // average across the whole set, not per target.
        var flux = $@"
from(bucket: ""{_bucket}"")
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""latency"")
  |> filter(fn: (r) => {idFilter})
  |> filter(fn: (r) => r._field == ""loss_percent"")
  |> group()
  |> mean()
";
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var v = AsDoubleOrNull(record.GetValueByKey("_value"));
            if (v != null) return v;
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
        /// <summary>The peak-loss minute of the coalesced event, used to center the chart.</summary>
        public required DateTime Timestamp { get; init; }
        public required string TargetType { get; init; }
        public string? TargetId { get; init; }
        public double LossPercent { get; init; }

        /// <summary>First and last loss minute of the coalesced event. Stepping back queries
        /// strictly before <see cref="EventStart"/>; stepping forward strictly after
        /// <see cref="EventEnd"/>, so a sustained loss burst is one stop, not many.</summary>
        public DateTime EventStart { get; init; }
        public DateTime EventEnd { get; init; }
    }

    /// <summary>One qualifying loss minute before coalescing into an event.</summary>
    private readonly record struct LossMinute(DateTime Time, double Loss, string? TargetId, string TargetType);

    /// <summary>Loss minutes more than this far apart start a new event. Set to one minute so
    /// only truly adjacent minutes coalesce; a single clean minute (load/loss dropping out, then
    /// resuming) splits the burst, keeping back-to-back transfer spikes as distinct events.</summary>
    private static readonly TimeSpan LossEventCoalesceGap = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Coalesce time-ascending loss minutes into events (gap &lt;= <see cref="LossEventCoalesceGap"/>)
    /// and return the boundary event represented by its peak-loss minute: the earliest event when
    /// stepping forward (<paramref name="pickEarliest"/>), else the latest. Stepping by event, not
    /// by minute, keeps a multi-minute burst from fragmenting into separate stops.
    /// </summary>
    private static RecentLossEvent? SelectBoundaryEvent(List<LossMinute> minutes, bool pickEarliest)
    {
        if (minutes.Count == 0) return null;

        var eventStart = minutes[0].Time;
        var eventEnd = minutes[0].Time;
        var peak = minutes[0];
        var events = new List<(DateTime Start, DateTime End, LossMinute Peak)>();
        for (var i = 1; i < minutes.Count; i++)
        {
            var m = minutes[i];
            if (m.Time - eventEnd > LossEventCoalesceGap)
            {
                events.Add((eventStart, eventEnd, peak));
                eventStart = m.Time;
                eventEnd = m.Time;
                peak = m;
            }
            else
            {
                eventEnd = m.Time;
                if (m.Loss > peak.Loss) peak = m;
            }
        }
        events.Add((eventStart, eventEnd, peak));

        var chosen = pickEarliest ? events[0] : events[^1];
        return new RecentLossEvent
        {
            Timestamp = chosen.Peak.Time,
            TargetType = chosen.Peak.TargetType,
            TargetId = chosen.Peak.TargetId,
            LossPercent = chosen.Peak.Loss,
            EventStart = chosen.Start,
            EventEnd = chosen.End,
        };
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
        /// <summary>Raw ifName tag ("eth8", "0/1") - stable across user-assigned aliases.</summary>
        public string? PortId { get; init; }
        public double? RateInBps { get; init; }
        public double? RateOutBps { get; init; }
    }

    /// <summary>One device's total throughput over one window, every interface and both directions.</summary>
    public record DeviceRateTotalPoint
    {
        /// <summary>Device MAC, as stored in the tag.</summary>
        public required string DeviceMac { get; init; }

        /// <summary>Window start (UTC).</summary>
        public required DateTime Time { get; init; }

        /// <summary>Summed rate over the window, in bits per second.</summary>
        public required double Bps { get; init; }
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

    public record StarlinkPoint
    {
        public required DateTime Time { get; init; }
        public double? PowerInAvgW { get; init; }
        public double? PowerInMaxW { get; init; }
        public double? PingDropRateAvg { get; init; }
        public double? PingDropRateMax { get; init; }
        public double? FractionObstructed { get; init; }
        public int? EthSpeedMbps { get; init; }
        public long? UptimeS { get; init; }
        public int? GpsSats { get; init; }
        public double? AlignmentOffsetDeg { get; init; }
        public long? OutageCountDelta { get; init; }
        public double? OutageSecondsDelta { get; init; }
        public int? AlertCount { get; init; }
    }

    public record OntPoint
    {
        public required DateTime Time { get; init; }
        public double? RxPowerDbm { get; init; }
        public double? TxPowerDbm { get; init; }
        public double? TemperatureC { get; init; }
        public double? VoltageV { get; init; }
        public double? BiasMa { get; init; }
        public long? FecErrors { get; init; }
        public long? BipErrors { get; init; }
        /// <summary>Raw PON link status influx value ("operation", "popup", ...); null when the
        /// source didn't report an O-state on that poll (DDM sticks, a stats-page hiccup).</summary>
        public string? PonLinkStatus { get; init; }
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

    /// <summary>
    /// No-op. Instances are owned by MonitoringInfluxRegistry but handed out
    /// through a scoped forwarding registration, so the DI container calls
    /// DisposeAsync whenever a request/circuit scope ends. Disposing here would
    /// tear down the shared client (its config/flush semaphores) out from under
    /// the collection agent, ISP Health, and chart reads - every subsequent call
    /// then throws ObjectDisposedException. Only the registry may tear it down,
    /// via DisposeOwnedAsync. Mirrors UniFiConnectionService.Dispose/DisposeOwned.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Real teardown, invoked only by the owning registry at app shutdown.</summary>
    public async ValueTask DisposeOwnedAsync()
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
        _rawQueryHttp?.Dispose();
        _rawQueryHttp = null;
    }

    private static string NormalizeMac(string mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace('-', ':');

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private readonly record struct BufferedPoint(PointData Point, bool Longterm);
}

public record InfluxHealthResult(bool Reachable, string? Error);
