using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Core;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Registers schedule executor delegates that bridge the Alerts project's ScheduleService
/// to the Web project's concrete services (audit, WAN speed test, LAN speed test).
/// </summary>
public static class ScheduleExecutorRegistration
{
    public static void RegisterScheduleExecutors(this WebApplication app)
    {
        if (!FeatureFlags.SchedulingEnabled)
            return;

        var scheduleService = app.Services.GetRequiredService<NetworkOptimizer.Alerts.ScheduleService>();

        scheduleService.AuditExecutor = (siteKey, ct) => ExecuteAuditAsync(app.Services, siteKey, ct);
        scheduleService.WanSpeedTestExecutor = (siteKey, taskId, targetId, targetConfig, ct) =>
            ExecuteWanSpeedTestAsync(app.Services, siteKey, taskId, targetId, targetConfig, ct);
        scheduleService.LanSpeedTestExecutor = (siteKey, targetId, _, ct) =>
            ExecuteLanSpeedTestAsync(app.Services, siteKey, targetId, ct);
    }

    /// <summary>Scheduled operations record a clean failure for license-restricted sites.</summary>
    private static bool IsLicenseRestricted(IServiceProvider services, string siteKey) =>
        !services.GetRequiredService<Licensing.LicenseStateService>().IsSiteOperational(siteKey);

    private const string LicenseRestrictedError = "Site is restricted by licensing";

    private static async Task<(bool Success, string? Summary, string? Error)> ExecuteAuditAsync(
        IServiceProvider services, string siteKey, CancellationToken ct)
    {
        if (IsLicenseRestricted(services, siteKey))
            return (false, null, LicenseRestrictedError);

        // Ensure the site's console connection is fresh for scheduled audits
        // (fingerprint cache expires after 24h)
        var connService = services.GetRequiredService<SiteConnectionRegistry>().GetFor(siteKey);
        if (!connService.IsConnected)
            await connService.ReconnectAsync();

        // Pin the scope so the audit pipeline (scoped repositories, scoped
        // console forwarding) runs against this site's database and console.
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteKey);
        var auditService = scope.ServiceProvider.GetRequiredService<AuditService>();
        if (auditService.IsRunning)
            return (false, null, "Audit is already running");

        // A scheduled scan has no user behind it: run the gated scan as system so it authorizes
        // and audits as "system:scheduler:audit" (design doc 06).
        using var systemScope = Identity.SystemScope.Enter(scope.ServiceProvider, "scheduler:audit");
        var auditScan = scope.ServiceProvider.GetRequiredService<IAuditScanService>();

        try
        {
            var result = await auditScan.RunAuditAsync(new AuditOptions { IsScheduled = true });
            var summary = result.CriticalCount > 0 || result.WarningCount > 0
                ? $"Score: {result.Score} - {result.CriticalCount} critical, {result.WarningCount} recommended"
                : $"Score: {result.Score}";
            return (true, summary, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>Scope pinned to a site so scoped services hit that site's database and console.</summary>
    private static IServiceScope CreatePinnedScope(IServiceProvider services, string siteKey)
    {
        var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteKey);
        // Scheduled work has no user caller; gated services resolved here run as system and the
        // restore handle dies with the scope (design doc 06).
        Identity.SystemScope.Enter(scope.ServiceProvider, "scheduler:speedtest");
        return scope;
    }

    /// <summary>
    /// A WAN speed test schedule's stored config. Every field is absent-tolerant: a schedule made
    /// before a field existed keeps the meaning it had when it was created, which for
    /// <c>wanContextId</c> is the site's default path.
    /// </summary>
    internal static (string TestType, bool MaxMode, string? WanGroup, string? WanName, int? WanContextId, string[]? MultiInterfaces)
        ParseWanTestConfig(string? targetConfig)
    {
        var testType = "gateway";
        var maxMode = false;
        string? wanGroup = null;
        string? wanName = null;
        int? wanContextId = null;
        string[]? multiInterfaces = null;

        if (!string.IsNullOrEmpty(targetConfig))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(targetConfig);
            var root = doc.RootElement;
            if (root.TryGetProperty("testType", out var tt))
                testType = tt.GetString() ?? "gateway";
            if (root.TryGetProperty("maxMode", out var mm))
                maxMode = mm.GetBoolean();
            if (root.TryGetProperty("wanContextId", out var wc) && wc.TryGetInt32(out var contextId))
                wanContextId = contextId;
            if (root.TryGetProperty("wanGroup", out var wg))
                wanGroup = wg.GetString();
            if (root.TryGetProperty("wanName", out var wn))
                wanName = wn.GetString();
            if (root.TryGetProperty("interfaces", out var ifaces) && ifaces.ValueKind == System.Text.Json.JsonValueKind.Array)
                multiInterfaces = ifaces.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }

        return (testType, maxMode, wanGroup, wanName, wanContextId, multiInterfaces);
    }

    private static async Task<(bool Success, string? Summary, string? Error)> ExecuteWanSpeedTestAsync(
        IServiceProvider services, string siteKey, int taskId, string? targetId, string? targetConfig, CancellationToken ct)
    {
        if (IsLicenseRestricted(services, siteKey))
            return (false, null, LicenseRestrictedError);

        // Scheduled runs resolve the gated interfaces from this pinned scope rather than reaching
        // into the registry for the raw instance, so an unattended run is authorized as system and
        // audited exactly like a user-initiated one. The registry hands out the same per-site
        // instance either way, so "already running" still refuses a concurrent run.
        using var scope = CreatePinnedScope(services, siteKey);

        try
        {
            var (testType, maxMode, wanGroup, wanName, wanContextId, multiInterfaces) =
                ParseWanTestConfig(targetConfig);

            // Reconcile WAN metadata against live controller data before running the test.
            // Scheduled configs bake interface, group, and name at creation time; these go stale
            // when users reassign logical interfaces in UniFi.
            if (testType != "server" && (wanGroup != null || wanName != null))
            {
                var reconcileResult = await ReconcileWanMetadataAsync(
                    services, siteKey, taskId, targetId, wanGroup, wanName, multiInterfaces,
                    testType, maxMode, ct);

                if (reconcileResult != null)
                {
                    if (!reconcileResult.Value.Success)
                        return (false, null, reconcileResult.Value.Error);

                    // Apply reconciled values
                    targetId = reconcileResult.Value.TargetId;
                    wanGroup = reconcileResult.Value.WanGroup;
                    wanName = reconcileResult.Value.WanName;
                    multiInterfaces = reconcileResult.Value.MultiInterfaces;
                }
            }

            Iperf3Result? result;

            if (testType == "server")
            {
                // The server-side ("Server") test runs the uwnspeedtest binary. For
                // the default site it runs on THIS host; for an agent-backed site the
                // service dispatches it to the site's agent so it measures the site's
                // own WAN. The per-site instance resolves through the registry, and
                // the service itself refuses sites that are neither (RunTestAsync).
                var serverService = scope.ServiceProvider.GetRequiredService<IUwnSpeedTestService>();
                if (await serverService.IsRunningAsync())
                    return (false, null, "WAN speed test is already running");
                result = await serverService.RunTestAsync(
                    maxMode: maxMode, wanContextId: wanContextId, cancellationToken: ct);
            }
            else
            {
                // The site's own gateway runs the test and the result lands in the
                // site's database - resolved by site key through the registry.
                var gatewayService = scope.ServiceProvider.GetRequiredService<IGatewayWanSpeedTestService>();
                if (await gatewayService.IsRunningAsync())
                    return (false, null, "WAN speed test is already running");

                if (multiInterfaces is { Length: > 1 })
                {
                    var sqmService = scope.ServiceProvider.GetRequiredService<ISqmService>();
                    var allWans = await sqmService.GetWanInterfacesFromControllerAsync();
                    var selectedWans = allWans.Where(w => multiInterfaces.Contains(w.Interface)).ToList();
                    result = await gatewayService.RunTestAsync(
                        "", wanGroup, wanName,
                        allInterfaces: selectedWans,
                        maxMode: maxMode,
                        cancellationToken: ct);
                }
                else
                {
                    result = await gatewayService.RunTestAsync(
                        targetId ?? "eth4", wanGroup, wanName,
                        maxMode: maxMode,
                        cancellationToken: ct);
                }
            }

            if (result == null)
                return (false, null, "WAN speed test returned no result");

            var dl = result.DownloadBitsPerSecond / 1_000_000.0;
            var ul = result.UploadBitsPerSecond / 1_000_000.0;
            return (true, $"{dl:F0} / {ul:F0} Mbps", null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static async Task<(bool Success, string? Summary, string? Error)> ExecuteLanSpeedTestAsync(
        IServiceProvider services, string siteKey, string? targetId, CancellationToken ct)
    {
        if (IsLicenseRestricted(services, siteKey))
            return (false, null, LicenseRestrictedError);

        if (string.IsNullOrEmpty(targetId))
            return (false, null, "No target device specified");

        // The site's own LAN speed test instance: its devices, SSH credentials, and
        // results, with tests reaching the site over VPN or the agent tunnel. Resolved as the gated
        // interface from a pinned scope so the unattended run is authorized as system and audited.
        using var scope = CreatePinnedScope(services, siteKey);
        var lanService = scope.ServiceProvider.GetRequiredService<IIperf3SpeedTestService>();
        var devices = await lanService.GetDevicesAsync();
        var device = devices.FirstOrDefault(d => d.Host == targetId);

        // Fall back to UniFi-discovered devices if not found in manual config
        if (device == null)
        {
            var connService = services.GetRequiredService<SiteConnectionRegistry>().GetFor(siteKey);
            try
            {
                var discovered = await connService.GetDiscoveredDevicesAsync(ct);
                var unifiDevice = discovered.FirstOrDefault(d =>
                    d.IpAddress == targetId && d.Type != DeviceType.Gateway && d.CanRunIperf3);
                if (unifiDevice != null)
                {
                    device = new DeviceSshConfiguration
                    {
                        Name = unifiDevice.Name ?? "Unknown Device",
                        Host = unifiDevice.IpAddress,
                        DeviceType = unifiDevice.Type,
                        Enabled = true,
                        StartIperf3Server = true
                    };
                }
            }
            catch { /* UniFi unavailable - fall through to error */ }
        }

        if (device == null)
            return (false, null, $"Device not found: {targetId}");

        try
        {
            var result = await lanService.RunSpeedTestAsync(device);
            if (result.ErrorMessage != null)
                return (false, null, result.ErrorMessage);

            var dl = result.DownloadBitsPerSecond / 1_000_000.0;
            var ul = result.UploadBitsPerSecond / 1_000_000.0;
            return (true, $"{dl:F0} / {ul:F0} Mbps", null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    #region WAN Reconciliation

    private record struct ReconcileResult(
        bool Success, string? Error,
        string? TargetId, string? WanGroup, string? WanName, string[]? MultiInterfaces);

    /// <summary>
    /// Reconcile WAN metadata against live controller data. Returns null if no reconciliation
    /// was needed (controller unreachable or empty), updated values on success, or an error
    /// if the schedule was irreconcilable and disabled.
    /// </summary>
    private static async Task<ReconcileResult?> ReconcileWanMetadataAsync(
        IServiceProvider services, string siteKey, int taskId,
        string? targetId, string? wanGroup, string? wanName, string[]? multiInterfaces,
        string testType, bool maxMode, CancellationToken ct)
    {
        try
        {
            List<WanInterfaceInfo> liveWans;
            using (var scope = CreatePinnedScope(services, siteKey))
            {
                var sqmService = scope.ServiceProvider.GetRequiredService<ISqmService>();
                liveWans = await sqmService.GetWanInterfacesFromControllerAsync();
            }

            if (liveWans.Count == 0)
                return null; // Controller unreachable or no WANs - skip reconciliation

            var logger = services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("WanScheduleReconciliation");

            if (multiInterfaces is { Length: > 1 })
                return await ReconcileMultiWanAsync(
                    services, siteKey, logger, taskId, targetId, wanGroup, wanName, multiInterfaces,
                    testType, maxMode, liveWans, ct);

            return await ReconcileSingleWanAsync(
                services, siteKey, logger, taskId, targetId, wanGroup, wanName,
                testType, maxMode, liveWans, ct);
        }
        catch (Exception ex)
        {
            // Reconciliation failure is non-fatal - proceed with stored values
            var logger = services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("WanScheduleReconciliation");
            logger.LogWarning(ex,
                "WAN schedule reconciliation failed for task {TaskId}, proceeding with stored values", taskId);
            return null;
        }
    }

    /// <summary>
    /// Reconcile a single WAN interface's stored metadata against live controller data using 2-of-3 matching.
    /// Returns (matched, interface, group, name). If fewer than 2 fields match any live WAN, matched=false.
    /// </summary>
    private static (bool Matched, string Interface, string? Group, string? Name) MatchWanInterface(
        string? storedInterface, string? storedGroup, string? storedName,
        List<WanInterfaceInfo> liveWans)
    {
        foreach (var live in liveWans)
        {
            var ifaceMatch = string.Equals(storedInterface, live.Interface, StringComparison.OrdinalIgnoreCase);
            var groupMatch = string.Equals(storedGroup, live.NetworkGroup, StringComparison.OrdinalIgnoreCase);
            var nameMatch = string.Equals(storedName, live.Name, StringComparison.OrdinalIgnoreCase);

            var matchCount = (ifaceMatch ? 1 : 0) + (groupMatch ? 1 : 0) + (nameMatch ? 1 : 0);

            if (matchCount >= 2)
                return (true, live.Interface, live.NetworkGroup, live.Name);
        }

        return (false, storedInterface ?? "", storedGroup, storedName);
    }

    private static async Task<ReconcileResult> ReconcileSingleWanAsync(
        IServiceProvider services, string siteKey, ILogger logger, int taskId,
        string? targetId, string? wanGroup, string? wanName,
        string testType, bool maxMode,
        List<WanInterfaceInfo> liveWans, CancellationToken ct)
    {
        var (matched, newIface, newGroup, newName) =
            MatchWanInterface(targetId, wanGroup, wanName, liveWans);

        if (!matched)
        {
            await DisableScheduleAsync(services, siteKey, taskId, ct);
            return new ReconcileResult(false,
                $"WAN schedule disabled: could not reconcile interface {targetId} " +
                $"(group={wanGroup}, name={wanName}) against live controller data",
                null, null, null, null);
        }

        if (newIface != targetId || newGroup != wanGroup || newName != wanName)
        {
            var configObj = new Dictionary<string, object?>
            {
                ["testType"] = testType,
                ["maxMode"] = maxMode
            };
            if (newGroup != null) configObj["wanGroup"] = newGroup;
            if (newName != null) configObj["wanName"] = newName;

            await PersistScheduleUpdateAsync(services, siteKey, taskId, newIface, configObj, ct);
            logger.LogInformation(
                "Reconciled WAN schedule {TaskId}: iface={Iface} group={Group} name={Name}",
                taskId, newIface, newGroup, newName);

            return new ReconcileResult(true, null, newIface, newGroup, newName, null);
        }

        // No changes needed
        return new ReconcileResult(true, null, targetId, wanGroup, wanName, null);
    }

    private static async Task<ReconcileResult> ReconcileMultiWanAsync(
        IServiceProvider services, string siteKey, ILogger logger, int taskId,
        string? targetId, string? wanGroup, string? wanName, string[] multiInterfaces,
        string testType, bool maxMode,
        List<WanInterfaceInfo> liveWans, CancellationToken ct)
    {
        var groups = wanGroup?.Split('+') ?? Array.Empty<string>();
        var names = wanName?.Split(" + ") ?? Array.Empty<string>();
        var updatedInterfaces = new List<string>();
        var updatedGroups = new List<string>();
        var updatedNames = new List<string>();
        var anyUpdated = false;

        for (int i = 0; i < multiInterfaces.Length; i++)
        {
            var iface = multiInterfaces[i];
            var grp = i < groups.Length ? groups[i] : null;
            var nm = i < names.Length ? names[i] : null;

            var (matched, newIface, newGroup, newName) =
                MatchWanInterface(iface, grp, nm, liveWans);

            if (!matched)
            {
                await DisableScheduleAsync(services, siteKey, taskId, ct);
                return new ReconcileResult(false,
                    $"WAN schedule disabled: could not reconcile interface {iface} " +
                    $"(group={grp}, name={nm}) against live controller data",
                    null, null, null, null);
            }

            updatedInterfaces.Add(newIface);
            updatedGroups.Add(newGroup ?? grp ?? "WAN");
            updatedNames.Add(newName ?? nm ?? "");
            if (newIface != iface || newGroup != grp || newName != nm)
                anyUpdated = true;
        }

        var newTargetId = string.Join(",", updatedInterfaces);
        var newWanGroup = string.Join("+", updatedGroups);
        var newWanName = string.Join(" + ", updatedNames);
        var newMultiInterfaces = updatedInterfaces.ToArray();

        if (anyUpdated)
        {
            var configObj = new Dictionary<string, object?>
            {
                ["testType"] = testType,
                ["maxMode"] = maxMode,
                ["wanGroup"] = newWanGroup,
                ["wanName"] = newWanName,
                ["interfaces"] = newMultiInterfaces
            };

            await PersistScheduleUpdateAsync(services, siteKey, taskId, newTargetId, configObj, ct);
            logger.LogInformation("Reconciled multi-WAN schedule {TaskId}: updated groups/names", taskId);
        }

        return new ReconcileResult(true, null, newTargetId, newWanGroup, newWanName, newMultiInterfaces);
    }

    private static async Task DisableScheduleAsync(IServiceProvider services, string siteKey, int taskId, CancellationToken ct)
    {
        // Schedule rows live in each site's own database; task ids are per-site sequences.
        using var scope = CreatePinnedScope(services, siteKey);
        var repo = scope.ServiceProvider.GetRequiredService<IScheduleRepository>();
        var task = await repo.GetByIdAsync(taskId, ct);
        if (task != null)
        {
            task.Enabled = false;
            await repo.UpdateAsync(task, ct);
        }
    }

    private static async Task PersistScheduleUpdateAsync(
        IServiceProvider services, string siteKey, int taskId, string? newTargetId,
        Dictionary<string, object?> configObj, CancellationToken ct)
    {
        var newConfig = System.Text.Json.JsonSerializer.Serialize(configObj);
        using var scope = CreatePinnedScope(services, siteKey);
        var repo = scope.ServiceProvider.GetRequiredService<IScheduleRepository>();
        var task = await repo.GetByIdAsync(taskId, ct);
        if (task != null)
        {
            task.TargetId = newTargetId;
            task.TargetConfig = newConfig;
            await repo.UpdateAsync(task, ct);
        }
    }

    #endregion
}
