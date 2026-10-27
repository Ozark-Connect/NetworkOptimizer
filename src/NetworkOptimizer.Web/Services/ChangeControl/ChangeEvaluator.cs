using System.Text.Json;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// Runs the impact diff on newly recorded changes (Security Audit on the config before and after,
/// same live telemetry), stores the impact lines, and publishes the change alerts (spec "Change
/// alerting"): security regression, unattributed class A/B/D, every human change, the Network app
/// upgrade summary, the management-path device check, and the daily digest.
/// </summary>
public sealed class ChangeEvaluator : IChangeEvaluator
{
    private static readonly TimeSpan ManagementCheckDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ManagementCheckHorizon = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IAlertEventBus? _bus;
    private readonly ILogger<ChangeEvaluator> _logger;

    public ChangeEvaluator(IServiceScopeFactory scopes, ILogger<ChangeEvaluator> logger, IAlertEventBus? bus = null)
    {
        _scopes = scopes;
        _bus = bus;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EvaluateAsync(string siteSlug, IReadOnlyList<ConfigChange> changes, ConfigState before, ConfigState after, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteSlug);
        using var system = Identity.SystemScope.Enter(scope.ServiceProvider, ChangeControlRepositoryAccessor.SystemActor);
        var repo = scope.ServiceProvider.GetRequiredService<Storage.Interfaces.IChangeControlRepository>();

        var objects = await repo.GetChangeObjectsAsync(changes.Select(c => c.Id).ToList(), cancellationToken);
        var collectionsByChange = objects.GroupBy(o => o.ChangeId).ToDictionary(g => g.Key, g => g.Select(o => o.Collection).ToHashSet());

        ImpactResult? impact = null;
        if (ImpactAnalyzer.Triggers(objects.Select(o => o.Collection)))
            impact = await RunImpactAsync(scope.ServiceProvider, before, after, cancellationToken);

        if (impact != null && changes.FirstOrDefault()?.SnapshotId is { } snapshotId && await repo.GetSnapshotAsync(snapshotId, cancellationToken) is { } snap)
        {
            SetAuditCount(snap, impact);
            await repo.UpdateSnapshotAsync(snap, cancellationToken);
        }

        foreach (var change in changes)
        {
            var collections = collectionsByChange.GetValueOrDefault(change.Id) ?? [];
            var gotImpact = impact != null && ImpactAnalyzer.Triggers(collections);
            if (gotImpact)
            {
                change.RegressionCount = impact!.Regressions.Count;
                change.FixCount = impact.Fixes.Count;
                change.ImpactJson = JsonSerializer.Serialize(impact.Regressions.Concat(impact.Fixes));
            }
            change.Evaluated = true;
            await repo.UpdateChangeAsync(change, cancellationToken);
            await PublishChangeAlertsAsync(siteSlug, change, gotImpact ? impact : null, cancellationToken);
        }

        if (impact != null)
            await CheckDriftRegressionAsync(scope.ServiceProvider, repo, siteSlug, after, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CountSnapshotAsync(string siteSlug, int snapshotId, int? previousId, ConfigState before, ConfigState after,
        bool auditInputsChanged, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteSlug);
        using var system = Identity.SystemScope.Enter(scope.ServiceProvider, ChangeControlRepositoryAccessor.SystemActor);
        var repo = scope.ServiceProvider.GetRequiredService<Storage.Interfaces.IChangeControlRepository>();
        if (await repo.GetSnapshotAsync(snapshotId, cancellationToken) is not { } snap) return;

        var previous = previousId is { } id ? await repo.GetSnapshotAsync(id, cancellationToken) : null;
        if (!auditInputsChanged && previous?.AuditIssueCount != null)
        {
            snap.AuditIssueCount = previous.AuditIssueCount;
            snap.AuditIssuesJson = previous.AuditIssuesJson;
            snap.AuditRegressionCount = 0;
        }
        else if (await RunImpactAsync(scope.ServiceProvider, before, after, cancellationToken) is { } impact)
            SetAuditCount(snap, impact);
        else
            return;
        await repo.UpdateSnapshotAsync(snap, cancellationToken);
    }

    private static void SetAuditCount(ConfigSnapshot snap, ImpactResult impact)
    {
        snap.AuditIssueCount = impact.AfterIssueCount;
        snap.AuditRegressionCount = impact.Regressions.Count;
        snap.AuditIssuesJson = JsonSerializer.Serialize(impact.AfterIssues.Select(i => new { i.Key, i.Title, i.Severity }));
    }

    // The regression set last alerted per site, so unchanged drift does not alert again on every change.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _driftAlerted = new();

    /// <summary>
    /// change.drift_regression: the Security Audit on the Baseline against the config now. A template
    /// Baseline is laid over the current config, so collections it does not carry are not "drift".
    /// </summary>
    private async Task CheckDriftRegressionAsync(IServiceProvider sp, Storage.Interfaces.IChangeControlRepository repo, string slug,
        ConfigState current, CancellationToken ct)
    {
        try
        {
            var settings = await repo.GetSettingsAsync(ct);
            ConfigState? baseline = null;
            string label;
            if (settings.BaselineTemplateId is { } templateId
                && await sp.GetRequiredService<ConfigTemplateStore>().LoadAsync(templateId, ct) is { } template)
            {
                baseline = current.Clone();
                foreach (var (collection, list) in template.State.Collections) baseline.Collections[collection] = list;
                label = $"template {template.View.Name}";
            }
            else if (await repo.GetBaselineSnapshotAsync(ct) is { } snap)
            {
                baseline = await sp.GetRequiredService<ChangeControlRegistry>().GetFor(slug).LoadSnapshotStateAsync(snap.Id, ct);
                label = "the Baseline snapshot";
            }
            else return;
            if (baseline == null) return;

            var drift = await RunImpactAsync(sp, baseline, current, ct);
            var key = drift == null ? "" : string.Join("|", drift.Regressions.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal));
            if (_driftAlerted.TryGetValue(slug, out var last) && last == key) return;
            _driftAlerted[slug] = key;
            if (drift is not { Regressions.Count: > 0 }) return;
            await Publish(new AlertEvent
            {
                EventType = ChangeAlerts.DriftRegression,
                Source = ChangeAlerts.Source,
                Severity = AlertSeverity.Warning,
                Title = $"Drift from Baseline: {drift.Regressions[0].Title}",
                Message = $"Compared with {label}, the Security Audit now finds: {string.Join("; ", drift.Regressions.Take(5).Select(r => r.Title))}. Return to Baseline from Change Control - Snapshots.",
                SourceUrl = "/change-control/snapshots",
                SiteSlug = slug,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Drift regression check failed for {Site}", slug);
        }
    }

    /// <inheritdoc />
    public async Task<ImpactResult?> RunImpactAsync(string siteSlug, ConfigState before, ConfigState after, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteSlug);
        using var system = Identity.SystemScope.Enter(scope.ServiceProvider, ChangeControlRepositoryAccessor.SystemActor);
        return await RunImpactAsync(scope.ServiceProvider, before, after, ct);
    }

    /// <inheritdoc />
    public async Task<List<NetworkOptimizer.Audit.Models.NetworkInfo>?> GetNetworksAsync(string siteSlug, ConfigState state, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteSlug);
        using var system = Identity.SystemScope.Enter(scope.ServiceProvider, ChangeControlRepositoryAccessor.SystemActor);
        try
        {
            var connection = scope.ServiceProvider.GetRequiredService<UniFiConnectionService>();
            var liveDevices = connection.Client == null ? null : await connection.Client.GetDevicesRawJsonAsync(ct);
            if (string.IsNullOrEmpty(liveDevices)) return null;
            var inputs = ImpactAnalyzer.BuildInputs(state, liveDevices);
            var result = await scope.ServiceProvider.GetRequiredService<AuditService>().RunConfigComparisonAsync(inputs, inputs, state.NetworkAppVersion);
            // The web result carries the purpose as its display string; map it back to the enum.
            var purposes = Enum.GetValues<NetworkOptimizer.Audit.Models.NetworkPurpose>()
                .GroupBy(p => NetworkOptimizer.Audit.Models.NetworkPurposeExtensions.ToDisplayString(p)).ToDictionary(g => g.Key, g => g.First());
            return result?.Before.Networks.Select(n => new NetworkOptimizer.Audit.Models.NetworkInfo
            {
                Id = n.Id, Name = n.Name, VlanId = n.VlanId, Subnet = n.Subnet,
                Purpose = purposes.GetValueOrDefault(n.Purpose, NetworkOptimizer.Audit.Models.NetworkPurpose.Unknown),
            }).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Change Control could not read network purposes");
            return null;
        }
    }

    private async Task<ImpactResult?> RunImpactAsync(IServiceProvider sp, ConfigState before, ConfigState after, CancellationToken ct)
    {
        try
        {
            var audit = sp.GetRequiredService<AuditService>();
            var connection = sp.GetRequiredService<UniFiConnectionService>();
            var liveDevices = connection.Client == null ? null : await connection.Client.GetDevicesRawJsonAsync(ct);
            if (string.IsNullOrEmpty(liveDevices)) return null;
            // Across a Network version the before state's IDs can predate live telemetry's (Network 11's UUIDs),
            // and an audit joining the two would lose every port-to-network link on the before side.
            if (!string.Equals(before.NetworkAppVersion, after.NetworkAppVersion, StringComparison.OrdinalIgnoreCase))
                before = ConfigDiffEngine.InAfterIds(before, after);
            List<NetworkOptimizer.UniFi.Models.UniFiFirewallGroup>? liveGroups = null;
            try { liveGroups = await connection.Client!.GetFirewallGroupsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Change Control impact diff: live firewall groups unavailable");
            }
            var result = await audit.RunConfigComparisonAsync(
                ImpactAnalyzer.BuildInputs(before, liveDevices, liveGroups), ImpactAnalyzer.BuildInputs(after, liveDevices, liveGroups),
                after.NetworkAppVersion);
            return result == null ? null : ImpactAnalyzer.Compare(result.Value.Before, result.Value.After);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Change Control impact diff failed");
            return null;
        }
    }

    /// <summary>Maps an audit rule severity to an alert severity.</summary>
    public static AlertSeverity ToAlertSeverity(AuditSeverity? severity) => severity switch
    {
        AuditSeverity.Critical => AlertSeverity.Critical,
        AuditSeverity.Recommended => AlertSeverity.Warning,
        _ => AlertSeverity.Info,
    };

    private async Task PublishChangeAlertsAsync(string slug, ConfigChange change, ImpactResult? impact, CancellationToken ct)
    {
        if (_bus == null || change.Kind != ConfigChangeKind.Config) return;
        var who = change.ActorKind switch
        {
            ConfigChangeActorKind.Person => change.ActorName ?? "An admin",
            ConfigChangeActorKind.NetworkOptimizer => "Network Optimizer",
            ConfigChangeActorKind.LikelyUniFi => "UniFi (likely)",
            _ => "Unattributed",
        };
        var context = new Dictionary<string, string>
        {
            ["change_id"] = change.Id.ToString(),
            ["who"] = who,
            ["objects"] = change.ObjectCount.ToString(),
        };

        if (impact is { Regressions.Count: > 0 })
        {
            var lines = string.Join("; ", impact.Regressions.Take(5).Select(r => r.Title));
            await Publish(new AlertEvent
            {
                EventType = ChangeAlerts.SecurityRegression,
                Source = ChangeAlerts.Source,
                Severity = ToAlertSeverity(impact.WorstRegression),
                Title = $"Security regression: {impact.Regressions[0].Title}",
                Message = $"{change.Summary} ({who}). The Security Audit now finds: {lines}.",
                Context = context,
                SourceUrl = ChangeAlerts.ChangeUrl(change.Id),
                SiteSlug = slug,
            }, ct);
        }

        var unattributed = change.ActorKind is ConfigChangeActorKind.NoAdminRecorded or ConfigChangeActorKind.LikelyUniFi;
        if (unattributed)
        {
            var tags = string.IsNullOrEmpty(change.ServiceTagsJson) ? [] : JsonSerializer.Deserialize<List<string>>(change.ServiceTagsJson) ?? [];
            var classD = tags.Where(ServiceTags.ClassD.Contains).ToList();
            // Each reason: the title's headline, and the sentence the message adds when it is not the headline.
            AlertSeverity? severity = null;
            var reasons = new List<(string Headline, string Also)>();
            if (change.ManagementPath) { severity = AlertSeverity.Critical; reasons.Add(("Management path changed", "It also changes the management path.")); }
            if (impact is { Regressions.Count: > 0 })
            {
                severity ??= ToAlertSeverity(impact.WorstRegression);
                reasons.Add(("Security Audit issue added", "It also adds a Security Audit issue."));
            }
            if (classD.Count > 0)
            {
                severity ??= AlertSeverity.Warning;
                reasons.Add(($"{string.Join(", ", classD)} changed", $"It also changes {string.Join(", ", classD)}."));
            }
            if (severity != null)
                await Publish(new AlertEvent
                {
                    EventType = ChangeAlerts.Unattributed,
                    Source = ChangeAlerts.Source,
                    Severity = severity.Value,
                    Title = $"{reasons[0].Headline}: {change.Summary}",
                    Message = string.Join(" ", new[] { "UniFi Network recorded no admin for this change. It may be UniFi's own, a device setting UniFi applied without logging it, or an edit over SSH." }
                        .Concat(reasons.Skip(1).Select(r => r.Also))),
                    Context = context,
                    SourceUrl = ChangeAlerts.ChangeUrl(change.Id),
                    SiteSlug = slug,
                }, ct);
        }

        if (change.ActorKind == ConfigChangeActorKind.Person)
            await Publish(new AlertEvent
            {
                EventType = ChangeAlerts.Any,
                Source = ChangeAlerts.Source,
                Severity = AlertSeverity.Info,
                Title = $"{who}: {change.Summary}",
                Message = change.ActorIp == null ? change.Summary : $"{change.Summary}. Source IP {change.ActorIp}.",
                Context = context,
                SourceUrl = ChangeAlerts.ChangeUrl(change.Id),
                SiteSlug = slug,
            }, ct);
    }

    private async Task Publish(AlertEvent alert, CancellationToken ct)
    {
        try { if (_bus != null) await _bus.PublishAsync(alert, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Could not publish {Type}", alert.EventType); }
    }

    /// <summary>The info-level summary after a Network app upgrade (spec: one per upgrade).</summary>
    public async Task NetworkAppUpgradeSummaryAsync(string slug, string? from, string? to, IReadOnlyList<ConfigChange> changes, CancellationToken ct)
    {
        var objects = changes.Sum(c => c.ObjectCount);
        await Publish(new AlertEvent
        {
            EventType = ChangeAlerts.NetworkAppUpgrade,
            Source = ChangeAlerts.Source,
            Severity = AlertSeverity.Info,
            // No regression count: the upgrade's audit waits for devices to reconnect and alerts on its own.
            Title = $"Network app {from} to {to}: {(objects == 0 ? "no config changes" : $"{objects} objects changed")}",
            Message = objects == 0 ? "The upgrade left the site's config as it was."
                : "Objects changed during the upgrade are on the Changes tab, labeled \"UniFi (likely)\".",
            SourceUrl = changes.Count > 0 ? ChangeAlerts.ChangeUrl(changes[0].Id) : "/change-control/changes",
            SiteSlug = slug,
        }, ct);
    }

    /// <summary>
    /// After a management-path change, checks whether devices that were online before it went
    /// offline, and alerts with the device names and the recovery step (spec class B).
    /// </summary>
    public async Task CheckManagementPathAsync(ChangeControlSiteEngine engine, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var pending = await engine.Repositories.UseAsync(async (r, c) =>
            (await r.GetChangesAsync(now - ManagementCheckHorizon, now, 200, c))
                .Where(x => x.ManagementPath && !x.ManagementChecked && now - x.DetectedAt >= ManagementCheckDelay).ToList(), ct);
        if (pending.Count == 0) return;

        var live = await engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/device", null, ct);
        if (!live.IsSuccess) return;
        var nowState = (ConfigSnapshotReader.ExtractList(live.Body) ?? [])
            .ToDictionary(d => d["mac"]?.ToString()?.ToLowerInvariant() ?? "", d => (State: d["state"]?.ToString(), Name: d["name"]?.ToString()));

        foreach (var change in pending)
        {
            var offline = new List<string>();
            if (change.PreviousSnapshotId is { } prevId && await engine.LoadSnapshotStateAsync(prevId, ct) is { } before)
            {
                foreach (var d in before.Get("device"))
                {
                    var mac = d["mac"]?.ToString()?.ToLowerInvariant() ?? "";
                    var wasOnline = d[ConfigCollections.DeviceMetaField]?["state"]?.ToString() == "1";
                    if (wasOnline && nowState.TryGetValue(mac, out var s) && s.State == "0")
                        offline.Add(string.IsNullOrEmpty(s.Name) ? mac : s.Name!);
                }
            }
            change.ManagementChecked = true;
            await engine.Repositories.UseAsync((r, c) => r.UpdateChangeAsync(change, c), ct);
            if (offline.Count == 0) continue;

            await Publish(new AlertEvent
            {
                EventType = ChangeAlerts.ManagementPath,
                Source = ChangeAlerts.Source,
                Severity = AlertSeverity.Critical,
                Title = $"Devices offline after a management-path change: {string.Join(", ", offline.Take(5))}",
                Message = $"{change.Summary}. Roll Back may not reach a device that lost its management path. "
                          + "If it stays offline, SSH to it and run set-inform with the UniFi Console's inform URL.",
                Context = new Dictionary<string, string> { ["change_id"] = change.Id.ToString(), ["devices"] = string.Join(", ", offline) },
                SourceUrl = ChangeAlerts.ChangeUrl(change.Id),
                SiteSlug = engine.SiteSlug,
            }, ct);
        }
    }

    /// <summary>The daily per-site summary: counts by actor, regressions, and incidents after changes.</summary>
    public async Task DigestAsync(ChangeControlSiteEngine engine, int incidentsAfter, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var changes = await engine.Repositories.UseAsync((r, c) => r.GetChangesAsync(now.AddDays(-1), now, 1000, c), ct);
        var config = changes.Where(c => c.Kind == ConfigChangeKind.Config).ToList();
        var byActor = config.GroupBy(c => c.ActorKind switch
            {
                ConfigChangeActorKind.Person => c.ActorName ?? "Admin",
                ConfigChangeActorKind.NetworkOptimizer => "Network Optimizer",
                _ => "Unattributed",
            })
            .Select(g => $"{g.Key} {g.Count()}");
        await Publish(new AlertEvent
        {
            EventType = ChangeAlerts.Digest,
            Source = ChangeAlerts.Source,
            Severity = AlertSeverity.Info,
            Title = $"Changes in the last 24 hours: {config.Count}",
            Message = config.Count == 0 ? "No config changes."
                : $"By who: {string.Join(", ", byActor)}. Security regressions: {config.Sum(c => c.RegressionCount)}. "
                  + $"Firmware steps: {changes.Count - config.Count}. Incidents after a change: {incidentsAfter}.",
            SourceUrl = "/change-control/changes",
            SiteSlug = engine.SiteSlug,
        }, ct);
    }
}
