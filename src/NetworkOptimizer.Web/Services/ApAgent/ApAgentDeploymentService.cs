using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Deploys and supervises the AP Agent on one site's access points.
///
/// The agent is ephemeral: it lives in tmpfs, so an AP loses it on every reboot and every firmware
/// upgrade, and redeploy is the normal path rather than a repair. Two triggers drive it, because
/// either alone is insufficient - the reboot signal for fast recovery, and a periodic health poll
/// as the authority, since the reboot signal can silently fail to arrive.
///
/// Modeled on <see cref="WanSteerDeploymentService"/>: a binary-version contract file shared with
/// the Go build, a status probe that gathers everything in one SSH round trip, and an idempotent
/// redeploy.
/// </summary>
public sealed class ApAgentDeploymentService : IApAgentDeploymentService, IDisposable
{
    /// <summary>Per-site setting key: whether AP Agent deployment runs on this site at all.</summary>
    public const string SiteEnabledSettingKey = "ap_agent.enabled";

    /// <summary>
    /// The agent contract version this app ships. Read from the SAME src/apagent/binary-version
    /// file the Go binary embeds, so the app and the deployed agent can never disagree about the
    /// wire shape. To change it, edit that file - not this code.
    /// </summary>
    internal static readonly int ExpectedBinaryVersion = ReadExpectedBinaryVersion();

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Deploys open an SSH session and move ~10 MB, so a site runs a couple at a time, not a fleet.</summary>
    private readonly SemaphoreSlim _deployGate = new(2);

    private readonly ILogger<ApAgentDeploymentService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ApAgentTransferSelector _transferSelector;
    private readonly ApAgentHealthClient _healthClient;
    private readonly ApAgentTargetDirectory _directory;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly NetworkOptimizer.Core.ISiteWorkGate _siteWorkGate;
    private readonly Firmware.RolloutSuppressionRegistry _rolloutSuppression;
    private readonly string _siteSlug;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    private readonly ApAgentRetryPolicy _retry = new();
    private readonly Dictionary<string, ApAgentAssessment> _lastAssessment = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How the binary last crossed the wire per AP, when not over SFTP. In-memory like the assessments.</summary>
    private readonly Dictionary<string, string> _transferNotes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>APs whose in-flight work is a removal rather than a deploy.</summary>
    private readonly HashSet<string> _removing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Firmware last probed per AP, rendered into the transfer-failure detail at display time.</summary>
    private readonly Dictionary<string, string> _lastFirmware = new(StringComparer.OrdinalIgnoreCase);
    private DeviceRebootTracker? _rebootTracker;
    private bool _disposed;

    /// <summary>Creates the service for one site.</summary>
    public ApAgentDeploymentService(
        ILogger<ApAgentDeploymentService> logger,
        IServiceProvider serviceProvider,
        ApAgentTransferSelector transferSelector,
        ApAgentHealthClient healthClient,
        ApAgentTargetDirectory directory,
        ICredentialProtectionService credentialProtection,
        NetworkOptimizer.Core.ISiteWorkGate siteWorkGate,
        Firmware.RolloutSuppressionRegistry rolloutSuppression,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _transferSelector = transferSelector;
        _healthClient = healthClient;
        _directory = directory;
        _credentialProtection = credentialProtection;
        _siteWorkGate = siteWorkGate;
        _rolloutSuppression = rolloutSuppression;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;

        SubscribeToRebootSignal();
    }

    /// <summary>
    /// Whether the supervision loop runs for this site. Set by the registry's reconcile pass, like
    /// every other per-site monitor.
    /// </summary>
    public bool Active { get; set; }

    /// <inheritdoc />
    public Task<int> GetExpectedBinaryVersionAsync() => Task.FromResult(ExpectedBinaryVersion);

    /// <inheritdoc />
    public async Task<bool> IsSiteEnabledAsync()
    {
        using var scope = CreateSiteScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        var setting = await db.SystemSettings.FindAsync(SiteEnabledSettingKey);
        return bool.TryParse(setting?.Value, out var enabled) && enabled;
    }

    /// <inheritdoc />
    public async Task SetSiteEnabledAsync(bool enabled)
    {
        using var scope = CreateSiteScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        var setting = await db.SystemSettings.FindAsync(SiteEnabledSettingKey);
        if (setting == null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = SiteEnabledSettingKey, Value = enabled.ToString() });
        }
        else
        {
            setting.Value = enabled.ToString();
        }
        await db.SaveChangesAsync();
        _directory.Invalidate(_siteSlug);
        _logger.LogInformation("AP Agent deployment {State} for site {Site}", enabled ? "enabled" : "disabled", _siteSlug);

        // Switching off has to reach the access points. Supervision stopping only means nobody is
        // watching any more, which would leave unsupervised agents running on hardware the operator
        // just said to stop using. Best effort: an access point that cannot be reached loses its
        // agent at its next reboot anyway, since nothing here survives one.
        if (!enabled)
        {
            await RemoveFromAllAsync(CancellationToken.None);
            return;
        }

        // Supervision ticks every couple of minutes, so waiting for it means the table sits empty
        // right after the operator switches this on. Run one pass now, off the request thread so
        // the toggle returns immediately.
        _ = Task.Run(async () =>
        {
            try { await SuperviseAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "First supervision pass failed for site {Site}", _siteSlug);
            }
        });
    }

    /// <summary>
    /// Removes the agent from every access point that has one, for the site-wide off switch.
    /// Failures are logged rather than thrown: one unreachable access point must not stop the rest
    /// from being cleaned up, and the setting has already been saved.
    /// </summary>
    private async Task RemoveFromAllAsync(CancellationToken ct)
    {
        Dictionary<string, ApAgentDeployment> records;
        try { records = await LoadRecordsAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AP Agent records could not be read while disabling site {Site}", _siteSlug);
            return;
        }

        var deployed = records.Values.Where(r => !string.IsNullOrEmpty(r.DeployedVersion)).ToList();
        if (deployed.Count == 0) return;

        _logger.LogInformation("Removing the AP Agent from {Count} access point(s) on site {Site}",
            deployed.Count, _siteSlug);

        foreach (var record in deployed)
        {
            try
            {
                var result = await RemoveAsync(record.DeviceMac, ct);
                if (!result.Success)
                {
                    _logger.LogWarning("AP Agent could not be removed from {Mac} on site {Site}: {Error}",
                        record.DeviceMac, _siteSlug, result.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AP Agent removal failed for {Mac} on site {Site}", record.DeviceMac, _siteSlug);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApAgentFleetEntry>> GetFleetAsync(CancellationToken ct = default)
    {
        var accessPoints = await GetAccessPointsAsync(ct);
        var records = await LoadRecordsAsync(ct);
        var fleet = new List<ApAgentFleetEntry>(accessPoints.Count);

        // Assessments live in memory and only supervision writes them, so a restarted server shows
        // an empty table until the first pass lands. Fill the gaps here rather than rendering
        // Unknown for minutes: only for access points with nothing recorded, so a warm cache costs
        // nothing.
        await AssessMissingAsync(accessPoints, records, ct);

        foreach (var ap in accessPoints)
        {
            var mac = NormalizeMac(ap.Mac);
            records.TryGetValue(mac, out var record);
            ApAgentAssessment? assessment;
            lock (_lastAssessment) _lastAssessment.TryGetValue(mac, out assessment);
            string? transferNote;
            lock (_transferNotes) _transferNotes.TryGetValue(mac, out transferNote);
            string? firmware;
            lock (_lastFirmware) _lastFirmware.TryGetValue(mac, out firmware);
            bool removing;
            lock (_removing) removing = _removing.Contains(mac);

            fleet.Add(new ApAgentFleetEntry
            {
                DeviceMac = mac,
                DeviceName = ap.Name,
                Model = ap.FriendlyModelName,
                Host = ap.DisplayIpAddress,
                DeviceOnline = IsOnline(ap),
                Enabled = record?.Enabled ?? true,
                DeployInProgress = _retry.IsWorkInFlight(mac) && !removing,
                RemoveInProgress = removing,
                State = assessment?.State ?? ApAgentState.Unknown,
                RecommendedAction = assessment?.Action ?? ApAgentAction.None,
                Detail = assessment?.Detail,
                Architecture = record?.Architecture,
                Firmware = firmware,
                TransferNote = transferNote,
                DeployedVersion = record?.DeployedVersion,
                DeployedBinaryVersion = record?.DeployedBinaryVersion,
                LastDeployedAt = record?.LastDeployedAt,
                LastHealthyAt = record?.LastHealthyAt,
                LastError = record?.LastError,
                NextAttemptAt = _retry.NextAttemptAt(mac),
                ConsecutiveFailures = _retry.ConsecutiveFailures(mac),
            });
        }

        return fleet;
    }

    /// <inheritdoc />
    public async Task<ApAgentSshStatus> GetStatusAsync(string deviceMac, CancellationToken ct = default)
    {
        var ap = await FindAccessPointAsync(deviceMac, ct);
        if (ap == null)
            return new ApAgentSshStatus { Reachable = false, Error = "No access point with that MAC on this site." };

        return await ProbeStatusAsync(TargetFor(ap), ct);
    }

    /// <inheritdoc />
    public async Task<ApAgentAssessment> CheckHealthAsync(string deviceMac, CancellationToken ct = default)
    {
        var ap = await FindAccessPointAsync(deviceMac, ct);
        if (ap == null)
            return new ApAgentAssessment(ApAgentState.Unknown, ApAgentAction.None, "No access point with that MAC on this site.");

        var record = await GetOrCreateRecordAsync(NormalizeMac(ap.Mac), ap.Name, ct);
        return await AssessAsync(ap, record, ct);
    }

    /// <inheritdoc />
    public async Task<ApAgentCapabilityReport?> GetCapabilitiesAsync(string deviceMac, CancellationToken ct = default)
    {
        var ap = await FindAccessPointAsync(deviceMac, ct);
        if (ap == null) return null;

        var record = await GetOrCreateRecordAsync(NormalizeMac(ap.Mac), ap.Name, ct);
        return await _healthClient.GetCapabilitiesAsync(
            _siteSlug, ap.DisplayIpAddress, ResolveToken(record), HealthTimeout, ct);
    }

    /// <inheritdoc />
    public async Task<ApAgentOperationResult> DeployAsync(
        string deviceMac, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!_siteWorkGate.IsSiteOperational(_siteSlug))
            return ApAgentOperationResult.Fail("This site is license-restricted; AP Agent deployment is unavailable.");

        var mac = NormalizeMac(deviceMac);

        // The in-flight guard is the point, not an optimization: a supervision tick landing on top
        // of a slow deploy would open a second SSH session and a second transfer to the same AP.
        using var claim = _retry.TryBeginWork(mac);
        if (claim == null)
            return ApAgentOperationResult.InProgress();

        await _deployGate.WaitAsync(ct);
        try
        {
            return await DeployCoreAsync(mac, progress, ct);
        }
        finally
        {
            _deployGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<ApAgentOperationResult> RestartAsync(string deviceMac, CancellationToken ct = default)
        => RestartCoreAsync(deviceMac, repushToken: false, ct);

    /// <summary>
    /// Restarts the agent already on the AP. With <paramref name="repushToken"/>, first mints a new
    /// token and rewrites the support files: the agent reads its token only from the init script
    /// (procd) or the token file, both written at deploy, so a plain restart keeps the rejected one.
    /// </summary>
    private async Task<ApAgentOperationResult> RestartCoreAsync(string deviceMac, bool repushToken, CancellationToken ct)
    {
        var ap = await FindAccessPointAsync(deviceMac, ct);
        if (ap == null) return ApAgentOperationResult.Fail("No access point with that MAC on this site.");

        var mac = NormalizeMac(ap.Mac);
        using var claim = _retry.TryBeginWork(mac);
        if (claim == null)
            return ApAgentOperationResult.InProgress();

        var target = TargetFor(ap);
        var status = await ProbeStatusAsync(target, ct);
        if (!status.Reachable)
            return ApAgentOperationResult.Fail(status.Error ?? "Could not reach the access point over SSH.");

        if (!status.BinaryDeployed)
            return ApAgentOperationResult.Fail("The agent is not on this access point; deploy it instead.");

        // The token changes under callers on a repush, as in a deploy; keep them off it meanwhile.
        using var hold = repushToken ? _directory.HoldDuringDeploy(_siteSlug, mac) : null;

        await RunAsync(target, ApAgentScripts.StopCommand(status.ProcdAvailable), ct);

        string token;
        if (repushToken)
        {
            token = await RotateTokenAsync(mac, ct);
            var wrote = await WriteSupportFilesAsync(target, token, status.ProcdAvailable, ct);
            if (!wrote.Success)
            {
                await RecordFailureAsync(mac, wrote.Error, ct);
                return wrote;
            }
        }
        else
        {
            token = ResolveToken(await GetOrCreateRecordAsync(mac, ap.Name, ct));
        }

        var started = await StartAgentAsync(target, token, status.ProcdAvailable, ct);

        if (!started.Success)
        {
            await RecordFailureAsync(mac, started.Error ?? "The agent did not start.", ct);
            return started;
        }

        // Same reason as after a deploy: the row otherwise keeps the verdict that caused the
        // restart until the next supervision pass, even once the agent is healthy.
        var verified = await VerifyStartedAsync(ap, mac, ct);
        if (!await RecordVerificationAsync(mac, verified, ct))
        {
            _logger.LogWarning("AP Agent restarted on {Host} on site {Site} but failed verification: {State} - {Detail}",
                ap.DisplayIpAddress, _siteSlug, verified.State, verified.Detail);
            return ApAgentOperationResult.Fail(verified.Detail, verified.State);
        }

        return ApAgentOperationResult.Ok(verified.State);
    }

    /// <inheritdoc />
    public async Task<ApAgentOperationResult> RemoveAsync(string deviceMac, CancellationToken ct = default)
    {
        var ap = await FindAccessPointAsync(deviceMac, ct);
        if (ap == null) return ApAgentOperationResult.Fail("No access point with that MAC on this site.");

        var mac = NormalizeMac(ap.Mac);
        using var claim = _retry.TryBeginWork(mac);
        if (claim == null)
            return ApAgentOperationResult.InProgress();

        // The claim is shared with deploys, so this is what tells the table which one is running.
        // Held to the end: cleared any earlier, the row reads as Deploying while the claim lasts.
        lock (_removing) _removing.Add(mac);
        try
        {
            return await RemoveCoreAsync(ap, mac, ct);
        }
        finally
        {
            lock (_removing) _removing.Remove(mac);
        }
    }

    private async Task<ApAgentOperationResult> RemoveCoreAsync(DiscoveredDevice ap, string mac, CancellationToken ct)
    {
        var target = TargetFor(ap);
        var status = await ProbeStatusAsync(target, ct);
        var result = await RunAsync(target, ApAgentScripts.RemoveCommand(status.ProcdAvailable), ct);

        if (!result.success)
            return ApAgentOperationResult.Fail($"Failed to remove the agent: {result.output}");

        await UpdateRecordAsync(mac, r =>
        {
            r.DeployedVersion = null;
            r.DeployedBinaryVersion = null;
            r.LastDeployedAt = null;
            r.LastError = null;
        }, ct);

        _retry.Forget(mac);
        lock (_lastAssessment) _lastAssessment.Remove(mac);
        lock (_transferNotes) _transferNotes.Remove(mac);
        _directory.Invalidate(_siteSlug);

        _logger.LogInformation("AP Agent removed from {Host} on site {Site}", ap.DisplayIpAddress, _siteSlug);
        return ApAgentOperationResult.Ok(ApAgentState.Unknown);
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(string deviceMac, bool enabled, CancellationToken ct = default)
    {
        var mac = NormalizeMac(deviceMac);
        await UpdateRecordAsync(mac, r => r.Enabled = enabled, ct);
        if (!enabled) _retry.Forget(mac);
        _directory.Invalidate(_siteSlug);
    }

    /// <summary>
    /// One supervision pass over the site's access points: probe each agent, and take the single
    /// action its state warrants. Called by the registry, so it is deliberately absent from the
    /// gated interface.
    /// </summary>
    public async Task SuperviseAsync(CancellationToken ct)
    {
        if (!Active || !_siteWorkGate.IsSiteOperational(_siteSlug)) return;
        if (!await IsSiteEnabledAsync()) return;

        var accessPoints = await GetAccessPointsAsync(ct);
        var records = await LoadRecordsAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var ap in accessPoints)
        {
            if (ct.IsCancellationRequested) return;

            var mac = NormalizeMac(ap.Mac);
            records.TryGetValue(mac, out var record);
            if (record is { Enabled: false }) continue;

            // A firmware rollout holds the agent off an AP that is mid-upgrade. The hold lapses on
            // its own if the rollout stops refreshing it, and the next pass redeploys as normal.
            if (_rolloutSuppression.IsAgentHeld(_siteSlug, mac, now)) continue;

            // The stagger: a server restart must not fire a simultaneous transfer at every AP on
            // the site, so each one waits out its own offset before its first pass.
            if (now - _startedAtUtc < ApAgentRetryPolicy.StaggerOffset(mac)) continue;

            if (_retry.IsWorkInFlight(mac) || !_retry.IsReady(mac, now)) continue;

            try
            {
                record ??= await GetOrCreateRecordAsync(mac, ap.Name, ct);
                var assessment = await AssessAsync(ap, record, ct);
                await ActOnAsync(ap, assessment, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AP Agent supervision failed for {Host} on site {Site}", ap.DisplayIpAddress, _siteSlug);
            }
        }
    }

    /// <summary>Probes one AP and classifies what it found, caching the verdict for the fleet table.</summary>
    /// <summary>
    /// Probes access points that have no assessment yet, so a cold cache does not read as "nothing
    /// is running". Bounded: it skips anything a deploy is already working on, and one slow probe
    /// cannot hold up the table.
    /// </summary>
    private async Task AssessMissingAsync(
        IReadOnlyList<DiscoveredDevice> accessPoints,
        Dictionary<string, ApAgentDeployment> records,
        CancellationToken ct)
    {
        var missing = new List<(DiscoveredDevice Ap, ApAgentDeployment Record)>();

        foreach (var ap in accessPoints)
        {
            var mac = NormalizeMac(ap.Mac);
            if (!records.TryGetValue(mac, out var record) || !record.Enabled) continue;

            bool known;
            lock (_lastAssessment) known = _lastAssessment.ContainsKey(mac);
            if (!known) missing.Add((ap, record));
        }

        if (missing.Count == 0) return;

        using var cold = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cold.Token);

        foreach (var (ap, record) in missing)
        {
            if (linked.IsCancellationRequested) return;

            var mac = NormalizeMac(ap.Mac);
            using var claim = _retry.TryBeginWork(mac);
            if (claim == null) continue;

            try
            {
                var assessment = await AssessAsync(ap, record, linked.Token);
                lock (_lastAssessment) _lastAssessment[mac] = assessment;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cold assessment failed for {Mac} on site {Site}", mac, _siteSlug);
            }
        }
    }

    private async Task<ApAgentAssessment> AssessAsync(DiscoveredDevice ap, ApAgentDeployment record, CancellationToken ct)
    {
        var supported = record.Architecture == null || ApAgentScripts.SupportsArchitecture(record.Architecture);
        var observation = await _healthClient.ProbeAsync(
            _siteSlug, ap.DisplayIpAddress, ResolveToken(record), IsOnline(ap), supported,
            ExpectedBinaryVersion, HealthTimeout, ct);

        var assessment = ApAgentHealthClassifier.Classify(observation with
        {
            Detail = observation.Detail ?? (supported ? null : ApAgentScripts.UnsupportedReason(record.Architecture)),
        });

        lock (_lastAssessment) _lastAssessment[record.DeviceMac] = assessment;

        if (assessment.State == ApAgentState.Healthy || assessment.State == ApAgentState.OutOfDate)
        {
            _retry.RecordSuccess(record.DeviceMac);
            await UpdateRecordAsync(record.DeviceMac, r =>
            {
                r.LastHealthyAt = DateTime.UtcNow;
                r.LastError = null;
                if (observation.Health is { } health)
                {
                    r.DeployedVersion = health.Version;
                    r.DeployedBinaryVersion = health.BinaryVersion;
                }
            }, ct);
        }

        return assessment;
    }

    /// <summary>How long a fresh agent gets to bind its port: it runs its capability probes first.</summary>
    private static readonly TimeSpan VerifyBindWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// One signed health probe against a freshly started agent, so the deploy reports what a real
    /// caller will get. A refusal inside the bind window is the agent still probing, not a failure.
    /// </summary>
    private async Task<ApAgentAssessment> VerifyStartedAsync(DiscoveredDevice ap, string mac, CancellationToken ct)
    {
        // Read fresh: after a deploy, the record holds the token it just rotated.
        var record = await GetOrCreateRecordAsync(mac, ap.Name, ct);
        var deadline = DateTime.UtcNow + VerifyBindWindow;
        while (true)
        {
            var assessment = await AssessAsync(ap, record, ct);
            if (assessment.State != ApAgentState.NotListening || DateTime.UtcNow >= deadline)
                return assessment;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>
    /// Records a post-start verification against the retry policy. True when the agent answers, or
    /// when no probe was sent (ApOffline: the console's device list says offline), so there is no
    /// verdict to fail on. A clock problem is not backed off: nothing on our side fixes it.
    /// </summary>
    private async Task<bool> RecordVerificationAsync(string mac, ApAgentAssessment verified, CancellationToken ct)
    {
        if (verified.State is ApAgentState.Healthy or ApAgentState.OutOfDate or ApAgentState.ApOffline or ApAgentState.Unknown)
        {
            _retry.RecordSuccess(mac);
            return true;
        }

        await RecordFailureAsync(mac, verified.Detail, ct, backOff: verified.State != ApAgentState.ClockSkewed);
        return false;
    }

    /// <summary>Carries out the one action an assessment warrants, and nothing else.</summary>
    private async Task ActOnAsync(DiscoveredDevice ap, ApAgentAssessment assessment, CancellationToken ct)
    {
        var mac = NormalizeMac(ap.Mac);

        switch (assessment.Action)
        {
            case ApAgentAction.Redeploy:
            case ApAgentAction.Upgrade:
                AuditSupervisorAction(ap, AuditActions.ApAgentDeployed, assessment, await DeployAsync(mac, null, ct));
                break;

            case ApAgentAction.RepushConfig:
                AuditSupervisorAction(ap, AuditActions.ApAgentRestarted, assessment, await RestartCoreAsync(mac, repushToken: true, ct));
                break;

            case ApAgentAction.RestartInPlace:
                AuditSupervisorAction(ap, AuditActions.ApAgentRestarted, assessment, await RestartAsync(mac, ct));
                break;

            case ApAgentAction.SurfacePathProblem:
                // Nothing to deploy into a blocked path: SSH is filtered the same way, so the only
                // useful output is the named cause on the AP's row.
                await RecordFailureAsync(mac, assessment.Detail, ct);
                break;

            case ApAgentAction.SurfaceClockProblem:
                await RecordFailureAsync(mac, assessment.Detail, ct, backOff: false);
                break;

            case ApAgentAction.Wait:
            case ApAgentAction.None:
                break;
        }
    }

    /// <summary>
    /// Audits a deploy or restart the supervisor started on its own. It calls this instance directly,
    /// not through the gated interface, so the user-action audit never sees it.
    /// </summary>
    private void AuditSupervisorAction(
        DiscoveredDevice ap, string action, ApAgentAssessment assessment, ApAgentOperationResult result)
    {
        if (result.AlreadyInProgress) return;
        _serviceProvider.GetService<Auditing.IAuditLogger>()?.Log(Auditing.AuditEventBuilder.FromSystem(
            AuditCategories.Action, action,
            outcome: result.Success ? AuditOutcomes.Success : AuditOutcomes.Failure,
            targetType: "ap", targetId: NormalizeMac(ap.Mac), targetName: ap.Name,
            siteSlug: _siteSlug,
            details: new { reason = assessment.Action.ToString(), error = result.Error }));
    }

    private async Task<ApAgentOperationResult> DeployCoreAsync(string mac, IProgress<string>? progress, CancellationToken ct)
    {
        var ap = await FindAccessPointAsync(mac, ct);
        if (ap == null) return ApAgentOperationResult.Fail("No access point with that MAC on this site.");

        progress?.Report("Checking the access point...");
        var target = TargetFor(ap);
        var status = await ProbeStatusAsync(target, ct);
        if (!status.Reachable)
        {
            var error = status.Error ?? "Could not reach the access point over SSH.";
            await RecordFailureAsync(mac, error, ct);
            return ApAgentOperationResult.Fail(error);
        }

        await UpdateRecordAsync(mac, r => r.Architecture = status.Machine, ct);

        if (!string.IsNullOrEmpty(status.Firmware))
            lock (_lastFirmware) _lastFirmware[mac] = status.Firmware;

        if (!status.SupportedArchitecture)
        {
            var reason = ApAgentScripts.UnsupportedReason(status.Machine, status.ByteOrder);
            lock (_lastAssessment) _lastAssessment[mac] = new ApAgentAssessment(ApAgentState.Unsupported, ApAgentAction.None, reason);
            await RecordFailureAsync(mac, reason, ct, backOff: false);
            return ApAgentOperationResult.Fail(reason, ApAgentState.Unsupported);
        }

        // Past the architecture gate, so BinaryName is set: it is what made the AP supported.
        var localPath = Path.Combine(AppContext.BaseDirectory, "tools", status.BinaryName!);
        if (!File.Exists(localPath))
        {
            _logger.LogWarning("AP Agent binary not found at {Path}", localPath);
            // Backed off like every other failure here: the supervisor would otherwise SSH into the AP
            // and write an audit row every tick until someone builds the binary.
            var missing = $"The AP Agent binary for {status.Machine} is not included in this build.";
            await RecordFailureAsync(mac, missing, ct);
            return ApAgentOperationResult.Fail(missing);
        }

        await GetOrCreateRecordAsync(mac, ap.Name, ct);

        // Idempotent: the same binary already running is the common case on a supervision tick, and
        // re-pushing 10 MB to prove it would be the expensive way to learn nothing.
        var localMd5 = ComputeMd5(localPath);
        var binaryIsCurrent = string.Equals(localMd5, status.BinaryMd5, StringComparison.OrdinalIgnoreCase);
        if (binaryIsCurrent && status.IsRunning && status.DeployedBinaryVersion >= ExpectedBinaryVersion)
        {
            _retry.RecordSuccess(mac);
            return ApAgentOperationResult.Ok();
        }

        // Past the idempotent return, so this covers real deploys only. The agent is down and its
        // token is about to change; holding it out of the directory keeps callers off it throughout.
        using var hold = _directory.HoldDuringDeploy(_siteSlug, mac);

        progress?.Report("Stopping any running agent...");
        await RunAsync(target, ApAgentScripts.StopCommand(status.ProcdAvailable), ct);

        if (!binaryIsCurrent)
        {
            progress?.Report("Transferring the agent...");
            var transferred = await TransferBinaryAsync(mac, target, localPath, localMd5, status, ct);
            if (!transferred.Success)
            {
                if (transferred.State == ApAgentState.TransferFailed)
                    lock (_lastAssessment) _lastAssessment[mac] = new ApAgentAssessment(ApAgentState.TransferFailed, ApAgentAction.None, transferred.Error!);
                await RecordFailureAsync(mac, transferred.Error, ct);
                return transferred;
            }
        }

        var token = await RotateTokenAsync(mac, ct);

        progress?.Report("Writing the service definition...");
        var wrote = await WriteSupportFilesAsync(target, token, status.ProcdAvailable, ct);
        if (!wrote.Success)
        {
            await RecordFailureAsync(mac, wrote.Error, ct);
            return wrote;
        }

        progress?.Report("Starting the agent...");
        var started = await StartAgentAsync(target, token, status.ProcdAvailable, ct);
        if (!started.Success)
        {
            await RecordFailureAsync(mac, started.Error, ct);
            return started;
        }

        var after = await ProbeStatusAsync(target, ct);
        await UpdateRecordAsync(mac, r =>
        {
            r.DeviceName = ap.Name;
            r.LastDeployedAt = DateTime.UtcNow;
            r.DeployedVersion = after.Version;
            r.DeployedBinaryVersion = after.DeployedBinaryVersion;
            r.LastError = null;
        }, ct);

        // A running process proves nothing about auth: only a signed request does. Without this, a
        // deploy onto an AP that rejects every signature showed green until the first real call.
        progress?.Report("Verifying the agent...");
        var verified = await VerifyStartedAsync(ap, mac, ct);
        _directory.Invalidate(_siteSlug);

        if (!await RecordVerificationAsync(mac, verified, ct))
        {
            _logger.LogWarning("AP Agent deployed to {Host} on site {Site} but failed verification: {State} - {Detail}",
                ap.DisplayIpAddress, _siteSlug, verified.State, verified.Detail);
            return ApAgentOperationResult.Fail(verified.Detail, verified.State);
        }

        _logger.LogInformation("AP Agent deployed to {Host} on site {Site} (version {Version})",
            ap.DisplayIpAddress, _siteSlug, after.Version ?? "unknown");
        progress?.Report("AP Agent deployed.");
        return ApAgentOperationResult.Ok();
    }

    /// <summary>
    /// Pushes the binary over the probed transfer chain, attempting each method in order until one
    /// both uploads and verifies. The md5 comparison is the only integrity check in the path:
    /// without it a truncated binary surfaces as "The agent did not start" and burns a retry cycle
    /// before the next pass's md5 catches it.
    /// </summary>
    private async Task<ApAgentOperationResult> TransferBinaryAsync(
        string mac, DeviceSshTarget target, string localPath, string localMd5, ApAgentSshStatus status, CancellationToken ct)
    {
        var host = target.Host;
        var connection = await BuildConnectionAsync(target, ct);
        if (connection == null)
            return ApAgentOperationResult.Fail(await Router.ResolveAsync(target, ct) == SshCredentialRoute.DeviceCredentials
                ? "Device SSH credentials are not configured for this site."
                : "Gateway SSH credentials are not configured for this site.");

        var mkdir = await RunAsync(target, $"mkdir -p {ApAgentPaths.RemoteDir}", ct);
        if (!mkdir.success)
            return ApAgentOperationResult.Fail($"Could not create the install directory: {mkdir.output}");

        var remotePath = ApAgentPaths.RemoteBinaryPath(status.BinaryName!);
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var transfer in _transferSelector.Resolve(status))
        {
            string? error = null;
            try
            {
                await transfer.UploadAsync(connection, localPath, remotePath, ct);

                var check = await RunAsync(target, $"md5sum {remotePath} 2>/dev/null | cut -d' ' -f1", ct);
                if (!check.success || !string.Equals(check.output.Trim(), localMd5, StringComparison.OrdinalIgnoreCase))
                    error = "The copied file did not match the original (md5 mismatch).";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error != null)
            {
                failures[transfer.Name] = error;
                _logger.LogDebug("AP Agent transfer to {Host} over {Method} failed: {Error}", host, transfer.Name, error);
                continue;
            }

            var chmod = await RunAsync(target, $"chmod +x {remotePath}", ct);
            if (!chmod.success)
                return ApAgentOperationResult.Fail($"Could not make the agent executable: {chmod.output}");

            // Information rather than Debug on a non-default method, so an AP sitting on a slower
            // path is visible in the log rather than silently slow.
            if (transfer.Name != ApAgentTransferSelector.SftpMethod)
                _logger.LogInformation("AP Agent transferred to {Host} over {Method} on site {Site}", host, transfer.Name, _siteSlug);

            var note = TransferNoteFor(transfer.Name, status);
            lock (_transferNotes)
            {
                if (note == null) _transferNotes.Remove(mac);
                else _transferNotes[mac] = note;
            }

            return ApAgentOperationResult.Ok();
        }

        var detail = string.Join("\n",
            "Could not copy the AP Agent to this access point.",
            "- SFTP: " + (failures.TryGetValue(ApAgentTransferSelector.SftpMethod, out var sftpError) ? sftpError : "not available on this firmware"),
            "- SCP: " + (failures.TryGetValue(ApAgentTransferSelector.ScpMethod, out var scpError) ? scpError : "not available on this firmware"),
            "- Direct copy: " + failures[ApAgentTransferSelector.ExecMethod]);
        _logger.LogWarning("AP Agent transfer to {Host} on site {Site} failed over every method: {Detail}", host, _siteSlug, detail);
        return ApAgentOperationResult.Fail(detail, ApAgentState.TransferFailed);
    }

    /// <summary>
    /// The detail-panel note for a deploy that did not go over SFTP. Curated copy - do not reword.
    /// "doesn't support" is only true of a method whose binary was genuinely absent; one that was
    /// present and failed anyway was refused.
    /// </summary>
    private static string? TransferNoteFor(string method, ApAgentSshStatus status)
    {
        if (method == ApAgentTransferSelector.SftpMethod) return null;

        bool unsupported;
        string note;
        if (method == ApAgentTransferSelector.ScpMethod)
        {
            unsupported = !status.SftpAvailable;
            note = unsupported
                ? "Copied over SCP: this firmware doesn't support SFTP."
                : "Copied over SCP: SFTP was refused.";
        }
        else
        {
            unsupported = !status.SftpAvailable && !status.ScpAvailable;
            note = unsupported
                ? "Copied directly: this firmware doesn't support SFTP or SCP."
                : "Copied directly: SFTP and SCP were refused.";
        }

        // ARM plus pre-8 firmware is a U6-class AP without needing a model list; the model is never
        // a decision input here. The hint describes ARM U6 hardware; on a MIPS AP it would mislead.
        if (unsupported && status.BinaryName == ApAgentPaths.BinaryPrefix + "arm" && FirmwareMajorBelow8(status.Firmware))
            note += " U6 gets SFTP on the 8.8.5+ shared U7 stack firmware.";
        return note;
    }

    private static bool FirmwareMajorBelow8(string? firmware)
    {
        var shortForm = FirmwareVersionFormat.ShortOrNull(firmware);
        if (shortForm == null) return false;
        return int.TryParse(shortForm.Split('.')[0], out var major) && major < 8;
    }

    private async Task<ApAgentOperationResult> WriteSupportFilesAsync(DeviceSshTarget target, string token, bool procdAvailable, CancellationToken ct)
    {
        var commands = new List<string>
        {
            ApAgentScripts.WriteFileCommand(WrapperScript(), ApAgentPaths.RemoteWrapperPath, "755"),
            ApAgentScripts.WriteFileCommand(token + "\n", ApAgentPaths.RemoteTokenPath, "600"),
        };

        if (procdAvailable)
            commands.Add(ApAgentScripts.WriteFileCommand(ApAgentScripts.InitScript(token), ApAgentPaths.RemoteInitScriptPath, "755"));

        var result = await RunAsync(target, string.Join(" && ", commands), ct);
        return result.success
            ? ApAgentOperationResult.Ok()
            : ApAgentOperationResult.Fail($"Could not write the agent's support files: {result.output}");
    }

    private async Task<ApAgentOperationResult> StartAgentAsync(DeviceSshTarget target, string token, bool procdAvailable, CancellationToken ct)
    {
        var result = await RunAsync(target, ApAgentScripts.StartCommand(procdAvailable), ct);
        if (result.success && result.output.Contains("started", StringComparison.Ordinal))
            return ApAgentOperationResult.Ok();

        var detail = result.output.Trim();
        return ApAgentOperationResult.Fail(string.IsNullOrEmpty(detail)
            ? "The agent did not start."
            : $"The agent did not start: {detail}");
    }

    private async Task<ApAgentSshStatus> ProbeStatusAsync(DeviceSshTarget target, CancellationToken ct)
    {
        var result = await RunAsync(target, ApAgentScripts.StatusProbeCommand(), ct);
        return ApAgentScripts.ParseStatus(result.output, result.success);
    }

    /// <summary>The device's SSH identity for the shared router, which picks its credentials and host.</summary>
    private static DeviceSshTarget TargetFor(DiscoveredDevice device) => DeviceSshTarget.From(device);

    private DeviceSshRouter Router => _serviceProvider.GetRequiredService<DeviceSshRouterRegistry>().GetFor(_siteSlug);

    private Task<(bool success, string output)> RunAsync(DeviceSshTarget target, string command, CancellationToken ct)
        => Router.RunAsync(target, command, SshTimeout, ct);

    /// <summary>The connection the file transfer dials, on the same route the commands take.</summary>
    private Task<SshConnectionInfo?> BuildConnectionAsync(DeviceSshTarget target, CancellationToken ct)
        => Router.GetConnectionAsync(target, ct);

    /// <summary>
    /// Subscribes to the existing reboot tracker rather than watching uptime again. This is the
    /// proactive half: an AP that just came back has lost its agent, and the tracker already knows.
    /// </summary>
    private void SubscribeToRebootSignal()
    {
        try
        {
            _rebootTracker = _serviceProvider.GetRequiredService<DeviceRebootRegistry>().GetFor(_siteSlug);
            _rebootTracker.DeviceRebooted += OnDeviceRebooted;
        }
        catch (Exception ex)
        {
            // The health poll is the authority, so losing this trigger costs recovery speed, not
            // correctness. Never let it take the service down with it.
            _logger.LogWarning(ex, "AP Agent could not subscribe to the reboot signal for site {Site}", _siteSlug);
        }
    }

    private void OnDeviceRebooted(DeviceRebootTracker.DeviceBootEvent boot)
    {
        if (boot.DeviceType is not (DeviceType.AccessPoint or DeviceType.Gateway)) return;

        var mac = NormalizeMac(boot.DeviceMac);
        _retry.RecordSuccess(mac);
        _logger.LogInformation("Access point {Mac} rebooted; the AP Agent will be redeployed on the next pass", mac);
    }

    private async Task<List<DiscoveredDevice>> GetAccessPointsAsync(CancellationToken ct)
    {
        var connection = _serviceProvider.GetRequiredService<SiteConnectionRegistry>().GetFor(_siteSlug);
        var devices = await connection.GetDiscoveredDevicesAsync(ct);
        return devices
            .Where(d => UniFiDiscovery.BroadcastsWifi(d) && !string.IsNullOrEmpty(d.DisplayIpAddress))
            .ToList();
    }

    private async Task<DiscoveredDevice?> FindAccessPointAsync(string deviceMac, CancellationToken ct)
    {
        var mac = NormalizeMac(deviceMac);
        var accessPoints = await GetAccessPointsAsync(ct);
        return accessPoints.FirstOrDefault(d => NormalizeMac(d.Mac) == mac);
    }

    private async Task<Dictionary<string, ApAgentDeployment>> LoadRecordsAsync(CancellationToken ct)
    {
        using var scope = CreateSiteScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        var rows = await db.ApAgentDeployments.AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(r => r.DeviceMac, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<ApAgentDeployment> GetOrCreateRecordAsync(string deviceMac, string? deviceName, CancellationToken ct)
    {
        using var scope = CreateSiteScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();

        var record = await db.ApAgentDeployments.FirstOrDefaultAsync(r => r.DeviceMac == deviceMac, ct);
        if (record != null) return record;

        record = new ApAgentDeployment
        {
            DeviceMac = deviceMac,
            DeviceName = deviceName,
            Token = _credentialProtection.Encrypt(GenerateToken()),
        };
        db.ApAgentDeployments.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    private async Task UpdateRecordAsync(string deviceMac, Action<ApAgentDeployment> mutate, CancellationToken ct)
    {
        using var scope = CreateSiteScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();

        var record = await db.ApAgentDeployments.FirstOrDefaultAsync(r => r.DeviceMac == deviceMac, ct);
        if (record == null)
        {
            record = new ApAgentDeployment
            {
                DeviceMac = deviceMac,
                Token = _credentialProtection.Encrypt(GenerateToken()),
            };
            db.ApAgentDeployments.Add(record);
        }

        mutate(record);
        record.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordFailureAsync(string deviceMac, string? error, CancellationToken ct, bool backOff = true)
    {
        if (backOff) _retry.RecordFailure(deviceMac, DateTime.UtcNow);
        await UpdateRecordAsync(deviceMac, r => r.LastError = Truncate(error, 500), ct);
    }

    /// <summary>
    /// Mints the access point a new signing token, stored before it is pushed so a failed write
    /// never leaves the agent serving on a secret nothing recorded. Deploy path only: the agent
    /// reads its token once at startup.
    /// </summary>
    private async Task<string> RotateTokenAsync(string deviceMac, CancellationToken ct)
    {
        var token = GenerateToken();
        await UpdateRecordAsync(deviceMac, r => r.Token = _credentialProtection.Encrypt(token), ct);
        _directory.Invalidate(_siteSlug);
        return token;
    }

    /// <summary>
    /// The AP's signing token, decrypted. A token that cannot be decrypted is replaced rather than
    /// used: the agent refuses anything under 16 characters, so a garbled one would fail every
    /// probe silently.
    /// </summary>
    private string ResolveToken(ApAgentDeployment record)
    {
        if (string.IsNullOrEmpty(record.Token)) return GenerateToken();
        try
        {
            var token = _credentialProtection.Decrypt(record.Token);
            return token.Length >= 16 ? token : GenerateToken();
        }
        catch
        {
            return GenerateToken();
        }
    }

    /// <summary>The architecture-gating wrapper, embedded from the same file the Go build ships.</summary>
    private static string WrapperScript()
    {
        var asm = typeof(ApAgentDeploymentService).Assembly;
        using var stream = asm.GetManifestResourceStream("apagent.apagent.sh")
            ?? throw new InvalidOperationException("The AP Agent wrapper script is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int ReadExpectedBinaryVersion()
    {
        try
        {
            var asm = typeof(ApAgentDeploymentService).Assembly;
            using var stream = asm.GetManifestResourceStream("apagent.binary-version");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                if (int.TryParse(reader.ReadToEnd().Trim(), out var version))
                    return version;
            }
        }
        catch
        {
            // Fall through to the baseline below.
        }

        // A missing resource must not produce a bogus "out of date" verdict against real deployments.
        return 1;
    }

    /// <summary>256 bits of entropy as hex. The agent refuses a token under 16 characters.</summary>
    private static string GenerateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static string ComputeMd5(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    private static bool IsOnline(DiscoveredDevice device) => device.State == 1;

    private static string NormalizeMac(string? mac) => (mac ?? "").Trim().ToLowerInvariant();

    private static string? Truncate(string? value, int max)
        => value != null && value.Length > max ? value[..max] : value;

    /// <summary>
    /// A DI scope pinned to this instance's site, so the scoped DbContext resolves to that site's
    /// database rather than the default site's.
    /// </summary>
    private IServiceScope CreateSiteScope()
    {
        var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        return scope;
    }

    /// <summary>Releases this site's instance. Called by the registry that owns it.</summary>
    public void DisposeOwned() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_rebootTracker != null)
            _rebootTracker.DeviceRebooted -= OnDeviceRebooted;

        _deployGate.Dispose();
    }
}
