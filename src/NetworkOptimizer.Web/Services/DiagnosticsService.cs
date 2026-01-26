using Microsoft.Extensions.Caching.Memory;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Diagnostics;
using NetworkOptimizer.Diagnostics.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for running network diagnostics and caching results.
/// </summary>
public class DiagnosticsService
{
    private const string CacheKeyLastResult = "DiagnosticsService_LastResult";
    private const string CacheKeyLastRunTime = "DiagnosticsService_LastRunTime";
    private const string CacheKeyIsRunning = "DiagnosticsService_IsRunning";

    private readonly ILogger<DiagnosticsService> _logger;
    private readonly UniFiConnectionService _connectionService;
    private readonly FingerprintDatabaseService _fingerprintService;
    private readonly IeeeOuiDatabase _ieeeOuiDb;
    private readonly IMemoryCache _cache;
    private readonly ILoggerFactory _loggerFactory;

    public DiagnosticsService(
        ILogger<DiagnosticsService> logger,
        UniFiConnectionService connectionService,
        FingerprintDatabaseService fingerprintService,
        IeeeOuiDatabase ieeeOuiDb,
        IMemoryCache cache,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _connectionService = connectionService;
        _fingerprintService = fingerprintService;
        _ieeeOuiDb = ieeeOuiDb;
        _cache = cache;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Get the last diagnostics result (if any).
    /// </summary>
    public DiagnosticsResult? LastResult => _cache.Get<DiagnosticsResult>(CacheKeyLastResult);

    /// <summary>
    /// Get the time of the last diagnostics run.
    /// </summary>
    public DateTime? LastRunTime => _cache.Get<DateTime?>(CacheKeyLastRunTime);

    /// <summary>
    /// Check if diagnostics are currently running.
    /// </summary>
    public bool IsRunning => _cache.Get<bool>(CacheKeyIsRunning);

    /// <summary>
    /// Clear cached diagnostics results.
    /// </summary>
    public void ClearCache()
    {
        _cache.Remove(CacheKeyLastResult);
        _cache.Remove(CacheKeyLastRunTime);
        _logger.LogInformation("Diagnostics cache cleared");
    }

    /// <summary>
    /// Run network diagnostics.
    /// </summary>
    /// <param name="options">Options to control which analyzers run</param>
    /// <returns>Diagnostics result</returns>
    public async Task<DiagnosticsResult> RunDiagnosticsAsync(DiagnosticsOptions? options = null)
    {
        if (IsRunning)
        {
            _logger.LogWarning("Diagnostics already running, returning last result");
            return LastResult ?? new DiagnosticsResult();
        }

        _cache.Set(CacheKeyIsRunning, true);

        try
        {
            _logger.LogInformation("Starting diagnostics run");

            if (!_connectionService.IsConnected || _connectionService.Client == null)
            {
                _logger.LogWarning("Cannot run diagnostics: UniFi controller not connected");
                return CreateErrorResult("Controller Not Connected",
                    "Cannot run diagnostics without an active connection to the UniFi controller.");
            }

            // Fetch all required data in parallel
            var devicesTask = _connectionService.Client.GetDevicesAsync();
            var clientsTask = _connectionService.Client.GetClientsAsync();
            var networksTask = _connectionService.Client.GetNetworkConfigsAsync();
            var portProfilesTask = _connectionService.Client.GetPortProfilesAsync();
            var clientHistoryTask = _connectionService.Client.GetClientHistoryAsync(withinHours: 720); // 30 days

            await Task.WhenAll(devicesTask, clientsTask, networksTask, portProfilesTask, clientHistoryTask);

            var devices = await devicesTask;
            var clients = await clientsTask;
            var networks = await networksTask;
            var portProfiles = await portProfilesTask;
            var clientHistory = await clientHistoryTask;

            _logger.LogInformation(
                "Fetched data for diagnostics: {DeviceCount} devices, {ClientCount} clients, " +
                "{NetworkCount} networks, {ProfileCount} port profiles, {HistoryCount} history clients",
                devices.Count, clients.Count, networks.Count, portProfiles.Count, clientHistory.Count);

            // Get fingerprint database for device detection
            var fingerprintDb = await _fingerprintService.GetDatabaseAsync();

            // Create device detection service with all available data sources
            var deviceDetection = new DeviceTypeDetectionService(
                _loggerFactory.CreateLogger<DeviceTypeDetectionService>(),
                fingerprintDb,
                _ieeeOuiDb,
                _loggerFactory);

            // Create and run the diagnostics engine
            var engine = new DiagnosticsEngine(
                deviceDetection,
                _loggerFactory.CreateLogger<DiagnosticsEngine>(),
                _loggerFactory.CreateLogger<Diagnostics.Analyzers.ApLockAnalyzer>(),
                _loggerFactory.CreateLogger<Diagnostics.Analyzers.TrunkConsistencyAnalyzer>(),
                _loggerFactory.CreateLogger<Diagnostics.Analyzers.PortProfileSuggestionAnalyzer>());

            var result = engine.RunDiagnostics(clients, devices, portProfiles, networks, options, clientHistory);

            // Cache the result
            _cache.Set(CacheKeyLastResult, result);
            _cache.Set(CacheKeyLastRunTime, DateTime.UtcNow);

            _logger.LogInformation(
                "Diagnostics completed: {Total} issues found in {Duration}ms",
                result.TotalIssueCount, result.Duration.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostics run failed");
            return CreateErrorResult("Diagnostics Failed", ex.Message);
        }
        finally
        {
            _cache.Set(CacheKeyIsRunning, false);
        }
    }

    private static DiagnosticsResult CreateErrorResult(string title, string message)
    {
        return new DiagnosticsResult
        {
            Timestamp = DateTime.UtcNow,
            // Add a synthetic issue to show the error
            ApLockIssues = new List<ApLockIssue>
            {
                new ApLockIssue
                {
                    ClientName = title,
                    Recommendation = message,
                    Severity = ApLockSeverity.Unknown
                }
            }
        };
    }
}
