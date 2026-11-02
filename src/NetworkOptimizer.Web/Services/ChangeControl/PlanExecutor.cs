using System.Text.Json.Nodes;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// Applies a plan's operations to the console in order (spec, diff-engine.md "Verify"): deletes go
/// through the batch endpoints where the app has one, each run of writes to one collection is
/// verified by re-reading it and comparing only the fields sent, and a failed write is followed by a
/// re-read, because UniFi can answer an error and still write.
/// </summary>
[VendorSpecific("UniFi", "v1 data envelope ids, v2 object ids, batch-delete bodies, normalized fields per Network app version")]
public sealed class PlanExecutor
{
    /// <summary>
    /// Fields UniFi rewrites to its own value on write, per Network app major version (grounding.md:
    /// portconf.stp_bpdu_guard_enabled on 11). Reported as "normalized by UniFi", not as a failure.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> NormalizedFields = new Dictionary<string, string[]>
    {
        ["portconf"] = ["stp_bpdu_guard_enabled"],
    };

    private readonly IUniFiConfigApi _api;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _created = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<TimeSpan> _readBackRetries;

    private static readonly TimeSpan[] DefaultReadBackRetries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)];

    /// <param name="api">The console.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="readBackRetries">Waits before each re-read of a write that does not read back yet; 1, 2, and 3 s by default.</param>
    public PlanExecutor(IUniFiConfigApi api, ILogger logger, IReadOnlyList<TimeSpan>? readBackRetries = null)
    {
        _api = api;
        _logger = logger;
        _readBackRetries = readBackRetries ?? DefaultReadBackRetries;
    }

    /// <summary>IDs of objects the run created, by operation key.</summary>
    public IReadOnlyDictionary<string, string> CreatedIds => _created;

    /// <summary>Applies the operations. Stops at the first write that neither applied nor can be confirmed by a re-read.</summary>
    /// <param name="doc">The plan.</param>
    /// <param name="hasGateway">The console has a gateway; gateway-bound writes wait otherwise.</param>
    /// <param name="progress">Reports each finished operation.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<(bool Ok, PlanResult Result)> ApplyAsync(PlanDocument doc, bool hasGateway,
        Action<OperationResult>? progress, CancellationToken ct)
    {
        var result = new PlanResult();
        var ops = doc.Operations;
        var i = 0;
        var failed = false;
        while (i < ops.Count)
        {
            // A run: consecutive operations on one collection, verified together.
            var run = new List<PlanOperation> { ops[i] };
            while (i + run.Count < ops.Count && ops[i + run.Count].Collection == ops[i].Collection && ops[i + run.Count].Action == ops[i].Action)
                run.Add(ops[i + run.Count]);
            i += run.Count;

            var def = ConfigCollections.Get(run[0].Collection)!;
            var sent = new List<PlanOperation>();
            foreach (var op in run)
            {
                if (op.NeedsGateway && !hasGateway)
                {
                    Record(result, progress, new OperationResult { Key = op.Key, Outcome = "Waiting", Message = NeedsGatewayMessage });
                    continue;
                }
                sent.Add(op);
            }
            if (sent.Count == 0) continue;

            if (sent[0].Action == PlanAction.Delete && def.Write is { DeletePath: null, BatchDeletePath: null, ForgetPath: { } forgetPath })
            {
                var macs = new JsonArray(sent.Select(o => (JsonNode?)o.Body?["mac"]?.DeepClone()).ToArray());
                var body = new JsonObject { ["cmd"] = "forget-sta", ["macs"] = macs }.ToJsonString();
                var response = await _api.SendAsync(def.Scope, HttpMethod.Post, forgetPath, body, ct);
                var outcome = response.IsSuccess ? "Applied" : "Error";
                foreach (var op in sent)
                    Record(result, progress, new OperationResult { Key = op.Key, Outcome = outcome, Message = response.IsSuccess ? null : Error(response) });
            }
            else if (sent[0].Action == PlanAction.Delete && def.Write?.BatchDeletePath != null && sent.Count > 0)
            {
                var ids = new JsonArray(sent.Select(o => (JsonNode?)o.ObjectId).ToArray());
                var response = await _api.SendAsync(def.Scope, HttpMethod.Post, def.Write.BatchDeletePath, ids.ToJsonString(), ct);
                var outcome = response.IsSuccess ? "Applied" : "Error";
                foreach (var op in sent)
                    Record(result, progress, new OperationResult { Key = op.Key, Outcome = outcome, Message = response.IsSuccess ? null : Error(response) });
            }
            else
            {
                if (def.Key == "firewall_zone" && sent.Any(o => o.ContinueOnError))
                    await LogZoneCleanupAsync(def, sent.Where(o => o.ContinueOnError).ToList(), ct);
                if (def.Key == "firewall_zone" && sent[0].Action != PlanAction.Delete)
                    await RememberAutoNetworksAsync(sent, ct);
                foreach (var op in sent)
                {
                    var r = await SendAsync(def, op, ct);
                    Record(result, progress, r);
                }
            }

            // Verify the run against a fresh read of the collection. A read straight after an accepted write can
            // still serve the old object (a device's port_overrides, Network 11.0.99), so a write the console
            // accepted is read again before it is reported as not applied.
            var verified = sent.Select(op => (Op: op, R: result.Operations.Last(x => x.Key == op.Key)))
                .Where(x => x.R.Outcome != "Waiting")
                .Select(x => (x.Op, x.R, Sent: (x.R.Outcome, x.R.Message))).ToList();
            for (var attempt = 0; ; attempt++)
            {
                var (objects, readError) = await new ConfigSnapshotReader(_logger).ReadCollectionAsync(_api, def, ct);
                if (objects == null)
                {
                    result.Operations.Add(new OperationResult { Key = $"{def.Key}:verify", Outcome = "Error", Message = $"Could not re-read {def.DisplayName}: {readError}" });
                    return (false, result);
                }
                foreach (var (op, r, before) in verified)
                {
                    (r.Outcome, r.Message) = before;
                    r.NotApplied.Clear();
                    r.Normalized.Clear();
                    Verify(def, op, objects, r);
                }
                var unconfirmed = verified.Count(x => x.Sent.Outcome == "Applied" && x.R.Outcome is "Partial" or "Error");
                if (unconfirmed == 0 || attempt >= _readBackRetries.Count) break;
                _logger.LogDebug("Change Control: {Count} {Collection} write(s) not read back yet; reading again", unconfirmed, def.Key);
                await Task.Delay(_readBackRetries[attempt], ct);
            }
            foreach (var (op, r, _) in verified)
            {
                if (r.Outcome == "Error")
                {
                    _logger.LogWarning("Change Control write {Key} failed: {Message}", op.Key, r.Message);
                    if (!op.ContinueOnError) return (false, result);
                    failed = true;
                }
            }
        }
        await ReassertAutoAsync(def: ConfigCollections.Get("networkconf")!, ct);
        return (!failed, result);
    }

    // Networks this run wrote with setting_preference "auto", by ID, and whether it wrote a zone.
    private readonly Dictionary<string, JsonObject> _autoNetworks = new(StringComparer.OrdinalIgnoreCase);
    private bool _zoneWritten;

    /// <summary>
    /// A zone write saves every network in the zone as setting_preference "manual" (Network 11), and
    /// a plan writes zones after networks. Each network this run wrote as "auto" gets it back once all writes are done.
    /// </summary>
    /// <summary>Before zone writes: every live "auto" network in a zone about to be written, unless this run wrote it already.</summary>
    private async Task RememberAutoNetworksAsync(List<PlanOperation> zones, CancellationToken ct)
    {
        var inZones = zones.SelectMany(z => z.Body?["network_ids"] as JsonArray ?? []).Select(n => n?.ToString())
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (inZones.Count == 0) return;
        var (networks, _) = await new ConfigSnapshotReader(_logger).ReadCollectionAsync(_api, ConfigCollections.Get("networkconf")!, ct);
        foreach (var n in networks ?? [])
        {
            var id = ConfigState.IdOf(n);
            if (inZones.Contains(id) && n["setting_preference"]?.ToString() == "auto" && !_autoNetworks.ContainsKey(id))
                _autoNetworks[id] = n.DeepClone().AsObject();
        }
    }

    [VendorSpecific("UniFi", "firewall/zone PUT resaves its networks with setting_preference manual")]
    private async Task ReassertAutoAsync(ConfigCollectionDef def, CancellationToken ct)
    {
        if (!_zoneWritten || _autoNetworks.Count == 0 || def.Write?.UpdatePath is not { } update) return;
        foreach (var (id, body) in _autoNetworks)
        {
            var response = await _api.SendAsync(def.Scope, def.Write.UpdateMethod, update.Replace("{id}", id), body.ToJsonString(), ct);
            if (!response.IsSuccess)
                _logger.LogWarning("Change Control: network {Name} could not be set back to Auto after the zone writes: {Message}",
                    body["name"]?.ToString() ?? id, Error(response));
        }
    }

    /// <summary>
    /// Before a zone update that only clears out networks the plan deleted: whether the zone still lists them, or
    /// the network delete took them out (UniFi keeps them, Network 11).
    /// </summary>
    private async Task LogZoneCleanupAsync(ConfigCollectionDef def, List<PlanOperation> ops, CancellationToken ct)
    {
        var (zones, _) = await new ConfigSnapshotReader(_logger).ReadCollectionAsync(_api, def, ct);
        foreach (var op in ops)
        {
            var zone = zones?.FirstOrDefault(z => ConfigState.IdOf(z).Equals(op.ObjectId, StringComparison.OrdinalIgnoreCase));
            if (zone == null) continue;
            var want = (op.Body?["network_ids"] as JsonArray ?? []).Select(n => n?.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var still = (zone["network_ids"] as JsonArray ?? []).Select(n => n?.ToString()).OfType<string>().Where(id => !want.Contains(id)).ToList();
            _logger.LogInformation(still.Count == 0
                    ? "Change Control: zone {Zone} no longer lists the deleted network(s); the delete removed them"
                    : "Change Control: zone {Zone} still lists deleted network(s) {Ids} after the delete; clearing them",
                op.ObjectName, string.Join(", ", still));
        }
    }

    private static readonly string[] DeviceTypeFields = ["dev_id_override", "fingerprint_override"];

    /// <summary>
    /// A client's device type: rest/user answers 200 and stores neither field, so it is set by MAC on its own route,
    /// or cleared there (Network 11.0.97). Null when nothing was needed or it applied, else the console's refusal.
    /// </summary>
    [VendorSpecific("UniFi", "v2 station/{mac}/fingerprint_override: PUT {dev_id_override} sets, DELETE clears")]
    private async Task<string?> WriteDeviceTypeAsync(PlanOperation op, JsonNode? body, CancellationToken ct)
    {
        if (op.Collection != "user" || op.StationMac is not { Length: > 0 } mac || body is not JsonObject b) return null;
        if (!DeviceTypeFields.Any(f => op.WriteFields.Contains(f) || b.ContainsKey(f))) return null;
        // UniFi stores the ID as a string ("4978"); the route takes either.
        var devId = b["dev_id_override"] is JsonValue v
            && (v.TryGetValue<long>(out var id) || (v.TryGetValue<string>(out var s) && long.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id)))
            ? id : (long?)null;
        // Turned off, or no device ID to set: cleared. A partial update that changes only the ID keeps it on.
        var on = b["fingerprint_override"] is JsonValue f && f.TryGetValue<bool>(out var flag) ? flag && devId != null : devId != null;
        var path = $"station/{mac}/fingerprint_override";
        var response = on
            ? await _api.SendAsync(UniFiApiScope.SiteV2, HttpMethod.Put, path, new JsonObject { ["dev_id_override"] = devId }.ToJsonString(), ct)
            : await _api.SendAsync(UniFiApiScope.SiteV2, HttpMethod.Delete, path, null, ct);
        return response.IsSuccess ? null : Error(response);
    }

    private static void Record(PlanResult result, Action<OperationResult>? progress, OperationResult r)
    {
        result.Operations.Add(r);
        progress?.Invoke(r);
    }

    private static string Error(UniFiRawResponse r) => r.ErrorCode ?? $"HTTP {r.Status}";

    private async Task<OperationResult> SendAsync(ConfigCollectionDef def, PlanOperation op, CancellationToken ct)
    {
        var write = def.Write!;
        var body = Resolve(op.Body);
        // A reference to an object not created yet (its create is waiting) must never reach the console as text.
        if (body?.ToJsonString().Contains(PlanBuilder.RefPrefix, StringComparison.Ordinal) == true)
            return new OperationResult { Key = op.Key, Outcome = "Waiting", Message = "Refers to an object this plan has not created yet" };
        var id = ResolveId(op.ObjectId);
        HttpMethod method;
        string path;
        switch (op.Action)
        {
            case PlanAction.Create:
                method = HttpMethod.Post;
                path = write.CreatePath!;
                break;
            case PlanAction.Update:
                method = write.UpdateMethod;
                path = write.UpdatePath!.Replace("{id}", id).Replace("{key}", body?["key"]?.ToString() ?? op.ObjectName);
                break;
            case PlanAction.Delete:
                method = HttpMethod.Delete;
                path = write.DeletePath!.Replace("{id}", id);
                break;
            case PlanAction.Reorder:
                method = HttpMethod.Put;
                path = "firewall-policies/batch-reorder";
                break;
            default:
                return new OperationResult { Key = op.Key, Outcome = "Error", Message = "Unknown action" };
        }

        var response = await _api.SendAsync(def.Scope, method, path, body?.ToJsonString(), ct);
        if (response.IsSuccess)
        {
            if (op.Action == PlanAction.Create && NewId(response.Body) is { } newId)
            {
                _created[op.Key] = newId;
                op.ObjectId = newId;
                if (await KeepAutoSettingAsync(def, body, newId, ct) is { } refusedAuto)
                    return new OperationResult { Key = op.Key, Outcome = "Error", Message = $"Setting Preference: {refusedAuto}" };
            }
            if (def.Key == "firewall_zone" && op.Action != PlanAction.Delete) _zoneWritten = true;
            if (def.Key == "networkconf" && op.Action is PlanAction.Create or PlanAction.Update && op.ObjectId is { } netId
                && body?["setting_preference"]?.ToString() == "auto")
            {
                var keep = body.DeepClone().AsObject();
                keep["_id"] = netId;
                _autoNetworks[netId] = keep;
            }
            if (await WriteDeviceTypeAsync(op, body, ct) is { } refused)
                return new OperationResult { Key = op.Key, Outcome = "Error", Message = $"Device type: {refused}" };
            return new OperationResult { Key = op.Key, Outcome = "Applied" };
        }
        // Delete of something already gone counts as done.
        if (op.Action == PlanAction.Delete && (response.Status == 404 || (response.ErrorCode ?? "").Contains("IdInvalid", StringComparison.OrdinalIgnoreCase)))
            return new OperationResult { Key = op.Key, Outcome = "Applied", Message = "Already gone" };
        return new OperationResult { Key = op.Key, Outcome = "Error", Message = Error(response), NoReply = response.Status == 0 };
    }

    /// <summary>
    /// UniFi stores a network created through the API as setting_preference "manual" whatever the create sent, and keeps
    /// "auto" on an update (Network 11). A network created as "auto" gets it back in a second write.
    /// Null when nothing was needed or it applied, else the console's refusal.
    /// </summary>
    [VendorSpecific("UniFi", "rest/networkconf create forces setting_preference manual; PUT keeps auto")]
    private async Task<string?> KeepAutoSettingAsync(ConfigCollectionDef def, JsonNode? body, string id, CancellationToken ct)
    {
        if (def.Key != "networkconf" || body?["setting_preference"]?.ToString() != "auto" || def.Write?.UpdatePath is not { } update)
            return null;
        var again = body.DeepClone().AsObject();
        again["_id"] = id;
        var response = await _api.SendAsync(def.Scope, def.Write.UpdateMethod, update.Replace("{id}", id), again.ToJsonString(), ct);
        return response.IsSuccess ? null : Error(response);
    }

    internal const string NeedsGatewayMessage = "Needs the gateway adopted first";

    /// <summary>The created object's ID from a v1 envelope or a v2 object.</summary>
    internal static string? NewId(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject o && o["data"] is JsonArray data) node = data.FirstOrDefault();
            if (node is JsonArray arr) node = arr.FirstOrDefault();
            return node is JsonObject obj ? (obj["_id"] ?? obj["id"])?.ToString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private string? ResolveId(string? id) =>
        id != null && id.StartsWith(PlanBuilder.RefPrefix, StringComparison.Ordinal) && _created.TryGetValue(id[PlanBuilder.RefPrefix.Length..], out var real) ? real : id;

    /// <summary>The body with every <c>@ref:</c> token replaced by the ID its object got.</summary>
    internal JsonNode? Resolve(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Resolve(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Resolve).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith(PlanBuilder.RefPrefix, StringComparison.Ordinal) =>
            _created.TryGetValue(s[PlanBuilder.RefPrefix.Length..], out var id) ? JsonValue.Create(id) : v.DeepClone(),
        _ => node?.DeepClone(),
    };

    /// <summary>
    /// Compares the re-read object with the fields the write sent. A refused write that the re-read
    /// shows as applied counts as applied (UniFi can answer an error and still write, zone create).
    /// </summary>
    private void Verify(ConfigCollectionDef def, PlanOperation op, IReadOnlyList<JsonObject> objects, OperationResult r)
    {
        var refusal = r.Outcome == "Error" ? r.Message : null;
        JsonObject? current = op.Action switch
        {
            PlanAction.Create => op.ObjectId != null
                ? objects.FirstOrDefault(o => ConfigState.IdOf(o) == ResolveId(op.ObjectId))
                : objects.FirstOrDefault(o => ConfigState.DisplayName(def, o) == op.ObjectName),
            PlanAction.Update => objects.FirstOrDefault(o => ConfigState.IdOf(o) == ResolveId(op.ObjectId)),
            _ => null,
        };

        switch (op.Action)
        {
            case PlanAction.Delete:
                // A forgotten client still connected comes back as a bare record, possibly under the same ID: only one
                // that kept its config (name, fixed IP, ...) means the forget did not apply.
                var forgot = def.Write is { DeletePath: null, BatchDeletePath: null, ForgetPath: not null };
                var stillThere = objects.Any(o => ConfigState.IdOf(o) == ResolveId(op.ObjectId) && (!forgot || ConfigDiffEngine.HasClientConfig(o)));
                r.Outcome = stillThere ? "Error" : "Applied";
                r.Message = stillThere ? refusal ?? "Still on the console after the delete" : refusal == null ? r.Message : $"Applied, although UniFi answered {refusal}";
                return;
            case PlanAction.Reorder:
                return;
        }

        if (current == null)
        {
            r.Outcome = "Error";
            r.Message = refusal ?? "Not found on the console after the write";
            return;
        }
        if (op.Action == PlanAction.Create && op.ObjectId == null)
        {
            op.ObjectId = ConfigState.IdOf(current);
            _created[op.Key] = op.ObjectId;
        }

        var body = Resolve(op.Body) as JsonObject ?? [];
        var normalized = NormalizedFields.GetValueOrDefault(def.Key, []);
        foreach (var field in op.WriteFields)
        {
            if (!body.ContainsKey(field)) continue;
            var sentValue = body[field];
            var got = current[field];
            var same = ConfigCollections.IsSecretField(def, field)
                ? ConfigDiffEngine.HashOf(sentValue) == ConfigDiffEngine.HashOf(got)
                : Equivalent(def, field, sentValue, got);
            if (same) continue;
            if (normalized.Contains(field) || IsNormalizedHere(def.Key, field, current)) r.Normalized.Add(field);
            else r.NotApplied.Add(field);
        }

        if (r.NotApplied.Count > 0)
        {
            r.Outcome = refusal != null ? "Error" : "Partial";
            r.Message = refusal ?? $"Not applied by UniFi: {string.Join(", ", r.NotApplied.Select(ChangeDescriber.LabelFor))}";
        }
        else
        {
            r.Outcome = "Applied";
            if (refusal != null) r.Message = $"Applied, although UniFi answered {refusal}";
            if (r.Normalized.Count > 0) r.Message = $"Normalized by UniFi: {string.Join(", ", r.Normalized.Select(ChangeDescriber.LabelFor))}";
        }
    }

    /// <summary>
    /// A field UniFi drops only in some shapes: Network 11 keeps no voice network on a native port profile
    /// (one untagged network; grounding.md, phase 2).
    /// </summary>
    internal static bool IsNormalizedHere(string collection, string field, JsonObject current) =>
        collection == "portconf" && field == "voice_networkconf_id" && current["forward"]?.ToString() == "native";

    /// <summary>Sent and read-back values match, with sets compared as sets and only the sent part of radio entries.</summary>
    private static bool Equivalent(ConfigCollectionDef def, string field, JsonNode? sent, JsonNode? got)
    {
        if (CanonicalJson.DeepEquals(sent, got)) return true;
        // An emptied list reads back missing: UniFi stores no empty list.
        if (sent is JsonArray { Count: 0 } && got is null or JsonArray { Count: 0 }) return true;
        if (def.SetArrays.Contains(field) && sent is JsonArray sa && got is JsonArray ga)
            return sa.Select(CanonicalJson.Serialize).ToHashSet().SetEquals(ga.Select(CanonicalJson.Serialize));
        if (def.KeyedArrays.TryGetValue(field, out var key) && sent is JsonArray s && got is JsonArray g)
        {
            // UniFi adds defaults to array entries; every field sent must read back, extra fields are fine.
            foreach (var entry in s.OfType<JsonObject>())
            {
                var match = g.OfType<JsonObject>().FirstOrDefault(x => x[key]?.ToString() == entry[key]?.ToString());
                if (match == null) return false;
                if (entry.Any(p => !CanonicalJson.DeepEquals(p.Value, match[p.Key]))) return false;
            }
            return true;
        }
        if (sent is JsonObject so && got is JsonObject go)
            return so.All(p => CanonicalJson.DeepEquals(p.Value, go[p.Key]));
        return false;
    }
}
