using System.Text.Json.Nodes;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// The server half of the agent-side revert (spec "Agent sites"). Arms the site's On-Site Agent with
/// the calls that undo a plan before it applies, re-arms with the created IDs once it has, and
/// confirms or cancels when the server's own watch decides. Needs an agent that advertises the
/// capability and an API-key console connection: the key is what the agent replays the calls with.
/// </summary>
public sealed class AgentRevertArmer : IAgentRevertArmer
{
    // Covers the apply itself: the agent must not act on the pre-apply arm while writes are still going out.
    private static readonly TimeSpan ApplyMargin = TimeSpan.FromMinutes(3);

    private readonly string _slug;
    private readonly ChangeControlSiteEngine _engine;
    private readonly UniFiConnectionService _connection;
    private readonly AgentTunnelRegistry _agents;
    private readonly ILogger _logger;

    public AgentRevertArmer(string slug, ChangeControlSiteEngine engine, UniFiConnectionService connection, AgentTunnelRegistry agents, ILogger logger)
    {
        _slug = slug;
        _engine = engine;
        _connection = connection;
        _agents = agents;
        _logger = logger;
    }

    private AgentTunnelConnection? Agent() =>
        _agents.GetForSite(_slug).Where(c => c.HasCapability(ConfigRevert.Capability) && !c.IsStale).OrderByDescending(c => c.LastMessageAt).FirstOrDefault();

    public bool CanArm() => Agent() != null && _connection.Client?.ApiKeyForAgentRevert is { Length: > 0 };

    public async Task<bool> ArmAsync(ConfigPlan plan, PlanDocument doc, int preApplySnapshotId, TimeSpan window, CancellationToken ct)
    {
        if (!CanArm()) return false;
        var before = await _engine.LoadSnapshotStateAsync(preApplySnapshotId, ct);
        if (before == null) return false;
        var planned = PlanBuilder.PlannedState(await _engine.ReadLiveAsync(ct), doc);
        return Send(plan, doc, before, planned, DateTime.UtcNow + window + ApplyMargin);
    }

    public async Task RearmAsync(ConfigPlan plan, PlanDocument doc, ConfigState after, DateTime deadline, CancellationToken ct)
    {
        if (plan.PreApplySnapshotId is not { } preId || await _engine.LoadSnapshotStateAsync(preId, ct) is not { } before) return;
        Send(plan, doc, before, after, deadline);
    }

    public Task ConfirmAsync(ConfigPlan plan, CancellationToken ct) => Release(plan, cancelled: false);

    public Task CancelAsync(ConfigPlan plan, CancellationToken ct) => Release(plan, cancelled: true);

    private Task Release(ConfigPlan plan, bool cancelled)
    {
        foreach (var agent in _agents.GetForSite(_slug).Where(c => c.HasCapability(ConfigRevert.Capability)))
            _agents.TrySend(agent.AgentId, new ServerMessage { ConfigRevertConfirm = new ConfigRevertConfirm { PlanId = plan.Id, Cancelled = cancelled } });
        return Task.CompletedTask;
    }

    /// <summary>Acknowledges a report so the agent stops resending it.</summary>
    public void AcknowledgeReport(int agentId, long planId) =>
        _agents.TrySend(agentId, new ServerMessage { ConfigRevertConfirm = new ConfigRevertConfirm { PlanId = planId, ReportReceived = true } });

    private bool Send(ConfigPlan plan, PlanDocument doc, ConfigState before, ConfigState current, DateTime deadline)
    {
        var agent = Agent();
        var client = _connection.Client;
        if (agent == null || client?.ApiKeyForAgentRevert is not { Length: > 0 } key) return false;

        var touched = doc.Operations.Where(o => o.ObjectId != null).Select(o => (o.Collection, o.ObjectId!)).ToHashSet();
        var revert = PlanBuilder.Build(before, current, new PlanOptions { OnlyObjects = touched, Collections = doc.Collections.ToHashSet() });
        var arm = new ConfigRevertArm
        {
            PlanId = plan.Id,
            DeadlineUnixMs = new DateTimeOffset(deadline).ToUnixTimeMilliseconds(),
            ApiKey = key,
            IgnoreTlsErrors = client.IgnoresTlsErrors,
            HealthUrl = client.ResolveUrl(UniFiApiScope.SiteV1, "stat/health"),
            DevicesUrl = client.ResolveUrl(UniFiApiScope.SiteV1, "stat/device"),
            WatchInternet = doc.WatchInternet,
        };
        arm.Calls.AddRange(Calls(revert, client));
        if (doc.WatchDevices)
            arm.WatchDeviceMacs.AddRange(doc.Context.GetValueOrDefault("watch_online", "").Split(',', StringSplitOptions.RemoveEmptyEntries));

        var sent = _agents.TrySend(agent.AgentId, new ServerMessage { ConfigRevertArm = arm });
        _logger.LogInformation("Change Control plan {Plan} on site {Site}: agent revert {State} with {Calls} call(s)",
            plan.Id, _slug, sent ? "armed" : "not armed", arm.Calls.Count);
        return sent;
    }

    /// <summary>
    /// A revert plan as plain console calls, in plan order. A create captures its new ID for later
    /// calls' <c>@ref:</c> tokens; a call that needs an ID only the forward apply knows is left out.
    /// </summary>
    internal static List<ConfigRevertCall> Calls(PlanDocument revert, UniFiApiClient client)
    {
        var createKeys = revert.Operations.Where(o => o.Action == PlanAction.Create).Select(o => o.Key).ToHashSet();
        var calls = new List<ConfigRevertCall>();
        foreach (var op in revert.Operations)
        {
            var def = ConfigCollections.Get(op.Collection);
            if (def?.Write is not { } write) continue;
            var body = op.Body?.ToJsonString();
            var id = op.ObjectId ?? "";
            (string Method, UniFiApiScope Scope, string Path)? target = op.Action switch
            {
                PlanAction.Create when write.CreatePath != null => ("POST", def.Scope, write.CreatePath),
                PlanAction.Update when write.UpdatePath != null => (write.UpdateMethod.Method, def.Scope,
                    write.UpdatePath.Replace("{id}", id).Replace("{key}", op.Body?["key"]?.ToString() ?? op.ObjectName)),
                PlanAction.Delete when write.DeletePath != null => ("DELETE", def.Scope, write.DeletePath.Replace("{id}", id)),
                PlanAction.Delete when write.BatchDeletePath != null => ("POST", def.Scope, write.BatchDeletePath),
                PlanAction.Reorder => ("PUT", UniFiApiScope.SiteV2, "firewall-policies/batch-reorder"),
                _ => null,
            };
            if (target is not { } t) continue;
            if (op.Action == PlanAction.Delete && write.DeletePath == null)
                body = new JsonArray(JsonValue.Create(id)).ToJsonString();

            var refs = References(t.Path, ['/', '?']).Concat(References(body ?? "", ['"']));
            if (refs.Any(r => !createKeys.Contains(r))) continue;
            calls.Add(new ConfigRevertCall
            {
                Key = op.Key, Method = t.Method, Url = client.ResolveUrl(t.Scope, t.Path), Body = body ?? "",
                CaptureId = op.Action == PlanAction.Create,
            });
        }
        return calls;
    }

    private static IEnumerable<string> References(string text, char[] terminators)
    {
        var at = 0;
        while ((at = text.IndexOf(PlanBuilder.RefPrefix, at, StringComparison.Ordinal)) >= 0)
        {
            at += PlanBuilder.RefPrefix.Length;
            var end = text.IndexOfAny(terminators, at);
            yield return end < 0 ? text[at..] : text[at..end];
        }
    }
}
