using System.Net;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Common metadata exposed during a WAN speed test for UI display.
/// </summary>
public record WanTestMetadata(string ServerInfo, string Location, string? WanIp);

/// <summary>
/// Base class for server-side WAN speed test services (Cloudflare, UWN).
/// Provides thread-safe state management, result CRUD, and background path analysis.
/// </summary>
public abstract class WanSpeedTestServiceBase
{
    protected readonly IDbContextFactory<NetworkOptimizerDbContext> DbFactory;
    protected readonly INetworkPathAnalyzer PathAnalyzer;
    protected readonly ILogger Logger;
    protected readonly Iperf3ServerService Iperf3Server;

    // Observable test state (polled by UI components)
    private readonly object _lock = new();
    private bool _isRunning;
    private string _currentPhase = "";
    private int _currentPercent;
    private string? _currentStatus;
    private Iperf3Result? _lastCompletedResult;
    private WanTestMetadata? _lastMetadata;

    /// <summary>The SpeedTestDirection for results created by this service.</summary>
    protected abstract SpeedTestDirection Direction { get; }

    /// <summary>All directions this service owns (for querying historical results).</summary>
    protected virtual SpeedTestDirection[] OwnedDirections => [Direction];

    /// <summary>Whether a WAN speed test is currently running.</summary>
    public bool IsRunning { get { lock (_lock) return _isRunning; } }

    /// <summary>Current test progress snapshot for UI polling.</summary>
    public (string Phase, int Percent, string? Status) CurrentProgress
    {
        get { lock (_lock) return (_currentPhase, _currentPercent, _currentStatus); }
    }

    /// <summary>Last completed result from the current session.</summary>
    public Iperf3Result? LastCompletedResult
    {
        get { lock (_lock) return _lastCompletedResult; }
    }

    /// <summary>Metadata from the most recent test (set early in the test lifecycle).</summary>
    public WanTestMetadata? LastMetadata
    {
        get { lock (_lock) return _lastMetadata; }
    }

    /// <summary>
    /// Fired when background path analysis completes for a result.
    /// UI components subscribe to refresh their display.
    /// </summary>
    public event Action<int>? OnPathAnalysisComplete;

    protected WanSpeedTestServiceBase(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        INetworkPathAnalyzer pathAnalyzer,
        ILogger logger,
        Iperf3ServerService iperf3Server)
    {
        DbFactory = dbFactory;
        PathAnalyzer = pathAnalyzer;
        Logger = logger;
        Iperf3Server = iperf3Server;
    }

    /// <summary>
    /// Run a WAN speed test with progress reporting.
    /// Pauses the iperf3 server during the test to free pipe handles.
    /// </summary>
    public async Task<Iperf3Result?> RunTestAsync(
        Action<(string Phase, int Percent, string? Status)>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                Logger.LogWarning("WAN speed test already in progress");
                return null;
            }
            _isRunning = true;
            _lastCompletedResult = null;
        }

        try
        {
            // Pause iperf3 server during WAN speed test to free pipe handles.
            if (Iperf3Server.IsRunning)
            {
                Logger.LogInformation("Pausing iperf3 server for WAN speed test");
                await Iperf3Server.PauseAsync();
            }

            var result = await RunTestCoreAsync(
                (phase, percent, status) =>
                {
                    lock (_lock) { _currentPhase = phase; _currentPercent = percent; _currentStatus = status; }
                    onProgress?.Invoke((phase, percent, status));
                },
                cancellationToken);

            if (result == null) return null;

            // Save to DB
            await using var db = await DbFactory.CreateDbContextAsync(cancellationToken);
            db.Iperf3Results.Add(result);
            await db.SaveChangesAsync(cancellationToken);
            var resultId = result.Id;

            lock (_lock) _lastCompletedResult = result;

            // Trigger background path analysis
            var wanIp = LastMetadata?.WanIp;
            var resolvedWanGroup = result.WanNetworkGroup;
            _ = Task.Run(async () => await AnalyzePathInBackgroundAsync(resultId, wanIp, resolvedWanGroup), CancellationToken.None);

            return result;
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("WAN speed test cancelled");
            lock (_lock) { _currentPhase = "Cancelled"; _currentPercent = 0; _currentStatus = "Test cancelled"; }
            onProgress?.Invoke(("Cancelled", 0, "Test cancelled"));
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "WAN speed test failed");
            lock (_lock) { _currentPhase = "Error"; _currentPercent = 0; _currentStatus = ex.Message; }
            onProgress?.Invoke(("Error", 0, ex.Message));

            // Save failed result
            try
            {
                var failedResult = CreateFailedResult(ex.Message);
                await using var db = await DbFactory.CreateDbContextAsync();
                db.Iperf3Results.Add(failedResult);
                await db.SaveChangesAsync();
                return failedResult;
            }
            catch (Exception saveEx)
            {
                Logger.LogWarning(saveEx, "Failed to save error result");
                return null;
            }
        }
        finally
        {
            await Iperf3Server.ResumeAsync();
            lock (_lock) _isRunning = false;
        }
    }

    /// <summary>
    /// Subclass implements the actual test phases (metadata, latency, throughput).
    /// Returns a fully populated Iperf3Result or null on failure.
    /// </summary>
    protected abstract Task<Iperf3Result?> RunTestCoreAsync(
        Action<string, int, string?> report,
        CancellationToken cancellationToken);

    /// <summary>Create a failed result with appropriate direction and device info.</summary>
    protected abstract Iperf3Result CreateFailedResult(string errorMessage);

    /// <summary>Set metadata visible to UI during the test.</summary>
    protected void SetMetadata(WanTestMetadata metadata)
    {
        lock (_lock) _lastMetadata = metadata;
    }

    /// <summary>Get recent WAN speed test results for this service's directions.</summary>
    public async Task<List<Iperf3Result>> GetResultsAsync(int count = 50, int hours = 0)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var directions = OwnedDirections;
        var query = db.Iperf3Results
            .Where(r => directions.Contains(r.Direction));

        if (hours > 0)
        {
            var cutoff = DateTime.UtcNow.AddHours(-hours);
            query = query.Where(r => r.TestTime >= cutoff);
        }

        query = query.OrderByDescending(r => r.TestTime);

        if (count > 0)
            query = query.Take(count);

        var results = await query.ToListAsync();

        // Fire-and-forget path analysis retries for recent results without valid paths.
        var retryWindow = DateTime.UtcNow.AddMinutes(-30);
        var recentCutoff = DateTime.UtcNow.AddSeconds(-10);
        var needsRetry = results.Where(r =>
            r.TestTime > retryWindow &&
            r.TestTime < recentCutoff &&
            r.Success &&
            (r.PathAnalysis == null ||
             r.PathAnalysis.Path == null ||
             !r.PathAnalysis.Path.IsValid))
            .Select(r => new { r.Id, r.WanNetworkGroup })
            .ToList();

        if (needsRetry.Count > 0)
        {
            Logger.LogInformation("Retrying path analysis in background for {Count} WAN results", needsRetry.Count);
            foreach (var item in needsRetry)
                _ = Task.Run(async () => await AnalyzePathInBackgroundAsync(item.Id, resolvedWanGroup: item.WanNetworkGroup));
        }

        return results;
    }

    /// <summary>Delete a WAN speed test result by ID.</summary>
    public async Task<bool> DeleteResultAsync(int id)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var result = await db.Iperf3Results.FindAsync(id);
        if (result == null || !OwnedDirections.Contains(result.Direction))
            return false;

        db.Iperf3Results.Remove(result);
        await db.SaveChangesAsync();
        Logger.LogInformation("Deleted WAN speed test result {Id}", id);
        return true;
    }

    /// <summary>Reassigns the WAN interface for a speed test result and re-runs path analysis.</summary>
    public async Task<bool> UpdateWanAssignmentAsync(int id, string wanNetworkGroup, string? wanName)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var result = await db.Iperf3Results.FindAsync(id);
        if (result == null || !OwnedDirections.Contains(result.Direction))
            return false;

        result.WanNetworkGroup = wanNetworkGroup;
        result.WanName = wanName;
        result.PathAnalysisJson = null;
        await db.SaveChangesAsync();

        Logger.LogInformation("Reassigned WAN for result {Id} to {Group} ({Name})", id, wanNetworkGroup, wanName);
        _ = Task.Run(async () => await AnalyzePathInBackgroundAsync(id, resolvedWanGroup: wanNetworkGroup), CancellationToken.None);

        return true;
    }

    /// <summary>Updates the notes for a WAN speed test result.</summary>
    public async Task<bool> UpdateNotesAsync(int id, string? notes)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var result = await db.Iperf3Results.FindAsync(id);
        if (result == null || !OwnedDirections.Contains(result.Direction))
            return false;

        result.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Background path analysis after test completes.</summary>
    protected virtual async Task AnalyzePathInBackgroundAsync(int resultId, string? wanIp = null, string? resolvedWanGroup = null)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            await using var db = await DbFactory.CreateDbContextAsync();
            var result = await db.Iperf3Results.FindAsync(resultId);
            if (result == null) return;

            var path = await PathAnalyzer.CalculatePathAsync(
                result.DeviceHost, result.LocalIp, retryOnFailure: true,
                wanIp: wanIp, resolvedWanGroup: resolvedWanGroup);

            var analysis = PathAnalyzer.AnalyzeSpeedTest(
                path,
                result.DownloadMbps,
                result.UploadMbps,
                result.DownloadRetransmits,
                result.UploadRetransmits,
                result.DownloadBytes,
                result.UploadBytes);

            result.PathAnalysis = analysis;
            await db.SaveChangesAsync();

            Logger.LogDebug("WAN speed test path analysis complete for result {Id}", resultId);
            OnPathAnalysisComplete?.Invoke(resultId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to analyze path for WAN speed test result {Id}", resultId);
        }
    }

    /// <summary>
    /// HttpContent that writes data in chunks and reports bytes as they're written to the stream.
    /// Used for upload throughput measurement with incremental byte counting.
    /// </summary>
    protected sealed class ProgressContent(byte[] data, Action<int> onBytesWritten) : HttpContent
    {
        private const int ChunkSize = 65536; // 64 KB chunks

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var count = Math.Min(ChunkSize, data.Length - offset);
                await stream.WriteAsync(data.AsMemory(offset, count));
                onBytesWritten(count);
                offset += count;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = data.Length;
            return true;
        }
    }
}
