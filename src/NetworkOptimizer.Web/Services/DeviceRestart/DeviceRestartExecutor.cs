using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Firmware;
using NetworkOptimizer.Web.Services.Monitoring.HealthChecks;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;

namespace NetworkOptimizer.Web.Services.DeviceRestart;

/// <summary>
/// Runs every site's requested device restarts. One device is in flight per site at a time, taken
/// root first down the uplink tree, so a switch and the AP it powers restart in that order and the
/// AP is not restarted again when the switch's restart already booted it. Every step is persisted,
/// so an app restart, or a gateway restart that takes the console with it, loses nothing.
/// </summary>
public sealed class DeviceRestartExecutor : BackgroundService, ISiteScopedRegistry
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PoeSampleInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A boot this close before the command still counts as the command's (clock skew, uptime rounding).</summary>
    private static readonly TimeSpan BootSlack = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly Licensing.LicenseStateService _license;
    private readonly RolloutSuppressionRegistry _suppression;
    private readonly DeviceRebootRegistry _reboots;
    private readonly IAuditLogger _audit;
    private readonly IAlertEventBus _alerts;
    private readonly PoeDrawMemory _poe;
    private readonly ILogger<DeviceRestartExecutor> _logger;

    private readonly ConcurrentDictionary<string, DeviceRestartSiteReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastPoeSample = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    public DeviceRestartExecutor(
        IServiceProvider services,
        IDbContextFactory<NetworkOptimizerDbContext> mainDb,
        Licensing.LicenseStateService license,
        RolloutSuppressionRegistry suppression,
        DeviceRebootRegistry reboots,
        IAuditLogger audit,
        IAlertEventBus alerts,
        PoeDrawMemory poe,
        ILogger<DeviceRestartExecutor> logger)
    {
        _services = services;
        _mainDb = mainDb;
        _license = license;
        _suppression = suppression;
        _reboots = reboots;
        _audit = audit;
        _alerts = alerts;
        _poe = poe;
        _logger = logger;
    }

    /// <summary>The site's reader, created on first use.</summary>
    public DeviceRestartSiteReader ReaderFor(string slug) =>
        _readers.GetOrAdd(slug, s => ActivatorUtilities.CreateInstance<DeviceRestartSiteReader>(_services, s));

    /// <summary>Runs a pass now instead of at the next tick, so Restart now starts at once.</summary>
    public void Nudge() => _wake.Release();

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _readers.TryRemove(slug, out _);
        _lastPoeSample.TryRemove(slug, out _);
        _suppression.ClearRestartActive(slug);
        _poe.ClearSite(slug);
        // A pass may be reading this site's database; the removal deletes it only after this returns.
        return async () =>
        {
            await _tickLock.WaitAsync();
            _tickLock.Release();
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _tickLock.WaitAsync(stoppingToken);
            try
            {
                foreach (var slug in await OperationalSitesAsync(stoppingToken))
                {
                    try
                    {
                        await TickSiteAsync(slug, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Device restart pass failed for site {Site}", slug);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Device restart pass failed");
            }
            finally
            {
                _tickLock.Release();
            }

            try { await _wake.WaitAsync(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<List<string>> OperationalSitesAsync(CancellationToken ct)
    {
        var slugs = new List<string>();
        if (_license.IsSiteOperational(SiteManagementService.DefaultSiteSlug))
            slugs.Add(SiteManagementService.DefaultSiteSlug);

        await using var db = await _mainDb.CreateDbContextAsync(ct);
        var setting = await db.SystemSettings.FindAsync([SystemSettingKeys.MultiSiteEnabled], ct);
        if (bool.TryParse(setting?.Value, out var enabled) && enabled)
        {
            var sites = await db.Sites.AsNoTracking().Where(s => s.Enabled && !s.IsDefault).Select(s => s.Slug).ToListAsync(ct);
            slugs.AddRange(sites.Where(_license.IsSiteOperational));
        }
        return slugs;
    }

    private async Task TickSiteAsync(string slug, CancellationToken ct)
    {
        var reader = ReaderFor(slug);
        if (!reader.DatabaseExists) return;
        var now = DateTime.UtcNow;

        await using var db = await reader.OpenDbAsync(ct);
        var open = await db.DeviceRestartRequests
            .Where(r => r.Status == DeviceRestartStatus.Pending || r.Status == DeviceRestartStatus.Waiting || r.Status == DeviceRestartStatus.Running)
            .ToListAsync(ct);

        // Nothing due and nothing under way: a restart scheduled for later costs no console reads.
        if (!open.Any(r => r.Status == DeviceRestartStatus.Running || r.DueAtUtc <= now))
        {
            MarkRestartActive(slug, open);
            // Keeps the PoE draw of every device known, so a device that hangs later can still be power-cycled.
            if (!_lastPoeSample.TryGetValue(slug, out var last) || now - last >= PoeSampleInterval)
            {
                _lastPoeSample[slug] = now;
                await reader.ReadTopologyAsync(ct);
            }
            return;
        }

        var read = await reader.ReadTopologyAsync(ct);
        if (read is not { } snapshot)
        {
            // The console is dark: most often the gateway restart this executor sent. Keep the
            // expected-outage windows open and wait for it to answer.
            foreach (var r in open.Where(r => r.LastCommandAt != null))
                RefreshWindows(slug, r, null);
            MarkRestartActive(slug, open);
            return;
        }
        _lastPoeSample[slug] = now;

        var topology = snapshot.Topology;
        var candidates = RestartRules.Candidates(open, topology, now);
        if (candidates.Count == 0)
        {
            MarkRestartActive(slug, open);
            return;
        }

        // One request takes the turn. One that is only held by a lockout passes it to the next, so
        // a device-level hold elsewhere in the tree does not stall an unrelated restart.
        var site = await reader.ReadSiteStateAsync(ct);
        foreach (var candidate in candidates)
        {
            if (await StepAsync(reader, db, open, candidate, topology, snapshot.Devices, site, now, ct))
                break;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A user cancelled or replaced the request during this pass; the next pass reads theirs.
            _logger.LogInformation("Device restart {Site}: a request changed during the pass; re-reading it next pass", slug);
        }
        MarkRestartActive(slug, open);
    }

    /// <summary>While anything is under way the Firmware Rollout holds its next wave.</summary>
    private void MarkRestartActive(string slug, List<DeviceRestartRequest> open)
    {
        if (open.Any(r => r.Status == DeviceRestartStatus.Running))
            _suppression.RefreshRestartActive(slug, DateTime.UtcNow);
        else
            _suppression.ClearRestartActive(slug);
    }

    /// <summary>
    /// Moves one request one step: hold, skip, send, confirm, retry, or settle. False when the
    /// request is only held by a lockout and the turn passes on.
    /// </summary>
    private async Task<bool> StepAsync(
        DeviceRestartSiteReader reader, NetworkOptimizerDbContext db, List<DeviceRestartRequest> open,
        DeviceRestartRequest request, RestartTopology topology, IReadOnlyList<DiscoveredDevice> devices,
        RestartSiteState site, DateTime now, CancellationToken ct)
    {
        var slug = reader.SiteSlug;
        var node = topology.Find(request.DeviceMac);
        var device = devices.FirstOrDefault(d => string.Equals(MacNormalizer.Normalize(d.Mac), MacNormalizer.Normalize(request.DeviceMac), StringComparison.OrdinalIgnoreCase));
        if (node == null || device == null)
        {
            await FailAsync(slug, open, request, topology, "The UniFi Console no longer lists this device.", now, ct);
            return true;
        }

        if (request.LastCommandAt is { } commandedAt)
        {
            await ConfirmAsync(reader, open, request, node, topology, commandedAt, now, ct);
            return true;
        }

        var lockout = RestartRules.Lockout(topology, request.DeviceMac, site, now);
        if (lockout != null)
        {
            if (request.Status != DeviceRestartStatus.Waiting || request.StatusReason != lockout)
            {
                _logger.LogInformation("Device restart {Site}: {Device} waits: {Reason}", slug, node.Name, lockout);
                request.Status = DeviceRestartStatus.Waiting;
                request.StatusReason = lockout;
            }
            return false;
        }

        if (request.Status == DeviceRestartStatus.Waiting)
        {
            await ReleaseFromLockoutAsync(reader, request, topology, devices, now, ct);
            return true;
        }

        if (request.Status != DeviceRestartStatus.Running)
        {
            request.Status = DeviceRestartStatus.Running;
            request.StartedAt ??= now;
            request.BatchStartedAt ??= now;
            request.StatusReason = null;
        }

        if (RestartRules.AlreadyRestarted(request, node, now))
        {
            var recent = await db.DeviceRestartRequests.AsNoTracking()
                .Where(r => r.Status == DeviceRestartStatus.Done && r.CompletedAt >= now - RestartRules.CoalescingWindow * 2)
                .ToListAsync(ct);
            var cause = RestartRules.UpstreamCause(topology, request.DeviceMac, recent);
            Settle(slug, request, cause != null ? DeviceRestartStatus.DoneByUpstream : DeviceRestartStatus.Done,
                cause != null ? $"Restarted with {cause.DeviceName}." : "It had already restarted.", now);
            return true;
        }

        // Three tries on SSH and the console, then one PoE power-cycle where a port powers the device.
        var poe = reader.PoweringPort(topology, request.DeviceMac);
        var lastTry = poe != null ? RestartRules.MaxAttempts + 1 : RestartRules.MaxAttempts;
        if (request.Attempts >= lastTry)
        {
            await FailAsync(slug, open, request, topology, request.StatusReason ?? "The restart command failed on every attempt.", now, ct);
            return true;
        }

        if (request.RequestedMethod == DeviceRestartMethod.PoeCycle)
        {
            if (poe == null)
            {
                // Asked for a power-cycle, but the device is no longer powered from a known switch port.
                _logger.LogInformation("Device restart {Site}: {Device} is no longer on PoE; restarting it the usual way", slug, node.Name);
                request.RequestedMethod = DeviceRestartMethod.Auto;
            }
            else
            {
                await SendAsync(reader, request, node, device, topology, DeviceRestartMethod.PoeCycle, poe, now, ct);
                return true;
            }
        }

        var reachable = node.IsOnline && topology.PathToRoot(request.DeviceMac).All(n => n.IsOnline);
        if (!reachable)
        {
            // Most often its upstream is still coming back from the restart before it.
            if (now - (request.StartedAt ?? now) < RestartRules.OnlineWait) return true;
            if (poe != null)
            {
                await SendAsync(reader, request, node, device, topology, DeviceRestartMethod.PoeCycle, poe, now, ct);
                return true;
            }
            await FailAsync(slug, open, request, topology,
                $"{node.Name} stayed offline for {TimeFormatHelper.FormatDuration(RestartRules.OnlineWait)}, and no PoE port powers it.", now, ct);
            return true;
        }

        var method = request.Attempts >= RestartRules.MaxAttempts ? DeviceRestartMethod.PoeCycle : DeviceRestartMethod.Auto;
        await SendAsync(reader, request, node, device, topology, method, poe, now, ct);
        return true;
    }

    /// <summary>
    /// A held restart whose lockout has cleared. A timed one runs after a short settle; a quiet one
    /// runs then only while its quiet hour lasts, and otherwise moves to the next quiet hour.
    /// </summary>
    private async Task ReleaseFromLockoutAsync(
        DeviceRestartSiteReader reader, DeviceRestartRequest request, RestartTopology topology,
        IReadOnlyList<DiscoveredDevice> devices, DateTime now, CancellationToken ct)
    {
        var runAt = now + RestartRules.LockoutSettle;
        request.Status = DeviceRestartStatus.Pending;
        request.StatusReason = null;
        request.StartedAt = null;
        request.BatchStartedAt = null;

        var quietHourEnds = request.DueAtUtc + TimeSpan.FromHours(1);
        if (request.Mode == DeviceRestartMode.Quiet && runAt >= quietHourEnds)
        {
            var proposal = await reader.ProposeQuietAsync(topology, devices, request.DeviceMac, ct);
            request.DueAtUtc = proposal.StartUtc;
            request.QuietBasis = proposal.Basis;
            Audit(reader.SiteSlug, request, AuditOutcomes.Success, "moved", $"The lockout outlasted the quiet hour; moved to {proposal.StartUtc:u}.");
            _logger.LogInformation("Device restart {Site}: {Device} moved to the next quiet hour, {At:u}", reader.SiteSlug, request.DeviceName, proposal.StartUtc);
            return;
        }

        request.DueAtUtc = runAt;
    }

    private async Task SendAsync(
        DeviceRestartSiteReader reader, DeviceRestartRequest request, RestartNode node, DiscoveredDevice device,
        RestartTopology topology, DeviceRestartMethod method, PoePort? poe, DateTime now, CancellationToken ct)
    {
        var slug = reader.SiteSlug;
        var client = reader.Connection.Client;
        request.Attempts++;

        var by = string.IsNullOrEmpty(request.RequestedByName) ? "Network Optimizer" : $"Network Optimizer, for {request.RequestedByName}";
        _reboots.GetFor(slug).ExpectCommandedRestart(request.DeviceMac, by);
        RefreshWindows(slug, request, topology);

        string? error = null;
        DeviceRestartMethod? used = null;

        if (method == DeviceRestartMethod.PoeCycle && poe != null)
        {
            try
            {
                if (client != null && await client.PowerCyclePortAsync(poe.SwitchMac, poe.PortIdx, ct))
                {
                    used = DeviceRestartMethod.PoeCycle;
                    request.PoeSwitchMac = poe.SwitchMac;
                    request.PoePortIdx = poe.PortIdx;
                }
                else error = $"The UniFi Console refused to power-cycle port {poe.PortIdx} on {poe.SwitchName}.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = $"Power-cycling port {poe.PortIdx} on {poe.SwitchName} failed: {ex.Message}";
            }
        }
        else
        {
            if (await reader.HasSshAsync(device, ct))
            {
                try
                {
                    var command = HealthCheckRemedies.BuildCommand(HealthCheckRemedy.RebootDevice, null)!;
                    var (ok, output) = await reader.Ssh.RunAsync(DeviceRestartSiteReader.Target(device), command, SshTimeout, ct);
                    if (ok) used = DeviceRestartMethod.Ssh;
                    else error = $"SSH: {Trim(output)}";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = $"SSH: {ex.Message}";
                }
            }

            if (used == null)
            {
                try
                {
                    if (client != null && await client.RestartDeviceAsync(request.DeviceMac, ct))
                        used = DeviceRestartMethod.Console;
                    else
                        error = (error != null ? error + " " : "") + "The UniFi Console refused the restart command.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = (error != null ? error + " " : "") + $"UniFi Console: {ex.Message}";
                }
            }
        }

        if (used is { } ran)
        {
            request.Method = ran;
            request.LastCommandAt = now;
            request.StatusReason = null;
            _logger.LogInformation("Device restart {Site}: sent {Method} to {Device} (attempt {Attempt})", slug, ran, node.Name, request.Attempts);
            Audit(slug, request, AuditOutcomes.Success, "sent", null);
            return;
        }

        // Both paths refused. Try again after the retry delay; the step above decides when to stop.
        request.StatusReason = error;
        request.DueAtUtc = now + RestartRules.RetryDelay;
        _logger.LogWarning("Device restart {Site}: {Device} attempt {Attempt} failed: {Error}", slug, node.Name, request.Attempts, error);
    }

    /// <summary>After a command: done once the device booted after it and is Online; retried once its budget runs out.</summary>
    private async Task ConfirmAsync(
        DeviceRestartSiteReader reader, List<DeviceRestartRequest> open, DeviceRestartRequest request, RestartNode node,
        RestartTopology topology, DateTime commandedAt, DateTime now, CancellationToken ct)
    {
        var slug = reader.SiteSlug;
        RefreshWindows(slug, request, topology);

        if (RestartRules.BootedAt(node, now) is { } booted && booted >= commandedAt - BootSlack && node.IsOnline)
        {
            Settle(slug, request, DeviceRestartStatus.Done, null, now);
            return;
        }

        if (now - commandedAt < RestartRules.OfflineBudget(node.Type)) return;

        // The command was accepted but the device never came back on a new boot.
        var poe = reader.PoweringPort(topology, request.DeviceMac);
        if (request.Attempts < RestartRules.MaxAttempts)
        {
            request.LastCommandAt = null;
            request.StatusReason = $"{node.Name} did not restart within {TimeFormatHelper.FormatDuration(RestartRules.OfflineBudget(node.Type))}.";
            return;
        }
        if (poe != null && request.Method != DeviceRestartMethod.PoeCycle)
        {
            request.LastCommandAt = null;
            request.StatusReason = $"{node.Name} did not restart; power-cycling its PoE port.";
            return;
        }
        await FailAsync(slug, open, request, topology,
            $"{node.Name} did not come back on a new boot within {TimeFormatHelper.FormatDuration(RestartRules.OfflineBudget(node.Type))} after {request.Attempts} attempts.", now, ct);
    }

    /// <summary>
    /// Tells offline, reboot, and health-check alerting the outage is planned, for the device and
    /// everything below it. Windows lapse after a few minutes, so this runs on every pass in flight.
    /// </summary>
    private void RefreshWindows(string slug, DeviceRestartRequest request, RestartTopology? topology)
    {
        var now = DateTime.UtcNow;
        _suppression.Refresh(slug, request.DeviceMac, now);
        if (topology != null)
            foreach (var below in topology.Subtree(request.DeviceMac))
                _suppression.Refresh(slug, below.Mac, now);
        if (request.DeviceType == DeviceType.Gateway)
        {
            _suppression.RefreshOsCycle(slug, now);
            _suppression.RefreshConsoleCycle(slug, now);
        }
    }

    private void Settle(string slug, DeviceRestartRequest request, DeviceRestartStatus status, string? reason, DateTime now)
    {
        request.Status = status;
        request.StatusReason = reason;
        request.CompletedAt = now;
        _logger.LogInformation("Device restart {Site}: {Device} {Status}{Reason}", slug, request.DeviceName, status, reason == null ? "" : $": {reason}");
        Audit(slug, request, AuditOutcomes.Success, status == DeviceRestartStatus.Done ? "done" : "done_by_upstream", reason);
    }

    /// <summary>Fails the request, and every open request below it: their path is in an unknown state.</summary>
    private async Task FailAsync(
        string slug, List<DeviceRestartRequest> open, DeviceRestartRequest request, RestartTopology topology,
        string reason, DateTime now, CancellationToken ct)
    {
        Fail(request, reason, now);
        await PublishFailedAsync(slug, request, ct);

        var below = topology.Subtree(request.DeviceMac).Select(n => n.Mac).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var child in open.Where(r => r != request && r.IsOpen && below.Contains(MacNormalizer.Normalize(r.DeviceMac)) && r.DueAtUtc <= now))
        {
            Fail(child, $"Not restarted: the restart of {request.DeviceName} above it failed.", now);
            await PublishFailedAsync(slug, child, ct);
        }

        void Fail(DeviceRestartRequest r, string why, DateTime at)
        {
            r.Status = DeviceRestartStatus.Failed;
            r.StatusReason = Trim(why);
            r.CompletedAt = at;
            _logger.LogWarning("Device restart {Site}: {Device} failed: {Reason}", slug, r.DeviceName, why);
            Audit(slug, r, AuditOutcomes.Failure, "failed", why);
        }
    }

    private async Task PublishFailedAsync(string slug, DeviceRestartRequest request, CancellationToken ct)
    {
        var suffix = slug == SiteManagementService.DefaultSiteSlug ? "" : $" (site {slug})";
        try
        {
            await new SiteAlertEventBus(_alerts, slug).PublishAsync(new AlertEvent
            {
                EventType = DeviceRestartAlertTypes.Failed,
                Source = "device",
                Severity = AlertSeverity.Warning,
                Title = $"Restart failed: {request.DeviceName}{suffix}",
                Message = request.StatusReason ?? "The restart did not complete.",
                DeviceId = request.DeviceMac,
                DeviceName = request.DeviceName,
                SourceUrl = "/",
                Tags = ["device", "restart"],
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Device restart {Site}: could not publish the failure alert", slug);
        }
    }

    private void Audit(string slug, DeviceRestartRequest request, string outcome, string step, string? reason) =>
        _audit.Log(AuditEventBuilder.FromSystem(
            AuditCategories.Action, AuditActions.DeviceRestartRun, outcome,
            targetType: "device", targetId: request.DeviceMac, targetName: request.DeviceName,
            siteSlug: slug == SiteManagementService.DefaultSiteSlug ? null : slug,
            details: new
            {
                step,
                method = request.Method?.ToString(),
                requestedMethod = request.RequestedMethod.ToString(),
                mode = request.Mode.ToString(),
                attempt = request.Attempts,
                poeSwitch = request.PoeSwitchMac,
                poePort = request.PoePortIdx,
                requestedBy = request.RequestedByName,
                reason,
            }));

    private static string Trim(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length <= 480 ? t : t[..480];
    }
}

/// <summary>Alert event types the device restart executor publishes. Each has a seeded rule in DefaultAlertRules.</summary>
public static class DeviceRestartAlertTypes
{
    /// <summary>A requested restart did not complete.</summary>
    public const string Failed = "device.restart_failed";
}
