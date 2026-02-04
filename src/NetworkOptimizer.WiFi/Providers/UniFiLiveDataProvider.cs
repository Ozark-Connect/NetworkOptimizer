using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Providers;

/// <summary>
/// Wi-Fi data provider that fetches live data from UniFi API.
/// Uses UniFiDiscovery for centralized device classification.
/// </summary>
public class UniFiLiveDataProvider : IWiFiDataProvider
{
    private readonly UniFiApiClient _client;
    private readonly UniFiDiscovery _discovery;
    private readonly ILogger<UniFiLiveDataProvider> _logger;

    public UniFiLiveDataProvider(UniFiApiClient client, UniFiDiscovery discovery, ILogger<UniFiLiveDataProvider> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ProviderName => "UniFi Live";
    public bool SupportsHistoricalData => true; // Via stat/report endpoints

    public async Task<List<AccessPointSnapshot>> GetAccessPointsAsync(CancellationToken cancellationToken = default)
    {
        // Use UniFiDiscovery for centralized device classification (same as Audit and Speed Test)
        var aps = await _discovery.DiscoverAccessPointsAsync(cancellationToken);
        var timestamp = DateTimeOffset.UtcNow;

        // Build a set of AP MACs for mesh parent detection
        var apMacs = new HashSet<string>(aps.Select(ap => ap.Mac.ToLowerInvariant()));

        return aps.Select(ap => MapToAccessPointSnapshot(ap, timestamp, apMacs)).ToList();
    }

    public async Task<List<WirelessClientSnapshot>> GetWirelessClientsAsync(CancellationToken cancellationToken = default)
    {
        var clients = await _client.GetClientsAsync(cancellationToken);
        var wirelessClients = clients.Where(c => c.IsWired == false).ToList();
        var timestamp = DateTimeOffset.UtcNow;

        // Get AP names for lookup using centralized classification
        var aps = await _discovery.DiscoverAccessPointsAsync(cancellationToken);
        var apNames = aps.ToDictionary(d => d.Mac.ToLowerInvariant(), d => d.Name);

        return wirelessClients.Select(c => MapToWirelessClientSnapshot(c, apNames, timestamp)).ToList();
    }

    public async Task<List<SiteWiFiMetrics>> GetSiteMetricsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        MetricGranularity granularity = MetricGranularity.FiveMinutes,
        CancellationToken cancellationToken = default)
    {
        // Use the stat/report endpoint for time-series data
        var reportType = granularity switch
        {
            MetricGranularity.FiveMinutes => "5minutes",
            MetricGranularity.Hourly => "hourly",
            MetricGranularity.Daily => "daily",
            _ => "5minutes"
        };

        var attrs = new[]
        {
            "time",  // Must include time to get the timestamp
            "ap-ng-cu_total", "ap-na-cu_total", "ap-6e-cu_total",
            "ap-ng-cu_interf", "ap-na-cu_interf", "ap-6e-cu_interf",
            "ap-ng-tx_retries", "ap-na-tx_retries", "ap-6e-tx_retries",
            "ap-ng-wifi_tx_attempts", "ap-na-wifi_tx_attempts", "ap-6e-wifi_tx_attempts",
            "ap-ng-wifi_tx_dropped", "ap-na-wifi_tx_dropped", "ap-6e-wifi_tx_dropped",
            "ap-ng-tx_packets", "ap-na-tx_packets", "ap-6e-tx_packets",
            "ap-ng-rx_packets", "ap-na-rx_packets", "ap-6e-rx_packets"
        };

        try
        {
            _logger.LogDebug("Fetching site metrics: {ReportType}, start={Start}, end={End}",
                reportType, start, end);

            var reportData = await _client.PostSiteReportAsync(
                reportType,
                start.ToUnixTimeMilliseconds(),
                end.ToUnixTimeMilliseconds(),
                attrs,
                cancellationToken);

            _logger.LogDebug("Site report response: ValueKind={ValueKind}, ArrayLength={Length}",
                reportData.ValueKind,
                reportData.ValueKind == System.Text.Json.JsonValueKind.Array ? reportData.GetArrayLength() : 0);

            var metrics = ParseSiteMetrics(reportData);
            _logger.LogInformation("Parsed {Count} site metrics data points", metrics.Count);
            return metrics;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch site metrics report");
            return new List<SiteWiFiMetrics>();
        }
    }

    public async Task<List<SiteWiFiMetrics>> GetApMetricsAsync(
        string[] apMacs,
        DateTimeOffset start,
        DateTimeOffset end,
        MetricGranularity granularity = MetricGranularity.FiveMinutes,
        CancellationToken cancellationToken = default)
    {
        var reportType = granularity switch
        {
            MetricGranularity.FiveMinutes => "5minutes",
            MetricGranularity.Hourly => "hourly",
            MetricGranularity.Daily => "daily",
            _ => "5minutes"
        };

        // AP endpoint uses ng-* prefix (no 'ap-' prefix like site endpoint)
        var attrs = new[]
        {
            "time",
            "ng-cu_total", "na-cu_total", "6e-cu_total",
            "ng-cu_interf", "na-cu_interf", "6e-cu_interf",
            "ng-tx_retries", "na-tx_retries", "6e-tx_retries",
            "ng-wifi_tx_attempts", "na-wifi_tx_attempts", "6e-wifi_tx_attempts",
            "ng-wifi_tx_dropped", "na-wifi_tx_dropped", "6e-wifi_tx_dropped",
            "ng-tx_packets", "na-tx_packets", "6e-tx_packets",
            "ng-rx_packets", "na-rx_packets", "6e-rx_packets"
        };

        try
        {
            _logger.LogDebug("Fetching AP metrics: {ReportType}, APs={Macs}, start={Start}, end={End}",
                reportType, string.Join(",", apMacs), start, end);

            var reportData = await _client.PostApReportAsync(
                reportType,
                apMacs,
                start.ToUnixTimeMilliseconds(),
                end.ToUnixTimeMilliseconds(),
                attrs,
                cancellationToken);

            _logger.LogDebug("AP report response: ValueKind={ValueKind}, ArrayLength={Length}",
                reportData.ValueKind,
                reportData.ValueKind == JsonValueKind.Array ? reportData.GetArrayLength() : 0);

            // Parse using AP-specific prefixes (no 'ap-' prefix)
            var metrics = ParseApMetrics(reportData);
            _logger.LogInformation("Parsed {Count} AP metrics data points", metrics.Count);
            return metrics;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch AP metrics report");
            return new List<SiteWiFiMetrics>();
        }
    }

    public async Task<List<ClientWiFiMetrics>> GetClientMetricsAsync(
        string clientMac,
        DateTimeOffset start,
        DateTimeOffset end,
        MetricGranularity granularity = MetricGranularity.FiveMinutes,
        CancellationToken cancellationToken = default)
    {
        var reportType = granularity switch
        {
            MetricGranularity.FiveMinutes => "5minutes",
            MetricGranularity.Hourly => "hourly",
            MetricGranularity.Daily => "daily",
            _ => "5minutes"
        };

        var attrs = new[]
        {
            "time",  // Must include time to get timestamp
            "signal", "tx_retries", "tx_packets", "rx_packets",
            "wifi_tx_attempts", "wifi_tx_dropped"
        };

        try
        {
            _logger.LogDebug("Fetching client metrics for {ClientMac}: {ReportType}, start={Start}, end={End}",
                clientMac, reportType, start, end);

            var reportData = await _client.PostUserReportAsync(
                reportType,
                clientMac,
                start.ToUnixTimeMilliseconds(),
                end.ToUnixTimeMilliseconds(),
                attrs,
                cancellationToken);

            _logger.LogDebug("Client report response for {ClientMac}: ValueKind={ValueKind}, ArrayLength={Length}",
                clientMac,
                reportData.ValueKind,
                reportData.ValueKind == System.Text.Json.JsonValueKind.Array ? reportData.GetArrayLength() : 0);

            var metrics = ParseClientMetrics(reportData, clientMac);
            _logger.LogInformation("Parsed {Count} client metrics data points for {ClientMac}", metrics.Count, clientMac);
            return metrics;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch client metrics report for {ClientMac}", clientMac);
            return new List<ClientWiFiMetrics>();
        }
    }

    public async Task<List<WlanConfiguration>> GetWlanConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var wlanConfigs = await _client.GetWlanConfigurationsAsync(cancellationToken);
            return wlanConfigs.Select(w => new WlanConfiguration
            {
                Id = w.Id,
                Name = w.Name,
                Enabled = w.Enabled,
                IsGuest = w.IsGuest,
                HideSsid = w.HideSsid,
                Security = w.Security,
                MloEnabled = w.MloEnabled,
                FastRoamingEnabled = w.FastRoamingEnabled,
                BssTransitionEnabled = w.BssTransition,
                L2IsolationEnabled = w.L2Isolation,
                BandSteeringEnabled = w.No2ghzOui,
                EnabledBands = ParseBands(w.WlanBands),
                MinRateSettings = new MinRateSettings
                {
                    Enabled2_4GHz = w.MinrateNgEnabled,
                    MinRate2_4GHz = w.MinrateNgEnabled ? w.MinrateNgDataRateKbps : null,
                    Enabled5GHz = w.MinrateNaEnabled,
                    MinRate5GHz = w.MinrateNaEnabled ? w.MinrateNaDataRateKbps : null,
                    AdvertiseLowerRates = w.MinrateNgAdvertisingRates || w.MinrateNaAdvertisingRates
                }
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch WLAN configurations");
            return new List<WlanConfiguration>();
        }
    }

    private static List<RadioBand> ParseBands(List<string>? bands)
    {
        if (bands == null || bands.Count == 0)
            return new List<RadioBand>();

        var result = new List<RadioBand>();
        foreach (var band in bands)
        {
            switch (band.ToLowerInvariant())
            {
                case "2g":
                    result.Add(RadioBand.Band2_4GHz);
                    break;
                case "5g":
                    result.Add(RadioBand.Band5GHz);
                    break;
                case "6g":
                    result.Add(RadioBand.Band6GHz);
                    break;
            }
        }
        return result;
    }

    public async Task<List<RoamingEvent>> GetRoamingEventsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? clientMac = null,
        CancellationToken cancellationToken = default)
    {
        // The roaming topology endpoint provides aggregate stats, not individual events.
        // Use GetClientConnectionEventsAsync for individual roaming events.
        _logger.LogDebug("Roaming events not yet implemented - use GetClientConnectionEventsAsync instead");
        return new List<RoamingEvent>();
    }

    /// <summary>
    /// Get client connection events (connects, disconnects, roams) for a specific client
    /// </summary>
    public async Task<List<ClientConnectionEvent>> GetClientConnectionEventsAsync(
        string clientMac,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Fetching client connection events for {ClientMac}", clientMac);
            var data = await _client.GetClientConnectionEventsAsync(clientMac, limit, cancellationToken);
            return ParseClientConnectionEvents(data, clientMac);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch client connection events for {ClientMac}", clientMac);
            return new List<ClientConnectionEvent>();
        }
    }

    private List<ClientConnectionEvent> ParseClientConnectionEvents(JsonElement data, string clientMac)
    {
        var events = new List<ClientConnectionEvent>();

        if (data.ValueKind != JsonValueKind.Array)
        {
            _logger.LogDebug("Client connection events data is not an array");
            return events;
        }

        foreach (var item in data.EnumerateArray())
        {
            try
            {
                var evt = new ClientConnectionEvent
                {
                    ClientMac = clientMac
                };

                if (item.TryGetProperty("id", out var idProp))
                    evt.Id = idProp.GetString() ?? "";

                if (item.TryGetProperty("key", out var keyProp))
                    evt.Key = keyProp.GetString() ?? "";

                if (item.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.Number)
                    evt.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsProp.GetInt64());

                // Determine event type from key
                // Note: Check DISCONNECTED before CONNECTED since "DISCONNECTED" contains "CONNECTED"
                evt.Type = evt.Key switch
                {
                    var k when k.Contains("ROAMED") => ClientConnectionEventType.Roamed,
                    var k when k.Contains("DISCONNECTED") => ClientConnectionEventType.Disconnected,
                    var k when k.Contains("CONNECTED") => ClientConnectionEventType.Connected,
                    _ => ClientConnectionEventType.Unknown
                };

                // Parse parameters
                if (item.TryGetProperty("parameters", out var paramsProp))
                {
                    // Client info
                    if (paramsProp.TryGetProperty("CLIENT", out var clientProp))
                    {
                        evt.ClientName = GetNestedString(clientProp, "name");
                    }

                    // WLAN
                    if (paramsProp.TryGetProperty("WLAN", out var wlanProp))
                    {
                        evt.WlanName = GetNestedString(wlanProp, "name");
                    }

                    // IP
                    if (paramsProp.TryGetProperty("IP", out var ipProp))
                    {
                        evt.IpAddress = GetNestedString(ipProp, "name");
                    }

                    // WiFi stats summary
                    if (paramsProp.TryGetProperty("WIFI_STATS", out var wifiStatsProp))
                    {
                        evt.WifiStats = GetNestedString(wifiStatsProp, "name");
                    }

                    // Current signal/band/channel
                    if (paramsProp.TryGetProperty("SIGNAL_STRENGTH", out var sigProp))
                    {
                        var sigStr = GetNestedString(sigProp, "name");
                        if (int.TryParse(sigStr, out var sig)) evt.Signal = sig;
                    }

                    if (paramsProp.TryGetProperty("RADIO_BAND", out var bandProp))
                    {
                        evt.RadioBand = GetNestedString(bandProp, "name");
                    }

                    if (paramsProp.TryGetProperty("CHANNEL", out var chanProp))
                    {
                        var chanStr = GetNestedString(chanProp, "name");
                        if (int.TryParse(chanStr, out var chan)) evt.Channel = chan;
                    }

                    if (paramsProp.TryGetProperty("CHANNEL_WIDTH", out var widthProp))
                    {
                        var widthStr = GetNestedString(widthProp, "name");
                        if (int.TryParse(widthStr, out var width)) evt.ChannelWidth = width;
                    }

                    // Device (for connect events)
                    if (paramsProp.TryGetProperty("DEVICE", out var deviceProp))
                    {
                        evt.ApMac = GetNestedString(deviceProp, "id");
                        evt.ApName = GetNestedString(deviceProp, "name");
                    }

                    // Roaming-specific: DEVICE_FROM, DEVICE_TO, previous signal/band
                    if (paramsProp.TryGetProperty("DEVICE_FROM", out var fromProp))
                    {
                        evt.FromApMac = GetNestedString(fromProp, "id");
                        evt.FromApName = GetNestedString(fromProp, "name");
                    }

                    if (paramsProp.TryGetProperty("DEVICE_TO", out var toProp))
                    {
                        evt.ToApMac = GetNestedString(toProp, "id");
                        evt.ToApName = GetNestedString(toProp, "name");
                    }

                    if (paramsProp.TryGetProperty("PREVIOUS_SIGNAL_STRENGTH", out var prevSigProp))
                    {
                        var prevSigStr = GetNestedString(prevSigProp, "name");
                        if (int.TryParse(prevSigStr, out var prevSig)) evt.PreviousSignal = prevSig;
                    }

                    if (paramsProp.TryGetProperty("PREVIOUS_RADIO_BAND", out var prevBandProp))
                    {
                        evt.PreviousRadioBand = GetNestedString(prevBandProp, "name");
                    }

                    if (paramsProp.TryGetProperty("PREVIOUS_CHANNEL", out var prevChanProp))
                    {
                        var prevChanStr = GetNestedString(prevChanProp, "name");
                        if (int.TryParse(prevChanStr, out var prevChan)) evt.PreviousChannel = prevChan;
                    }

                    // Disconnect-specific: DURATION, DATA_UP, DATA_DOWN
                    if (paramsProp.TryGetProperty("DURATION", out var durProp))
                    {
                        evt.Duration = GetNestedString(durProp, "name");
                    }

                    if (paramsProp.TryGetProperty("DATA_UP", out var dataUpProp))
                    {
                        evt.DataUp = GetNestedString(dataUpProp, "name");
                    }

                    if (paramsProp.TryGetProperty("DATA_DOWN", out var dataDownProp))
                    {
                        evt.DataDown = GetNestedString(dataDownProp, "name");
                    }
                }

                events.Add(evt);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse client connection event");
            }
        }

        _logger.LogDebug("Parsed {Count} client connection events", events.Count);
        return events;
    }

    private static string? GetNestedString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    /// <summary>
    /// Get roaming topology (aggregate roaming statistics between APs)
    /// </summary>
    public async Task<RoamingTopology?> GetRoamingTopologyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _client.GetRoamingTopologyAsync(cancellationToken);
            return ParseRoamingTopology(data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch roaming topology");
            return null;
        }
    }

    public async Task<List<ChannelScanResult>> GetChannelScanResultsAsync(
        string? apMac = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        CancellationToken cancellationToken = default)
    {
        var aps = await _discovery.DiscoverAccessPointsAsync(cancellationToken);

        if (!string.IsNullOrEmpty(apMac))
        {
            aps = aps.Where(d => d.Mac.Equals(apMac, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Get our own BSSIDs to identify own networks
        var ownBssids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ap in aps)
        {
            if (ap.VapTable != null)
            {
                foreach (var vap in ap.VapTable)
                {
                    if (!string.IsNullOrEmpty(vap.Bssid))
                    {
                        ownBssids.Add(vap.Bssid);
                    }
                }
            }
        }

        // Fetch neighboring networks (rogue APs) with time filter
        var rogueAps = await _client.GetRogueApsAsync(startTime, endTime, cancellationToken);
        _logger.LogDebug("Fetched {Count} neighboring networks from rogueap endpoint", rogueAps.Count);

        // Group rogue APs by detecting AP MAC and band
        var rogueApsByApAndBand = rogueAps
            .GroupBy(r => (ApMac: r.ApMac.ToLowerInvariant(), Band: r.Band ?? r.Radio))
            .ToDictionary(
                g => g.Key,
                g => g.ToList());

        var results = new List<ChannelScanResult>();
        var timestamp = DateTimeOffset.UtcNow;

        foreach (var ap in aps)
        {
            // Get neighbors for each radio band on this AP
            var apMacLower = ap.Mac.ToLowerInvariant();

            // Create results for each radio (even if no spectrum data)
            // Use RadioTableStats to get the bands this AP has
            var radioBands = ap.RadioTableStats?.Select(r => r.Radio).Distinct().ToList()
                ?? ap.RadioTable?.Select(r => r.Radio).Distinct().ToList()
                ?? new List<string>();

            foreach (var bandCode in radioBands)
            {
                var band = RadioBandExtensions.FromUniFiCode(bandCode);
                var result = new ChannelScanResult
                {
                    ApMac = ap.Mac,
                    ApName = ap.Name,
                    Band = band,
                    ScanTime = timestamp,
                    Channels = new List<ChannelInfo>(),
                    Neighbors = new List<NeighborNetwork>()
                };

                // Add spectrum data if available from scan_radio_table
                var scanRadio = ap.ScanRadioTable?.FirstOrDefault(sr => sr.Radio == bandCode);
                if (scanRadio?.SpectrumTable != null)
                {
                    foreach (var spectrum in scanRadio.SpectrumTable)
                    {
                        result.Channels.Add(new ChannelInfo
                        {
                            Channel = spectrum.Channel,
                            Width = spectrum.Width,
                            Utilization = spectrum.Utilization,
                            Interference = spectrum.Interference,
                            IsDfs = spectrum.IsDfs ?? false,
                            DfsState = spectrum.DfsState
                        });
                    }
                }

                // Add neighbors from rogueap endpoint
                if (rogueApsByApAndBand.TryGetValue((apMacLower, bandCode), out var neighbors))
                {
                    foreach (var neighbor in neighbors)
                    {
                        result.Neighbors.Add(new NeighborNetwork
                        {
                            Ssid = neighbor.Essid,
                            Bssid = neighbor.Bssid,
                            Channel = neighbor.Channel,
                            Width = neighbor.Width,
                            Signal = neighbor.Signal,
                            IsOwnNetwork = neighbor.IsUbnt || ownBssids.Contains(neighbor.Bssid),
                            Security = neighbor.Security,
                            LastSeen = neighbor.LastSeen.HasValue
                                ? DateTimeOffset.FromUnixTimeSeconds(neighbor.LastSeen.Value)
                                : null
                        });
                    }
                }

                results.Add(result);
            }
        }

        _logger.LogInformation("Spectrum: Loaded {ApCount} APs, {ResultCount} scan results, Found {NeighborCount} neighboring networks",
            aps.Count,
            results.Count,
            results.Sum(r => r.Neighbors.Count));

        return results;
    }

    #region Mapping Helpers

    private AccessPointSnapshot MapToAccessPointSnapshot(DiscoveredDevice ap, DateTimeOffset timestamp, HashSet<string> apMacs)
    {
        // Check if this AP has a wireless uplink to another AP (mesh child)
        var isMeshChild = false;
        string? meshParentMac = null;
        RadioBand? meshUplinkBand = null;
        int? meshUplinkChannel = null;

        if (ap.UplinkType?.Equals("wireless", StringComparison.OrdinalIgnoreCase) == true &&
            !string.IsNullOrEmpty(ap.UplinkMac))
        {
            var uplinkMacLower = ap.UplinkMac.ToLowerInvariant();
            if (apMacs.Contains(uplinkMacLower))
            {
                isMeshChild = true;
                meshParentMac = uplinkMacLower;
                meshUplinkBand = RadioBandExtensions.FromUniFiCode(ap.UplinkRadioBand);
                meshUplinkChannel = ap.UplinkChannel;
            }
        }

        var snapshot = new AccessPointSnapshot
        {
            Mac = ap.Mac,
            Name = ap.Name,
            Model = ap.FriendlyModelName,
            FirmwareVersion = ap.Firmware,
            Ip = ap.IpAddress,
            Satisfaction = ap.Satisfaction,
            Timestamp = timestamp,
            Radios = new List<RadioSnapshot>(),
            Vaps = new List<VapSnapshot>(),
            IsMeshChild = isMeshChild,
            MeshParentMac = meshParentMac,
            MeshUplinkBand = meshUplinkBand,
            MeshUplinkChannel = meshUplinkChannel
        };

        // Map radio_table_stats (runtime stats)
        if (ap.RadioTableStats != null)
        {
            foreach (var radioStats in ap.RadioTableStats)
            {
                var radioConfig = ap.RadioTable?.FirstOrDefault(r => r.Name == radioStats.Name);

                // Calculate interference as total utilization minus self-utilization
                int? interference = null;
                if (radioStats.CuTotal.HasValue)
                {
                    var selfRx = radioStats.CuSelfRx ?? 0;
                    var selfTx = radioStats.CuSelfTx ?? 0;
                    interference = Math.Max(0, radioStats.CuTotal.Value - selfRx - selfTx);
                }

                snapshot.Radios.Add(new RadioSnapshot
                {
                    Name = radioStats.Name,
                    Band = RadioBandExtensions.FromUniFiCode(radioStats.Radio),
                    Channel = radioStats.Channel,
                    ChannelWidth = radioConfig?.ChannelWidth,
                    TxPower = radioStats.TxPower,
                    TxPowerMode = radioConfig?.TxPowerMode,
                    AntennaGain = radioConfig?.AntennaGain,
                    Satisfaction = radioStats.Satisfaction,
                    ClientCount = radioStats.NumSta,
                    ChannelUtilization = radioStats.CuTotal,
                    Interference = interference,
                    TxRetriesPct = radioStats.TxRetriesPct,
                    MinRssiEnabled = radioConfig?.MinRssiEnabled ?? false,
                    MinRssi = radioConfig?.MinRssi,
                    RoamingAssistantEnabled = radioConfig?.AssistedRoamingEnabled ?? false,
                    RoamingAssistantRssi = radioConfig?.AssistedRoamingRssi,
                    HasDfs = radioConfig?.HasDfs ?? false
                });
            }
        }

        // Map vap_table (per-SSID stats)
        if (ap.VapTable != null)
        {
            foreach (var vap in ap.VapTable)
            {
                snapshot.Vaps.Add(new VapSnapshot
                {
                    Essid = vap.Essid,
                    Bssid = vap.Bssid,
                    Band = RadioBandExtensions.FromUniFiCode(vap.Radio),
                    Channel = vap.Channel,
                    ClientCount = vap.NumSta,
                    Satisfaction = vap.Satisfaction,
                    AvgClientSignal = vap.AvgClientSignal,
                    IsGuest = vap.IsGuest ?? false,
                    TxBytes = vap.TxBytes,
                    RxBytes = vap.RxBytes,
                    TxRetries = vap.TxRetries,
                    WifiTxAttempts = vap.WifiTxAttempts,
                    WifiTxDropped = vap.WifiTxDropped
                });
            }
        }

        snapshot.TotalClients = snapshot.Radios.Sum(r => r.ClientCount ?? 0);

        return snapshot;
    }

    private WirelessClientSnapshot MapToWirelessClientSnapshot(
        UniFiClientResponse client,
        Dictionary<string, string> apNames,
        DateTimeOffset timestamp)
    {
        var apMac = client.ApMac?.ToLowerInvariant() ?? "";
        apNames.TryGetValue(apMac, out var apName);

        return new WirelessClientSnapshot
        {
            Mac = client.Mac,
            Name = client.Name ?? client.Hostname ?? client.Mac,
            Ip = client.Ip,
            ApMac = client.ApMac ?? "",
            ApName = apName,
            Essid = client.Essid ?? "",
            Band = RadioBandExtensions.FromUniFiCode(client.Radio),
            Channel = client.Channel,
            Signal = client.Signal,
            Noise = client.Noise,
            Rssi = client.Rssi,
            Satisfaction = client.Satisfaction,
            WifiProtocol = client.RadioProto,
            WifiGeneration = ParseWifiGeneration(client.RadioProto),
            TxRate = client.TxRate,
            RxRate = client.RxRate,
            TxBytes = client.TxBytes,
            RxBytes = client.RxBytes,
            Uptime = client.Uptime,
            IsAuthorized = !client.Blocked,
            IsGuest = client.IsGuest,
            Manufacturer = client.Oui,
            Timestamp = timestamp
        };
    }

    private static int? ParseWifiGeneration(string? radioProto)
    {
        if (string.IsNullOrEmpty(radioProto)) return null;

        var proto = radioProto.ToLowerInvariant();
        return proto switch
        {
            "be" => 7,                    // Wi-Fi 7 (802.11be)
            "ax" => 6,                    // Wi-Fi 6/6E (802.11ax)
            "ac" => 5,                    // Wi-Fi 5 (802.11ac)
            "n" or "ng" or "na" => 4,     // Wi-Fi 4 (802.11n)
            "a" => 2,                     // 802.11a
            "g" => 3,                     // 802.11g
            "b" => 1,                     // 802.11b
            _ => null
        };
    }

    private List<SiteWiFiMetrics> ParseSiteMetrics(JsonElement data)
    {
        var metrics = new List<SiteWiFiMetrics>();

        if (data.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Site metrics data is not an array: {ValueKind}", data.ValueKind);
            return metrics;
        }

        // Log first item properties for debugging
        if (data.GetArrayLength() > 0)
        {
            var first = data[0];
            var props = first.EnumerateObject().Select(p => p.Name).ToList();
            _logger.LogDebug("First site metrics item properties (all {Count}): {Properties}", props.Count, string.Join(", ", props));

            // Log the actual value of "o" if present
            if (first.TryGetProperty("o", out var oVal))
            {
                _logger.LogDebug("Value of 'o' field: {Value} (type: {Type})", oVal.ToString(), oVal.ValueKind);
            }
        }

        foreach (var item in data.EnumerateArray())
        {
            // The "time" field contains the Unix timestamp in milliseconds
            if (!item.TryGetProperty("time", out var timeProp))
            {
                _logger.LogWarning("Site metrics item missing 'time' field");
                continue;
            }

            long timestamp;
            if (timeProp.ValueKind == JsonValueKind.Number)
            {
                timestamp = timeProp.GetInt64();
            }
            else if (timeProp.ValueKind == JsonValueKind.String && long.TryParse(timeProp.GetString(), out var parsed))
            {
                timestamp = parsed;
            }
            else
            {
                _logger.LogWarning("Site metrics item has invalid 'time' field type: {Type}", timeProp.ValueKind);
                continue;
            }

            var metric = new SiteWiFiMetrics
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
                ByBand = new Dictionary<RadioBand, BandMetrics>()
            };

            // Parse 2.4 GHz metrics
            metric.ByBand[RadioBand.Band2_4GHz] = new BandMetrics
            {
                Band = RadioBand.Band2_4GHz,
                ChannelUtilization = GetDoubleOrNull(item, "ap-ng-cu_total"),
                Interference = GetDoubleOrNull(item, "ap-ng-cu_interf"),
                TxRetries = GetLongOrNull(item, "ap-ng-tx_retries"),
                WifiTxAttempts = GetLongOrNull(item, "ap-ng-wifi_tx_attempts"),
                WifiTxDropped = GetLongOrNull(item, "ap-ng-wifi_tx_dropped"),
                TxPackets = GetLongOrNull(item, "ap-ng-tx_packets"),
                RxPackets = GetLongOrNull(item, "ap-ng-rx_packets")
            };

            // Parse 5 GHz metrics
            metric.ByBand[RadioBand.Band5GHz] = new BandMetrics
            {
                Band = RadioBand.Band5GHz,
                ChannelUtilization = GetDoubleOrNull(item, "ap-na-cu_total"),
                Interference = GetDoubleOrNull(item, "ap-na-cu_interf"),
                TxRetries = GetLongOrNull(item, "ap-na-tx_retries"),
                WifiTxAttempts = GetLongOrNull(item, "ap-na-wifi_tx_attempts"),
                WifiTxDropped = GetLongOrNull(item, "ap-na-wifi_tx_dropped"),
                TxPackets = GetLongOrNull(item, "ap-na-tx_packets"),
                RxPackets = GetLongOrNull(item, "ap-na-rx_packets")
            };

            // Parse 6 GHz metrics
            metric.ByBand[RadioBand.Band6GHz] = new BandMetrics
            {
                Band = RadioBand.Band6GHz,
                ChannelUtilization = GetDoubleOrNull(item, "ap-6e-cu_total"),
                Interference = GetDoubleOrNull(item, "ap-6e-cu_interf"),
                TxRetries = GetLongOrNull(item, "ap-6e-tx_retries"),
                WifiTxAttempts = GetLongOrNull(item, "ap-6e-wifi_tx_attempts"),
                WifiTxDropped = GetLongOrNull(item, "ap-6e-wifi_tx_dropped"),
                TxPackets = GetLongOrNull(item, "ap-6e-tx_packets"),
                RxPackets = GetLongOrNull(item, "ap-6e-rx_packets")
            };

            // Calculate TX retry percentages
            foreach (var band in metric.ByBand.Values)
            {
                if (band.WifiTxAttempts > 0 && band.TxRetries.HasValue)
                {
                    band.TxRetryPct = (double)band.TxRetries.Value / band.WifiTxAttempts.Value * 100;
                }
            }

            metrics.Add(metric);
        }

        return metrics;
    }

    private List<SiteWiFiMetrics> ParseApMetrics(JsonElement data)
    {
        var metrics = new List<SiteWiFiMetrics>();

        if (data.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("AP metrics data is not an array: {ValueKind}", data.ValueKind);
            return metrics;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("time", out var timeProp))
            {
                continue;
            }

            long timestamp;
            if (timeProp.ValueKind == JsonValueKind.Number)
            {
                timestamp = timeProp.GetInt64();
            }
            else if (timeProp.ValueKind == JsonValueKind.String && long.TryParse(timeProp.GetString(), out var parsed))
            {
                timestamp = parsed;
            }
            else
            {
                continue;
            }

            var metric = new SiteWiFiMetrics
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
                ByBand = new Dictionary<RadioBand, BandMetrics>()
            };

            // AP endpoint uses ng-* prefix (no 'ap-' prefix)
            metric.ByBand[RadioBand.Band2_4GHz] = new BandMetrics
            {
                Band = RadioBand.Band2_4GHz,
                ChannelUtilization = GetDoubleOrNull(item, "ng-cu_total"),
                Interference = GetDoubleOrNull(item, "ng-cu_interf"),
                TxRetries = GetLongOrNull(item, "ng-tx_retries"),
                WifiTxAttempts = GetLongOrNull(item, "ng-wifi_tx_attempts"),
                WifiTxDropped = GetLongOrNull(item, "ng-wifi_tx_dropped"),
                TxPackets = GetLongOrNull(item, "ng-tx_packets"),
                RxPackets = GetLongOrNull(item, "ng-rx_packets")
            };

            metric.ByBand[RadioBand.Band5GHz] = new BandMetrics
            {
                Band = RadioBand.Band5GHz,
                ChannelUtilization = GetDoubleOrNull(item, "na-cu_total"),
                Interference = GetDoubleOrNull(item, "na-cu_interf"),
                TxRetries = GetLongOrNull(item, "na-tx_retries"),
                WifiTxAttempts = GetLongOrNull(item, "na-wifi_tx_attempts"),
                WifiTxDropped = GetLongOrNull(item, "na-wifi_tx_dropped"),
                TxPackets = GetLongOrNull(item, "na-tx_packets"),
                RxPackets = GetLongOrNull(item, "na-rx_packets")
            };

            metric.ByBand[RadioBand.Band6GHz] = new BandMetrics
            {
                Band = RadioBand.Band6GHz,
                ChannelUtilization = GetDoubleOrNull(item, "6e-cu_total"),
                Interference = GetDoubleOrNull(item, "6e-cu_interf"),
                TxRetries = GetLongOrNull(item, "6e-tx_retries"),
                WifiTxAttempts = GetLongOrNull(item, "6e-wifi_tx_attempts"),
                WifiTxDropped = GetLongOrNull(item, "6e-wifi_tx_dropped"),
                TxPackets = GetLongOrNull(item, "6e-tx_packets"),
                RxPackets = GetLongOrNull(item, "6e-rx_packets")
            };

            // Calculate TX retry percentages
            foreach (var band in metric.ByBand.Values)
            {
                if (band.WifiTxAttempts > 0 && band.TxRetries.HasValue)
                {
                    band.TxRetryPct = (double)band.TxRetries.Value / band.WifiTxAttempts.Value * 100;
                }
            }

            metrics.Add(metric);
        }

        return metrics;
    }

    private List<ClientWiFiMetrics> ParseClientMetrics(JsonElement data, string clientMac)
    {
        var metrics = new List<ClientWiFiMetrics>();

        if (data.ValueKind != JsonValueKind.Array) return metrics;

        // Log first item for debugging
        if (data.GetArrayLength() > 0)
        {
            var first = data[0];
            var props = first.EnumerateObject().Select(p => $"{p.Name}:{p.Value.ValueKind}").ToList();
            _logger.LogDebug("First client metrics item properties: {Properties}", string.Join(", ", props));
        }

        foreach (var item in data.EnumerateArray())
        {
            // Parse timestamp (required)
            if (!item.TryGetProperty("time", out var timeProp))
            {
                continue;
            }

            long timestamp;
            if (timeProp.ValueKind == JsonValueKind.Number)
            {
                // Use GetDouble and cast to handle both integer and decimal values
                timestamp = (long)timeProp.GetDouble();
            }
            else if (timeProp.ValueKind == JsonValueKind.String && long.TryParse(timeProp.GetString(), out var parsed))
            {
                timestamp = parsed;
            }
            else
            {
                _logger.LogDebug("Skipping client metric item with invalid time type: {Type}", timeProp.ValueKind);
                continue;
            }

            var metric = new ClientWiFiMetrics
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
                ClientMac = clientMac,
                Signal = GetIntOrNull(item, "signal"),
                TxRetries = GetLongOrNull(item, "tx_retries"),
                TxPackets = GetLongOrNull(item, "tx_packets"),
                RxPackets = GetLongOrNull(item, "rx_packets"),
                WifiTxAttempts = GetLongOrNull(item, "wifi_tx_attempts"),
                WifiTxDropped = GetLongOrNull(item, "wifi_tx_dropped")
            };

            if (metric.WifiTxAttempts > 0 && metric.TxRetries.HasValue)
            {
                metric.TxRetryPct = (double)metric.TxRetries.Value / metric.WifiTxAttempts.Value * 100;
            }

            metrics.Add(metric);
        }

        return metrics;
    }

    private RoamingTopology? ParseRoamingTopology(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Undefined) return null;

        // Log top-level properties in response
        var topLevelProps = string.Join(", ", data.EnumerateObject().Select(p => p.Name));
        _logger.LogDebug("Roaming topology response properties: {Props}", topLevelProps);

        var topology = new RoamingTopology();

        // Parse clients
        if (data.TryGetProperty("clients", out var clients))
        {
            foreach (var client in clients.EnumerateArray())
            {
                topology.Clients.Add(new RoamingClient
                {
                    Mac = client.GetProperty("mac").GetString() ?? "",
                    Name = client.TryGetProperty("name", out var name) ? name.GetString() : null
                });
            }
        }

        // Parse edges (AP pairs)
        if (data.TryGetProperty("edges", out var edges))
        {
            _logger.LogDebug("Parsing {EdgeCount} edges from roaming topology", edges.GetArrayLength());
            foreach (var edge in edges.EnumerateArray())
            {
                // Log edge properties
                var edgeProps = string.Join(", ", edge.EnumerateObject().Select(p => p.Name));
                _logger.LogDebug("Edge properties: {Props}", edgeProps);
                var roamingEdge = new RoamingEdge
                {
                    Endpoint1Mac = edge.GetProperty("endpoint_1_mac").GetString() ?? "",
                    Endpoint2Mac = edge.GetProperty("endpoint_2_mac").GetString() ?? "",
                    TotalRoamAttempts = edge.TryGetProperty("total_roam_attempts", out var tra) ? tra.GetInt32() : 0,
                    TotalSuccessfulRoams = edge.TryGetProperty("total_successful_roams", out var tsr) ? tsr.GetInt32() : 0
                };

                if (edge.TryGetProperty("endpoint_1_to_endpoint_2", out var e1to2))
                {
                    roamingEdge.Endpoint1ToEndpoint2 = ParseDirectionStats(e1to2);
                }

                if (edge.TryGetProperty("endpoint_2_to_endpoint_1", out var e2to1))
                {
                    roamingEdge.Endpoint2ToEndpoint1 = ParseDirectionStats(e2to1);
                }

                if (edge.TryGetProperty("top_roaming_clients", out var topClients))
                {
                    foreach (var tc in topClients.EnumerateArray())
                    {
                        roamingEdge.TopRoamingClients.Add(new ClientRoamingStats
                        {
                            Mac = tc.GetProperty("mac").GetString() ?? "",
                            RoamAttempts = tc.TryGetProperty("roam_attempts", out var ra) ? ra.GetInt32() : 0,
                            SuccessfulRoams = tc.TryGetProperty("successful_roams", out var sr) ? sr.GetInt32() : 0
                        });
                    }
                }

                topology.Edges.Add(roamingEdge);
            }
        }

        // Parse vertices (APs)
        if (data.TryGetProperty("vertices", out var vertices))
        {
            foreach (var vertex in vertices.EnumerateArray())
            {
                var v = new RoamingVertex
                {
                    Mac = vertex.GetProperty("mac").GetString() ?? "",
                    Model = vertex.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
                    Name = vertex.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""
                };

                if (vertex.TryGetProperty("radios", out var radios))
                {
                    foreach (var radio in radios.EnumerateArray())
                    {
                        v.Radios.Add(new RoamingRadioInfo
                        {
                            Channel = radio.TryGetProperty("channel", out var ch) ? ch.GetInt32() : 0,
                            RadioBand = radio.TryGetProperty("radio_band", out var rb) ? rb.GetString() ?? "" : "",
                            UtilizationPercentage = radio.TryGetProperty("utilization_percentage", out var up) ? up.GetInt32() : 0
                        });
                    }
                }

                topology.Vertices.Add(v);
            }
        }

        return topology;
    }

    private RoamingDirectionStats ParseDirectionStats(JsonElement el)
    {
        // Debug: log all properties in the direction stats
        var props = string.Join(", ", el.EnumerateObject().Select(p => $"{p.Name}={p.Value}"));
        _logger.LogDebug("Direction stats properties: {Props}", props);

        return new RoamingDirectionStats
        {
            RoamAttempts = el.TryGetProperty("roam_attempts", out var ra) ? ra.GetInt32() : 0,
            SuccessfulRoams = el.TryGetProperty("successful_roams", out var sr) ? sr.GetInt32() : 0,
            FastRoaming = el.TryGetProperty("fast_roaming", out var fr) ? fr.GetInt32() : 0,
            TriggeredByMinimalRssi = el.TryGetProperty("triggered_by_minimal_rssi", out var mr) ? mr.GetInt32() : 0,
            TriggeredByRoamingAssistant = el.TryGetProperty("triggered_by_roaming_assistant", out var rass) ? rass.GetInt32() : 0
        };
    }

    private static double? GetDoubleOrNull(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetDouble();
        return null;
    }

    private static int? GetIntOrNull(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
        {
            // Use GetDouble and cast to handle potential decimal values
            return (int)val.GetDouble();
        }
        return null;
    }

    private static long? GetLongOrNull(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
        {
            // API may return floats, so convert to long via double
            return (long)val.GetDouble();
        }
        return null;
    }

    #endregion
}
