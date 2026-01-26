using System.Diagnostics;
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
}

/// <summary>
/// Main orchestrator for running all diagnostic analyzers.
/// </summary>
public class DiagnosticsEngine
{
    private readonly ApLockAnalyzer _apLockAnalyzer;
    private readonly TrunkConsistencyAnalyzer _trunkConsistencyAnalyzer;
    private readonly PortProfileSuggestionAnalyzer _portProfileSuggestionAnalyzer;
    private readonly ILogger<DiagnosticsEngine>? _logger;

    public DiagnosticsEngine(
        DeviceTypeDetectionService deviceTypeDetection,
        ILogger<DiagnosticsEngine>? logger = null,
        ILogger<ApLockAnalyzer>? apLockLogger = null,
        ILogger<TrunkConsistencyAnalyzer>? trunkConsistencyLogger = null,
        ILogger<PortProfileSuggestionAnalyzer>? portProfileSuggestionLogger = null)
    {
        _apLockAnalyzer = new ApLockAnalyzer(deviceTypeDetection, apLockLogger);
        _trunkConsistencyAnalyzer = new TrunkConsistencyAnalyzer(trunkConsistencyLogger);
        _portProfileSuggestionAnalyzer = new PortProfileSuggestionAnalyzer(portProfileSuggestionLogger);
        _logger = logger;
    }

    /// <summary>
    /// Run all enabled diagnostic analyzers.
    /// </summary>
    /// <param name="clients">All network clients</param>
    /// <param name="devices">All network devices</param>
    /// <param name="portProfiles">All port profiles</param>
    /// <param name="networks">All network configurations</param>
    /// <param name="options">Options to control which analyzers run</param>
    /// <returns>Complete diagnostics result</returns>
    public DiagnosticsResult RunDiagnostics(
        IEnumerable<UniFiClientResponse> clients,
        IEnumerable<UniFiDeviceResponse> devices,
        IEnumerable<UniFiPortProfile> portProfiles,
        IEnumerable<UniFiNetworkConfig> networks,
        DiagnosticsOptions? options = null)
    {
        options ??= new DiagnosticsOptions();
        var stopwatch = Stopwatch.StartNew();

        _logger?.LogInformation("Starting diagnostics run");

        var result = new DiagnosticsResult();
        var clientList = clients.ToList();
        var deviceList = devices.ToList();
        var profileList = portProfiles.ToList();
        var networkList = networks.ToList();

        // Run AP Lock Analyzer
        if (options.RunApLockAnalyzer)
        {
            _logger?.LogDebug("Running AP Lock Analyzer");
            try
            {
                result.ApLockIssues = _apLockAnalyzer.Analyze(clientList, deviceList);
                _logger?.LogDebug("AP Lock Analyzer found {Count} issues", result.ApLockIssues.Count);
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

        stopwatch.Stop();
        result.Duration = stopwatch.Elapsed;
        result.Timestamp = DateTime.UtcNow;

        _logger?.LogInformation(
            "Diagnostics completed in {Duration}ms - {Total} issues found " +
            "(AP Lock: {ApLock}, Trunk: {Trunk}, Profile: {Profile})",
            stopwatch.ElapsedMilliseconds,
            result.TotalIssueCount,
            result.ApLockIssues.Count,
            result.TrunkConsistencyIssues.Count,
            result.PortProfileSuggestions.Count);

        return result;
    }
}
