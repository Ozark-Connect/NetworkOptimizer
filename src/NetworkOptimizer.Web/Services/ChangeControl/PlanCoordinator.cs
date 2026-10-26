using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Firmware;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>Apply-time facts about the caller, checked by the coordinator (the gate checks the role).</summary>
public sealed record PlanActor(string Name, bool IsSiteAdmin);

/// <summary>
/// One site's plan lifecycle (spec "Approvals", "Lockout pre-flight", "Changes and incidents"):
/// create with its impact, acknowledgements, submit and approve, approval invalidation when the
/// touched objects change, the auto-pinned snapshot before every apply, the apply itself, the
/// verify snapshot, the commit-confirmed watch with automatic revert, and the before/after report.
/// </summary>
public sealed class PlanCoordinator
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly TimeSpan ReportDelay = TimeSpan.FromMinutes(65);
    private static readonly TimeSpan ReportWindow = TimeSpan.FromMinutes(60);
    private const int MissesToRevert = 2;

    private readonly ChangeControlSiteEngine _engine;
    private readonly ICredentialProtectionService _crypto;
    private readonly IChangeEvaluator? _evaluator;
    private readonly IAlertEventBus? _bus;
    private readonly IAuditLogger? _audit;
    private readonly Func<string, DateTime, DateTime, CancellationToken, Task<RolloutResourceStats?>>? _stats;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, int> _misses = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    /// <summary>The site is reached through an On-Site Agent (longer window, agent-side revert).</summary>
    public Func<bool> IsAgentSite { get; set; } = () => false;

    /// <summary>Console host for the inform-path checks.</summary>
    public Func<string?> ConsoleHost { get; set; } = () => null;

    /// <summary>Hands the revert to the On-Site Agent before an apply (step 15); null on direct sites.</summary>
    public IAgentRevertArmer? AgentRevert { get; set; }

    public PlanCoordinator(ChangeControlSiteEngine engine, ICredentialProtectionService crypto, ILogger logger,
        IChangeEvaluator? evaluator = null, IAlertEventBus? bus = null, IAuditLogger? audit = null,
        Func<string, DateTime, DateTime, CancellationToken, Task<RolloutResourceStats?>>? stats = null)
    {
        _engine = engine;
        _crypto = crypto;
        _logger = logger;
        _evaluator = evaluator;
        _bus = bus;
        _audit = audit;
        _stats = stats;
    }

    private string Slug => _engine.SiteSlug;

    /// <summary>Serializes and encrypts a plan document for storage.</summary>
    public string Seal(PlanDocument doc) => _crypto.EncryptForSite(JsonSerializer.Serialize(doc, Json), Slug);

    /// <summary>Decrypts a stored plan document.</summary>
    public PlanDocument Open(ConfigPlan plan) =>
        string.IsNullOrEmpty(plan.OperationsEncrypted)
            ? new PlanDocument()
            : JsonSerializer.Deserialize<PlanDocument>(_crypto.Decrypt(plan.OperationsEncrypted), Json) ?? new PlanDocument();

    /// <summary>The plan options for this site (agent flag, console host).</summary>
    public PlanOptions Options(PlanOptions? baseOptions = null) => new()
    {
        SameSite = baseOptions?.SameSite ?? true,
        Collections = baseOptions?.Collections,
        DeleteMissing = baseOptions?.DeleteMissing ?? true,
        OnlyObjects = baseOptions?.OnlyObjects,
        DeviceMap = baseOptions?.DeviceMap ?? new Dictionary<string, string>(),
        TargetHasGateway = baseOptions?.TargetHasGateway ?? true,
        AgentSite = IsAgentSite(),
        ConsoleHost = ConsoleHost(),
        ManagementAddresses = ManagementAddresses(),
    };

    /// <summary>Addresses Network Optimizer reaches the site from, for the lockout pre-flight.</summary>
    public Func<IReadOnlyList<string>> ManagementAddresses { get; set; } = () => [];

    /// <summary>
    /// Builds a plan against live, runs its impact, and stores it as a Draft. <paramref name="runImpact"/> false
    /// skips the Security Audit, only for a change no audit rule reads (a lease time).
    /// </summary>
    public async Task<ConfigPlan> CreateAsync(ConfigPlanKind kind, string name, string source, int? sourceSnapshotId, string? sourceSite,
        ConfigState desired, PlanOptions options, string createdBy, CancellationToken ct, Action<PlanDocument>? decorate = null,
        ConfigState? impactBefore = null, int? rollsBackChangeId = null, bool runImpact = true)
    {
        var live = await _engine.ReadLiveAsync(ct);
        var doc = PlanBuilder.Build(desired, live, Options(options));
        decorate?.Invoke(doc);
        if (IsAgentSite() && (doc.WatchDevices || doc.WatchInternet) && AgentRevert?.CanArm() != true)
            doc.Notes.Add("This site's On-Site Agent cannot hold the revert (it needs an agent update). "
                + "If the change cuts the tunnel, the automatic revert cannot reach the site.");
        if (runImpact) await RunImpactAsync(doc, impactBefore ?? live, live, ct);

        var plan = new ConfigPlan
        {
            Name = name,
            Kind = kind,
            Status = ConfigPlanStatus.Draft,
            SourceDescription = source,
            SourceSnapshotId = sourceSnapshotId,
            SourceSiteSlug = sourceSite,
            RollsBackChangeId = rollsBackChangeId,
            OperationsEncrypted = Seal(doc),
            HighestTier = doc.HighestTier,
            ObjectCount = doc.Operations.Count(o => o.Collection != "device"),
            DeviceCount = doc.Operations.Where(o => o.Collection == "device").Select(o => o.ObjectId).Distinct().Count(),
            ApprovedLiveHash = PlanReadiness.TouchedHash(doc, live),
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
            AcknowledgementsJson = JsonSerializer.Serialize(new PlanAcknowledgements()),
        };
        return await _engine.Repositories.UseAsync((r, c) => r.CreatePlanAsync(plan, c), ct);
    }

    /// <summary>
    /// Audits the planned config against <paramref name="before"/>: live normally, the legacy source
    /// for a translated firewall. Regressions the plan already carries (unproven rules) are kept.
    /// </summary>
    private async Task RunImpactAsync(PlanDocument doc, ConfigState before, ConfigState live, CancellationToken ct)
    {
        if (_evaluator == null || !ImpactAnalyzer.Triggers(doc.Collections)) return;
        var planned = PlanBuilder.PlannedState(live, doc);
        var impact = await _evaluator.RunImpactAsync(Slug, before, planned, ct);
        if (impact == null) return;
        doc.ImpactRan = true;
        doc.Regressions = [.. doc.Regressions, .. impact.Regressions];
        doc.Fixes = impact.Fixes;
    }

    /// <summary>Loads a plan and its document.</summary>
    public async Task<(ConfigPlan Plan, PlanDocument Doc, PlanAcknowledgements Acks)?> LoadAsync(int planId, CancellationToken ct)
    {
        var plan = await _engine.Repositories.UseAsync((r, c) => r.GetPlanAsync(planId, c), ct);
        if (plan == null) return null;
        var acks = string.IsNullOrEmpty(plan.AcknowledgementsJson) ? new PlanAcknowledgements()
            : JsonSerializer.Deserialize<PlanAcknowledgements>(plan.AcknowledgementsJson) ?? new PlanAcknowledgements();
        return (plan, Open(plan), acks);
    }

    /// <summary>Stores acknowledgements (Save Draft keeps them).</summary>
    public async Task SaveAcknowledgementsAsync(int planId, PlanAcknowledgements acks, CancellationToken ct)
    {
        var plan = await _engine.Repositories.UseAsync((r, c) => r.GetPlanAsync(planId, c), ct) ?? throw new InvalidOperationException("Plan not found.");
        if (ConfigPlanStatuses.Terminal.Contains(plan.Status)) throw new InvalidOperationException("The plan is finished.");
        plan.AcknowledgementsJson = JsonSerializer.Serialize(acks);
        await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
    }

    /// <summary>Sets when the plan applies: after approval, or at a time.</summary>
    public async Task ScheduleAsync(int planId, DateTime? scheduledForUtc, bool applyOnApproval, CancellationToken ct)
    {
        var plan = await _engine.Repositories.UseAsync((r, c) => r.GetPlanAsync(planId, c), ct) ?? throw new InvalidOperationException("Plan not found.");
        plan.ScheduledFor = scheduledForUtc;
        plan.ApplyOnApproval = applyOnApproval;
        if (plan.Status == ConfigPlanStatus.Approved && scheduledForUtc != null) plan.Status = ConfigPlanStatus.Scheduled;
        await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
    }

    /// <summary>Submits a plan for approval.</summary>
    public async Task SubmitAsync(int planId, string by, CancellationToken ct)
    {
        var plan = await _engine.Repositories.UseAsync((r, c) => r.GetPlanAsync(planId, c), ct) ?? throw new InvalidOperationException("Plan not found.");
        if (plan.Status != ConfigPlanStatus.Draft) throw new InvalidOperationException("Only a draft can be submitted.");
        plan.Status = ConfigPlanStatus.PendingApproval;
        plan.SubmittedBy = by;
        plan.SubmittedAt = DateTime.UtcNow;
        await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
        await Publish(ChangeAlerts.PlanAwaitingApproval, AlertSeverity.Info, $"Plan awaiting approval: {plan.Name}",
            $"Submitted by {by}.", plan.Id, ct);
    }

    /// <summary>Approves a plan and records the live state it was approved against.</summary>
    public async Task ApproveAsync(int planId, string by, CancellationToken ct)
    {
        var loaded = await LoadAsync(planId, ct) ?? throw new InvalidOperationException("Plan not found.");
        var plan = loaded.Plan;
        if (plan.Status != ConfigPlanStatus.PendingApproval) throw new InvalidOperationException("The plan is not waiting for approval.");
        var live = await _engine.ReadLiveAsync(ct);
        plan.ApprovedLiveHash = PlanReadiness.TouchedHash(loaded.Doc, live);
        plan.ApprovedBy = by;
        plan.ApprovedAt = DateTime.UtcNow;
        plan.Status = plan.ScheduledFor != null ? ConfigPlanStatus.Scheduled : ConfigPlanStatus.Approved;
        await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
        await Publish(ChangeAlerts.PlanApproved, AlertSeverity.Info, $"Plan approved: {plan.Name}",
            $"Approved by {by}." + (plan.ScheduledFor is { } at ? $" Scheduled for {at:u}." : plan.ApplyOnApproval ? " Applying now." : ""), plan.Id, ct);
        if (plan.ApplyOnApproval && plan.ScheduledFor == null)
        {
            // The gate audited the approval; the apply it starts needs its own row.
            var why = await ApplyAsync(planId, new PlanActor(by, IsSiteAdmin: true), requireApproval: false, automatic: false, ct);
            Audit(AuditActions.ChangeControlPlanApplied, plan, why == null ? AuditOutcomes.Success : AuditOutcomes.Failure, new { onApproval = true, by, why });
        }
    }

    /// <summary>Discards a plan that has not applied.</summary>
    public async Task CancelAsync(int planId, CancellationToken ct)
    {
        var plan = await _engine.Repositories.UseAsync((r, c) => r.GetPlanAsync(planId, c), ct) ?? throw new InvalidOperationException("Plan not found.");
        if (plan.Status is ConfigPlanStatus.Applying or ConfigPlanStatus.Watching || ConfigPlanStatuses.Terminal.Contains(plan.Status))
            throw new InvalidOperationException("The plan has already applied.");
        plan.Status = ConfigPlanStatus.Cancelled;
        plan.CompletedAt = DateTime.UtcNow;
        await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
    }

    /// <summary>
    /// Applies a plan. With approvals required, it must be Approved and its touched objects unchanged
    /// since approval, or it returns to Pending approval. Every apply is preceded by a pinned snapshot.
    /// </summary>
    /// <returns>Null on success, else why it did not apply.</returns>
    public async Task<string?> ApplyAsync(int planId, PlanActor actor, bool requireApproval, bool automatic, CancellationToken ct)
    {
        await _applyLock.WaitAsync(ct);
        try
        {
            var loaded = await LoadAsync(planId, ct);
            if (loaded == null) return "Plan not found.";
            var (plan, doc, acks) = loaded.Value;
            if (plan.Status is ConfigPlanStatus.Applying or ConfigPlanStatus.Watching || ConfigPlanStatuses.Terminal.Contains(plan.Status))
                return "The plan has already applied.";
            if (requireApproval && plan.Status is not (ConfigPlanStatus.Approved or ConfigPlanStatus.Scheduled))
                return "The plan needs a Site Admin's approval first.";

            var remaining = PlanReadiness.Remaining(doc, acks, actor.IsSiteAdmin);
            if (remaining.Count > 0) return "Still open: " + string.Join(", ", remaining) + ".";

            var live = await _engine.ReadLiveAsync(ct);
            if (PlanReadiness.TouchedHash(doc, live) != plan.ApprovedLiveHash)
            {
                plan.Status = requireApproval ? ConfigPlanStatus.PendingApproval : ConfigPlanStatus.Draft;
                plan.Message = "Objects this plan touches changed after it was built or approved. Review it again.";
                await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
                return plan.Message;
            }

            if (PlanReadiness.HasCertainLockout(doc))
                _audit?.Log(AuditEventBuilder.FromSystem(AuditCategories.Action, AuditActions.ChangeControlLockoutOverridden,
                    targetType: "config_plan", targetId: plan.Id.ToString(), targetName: plan.Name, siteSlug: Slug,
                    details: new { by = actor.Name, devices = PlanReadiness.ConfirmationText(doc) }));

            var pre = await _engine.TakeSnapshotAsync(ConfigSnapshotTrigger.BeforeApply, plan.Name, $"Before apply: {plan.Name}", ct);
            // No pinned snapshot means no way back, so nothing is written.
            if (pre == null)
                return "The snapshot before the apply could not be taken (the UniFi Console did not return the config). Nothing was written.";
            plan.PreApplySnapshotId = pre.Id;
            plan.Status = ConfigPlanStatus.Applying;
            plan.AppliedBy = actor.Name;
            plan.StartedAt = DateTime.UtcNow;
            await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);

            // Devices online now are the ones the commit-confirmed watch expects to stay online.
            var online = await OnlineDevicesAsync(ct);
            doc.Context["watch_online"] = string.Join(",", online);
            // Only a site whose internet was reachable can lose it; one with no gateway reports no signal.
            if (doc.WatchInternet)
                doc.Context[InternetBeforeContext] = await InternetReachableAsync(ct) == true ? "true" : "false";
            var settings = await _engine.Repositories.UseAsync((r, c) => r.GetSettingsAsync(c), ct);
            var window = TimeSpan.FromMinutes(IsAgentSite() ? settings.AgentCommitWindowMinutes : settings.CommitWindowMinutes);
            var watch = doc.WatchDevices || (doc.WatchInternet && settings.WatchInternet);
            if (watch && AgentRevert != null && pre != null)
                plan.AgentRevertArmed = await AgentRevert.ArmAsync(plan, doc, pre.Id, window, ct);

            bool ok;
            PlanResult result;
            using (UniFiWriteSource.Use($"{ChangeAttributor.PlanSourcePrefix}{plan.Id}"))
                (ok, result) = await new PlanExecutor(_engine.Api, _logger).ApplyAsync(doc, HasGateway(live), null, ct);

            // Verify: a fresh snapshot, compared with what the plan meant to write.
            var post = await _engine.TakeSnapshotAsync(ConfigSnapshotTrigger.AfterApply, plan.Name, null, ct);
            plan.PostApplySnapshotId = post?.Id;
            var after = post == null ? null : await _engine.LoadSnapshotStateAsync(post.Id, ct);
            if (after != null)
                result.VerifyDifferences = VerifyAgainst(doc, live, after,
                    result.Operations.Where(o => o.Outcome == "Waiting").Select(o => o.Key).ToHashSet(StringComparer.Ordinal));

            // A re-address that moves the console's own network writes it last; its reply can be lost with
            // the old address. Only those writes failing is the expected end, not a failure.
            var pathMove = doc.Context.TryGetValue(ReaddressPathContext, out var pathKeys) && pathKeys.Length > 0;
            if (!ok && pathMove && result.Operations.Where(o => o.Outcome == "Error").All(o => pathKeys!.Split(',').Contains(o.Key)))
            {
                ok = true;
                plan.Message = "The console's network moved last, so its reply could not come back from the old address. Check the site once Network Optimizer reconnects at the new one.";
            }
            plan.ResultJson = JsonSerializer.Serialize(result);
            plan.OperationsEncrypted = Seal(doc);
            if (!ok)
            {
                plan.Status = ConfigPlanStatus.Failed;
                plan.CompletedAt = DateTime.UtcNow;
                plan.Message = result.Operations.LastOrDefault(o => o.Outcome == "Error")?.Message ?? "A write failed.";
                await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
                if (plan.AgentRevertArmed && AgentRevert != null) await AgentRevert.CancelAsync(plan, ct);
                await Publish(ChangeAlerts.PlanFailed, AlertSeverity.Warning, $"Plan failed: {plan.Name}",
                    $"{plan.Message} Writes before it stay applied; Roll Back to the pinned snapshot puts them back.", plan.Id, ct);
                if (automatic) Audit(AuditActions.ChangeControlPlanApplied, plan, AuditOutcomes.Failure, new { plan.Message });
                return plan.Message;
            }

            plan.CompletedAt = DateTime.UtcNow;
            var waiting = result.Operations.Count(o => o.Outcome == "Waiting");
            if (waiting > 0)
                plan.Message = $"{waiting} write{(waiting == 1 ? "" : "s")} need the gateway adopted first. Run the plan again once it is.";
            if (watch)
            {
                plan.Status = ConfigPlanStatus.Watching;
                plan.WatchUntil = DateTime.UtcNow + window;
            }
            else
            {
                plan.Status = ConfigPlanStatus.Applied;
            }
            await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
            await MoveOwnSettingsAsync(plan, doc, ct);
            // Now the created IDs are known, the agent's copy also deletes what the plan created.
            if (plan.AgentRevertArmed && AgentRevert != null && after != null && plan.WatchUntil is { } deadline)
                await AgentRevert.RearmAsync(plan, doc, after, deadline, ct);
            if (automatic) Audit(AuditActions.ChangeControlPlanApplied, plan, AuditOutcomes.Success, new { scheduled = true });
            await Publish(ChangeAlerts.PlanApplied, AlertSeverity.Info, $"Plan applied: {plan.Name}",
                $"{doc.Operations.Count} writes by {actor.Name}." + (watch ? $" Watching for {window.TotalMinutes:0} minutes." : "")
                + (result.VerifyDifferences.Count > 0 ? $" {result.VerifyDifferences.Count} differences remain after verify." : ""), plan.Id, ct);
            _engine.PollSoon();
            return null;
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>
    /// Whether a gateway is adopted: without one, gateway-bound writes (routes, NAT, port forwards,
    /// traffic routes, QoS) are refused, so they wait instead of failing the plan (grounding: blank console).
    /// </summary>
    internal static bool HasGateway(ConfigState live) =>
        live.Get("device").Any(d => d["type"]?.ToString() is "ugw" or "udm" or "uxg"
                                    && d["adopted"]?.GetValueKind() != System.Text.Json.JsonValueKind.False);

    /// <summary>Differences left between the plan's intent and the console after it applied (class F).</summary>
    internal static List<string> VerifyAgainst(PlanDocument doc, ConfigState before, ConfigState after, IReadOnlySet<string>? waiting = null)
    {
        var intended = PlanBuilder.PlannedState(before, doc);
        var diffs = new List<string>();
        // A reference to an object this plan created compares as the ID the console gave it.
        var createdIds = doc.Operations.Where(o => o.Action == PlanAction.Create && o.ObjectId != null)
            .ToDictionary(o => PlanBuilder.RefPrefix + o.Key, o => o.ObjectId!, StringComparer.Ordinal);
        var unresolved = false;
        JsonNode? ResolveRefs(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, ResolveRefs(p.Value)))),
            JsonArray a => new JsonArray(a.Select(ResolveRefs).ToArray()),
            JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith(PlanBuilder.RefPrefix, StringComparison.Ordinal) =>
                createdIds.TryGetValue(s, out var id) ? JsonValue.Create(id) : MarkUnresolved(v),
            _ => node?.DeepClone(),
        };
        JsonNode MarkUnresolved(JsonValue v) { unresolved = true; return v.DeepClone(); }

        foreach (var op in doc.Operations.Where(o => o.Action is PlanAction.Update or PlanAction.Create))
        {
            if (waiting?.Contains(op.Key) == true) continue;
            var def = ConfigCollections.Get(op.Collection);
            if (def == null) continue;
            var want = intended.Get(op.Collection).FirstOrDefault(o => ConfigState.IdOf(o) == op.ObjectId || ConfigState.IdOf(o) == PlanBuilder.RefPrefix + op.Key);
            var got = after.Get(op.Collection).FirstOrDefault(o => op.ObjectId != null && ConfigState.IdOf(o) == op.ObjectId)
                      ?? after.Get(op.Collection).FirstOrDefault(o => ConfigState.DisplayName(def, o) == op.ObjectName);
            if (want == null) continue;
            if (got == null) { diffs.Add($"{def.Noun} {op.ObjectName}: not on the console"); continue; }
            foreach (var field in op.WriteFields)
            {
                if (ConfigCollections.IsSecretField(def, field)) continue;
                unresolved = false;
                var w = ResolveRefs(want[field]);
                if (unresolved) continue;
                if (PlanExecutor.NormalizedFields.GetValueOrDefault(op.Collection, []).Contains(field)) continue;
                var single = ConfigDiffEngine.CompareObjects(def, new JsonObject { [field] = w?.DeepClone() }, new JsonObject { [field] = got[field]?.DeepClone() });
                if (single != null) diffs.Add($"{def.Noun} {op.ObjectName}: {ChangeDescriber.LabelFor(field)} did not land");
            }
        }
        return diffs;
    }

    /// <summary>
    /// A report from the On-Site Agent about a revert it held: it reverted (or tried to) while the
    /// server could not see the site, or it is holding because every local check passed.
    /// </summary>
    public async Task OnAgentReportAsync(int agentId, AgentProtocol.ConfigRevertReport report, CancellationToken ct)
    {
        var plan = await _engine.Repositories.UseAsync((r, c) => r.GetPlanAsync((int)report.PlanId, c), ct);
        if (plan != null && plan.Status is ConfigPlanStatus.Watching or ConfigPlanStatus.Applied)
        {
            if (report.State is AgentProtocol.ConfigRevert.Reverted or AgentProtocol.ConfigRevert.RevertFailed)
            {
                var reverted = report.State == AgentProtocol.ConfigRevert.Reverted;
                plan.Status = reverted ? ConfigPlanStatus.Reverted : ConfigPlanStatus.Failed;
                plan.Message = report.Detail;
                plan.CompletedAt = DateTime.UtcNow;
                _misses.TryRemove(plan.Id, out _);
                await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
                Audit(AuditActions.ChangeControlPlanReverted, plan, reverted ? AuditOutcomes.Success : AuditOutcomes.Failure,
                    new { by = "On-Site Agent", report.Detail, report.CallsOk, report.CallsFailed });
                await Publish(ChangeAlerts.PlanReverted, reverted ? AlertSeverity.Warning : AlertSeverity.Critical,
                    reverted ? $"Plan reverted by the On-Site Agent: {plan.Name}" : $"On-Site Agent revert incomplete: {plan.Name}",
                    report.Detail + (reverted ? "" : " Roll Back to the pinned snapshot from Change Control."), plan.Id, ct);
                _engine.PollSoon();
            }
            else if (report.State == AgentProtocol.ConfigRevert.Held)
            {
                plan.Message = report.Detail;
                await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
            }
        }
        AgentRevert?.AcknowledgeReport(agentId, report.PlanId);
    }

    /// <summary>Plans due to apply, commit-confirmed watches, and reports. Called from the registry tick.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var plans = await _engine.Repositories.UseAsync((r, c) => r.GetPlansAsync(
            [ConfigPlanStatus.Approved, ConfigPlanStatus.Scheduled, ConfigPlanStatus.Watching, ConfigPlanStatus.Applied], 50, c), ct);
        var now = DateTime.UtcNow;
        foreach (var plan in plans)
        {
            try
            {
                if (plan.Status is ConfigPlanStatus.Approved or ConfigPlanStatus.Scheduled && plan.ScheduledFor is { } at && at <= now && _engine.Api.IsConnected)
                {
                    // Readiness was checked when it was booked (with the booker's role) or approved
                    // (by a Site Admin); the audit names that person.
                    var booker = plan.ApprovedBy ?? plan.AppliedBy ?? "Network Optimizer";
                    var why = await ApplyAsync(plan.Id, new PlanActor($"{booker} (scheduled)", IsSiteAdmin: true),
                        requireApproval: false, automatic: true, ct);
                    if (why != null) _logger.LogWarning("Scheduled plan {Plan} did not apply: {Why}", plan.Id, why);
                }
                else if (plan.Status == ConfigPlanStatus.Watching)
                {
                    await WatchAsync(plan, ct);
                }
                else if (plan.Status == ConfigPlanStatus.Applied && plan.ReportJson == null && plan.CompletedAt is { } done
                         && now - done > ReportDelay && plan.Kind != ConfigPlanKind.Revert && _engine.Api.IsConnected)
                {
                    await BuildReportAsync(plan, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Change Control plan tick failed for plan {Plan}", plan.Id);
            }
        }
    }

    private async Task<HashSet<string>> OnlineDevicesAsync(CancellationToken ct)
    {
        var r = await _engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/device", null, ct);
        if (!r.IsSuccess) return [];
        return (ConfigSnapshotReader.ExtractList(r.Body) ?? [])
            .Where(d => d["state"]?.ToString() == "1")
            .Select(d => d["mac"]?.ToString()?.ToLowerInvariant() ?? "")
            .Where(m => m.Length > 0).ToHashSet();
    }

    /// <summary>
    /// Commit-confirmed: every device online before the apply stays online, and when the plan
    /// touched the WAN path, the internet stays reachable. Two misses in a row revert the plan.
    /// </summary>
    private async Task WatchAsync(ConfigPlan plan, CancellationToken ct)
    {
        var doc = Open(plan);
        var expected = doc.Context.GetValueOrDefault("watch_online", "").Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (!_engine.Api.IsConnected)
        {
            // An armed agent decides locally. Otherwise the server cannot revert what it cannot reach:
            // it says so once, with the devices and the recovery step, and keeps watching.
            if (plan.AgentRevertArmed) return;
            if (_misses.AddOrUpdate(plan.Id, 1, (_, n) => n + 1) == MissesToRevert)
                await Publish(ChangeAlerts.PlanFailed, AlertSeverity.Critical, $"UniFi Console unreachable after: {plan.Name}",
                    $"Network Optimizer lost the UniFi Console after applying this plan, so it cannot revert it. Watched: {string.Join(", ", expected)}. "
                    + "If the change cut the path, restore the snapshot taken before the apply from the UniFi Console's own backup, or SSH to an affected device and run set-inform with the console's inform URL.",
                    plan.Id, ct);
            return;
        }

        if (plan.WatchUntil is { } until && DateTime.UtcNow >= until)
        {
            plan.Status = ConfigPlanStatus.Applied;
            plan.Message = null;
            _misses.TryRemove(plan.Id, out _);
            await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
            if (plan.AgentRevertArmed && AgentRevert != null) await AgentRevert.ConfirmAsync(plan, ct);
            return;
        }

        var offline = doc.WatchDevices ? await GoneDevicesAsync(expected, ct) : [];
        string? reason = null;
        if (offline is null) reason = "The device list did not load";
        else if (offline.Count > 0) reason = $"{offline.Count} device{(offline.Count == 1 ? "" : "s")} went offline";
        else if (doc.WatchInternet && doc.Context.GetValueOrDefault(InternetBeforeContext) == "true"
                 && await InternetReachableAsync(ct) == false) reason = "Internet reachability was lost";

        if (reason == null)
        {
            _misses.TryRemove(plan.Id, out _);
            return;
        }
        var misses = _misses.AddOrUpdate(plan.Id, 1, (_, n) => n + 1);
        if (misses < MissesToRevert) return;
        _misses.TryRemove(plan.Id, out _);

        // Never revert a revert: its "before" is the state after the original change.
        if (plan.Kind == ConfigPlanKind.Revert)
        {
            plan.Status = ConfigPlanStatus.Applied;
            plan.Message = $"{reason} after the revert.";
            await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
            await Publish(ChangeAlerts.PlanFailed, AlertSeverity.Critical, $"Problem after revert: {plan.Name}",
                $"{reason} after the revert applied. Offline: {string.Join(", ", offline ?? [])}. "
                + "Check the device's uplink; if it stays offline, SSH to it and run set-inform with the UniFi Console's inform URL.", plan.Id, ct);
            return;
        }
        await RevertAsync(plan.Id, reason, offline ?? [], automatic: true, ct);
    }

    /// <summary>
    /// Watched devices now gone (missing, or state 0 disconnected, 6 heartbeat missed, 9 inform
    /// error, 11 isolated). Provisioning and upgrading are not gone: a gateway provisions for about
    /// two minutes after a config write. Null when the device list does not load.
    /// </summary>
    private async Task<List<string>?> GoneDevicesAsync(IReadOnlyCollection<string> expected, CancellationToken ct)
    {
        var r = await _engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/device", null, ct);
        if (!r.IsSuccess) return null;
        var state = (ConfigSnapshotReader.ExtractList(r.Body) ?? [])
            .Where(d => d["mac"] != null)
            .GroupBy(d => d["mac"]!.ToString().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First()["state"]?.ToString() ?? "");
        return expected.Where(m => !state.TryGetValue(m, out var s) || s is "0" or "6" or "9" or "11").ToList();
    }

    /// <summary>
    /// Internet reachability from the console's own health: the "www" subsystem (the gateway's own
    /// internet test) or, without it, the "wan" subsystem. Null when there is no signal: a site with
    /// no gateway reports "unknown" for both.
    /// </summary>
    private async Task<bool?> InternetReachableAsync(CancellationToken ct)
    {
        var r = await _engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/health", null, ct);
        if (!r.IsSuccess) return false;
        var subsystems = ConfigSnapshotReader.ExtractList(r.Body) ?? [];
        var www = subsystems.FirstOrDefault(s => s["subsystem"]?.ToString() == "www")
                  ?? subsystems.FirstOrDefault(s => s["subsystem"]?.ToString() == "wan");
        return HealthReachable(www?["status"]?.ToString());
    }

    /// <summary>A health status as reachability: "ok" is reachable, "unknown" or none is no signal.</summary>
    internal static bool? HealthReachable(string? status) => status switch
    {
        "ok" => true,
        null or "" or "unknown" => null,
        _ => false,
    };

    /// <summary>Context key: internet was reachable before the apply, so the watch may hold the plan to it.</summary>
    private const string InternetBeforeContext = "watch_internet_before";

    /// <summary>
    /// Puts back the pre-apply snapshot for the objects a plan touched, as a Revert plan applied at once.
    /// Where the revert cannot reach a device, the alert names it and the recovery step.
    /// </summary>
    /// <param name="by">The person reverting; null for the automatic watch, which acts as Site Admin.</param>
    public async Task<string?> RevertAsync(int planId, string reason, IReadOnlyList<string> offlineMacs, bool automatic, CancellationToken ct, PlanActor? by = null)
    {
        var loaded = await LoadAsync(planId, ct);
        if (loaded == null) return "Plan not found.";
        var (plan, doc, _) = loaded.Value;
        if (plan.PreApplySnapshotId is not { } snapId || await _engine.LoadSnapshotStateAsync(snapId, ct) is not { } before)
            return "The snapshot taken before the apply is not available.";
        // The server reverts now, so the agent must not replay the same revert later.
        if (plan.AgentRevertArmed && AgentRevert != null) await AgentRevert.CancelAsync(plan, ct);

        var touched = doc.Operations.Where(o => o.ObjectId != null).Select(o => (o.Collection, o.ObjectId!)).ToHashSet();
        var revert = await CreateAsync(ConfigPlanKind.Revert, $"Revert: {plan.Name}", $"Snapshot before {plan.Name}", snapId, null,
            before, new PlanOptions { OnlyObjects = touched, Collections = doc.Collections.ToHashSet() },
            automatic ? "Network Optimizer" : "Roll Back", ct);
        revert.RevertOfPlanId = plan.Id;
        await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(revert, c), ct);

        var revertDoc = Open(revert);
        // A person who is not a Site Admin never gets a lockout confirmation typed for them: that
        // revert stays a draft for a Site Admin in Review Plan.
        if (by is { IsSiteAdmin: false } && PlanReadiness.HasCertainLockout(revertDoc))
            return $"The revert would cut a device off from management. A Site Admin applies it from Review Plan ({revert.Name}).";
        // A revert puts back what was there; nothing else in it needs acknowledging.
        var acks = new PlanAcknowledgements
        {
            Items = revertDoc.Operations.Select(o => o.Key).ToHashSet(),
            Regressions = revertDoc.Regressions.Select(r => r.Key).ToHashSet(),
            TypedConfirmation = PlanReadiness.ConfirmationText(revertDoc),
        };
        await SaveAcknowledgementsAsync(revert.Id, acks, ct);
        var why = await ApplyAsync(revert.Id, by ?? new PlanActor("Network Optimizer", IsSiteAdmin: true), requireApproval: false, automatic: by == null, ct);

        plan = (await _engine.Repositories.UseAsync((r, c) => r.GetPlanAsync(planId, c), ct))!;
        // Reverted only when the revert went through; otherwise the change may still be live.
        plan.Status = why == null ? ConfigPlanStatus.Reverted : ConfigPlanStatus.Failed;
        plan.Message = why == null ? reason : $"{reason}. The revert did not apply: {why}";
        plan.CompletedAt = DateTime.UtcNow;
        await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);

        var names = offlineMacs.Count == 0 ? "" : $" Offline: {string.Join(", ", offlineMacs)}.";
        var recovery = offlineMacs.Count > 0 ? " If a device stays offline, SSH to it and run set-inform with the UniFi Console's inform URL." : "";
        await Publish(ChangeAlerts.PlanReverted, AlertSeverity.Critical, $"Plan reverted: {plan.Name}",
            $"{reason}.{names} Network Optimizer put back the snapshot taken before the apply"
            + (why == null ? "." : $", but the revert did not finish: {why}") + recovery, plan.Id, ct);
        if (automatic) Audit(AuditActions.ChangeControlPlanReverted, plan, why == null ? AuditOutcomes.Success : AuditOutcomes.Failure, new { reason, offline = offlineMacs, why });
        return why;
    }

    /// <summary>The before/after report, through Firmware Rollout's builder (spec: reuse it).</summary>
    private async Task BuildReportAsync(ConfigPlan plan, CancellationToken ct)
    {
        var doc = Open(plan);
        var started = plan.StartedAt ?? plan.CompletedAt!.Value;
        var done = plan.CompletedAt!.Value;
        var macs = new List<(string Mac, string Name, string Model, string Type)>();
        if (plan.PostApplySnapshotId is { } postId && await _engine.LoadSnapshotStateAsync(postId, ct) is { } state)
        {
            var touchedDevices = doc.Operations.Where(o => o.Collection == "device").Select(o => o.ObjectId).ToHashSet();
            foreach (var d in state.Get("device"))
            {
                var type = d[ConfigCollections.DeviceMetaField]?["type"]?.ToString() ?? "";
                var isGateway = type is "ugw" or "udm" or "uxg";
                if (!touchedDevices.Contains(ConfigState.IdOf(d)) && !isGateway) continue;
                macs.Add((d["mac"]?.ToString() ?? "", ConfigState.DisplayName(ConfigCollections.Get("device"), d), d["model"]?.ToString() ?? "", type));
            }
        }

        var steps = new List<FirmwareRolloutStep>();
        foreach (var (mac, name, model, type) in macs)
        {
            RolloutResourceStats? pre = null, post = null;
            if (_stats != null)
            {
                pre = await _stats(mac, started - ReportWindow, started, ct);
                post = await _stats(mac, done, done + ReportWindow, ct);
            }
            steps.Add(new FirmwareRolloutStep
            {
                DeviceMac = mac, DeviceName = name, Model = model, DeviceType = type,
                State = FirmwareRolloutStepState.LitmusPassed, BackAt = done,
                PreStatsJson = pre == null ? null : JsonSerializer.Serialize(pre),
                PostStatsJson = post == null ? null : JsonSerializer.Serialize(post),
            });
        }

        var incidents = await _engine.Repositories.UseAsync((r, c) => ChangeIncidents.StartedAsync(r, started, done + ChangeControlSiteEngine.IncidentWindow, c), ct);
        var rolloutPlan = new FirmwareRolloutPlan { StartedAt = started, CompletedAt = done, CreatedBy = plan.AppliedBy ?? "" };
        var rolloutDoc = new RolloutPlanDocument();
        rolloutDoc.Notes.Add($"Change Control plan: {plan.Name}");
        foreach (var s in incidents)
            rolloutDoc.Notes.Add($"Incident after{(s.IsWan ? " (WAN)" : "")}: {s.Incident.Title} ({s.Incident.FirstTriggeredAt:u})");
        var report = RolloutReportBuilder.Build(rolloutPlan, rolloutDoc, steps, DateTime.UtcNow);
        foreach (var row in report.Rows) row.Outcome = "Applied";
        plan.ReportJson = JsonSerializer.Serialize(report);
        await _engine.Repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), ct);
    }

    /// <summary>Moves Network Optimizer's own settings for a site's subnets (applied, revert, failure-safe).</summary>
    public Func<IReadOnlyList<SubnetMove>, IReadOnlyList<string>, CancellationToken, Task<List<SettingRewrite>>>? MoveSettings { get; set; }

    /// <summary>Plan context key carrying a re-address plan's subnet moves (JSON).</summary>
    public const string ReaddressContext = "readdress";

    /// <summary>Plan context key carrying the WAN and VPN ranges a re-address leaves alone (JSON string list).</summary>
    public const string ReaddressRemoteContext = "readdress_remote";

    /// <summary>Plan context key carrying the operation keys of the networks that hold the console's path (comma-separated).</summary>
    public const string ReaddressPathContext = "readdress_path";

    /// <summary>
    /// A re-address moves Network Optimizer's own settings with the site once its writes land; a Revert of
    /// one moves them back. A failure here never fails the plan: the console writes already happened.
    /// </summary>
    private async Task MoveOwnSettingsAsync(ConfigPlan plan, PlanDocument doc, CancellationToken ct)
    {
        if (MoveSettings == null) return;
        try
        {
            List<SubnetMove>? moves = null;
            var source = doc;
            if (doc.Context.TryGetValue(ReaddressContext, out var json)) moves = JsonSerializer.Deserialize<List<SubnetMove>>(json);
            else if (plan.Kind == ConfigPlanKind.Revert && plan.RevertOfPlanId is { } original
                     && await LoadAsync(original, ct) is { } o && o.Doc.Context.TryGetValue(ReaddressContext, out var originalJson))
            {
                moves = Readdressing.Reverse(JsonSerializer.Deserialize<List<SubnetMove>>(originalJson) ?? []);
                source = o.Doc;
            }
            if (moves == null || moves.Count == 0) return;
            var remote = source.Context.TryGetValue(ReaddressRemoteContext, out var r) ? JsonSerializer.Deserialize<List<string>>(r) ?? [] : [];
            var moved = await MoveSettings(moves, remote, ct);
            if (moved.Count > 0)
                Audit(AuditActions.ChangeControlPlanApplied, plan, AuditOutcomes.Success,
                    new { networkOptimizerSettingsMoved = moved.Count, settings = string.Join(", ", moved.Select(m => $"{m.Setting} {m.Name}").Distinct()) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Moving Network Optimizer settings for plan {PlanId} failed", plan.Id);
        }
    }

    private void Audit(string action, ConfigPlan plan, string outcome, object? details) =>
        _audit?.Log(AuditEventBuilder.FromSystem(AuditCategories.Action, action, outcome,
            targetType: "config_plan", targetId: plan.Id.ToString(), targetName: plan.Name, siteSlug: Slug, details: details));

    private async Task Publish(string type, AlertSeverity severity, string title, string message, int planId, CancellationToken ct)
    {
        if (_bus == null) return;
        try
        {
            await _bus.PublishAsync(new AlertEvent
            {
                EventType = type, Source = ChangeAlerts.Source, Severity = severity, Title = title, Message = message,
                SourceUrl = ChangeAlerts.PlanUrl(planId), SiteSlug = Slug,
                Context = new Dictionary<string, string> { ["plan_id"] = planId.ToString() },
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not publish {Type}", type);
        }
    }
}

/// <summary>Arms and confirms the On-Site Agent's local revert for a plan (spec "Agent sites").</summary>
public interface IAgentRevertArmer
{
    /// <summary>Whether the site has an agent that can hold a revert, on a connection it can replay with.</summary>
    bool CanArm();

    /// <summary>Hands the agent the revert calls and a deadline. False when the agent cannot hold one (older agent).</summary>
    Task<bool> ArmAsync(ConfigPlan plan, PlanDocument doc, int preApplySnapshotId, TimeSpan window, CancellationToken ct);

    /// <summary>Replaces the armed revert once the apply has run, so it also deletes what the plan created.</summary>
    Task RearmAsync(ConfigPlan plan, PlanDocument doc, ConfigState after, DateTime deadline, CancellationToken ct);

    /// <summary>Tells the agent the window passed with every check healthy.</summary>
    Task ConfirmAsync(ConfigPlan plan, CancellationToken ct);

    /// <summary>Tells the agent to drop its revert: the server reverted, or the plan failed before it took effect.</summary>
    Task CancelAsync(ConfigPlan plan, CancellationToken ct);

    /// <summary>Acknowledges an agent's report so it stops resending.</summary>
    void AcknowledgeReport(int agentId, long planId);
}
