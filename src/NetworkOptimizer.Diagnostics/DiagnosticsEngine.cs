using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Diagnostics.Analyzers;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics;

/// <summary>
/// Options for running diagnostics - allows enabling/disabling specific analyzers.
/// </summary>
public class DiagnosticsOptions
{
    /// <summary>
    /// Run the AP Lock analyzer (mobile devices locked to APs)
    /// </summary>
    public bool RunApLockAnalyzer { get; set; } = true;

    /// <summary>
    /// Run the Trunk Consistency analyzer (VLAN mismatches on trunk links)
    /// </summary>
    public bool RunTrunkConsistencyAnalyzer { get; set; } = true;

    /// <summary>
    /// Run the Port Profile Suggestion analyzer
    /// </summary>
    public bool RunPortProfileSuggestionAnalyzer { get; set; } = true;

    /// <summary>
    /// Run the Port Profile 802.1X analyzer
    /// </summary>
    public bool RunPortProfile8021xAnalyzer { get; set; } = true;

    /// <summary>
    /// Run the Performance analyzer (hardware accel, jumbo frames, flow control)
    /// </summary>
    public bool RunPerformanceAnalyzer { get; set; } = true;

    /// <summary>
    /// Run the Cellular Data Savings analyzer (QoS rules for 5G/LTE WANs)
    /// </summary>
    public bool RunCellularDataSavings { get; set; } = true;
}

/// <summary>
/// Main orchestrator for running all diagnostic analyzers.
/// </summary>
public class DiagnosticsEngine
{
    private readonly ApLockAnalyzer _apLockAnalyzer;
    private readonly TrunkConsistencyAnalyzer _trunkConsistencyAnalyzer;
    private readonly PortProfileSuggestionAnalyzer _portProfileSuggestionAnalyzer;
    private readonly PortProfile8021xAnalyzer _portProfile8021xAnalyzer;
    private readonly PerformanceAnalyzer _performanceAnalyzer;
    private readonly ILogger<DiagnosticsEngine>? _logger;

    public DiagnosticsEngine(
        DeviceTypeDetectionService deviceTypeDetection,
        ILogger<DiagnosticsEngine>? logger = null,
        ILogger<ApLockAnalyzer>? apLockLogger = null,
        ILogger<TrunkConsistencyAnalyzer>? trunkConsistencyLogger = null,
        ILogger<PortProfileSuggestionAnalyzer>? portProfileSuggestionLogger = null,
        ILogger<PortProfile8021xAnalyzer>? portProfile8021xLogger = null,
        ILogger<PerformanceAnalyzer>? performanceLogger = null)
    {
        _apLockAnalyzer = new ApLockAnalyzer(deviceTypeDetection, apLockLogger);
        _trunkConsistencyAnalyzer = new TrunkConsistencyAnalyzer(trunkConsistencyLogger);
        _portProfileSuggestionAnalyzer = new PortProfileSuggestionAnalyzer(portProfileSuggestionLogger);
        _portProfile8021xAnalyzer = new PortProfile8021xAnalyzer(portProfile8021xLogger);
        _performanceAnalyzer = new PerformanceAnalyzer(deviceTypeDetection, performanceLogger);
        _logger = logger;
    }

    /// <summary>
    /// Run all enabled diagnostic analyzers.
    /// </summary>
    /// <param name="clients">All network clients (online)</param>
    /// <param name="devices">All network devices</param>
    /// <param name="portProfiles">All port profiles</param>
    /// <param name="networks">All network configurations</param>
    /// <param name="options">Options to control which analyzers run</param>
    /// <param name="clientHistory">Optional historical clients for offline device detection</param>
    /// <param name="settingsData">Raw settings JSON for global switch settings</param>
    /// <param name="qosRulesData">Raw QoS rules JSON for cellular bandwidth checks</param>
    /// <returns>Complete diagnostics result</returns>
    public DiagnosticsResult RunDiagnostics(
        IEnumerable<UniFiClientResponse> clients,
        IEnumerable<UniFiDeviceResponse> devices,
        IEnumerable<UniFiPortProfile> portProfiles,
        IEnumerable<UniFiNetworkConfig> networks,
        DiagnosticsOptions? options = null,
        IEnumerable<UniFiClientDetailResponse>? clientHistory = null,
        JsonDocument? settingsData = null,
        JsonDocument? qosRulesData = null,
        JsonDocument? wanEnrichedData = null)
    {
        options ??= new DiagnosticsOptions();
        var stopwatch = Stopwatch.StartNew();

        _logger?.LogInformation("Starting diagnostics run");

        var result = new DiagnosticsResult();
        var clientList = clients.ToList();
        var deviceList = devices.ToList();
        var profileList = portProfiles.ToList();
        var networkList = networks.ToList();
        var historyList = clientHistory?.ToList() ?? new List<UniFiClientDetailResponse>();

        // Run AP Lock Analyzer
        if (options.RunApLockAnalyzer)
        {
            _logger?.LogDebug("Running AP Lock Analyzer");
            try
            {
                // Analyze online clients
                result.ApLockIssues = _apLockAnalyzer.Analyze(clientList, deviceList);
                _logger?.LogDebug("AP Lock Analyzer found {Count} online issues", result.ApLockIssues.Count);

                // Analyze offline clients from history
                if (historyList.Count > 0)
                {
                    var onlineMacs = clientList.Select(c => c.Mac.ToLowerInvariant()).ToHashSet();
                    var offlineIssues = _apLockAnalyzer.AnalyzeOfflineClients(historyList, deviceList, onlineMacs);
                    result.ApLockIssues.AddRange(offlineIssues);
                    _logger?.LogDebug("AP Lock Analyzer found {Count} offline issues", offlineIssues.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "AP Lock Analyzer failed");
            }
        }

        // Run Trunk Consistency Analyzer
        if (options.RunTrunkConsistencyAnalyzer)
        {
            _logger?.LogDebug("Running Trunk Consistency Analyzer");
            try
            {
                result.TrunkConsistencyIssues = _trunkConsistencyAnalyzer.Analyze(
                    deviceList, profileList, networkList);
                _logger?.LogDebug("Trunk Consistency Analyzer found {Count} issues", result.TrunkConsistencyIssues.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Trunk Consistency Analyzer failed");
            }
        }

        // Run Port Profile Suggestion Analyzer
        if (options.RunPortProfileSuggestionAnalyzer)
        {
            _logger?.LogDebug("Running Port Profile Suggestion Analyzer");
            try
            {
                result.PortProfileSuggestions = _portProfileSuggestionAnalyzer.Analyze(
                    deviceList, profileList, networkList);
                _logger?.LogDebug("Port Profile Suggestion Analyzer found {Count} suggestions", result.PortProfileSuggestions.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Port Profile Suggestion Analyzer failed");
            }
        }

        // Run Port Profile 802.1X Analyzer
        if (options.RunPortProfile8021xAnalyzer)
        {
            _logger?.LogDebug("Running Port Profile 802.1X Analyzer");
            try
            {
                result.PortProfile8021xIssues = _portProfile8021xAnalyzer.Analyze(profileList, networkList);
                _logger?.LogDebug("Port Profile 802.1X Analyzer found {Count} issues", result.PortProfile8021xIssues.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Port Profile 802.1X Analyzer failed");
            }
        }

        // Run Performance Analyzer
        if (options.RunPerformanceAnalyzer || options.RunCellularDataSavings)
        {
            _logger?.LogDebug("Running Performance Analyzer");
            try
            {
                result.PerformanceIssues = _performanceAnalyzer.Analyze(
                    deviceList, networkList, clientList, settingsData, qosRulesData, wanEnrichedData,
                    runPerformanceChecks: options.RunPerformanceAnalyzer,
                    runCellularChecks: options.RunCellularDataSavings);
                _logger?.LogDebug("Performance Analyzer found {Count} issues", result.PerformanceIssues.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Performance Analyzer failed");
            }
        }

        stopwatch.Stop();
        result.Duration = stopwatch.Elapsed;
        result.Timestamp = DateTime.UtcNow;

        _logger?.LogInformation(
            "Diagnostics completed in {Duration}ms - {Total} issues found " +
            "(AP Lock: {ApLock}, Trunk: {Trunk}, Profile: {Profile}, 802.1X: {Dot1x}, Performance: {Perf})",
            stopwatch.ElapsedMilliseconds,
            result.TotalIssueCount,
            result.ApLockIssues.Count,
            result.TrunkConsistencyIssues.Count,
            result.PortProfileSuggestions.Count,
            result.PortProfile8021xIssues.Count,
            result.PerformanceIssues.Count);

        return result;
    }
}
