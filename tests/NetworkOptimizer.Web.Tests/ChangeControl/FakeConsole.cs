using System.Text.Json.Nodes;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.ChangeControl;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// An in-memory UniFi Console: serves each collection's read path and applies creates, updates,
/// deletes, and batch deletes on the catalog's write paths. v1 answers in the data envelope, v2 bare.
/// </summary>
internal sealed class FakeConsole : IUniFiConfigApi
{
    private int _nextId = 1000;
    public ConfigState State { get; }
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

    /// <summary>Paths answered with this status instead of being applied.</summary>
    public Dictionary<string, int> Refuse { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths that answer an error but write anyway (UniFi's zone create).</summary>
    public HashSet<string> RefuseButWrite { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fields the console silently drops on write.</summary>
    public HashSet<string> DropFields { get; } = [];

    /// <summary>An empty list written reads back missing, as UniFi stores none (grounding.md 2026-10-29).</summary>
    public bool EmptyListsReadBackMissing { get; set; }

    public FakeConsole(ConfigState state) => State = state;

    /// <summary>False simulates a console the server cannot reach.</summary>
    public bool Connected { get; set; } = true;

    public bool IsConnected => Connected;

    public Task<(int Status, byte[] Bytes)> GetBytesAsync(UniFiApiScope scope, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult((404, Array.Empty<byte>()));

    public Task<UniFiRawResponse> SendAsync(UniFiApiScope scope, HttpMethod method, string path, string? json = null, CancellationToken cancellationToken = default)
    {
        Requests.Add((method, path, json));
        if (!Connected) return Task.FromResult(new UniFiRawResponse(0, "unreachable"));
        if (Refuse.TryGetValue(path, out var status))
            return Task.FromResult(new UniFiRawResponse(status, """{"meta":{"rc":"error","msg":"api.err.Refused"}}"""));

        if (method == HttpMethod.Get)
        {
            if (path == "stat/health") return Ok(scope, [new JsonObject { ["subsystem"] = "www", ["status"] = "ok" }], list: true);
            if (path == "stat/sysinfo") return Ok(scope, [new JsonObject { ["version"] = "10.6.106" }], list: true);
            var def = ConfigCollections.All.FirstOrDefault(d => d.ReadPath == path && d.Scope == scope);
            if (def == null || !State.Collections.ContainsKey(def.Key)) return Task.FromResult(new UniFiRawResponse(404, "{}"));
            return Ok(scope, State.Get(def.Key).Select(o => (JsonObject)o.DeepClone()).ToList(), list: true);
        }

        // Ahead of the update paths: "firewall-policies/{id}" would take batch-reorder for an ID.
        if (path == "firewall-policies/batch-reorder") return Task.FromResult(new UniFiRawResponse(200, "{}"));
        foreach (var def in ConfigCollections.All.Where(d => d.Write != null && d.Scope == scope))
        {
            var w = def.Write!;
            var list = State.Collections.GetValueOrDefault(def.Key);
            if (list == null) continue;
            if (method == HttpMethod.Post && path == w.CreatePath)
            {
                var obj = Clean((JsonObject)JsonNode.Parse(json!)!);
                obj["_id"] = $"id{_nextId++}";
                list.Add(obj);
                return RefuseButWrite.Contains(path) ? Task.FromResult(new UniFiRawResponse(404, """{"code":"Could not find hotspot firewall zone"}""")) : Ok(scope, [obj]);
            }
            if (method == HttpMethod.Post && path == w.BatchDeletePath)
            {
                var ids = ((JsonArray)JsonNode.Parse(json!)!).Select(n => n!.ToString()).ToHashSet();
                list.RemoveAll(o => ids.Contains(ConfigState.IdOf(o)));
                return Task.FromResult(new UniFiRawResponse(204, ""));
            }
            if (w.UpdatePath != null && method == w.UpdateMethod && Matches(w.UpdatePath, path, out var id))
            {
                var target = list.FirstOrDefault(o => ConfigState.IdOf(o) == id);
                if (target == null) return Task.FromResult(new UniFiRawResponse(400, """{"meta":{"rc":"error","msg":"api.err.IdInvalid"}}"""));
                foreach (var (k, v) in Clean((JsonObject)JsonNode.Parse(json!)!))
                {
                    if (EmptyListsReadBackMissing && v is JsonArray { Count: 0 }) target.Remove(k);
                    else target[k] = v?.DeepClone();
                }
                return Ok(scope, [target]);
            }
            if (w.DeletePath != null && method == HttpMethod.Delete && Matches(w.DeletePath, path, out var did))
            {
                list.RemoveAll(o => ConfigState.IdOf(o) == did);
                return Task.FromResult(new UniFiRawResponse(204, ""));
            }
        }
        return Task.FromResult(new UniFiRawResponse(404, "{}"));
    }

    private JsonObject Clean(JsonObject o)
    {
        foreach (var f in DropFields) o.Remove(f);
        return o;
    }

    private static bool Matches(string template, string path, out string id)
    {
        id = "";
        var t = template.Split('/');
        var p = path.Split('/');
        if (t.Length != p.Length) return false;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == "{id}") { id = p[i]; continue; }
            if (t[i] == "{key}") continue;
            if (t[i] != p[i]) return false;
        }
        return true;
    }

    private static Task<UniFiRawResponse> Ok(UniFiApiScope scope, List<JsonObject> data, bool list = false)
    {
        var arr = new JsonArray(data.Select(o => (JsonNode?)o.DeepClone()).ToArray());
        var body = scope == UniFiApiScope.SiteV1
            ? new JsonObject { ["meta"] = new JsonObject { ["rc"] = "ok" }, ["data"] = arr }.ToJsonString()
            : !list && data.Count == 1 ? data[0].ToJsonString() : arr.ToJsonString();
        return Task.FromResult(new UniFiRawResponse(200, body));
    }
}
