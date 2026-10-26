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

    public PlanExecutor(IUniFiConfigApi api, ILogger logger)
    {
        _api = api;
        _logger = logger;
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
                    Record(result, progress, new OperationResult { Key = op.Key, Outcome = "Waiting", Message = "Needs the gateway adopted first" });
                    continue;
                }
                sent.Add(op);
            }
            if (sent.Count == 0) continue;

            if (sent[0].Action == PlanAction.Delete && def.Write?.BatchDeletePath != null && sent.Count > 0)
            {
                var ids = new JsonArray(sent.Select(o => (JsonNode?)o.ObjectId).ToArray());
                var response = await _api.SendAsync(def.Scope, HttpMethod.Post, def.Write.BatchDeletePath, ids.ToJsonString(), ct);
                var outcome = response.IsSuccess ? "Applied" : "Error";
                foreach (var op in sent)
                    Record(result, progress, new OperationResult { Key = op.Key, Outcome = outcome, Message = response.IsSuccess ? null : Error(response) });
            }
            else
            {
                foreach (var op in sent)
                {
                    var r = await SendAsync(def, op, ct);
                    Record(result, progress, r);
                }
            }

            // Verify the run against a fresh read of the collection.
            var (objects, readError) = await new ConfigSnapshotReader(_logger).ReadCollectionAsync(_api, def, ct);
            if (objects == null)
            {
                result.Operations.Add(new OperationResult { Key = $"{def.Key}:verify", Outcome = "Error", Message = $"Could not re-read {def.DisplayName}: {readError}" });
                return (false, result);
            }
            foreach (var op in sent)
            {
                var r = result.Operations.Last(x => x.Key == op.Key);
                if (r.Outcome == "Waiting") continue;
                Verify(def, op, objects, r);
                if (r.Outcome == "Error")
                {
                    _logger.LogWarning("Change Control write {Key} failed: {Message}", op.Key, r.Message);
                    return (false, result);
                }
            }
        }
        return (true, result);
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
            }
            return new OperationResult { Key = op.Key, Outcome = "Applied" };
        }
        // Delete of something already gone counts as done.
        if (op.Action == PlanAction.Delete && (response.Status == 404 || (response.ErrorCode ?? "").Contains("IdInvalid", StringComparison.OrdinalIgnoreCase)))
            return new OperationResult { Key = op.Key, Outcome = "Applied", Message = "Already gone" };
        return new OperationResult { Key = op.Key, Outcome = "Error", Message = Error(response) };
    }

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
                var stillThere = objects.Any(o => ConfigState.IdOf(o) == ResolveId(op.ObjectId));
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
            if (normalized.Contains(field)) r.Normalized.Add(field);
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
            if (r.Normalized.Count > 0) r.Message = $"Normalized by UniFi: {string.Join(", ", r.Normalized)}";
        }
    }

    /// <summary>Sent and read-back values match, with sets compared as sets and only the sent part of radio entries.</summary>
    private static bool Equivalent(ConfigCollectionDef def, string field, JsonNode? sent, JsonNode? got)
    {
        if (CanonicalJson.DeepEquals(sent, got)) return true;
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
