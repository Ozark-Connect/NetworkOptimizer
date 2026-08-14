using System.Text.Json;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <inheritdoc />
/// <remarks>
/// Thin over the pieces that already exist: the planner builds the plan, the repository stores it,
/// and the site's executor runs it. The only judgement here is what a plan is allowed to be - one
/// at a time, something to upgrade, and the settings it was planned from persisted before it starts,
/// because the executor reads settings live rather than off the plan.
/// </remarks>
public class FirmwareRolloutService : IFirmwareRolloutService
{
    /// <summary>Least notice a manually planned window has to leave.</summary>
    private static readonly TimeSpan ManualWindowLead = TimeSpan.FromHours(1);

    private readonly IFirmwareRolloutRepository _repository;
    private readonly FirmwareRolloutOrchestrator _orchestrator;
    private readonly IFirmwareCommandClient _commands;
    private readonly IRolloutPlanningSource _planning;
    private readonly IReleaseMetadataSource _releaseMetadata;
    private readonly IAuditContext _audit;
    private readonly ICallerContext _caller;
    private readonly ILogger<FirmwareRolloutService> _logger;

    /// <param name="repository">This site's rollout store.</param>
    /// <param name="orchestrator">This site's executor.</param>
    /// <param name="commands">Firmware command surface (the catalog refresh and console reads).</param>
    /// <param name="planning">Topology, coverage, quiet window and rollback-image sources.</param>
    /// <param name="audit">Audit detail for the gated writes.</param>
    /// <param name="caller">Who is asking, recorded as the plan's author.</param>
    /// <param name="logger">Logger.</param>
    public FirmwareRolloutService(
        IFirmwareRolloutRepository repository,
        FirmwareRolloutOrchestrator orchestrator,
        IFirmwareCommandClient commands,
        IRolloutPlanningSource planning,
        IReleaseMetadataSource releaseMetadata,
        IAuditContext audit,
        ICallerContext caller,
        ILogger<FirmwareRolloutService> logger)
    {
        _repository = repository;
        _orchestrator = orchestrator;
        _commands = commands;
        _planning = planning;
        _releaseMetadata = releaseMetadata;
        _audit = audit;
        _caller = caller;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<FirmwareRolloutSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        _repository.GetSettingsAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<RolloutPlanView?> GetActivePlanAsync(CancellationToken cancellationToken = default)
    {
        var plan = await _repository.GetActivePlanAsync(cancellationToken);
        if (plan == null) return null;

        var document = ParseDocument(plan);
        var steps = await _repository.GetStepsAsync(plan.Id, cancellationToken);

        return new RolloutPlanView
        {
            Id = plan.Id,
            Status = plan.Status,
            CreatedAt = plan.CreatedAt,
            CreatedBy = plan.CreatedBy,
            ScheduledStartAt = plan.ScheduledStartAt,
            StartedAt = plan.StartedAt,
            CompletedAt = plan.CompletedAt,
            Plan = document,
            Steps = steps.Select(s => ToView(s, document)).ToList(),
            HasReport = !string.IsNullOrEmpty(plan.ReportJson),
        };
    }

    /// <inheritdoc />
    public async Task<List<RolloutPlanSummaryView>> GetPlanHistoryAsync(
        int limit = 20, CancellationToken cancellationToken = default)
    {
        var plans = await _repository.GetPlanHistoryAsync(limit, cancellationToken);
        return plans.Select(plan =>
        {
            var document = ParseDocument(plan);
            return new RolloutPlanSummaryView
            {
                Id = plan.Id,
                Status = plan.Status,
                CreatedAt = plan.CreatedAt,
                CreatedBy = plan.CreatedBy,
                ScheduledStartAt = plan.ScheduledStartAt,
                StartedAt = plan.StartedAt,
                CompletedAt = plan.CompletedAt,
                DeviceCount = document.Waves.Sum(w => w.Steps.Count),
                WaveCount = document.Waves.Count,
                HasReport = !string.IsNullOrEmpty(plan.ReportJson),
            };
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<RolloutReportView?> GetReportAsync(int planId, CancellationToken cancellationToken = default)
    {
        var plan = await _repository.GetPlanAsync(planId, cancellationToken);
        if (plan == null) return null;

        var document = ParseDocument(plan);
        var steps = await _repository.GetStepsAsync(plan.Id, cancellationToken);

        return new RolloutReportView
        {
            PlanId = plan.Id,
            Status = plan.Status,
            StartedAt = plan.StartedAt,
            CompletedAt = plan.CompletedAt,
            IsReady = !string.IsNullOrEmpty(plan.ReportJson),
            ReportJson = plan.ReportJson,
            Steps = steps.Select(s => ToView(s, document)).ToList(),
        };
    }

    /// <inheritdoc />
    public async Task<RolloutPreviewView> BuildPreviewAsync(
        FirmwareRolloutSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var (result, context) = await PlanAsync(settings, cancellationToken);
        var document = result.Document;

        var channels = await _commands.GetChannelAvailabilityAsync(cancellationToken);
        var console = await _commands.GetConsoleSystemInfoAsync(cancellationToken);
        var autoUpgrade = await _commands.GetAutoUpgradeEnabledAsync(cancellationToken);
        var active = await _repository.GetActivePlanAsync(cancellationToken);

        var preview = new RolloutPreviewView
        {
            Plan = document,
            Steps = result.Steps.Select(s => ToView(s, document)).ToList(),
            ProposedWindow = await _planning.ProposeWindowAsync(
                context, document.TotalEstimatedSeconds, settings, WindowLead(settings), cancellationToken),
            Channels = channels,
            TotalDeviceCount = context.Devices.Count,
            UpgradableCount = result.Steps.Count(s => s.State != FirmwareRolloutStepState.SkippedExcluded),
            ExcludedCount = result.Steps.Count(s => s.State == FirmwareRolloutStepState.SkippedExcluded),
            Devices = context.Devices.Select(d => new RolloutDeviceView
            {
                Mac = d.Mac,
                Name = string.IsNullOrEmpty(d.Name) ? d.DisplayModel : d.Name,
                Model = d.Model,
                DisplayModel = string.IsNullOrEmpty(d.DisplayModel) ? d.Model : d.DisplayModel,
                DeviceType = FirmwareDeviceTypes.Code(d.Type),
                CurrentVersion = d.FromVersion,
                TargetVersion = d.ToVersion,
                Upgradable = d.Upgradable,
            }).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            ConsoleConnected = context.ConsoleConnected,
            IsStandaloneConsole = console?.IsStandaloneConsole == true,
            // The step only exists where a Cloud Gateway runs the console: a self-hosted console
            // is out of scope, and a UXG-class gateway has network firmware only.
            HasCloudGateway = console?.IsStandaloneConsole == false && context.Devices.Any(d =>
                FirmwareTimingEstimator.Classify(d) == FirmwareDeviceClass.CloudGatewayUniFiOs),
            ConsoleAutoUpgradeEnabled = autoUpgrade == true,
            ConsoleOsAutoUpdateEnabled = console?.Firmware?.AutoUpdate?.IsScheduled == true,
            ConsoleAppsAutoUpdateEnabled = console?.Firmware?.AutoUpdate is { IsScheduled: true, IncludeApplications: true },
            HasActivePlan = active != null,
        };

        AddWarnings(preview, settings);
        await AttachChangelogLinksAsync(preview, cancellationToken);
        return preview;
    }

    /// <inheritdoc />
    public async Task SaveSettingsAsync(FirmwareRolloutSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveSettingsAsync(settings, cancellationToken);

        _audit.SetDetails(new
        {
            mode = settings.Mode.ToString(),
            globalChannel = settings.GlobalChannel,
            networkAppChannel = settings.NetworkAppChannel,
            unifiOsChannel = settings.UniFiOsChannel,
            includeUniFiOs = settings.IncludeUniFiOs,
            includeUniFiNetwork = settings.IncludeUniFiNetwork,
            spacingProfile = settings.SpacingProfile.ToString(),
            perWaveApproval = settings.PerWaveApproval,
            suppressStandardAlerts = settings.SuppressStandardAlerts,
        });
    }

    /// <inheritdoc />
    public async Task<int> SchedulePlanAsync(
        FirmwareRolloutSettings settings, DateTime startAtUtc, CancellationToken cancellationToken = default)
    {
        var plan = await CreatePlanAsync(settings, FirmwareRolloutStatus.Scheduled, startAtUtc, cancellationToken);

        _audit.SetTarget(plan.Id.ToString(), $"Firmware rollout {plan.Id}");
        _audit.SetDetails(new { planId = plan.Id, startAt = startAtUtc, devices = plan.DeviceCount, waves = plan.WaveCount });
        return plan.Id;
    }

    /// <inheritdoc />
    public async Task<int> StartNowAsync(
        FirmwareRolloutSettings settings, bool overrideHealthGate, CancellationToken cancellationToken = default)
    {
        var plan = await CreatePlanAsync(settings, FirmwareRolloutStatus.Draft, startAtUtc: null, cancellationToken);

        // The executor owns the transition to Running: it runs the health gate, the pre-flight
        // backup and the catalog refresh first, and postpones the plan itself if any of those say no.
        var started = await _orchestrator.StartNowAsync(plan.Id, overrideHealthGate, cancellationToken);
        if (!started)
        {
            _logger.LogInformation(
                "Firmware rollout {Id} was created but not started; the executor deferred it", plan.Id);
        }

        _audit.SetTarget(plan.Id.ToString(), $"Firmware rollout {plan.Id}");
        _audit.SetDetails(new
        {
            planId = plan.Id,
            devices = plan.DeviceCount,
            waves = plan.WaveCount,
            overrideHealthGate,
            started,
        });
        return plan.Id;
    }

    /// <inheritdoc />
    public async Task PauseAsync(int planId, CancellationToken cancellationToken = default)
    {
        await RequireActiveAsync(planId, cancellationToken);
        await _orchestrator.PauseAsync(cancellationToken);
        _audit.SetTarget(planId.ToString(), $"Firmware rollout {planId}");
    }

    /// <inheritdoc />
    public async Task ResumeAsync(int planId, CancellationToken cancellationToken = default)
    {
        var plan = await RequireActiveAsync(planId, cancellationToken);
        var waitingWave = ParseDocument(plan).WaitingApprovalWave;

        await _orchestrator.ResumeAsync(cancellationToken);

        _audit.SetTarget(planId.ToString(), $"Firmware rollout {planId}");
        // A resume off a wave boundary IS the per-wave approval, so the audit says which wave.
        if (waitingWave is int wave)
            _audit.SetDetails(new { planId, approvedWave = wave });
    }

    /// <inheritdoc />
    public async Task AbortAsync(int planId, CancellationToken cancellationToken = default)
    {
        await RequireActiveAsync(planId, cancellationToken);
        await _orchestrator.AbortAsync($"{ActorName()} stopped it", cancellationToken);
        _audit.SetTarget(planId.ToString(), $"Firmware rollout {planId}");
    }

    /// <inheritdoc />
    public async Task PostponeAsync(int planId, CancellationToken cancellationToken = default)
    {
        await RequireActiveAsync(planId, cancellationToken);
        if (!await _orchestrator.PostponeAsync(planId, cancellationToken))
            throw new InvalidOperationException("Only a rollout that has not started yet can be postponed.");

        var plan = await _repository.GetPlanAsync(planId, cancellationToken);
        _audit.SetTarget(planId.ToString(), $"Firmware rollout {planId}");
        _audit.SetDetails(new { planId, startAt = plan?.ScheduledStartAt });
    }

    /// <inheritdoc />
    public async Task<bool> RollbackStepAsync(int stepId, CancellationToken cancellationToken = default)
    {
        var accepted = await _orchestrator.RollbackStepAsync(stepId, cancellationToken);
        _audit.SetTarget(stepId.ToString());
        _audit.SetDetails(new { stepId, accepted });
        return accepted;
    }

    /// <summary>
    /// Plans against the live site. The catalog refresh comes first and is not optional: it is
    /// UniFi's own "Check for Updates", and it stages the builds the plan is about to command.
    /// </summary>
    private async Task<(RolloutPlanResult Result, RolloutPlanningContext Context)> PlanAsync(
        FirmwareRolloutSettings settings, CancellationToken cancellationToken)
    {
        var timings = await _repository.GetModelTimingsAsync(cancellationToken);
        var inputs = await RolloutPlanComposer.GatherAsync(_planning, timings, _commands, cancellationToken);
        return (RolloutPlanComposer.Plan(inputs, settings), inputs.Context);
    }

    private async Task<CreatedPlan> CreatePlanAsync(
        FirmwareRolloutSettings settings,
        FirmwareRolloutStatus status,
        DateTime? startAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var active = await _repository.GetActivePlanAsync(cancellationToken);
        if (active != null)
        {
            throw new InvalidOperationException(
                $"Rollout {active.Id} is already {active.Status.ToString().ToLowerInvariant()} on this site. " +
                "Finish or stop it before planning another.");
        }

        // Committing a plan commits the settings it was planned from: the executor reads settings
        // live (suppression, spacing, per-wave approval), so a plan built from unsaved ones would
        // run under whatever was stored instead.
        settings.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveSettingsAsync(settings, cancellationToken);

        var (result, _) = await PlanAsync(settings, cancellationToken);
        var upgrading = result.Steps.Count(s => s.State != FirmwareRolloutStepState.SkippedExcluded);
        if (upgrading == 0)
            throw new InvalidOperationException("Nothing on this site has a firmware update to install.");

        await _planning.PopulatePriorVersionsAsync(result.Document, result.Steps, cancellationToken);

        var plan = await _repository.CreatePlanAsync(new FirmwareRolloutPlan
        {
            Status = status,
            ScheduledStartAt = startAtUtc,
            PlanJson = JsonSerializer.Serialize(result.Document),
            CreatedBy = ActorName(),
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken);

        foreach (var step in result.Steps) step.PlanId = plan.Id;
        await _repository.AddStepsAsync(result.Steps, cancellationToken);

        _logger.LogInformation(
            "Firmware rollout {Id} planned: {Devices} devices in {Waves} waves", plan.Id, upgrading, result.Document.Waves.Count);

        return new CreatedPlan(plan.Id, upgrading, result.Document.Waves.Count);
    }

    private sealed record CreatedPlan(int Id, int DeviceCount, int WaveCount);

    /// <summary>
    /// Guards a control against a stale page: the executor acts on whatever plan is active, so a
    /// button pressed against a plan that has since finished must not move a different one.
    /// </summary>
    private async Task<FirmwareRolloutPlan> RequireActiveAsync(int planId, CancellationToken cancellationToken)
    {
        var active = await _repository.GetActivePlanAsync(cancellationToken);
        if (active == null)
            throw new InvalidOperationException("No firmware rollout is in progress on this site.");
        if (active.Id != planId)
            throw new InvalidOperationException($"Rollout {planId} is not the one in progress on this site.");
        return active;
    }

    /// <summary>
    /// Changelog links for the preview's target versions, GA-resolvable only. The feed caches for
    /// an hour and a miss is fine - a version without a link just renders unlinked.
    /// </summary>
    private async Task AttachChangelogLinksAsync(RolloutPreviewView preview, CancellationToken cancellationToken)
    {
        try
        {
            var byTarget = preview.Steps
                .Where(s => !string.IsNullOrEmpty(s.Model) && !string.IsNullOrEmpty(s.ToVersion))
                .GroupBy(s => (s.Model, s.ToVersion), StringTupleComparer.Instance)
                .Take(40);
            foreach (var group in byTarget)
            {
                var metadata = await _releaseMetadata.GetAsync(group.Key.Model, group.Key.ToVersion, cancellationToken);
                if (metadata?.ChangelogUrl == null) continue;
                foreach (var step in group) step.ChangelogUrl = metadata.ChangelogUrl;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Changelog links unavailable for the rollout preview");
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Model, string? ToVersion)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string Model, string? ToVersion) x, (string Model, string? ToVersion) y) =>
            string.Equals(x.Model, y.Model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ToVersion, y.ToVersion, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Model, string? ToVersion) obj) =>
            HashCode.Combine(
                obj.Model.ToLowerInvariant(),
                obj.ToVersion?.ToLowerInvariant());
    }

    private static void AddWarnings(RolloutPreviewView preview, FirmwareRolloutSettings settings)
    {
        if (!preview.ConsoleConnected)
            preview.Warnings.Add("The UniFi Console is not connected, so this preview may be out of date.");

        if (preview.UpgradableCount == 0)
            preview.Warnings.Add("Nothing on this site has a firmware update to install.");

        // Each UniFi auto-update layer races a rollout in its own way, so name the ones that are on.
        var autoUpdaters = new List<string>();
        if (preview.ConsoleAutoUpgradeEnabled) autoUpdaters.Add("devices");
        if (preview.ConsoleOsAutoUpdateEnabled) autoUpdaters.Add("UniFi OS");
        if (preview.ConsoleAppsAutoUpdateEnabled) autoUpdaters.Add("the applications");
        if (autoUpdaters.Count > 0)
        {
            var list = autoUpdaters.Count switch
            {
                1 => autoUpdaters[0],
                2 => $"{autoUpdaters[0]} and {autoUpdaters[1]}",
                _ => $"{string.Join(", ", autoUpdaters.Take(autoUpdaters.Count - 1))} and {autoUpdaters[^1]}",
            };
            preview.Warnings.Add(
                $"UniFi updates {list} on its own schedule. Rollouts still run; turning that off rules " +
                "out the rare case where both update at once.");
        }

        if (preview.IsStandaloneConsole && settings.IncludeUniFiOs)
        {
            preview.Warnings.Add(
                "This is a self-hosted UniFi OS Server, so its own operating system is left alone. The UniFi Network " +
                "application and your network devices are still covered.");
        }

        if (preview.HasActivePlan)
            preview.Warnings.Add("A rollout is already scheduled or running on this site.");

        if (!preview.Channels.EarlyAccessAvailable &&
            UsesChannel(settings, FirmwareChannels.Beta))
        {
            preview.Warnings.Add("This console does not offer early access builds, so those devices will stay on their current channel.");
        }
    }

    private static bool UsesChannel(FirmwareRolloutSettings settings, string channel) =>
        string.Equals(settings.GlobalChannel, channel, StringComparison.OrdinalIgnoreCase) ||
        (settings.PerDeviceTypeChannelsJson?.Contains(channel, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (settings.PerSkuChannelsJson?.Contains(channel, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Autopilot windows have to leave room for the heads-up alert; manual ones do not.</summary>
    private static TimeSpan WindowLead(FirmwareRolloutSettings settings) =>
        settings.Mode == FirmwareRolloutMode.Autopilot
            ? TimeSpan.FromHours(Math.Max(1, settings.NotifyHoursAhead))
            : ManualWindowLead;

    private static RolloutStepView ToView(FirmwareRolloutStep step, RolloutPlanDocument document)
    {
        var prior = document.PriorVersions
            .FirstOrDefault(p => string.Equals(p.Mac, step.DeviceMac, StringComparison.OrdinalIgnoreCase));
        var upgraded = step.State is FirmwareRolloutStepState.LitmusPassed or FirmwareRolloutStepState.RegressionFlagged;
        var planned = document.Waves
            .SelectMany(w => w.Steps)
            .FirstOrDefault(s => string.Equals(s.Mac, step.DeviceMac, StringComparison.OrdinalIgnoreCase));

        return new RolloutStepView
        {
            Id = step.Id,
            Mac = step.DeviceMac,
            Name = step.DeviceName,
            Model = step.Model,
            DisplayModel = string.IsNullOrEmpty(planned?.DisplayModel)
                ? NetworkOptimizer.UniFi.UniFiProductDatabase.GetBestProductName(step.Model, null)
                : planned.DisplayModel,
            DeviceType = step.DeviceType,
            Channel = step.Channel,
            FromVersion = step.FromVersion,
            ToVersion = step.ToVersion,
            Wave = step.Wave,
            State = step.State,
            CommandedAt = step.CommandedAt,
            WentDownAt = step.WentDownAt,
            BackAt = step.BackAt,
            DowntimeSeconds = step.DowntimeSeconds,
            Error = step.Error,
            CanRollBack = upgraded && !string.IsNullOrEmpty(prior?.Url),
            RollbackUnavailableReason = upgraded && string.IsNullOrEmpty(prior?.Url)
                ? prior?.UnavailableReason ?? "no image was cached for the version this device came from"
                : null,
        };
    }

    private static RolloutPlanDocument ParseDocument(FirmwareRolloutPlan plan)
    {
        try
        {
            return JsonSerializer.Deserialize<RolloutPlanDocument>(plan.PlanJson) ?? new RolloutPlanDocument();
        }
        catch (JsonException)
        {
            return new RolloutPlanDocument();
        }
    }

    private string ActorName()
    {
        var name = _caller.Current?.ActorName;
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }
}
