using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The WAN speed test surface shared by the server-side (uwnspeedtest) runner. Running a speed test
/// is the one mutating action an Operator may take (design doc 08); editing or deleting stored
/// results is an Admin change to recorded data.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IUwnSpeedTestService
{
    // These four are methods rather than properties because they are gated. A gated member is
    // intercepted, and the interceptor only handles Task-returning members asynchronously - so a
    // gated property has its role lookup blocked on synchronously, inside the Blazor circuit's
    // synchronization context. That works while the lookup hits its cache and deadlocks when it
    // misses and goes to the database, which would present as the WAN speed test page freezing for
    // no visible reason. Enforced by architecture test A2.

    /// <summary>True while a test is in flight (the UI blocks a second concurrent run).</summary>
    [RequireRole(Roles.Viewer)]
    Task<bool> IsRunningAsync();

    /// <summary>Live progress of the running test (phase, percent, status line).</summary>
    [RequireRole(Roles.Viewer)]
    Task<(string Phase, int Percent, string? Status)> GetCurrentProgressAsync();

    /// <summary>The most recently completed result, kept for the page to show after a run.</summary>
    [RequireRole(Roles.Viewer)]
    Task<Iperf3Result?> GetLastCompletedResultAsync();

    /// <summary>Metadata captured alongside the last run (server, path, interface).</summary>
    [RequireRole(Roles.Viewer)]
    Task<WanTestMetadata?> GetLastMetadataAsync();

    /// <summary>Raised with the result id once the post-test path analysis finishes.</summary>
    event Action<int>? OnPathAnalysisComplete;

    /// <summary>
    /// Runs a WAN speed test from this server, or from the site's agent when the agent owns the
    /// site's measurements.
    /// </summary>
    /// <param name="wanContextId">
    /// WAN context the agent run should measure, or null for the site's primary WAN. Ignored by a
    /// local run, which measures whatever this host's route takes.
    /// </param>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SpeedTestRun, TargetType = "wan_speedtest")]
    Task<Iperf3Result?> RunTestAsync(
        Action<(string Phase, int Percent, string? Status)>? onProgress = null,
        bool maxMode = false,
        int? wanContextId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stored WAN speed test results for this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<Iperf3Result>> GetResultsAsync(int count = 50, int hours = 0);

    /// <summary>Deletes a stored result.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SpeedTestDeleted, TargetType = "wan_speedtest")]
    Task<bool> DeleteResultAsync(int id);

    /// <summary>Re-assigns a stored result to a different WAN.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "wan_speedtest")]
    Task<bool> UpdateWanAssignmentAsync(int id, string wanNetworkGroup, string? wanName);

    /// <summary>Edits the notes on a stored result.</summary>
    [RequireRole(Roles.Operator)]
    Task<bool> UpdateNotesAsync(int id, string? notes);
}

/// <summary>
/// The gateway-run WAN speed test (the gateway runs the test against an external server). Same tiers
/// as <see cref="IUwnSpeedTestService"/>; deploying the binary to the gateway is Admin.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IGatewayWanSpeedTestService
{
    // Methods rather than properties for the same reason as IUwnSpeedTestService above: a gated
    // property blocks its role lookup synchronously on the circuit.

    /// <summary>True while a test is in flight.</summary>
    [RequireRole(Roles.Viewer)]
    Task<bool> IsRunningAsync();

    /// <summary>Live progress of the running test (phase, percent, status line).</summary>
    [RequireRole(Roles.Viewer)]
    Task<(string Phase, int Percent, string? Status)> GetCurrentProgressAsync();

    /// <summary>The most recently completed result, kept for the page to show after a run.</summary>
    [RequireRole(Roles.Viewer)]
    Task<Iperf3Result?> GetLastCompletedResultAsync();

    /// <summary>Raised with the result id once the post-test path analysis finishes.</summary>
    event Action<int>? OnPathAnalysisComplete;

    /// <summary>Whether the speed test binary is present on the gateway and current.</summary>
    [RequireRole(Roles.Viewer)]
    Task<(bool Deployed, bool NeedsUpdate)> CheckBinaryStatusAsync();

    /// <summary>Deploys or updates the speed test binary on the gateway.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "wan_speedtest_binary")]
    Task<(bool Success, string? Error)> DeployBinaryAsync(CancellationToken ct = default);

    /// <summary>Runs a WAN speed test from the gateway over one or more WAN interfaces.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SpeedTestRun, TargetType = "wan_speedtest")]
    Task<Iperf3Result?> RunTestAsync(
        string interfaceName,
        string? wanNetworkGroup,
        string? wanName,
        Action<(string Phase, int Percent, string? Status)>? onProgress = null,
        IReadOnlyList<WanInterfaceInfo>? allInterfaces = null,
        bool maxMode = false,
        CancellationToken cancellationToken = default);

    /// <summary>Stored gateway WAN results for this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<Iperf3Result>> GetResultsAsync(int count = 50, int hours = 0);

    /// <summary>Deletes a stored result.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SpeedTestDeleted, TargetType = "wan_speedtest")]
    Task<bool> DeleteResultAsync(int id);

    /// <summary>Edits the notes on a stored result.</summary>
    [RequireRole(Roles.Operator)]
    Task<bool> UpdateNotesAsync(int id, string? notes);

    /// <summary>Re-assigns a stored result to a different WAN.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "wan_speedtest")]
    Task<bool> UpdateWanAssignmentAsync(int id, string wanNetworkGroup, string? wanName);
}

/// <summary>
/// Client speed test results (OpenSpeedTest and client-initiated iperf3). The recording methods are
/// driven by anonymous client submissions on <c>/api/public/*</c>, which run in an explicit system
/// scope, so they carry the read tier here; deleting or editing recorded results is an Admin change.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IClientSpeedTestService
{
    /// <summary>Records a browser (OpenSpeedTest) result posted by a client.</summary>
    [RequireRole(Roles.Viewer)]
    Task<Iperf3Result> RecordOpenSpeedTestResultAsync(
        string clientIp, double downloadMbps, double uploadMbps, double? pingMs, double? jitterMs,
        double? downloadDataMb, double? uploadDataMb, string? userAgent,
        double? latitude = null, double? longitude = null, int? locationAccuracy = null,
        int? durationSeconds = null, string? externalServerId = null);

    /// <summary>Records a client-initiated iperf3 result relayed by the site's iperf3 server.</summary>
    [RequireRole(Roles.Viewer)]
    Task<Iperf3Result> RecordIperf3ClientResultAsync(
        string clientIp, double downloadBitsPerSecond, double uploadBitsPerSecond,
        long downloadBytes, long uploadBytes, int? downloadRetransmits, int? uploadRetransmits,
        int durationSeconds, int parallelStreams, string? rawJson, string? serverLocalIp = null);

    /// <summary>Recent client speed test results for this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<Iperf3Result>> GetResultsAsync(int count = 50, int hours = 0);

    /// <summary>Recent client WAN results for this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<Iperf3Result>> GetWanResultsAsync(int count = 50, int hours = 0);

    /// <summary>Results for one client address.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<Iperf3Result>> GetResultsByIpAsync(string clientIp, int count = 20);

    /// <summary>Results for one client MAC.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<Iperf3Result>> GetResultsByMacAsync(string clientMac, int count = 20);

    /// <summary>Deletes a stored result.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SpeedTestDeleted, TargetType = "client_speedtest")]
    Task<bool> DeleteResultAsync(int id);

    /// <summary>Edits the notes on a stored result.</summary>
    [RequireRole(Roles.Operator)]
    Task<bool> UpdateNotesAsync(int id, string? notes);
}
