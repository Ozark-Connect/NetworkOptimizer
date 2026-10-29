using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>A template for lists.</summary>
public sealed class TemplateView
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? SourceSiteSlug { get; init; }
    public List<string> Collections { get; init; } = [];
    /// <summary>Applied, its lists (a WLAN's PPSKs) replace each site's instead of adding to them.</summary>
    public bool ReplaceLists { get; init; }
    /// <summary>Its content holds a list <see cref="ReplaceLists"/> applies to (a WLAN with PPSKs).</summary>
    public bool HasLists { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// Reads and writes templates in the main database, content encrypted. Not gated itself: the gated
/// <see cref="IConfigTemplateService"/> and the site service call it. Instance-wide on purpose: the
/// SaaS fork partitions it by decorating <see cref="IConfigTemplateService"/>.
/// </summary>
public sealed class ConfigTemplateStore
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly ICredentialProtectionService _crypto;

    public ConfigTemplateStore(IDbContextFactory<NetworkOptimizerDbContext> mainDb, ICredentialProtectionService crypto)
    {
        _mainDb = mainDb;
        _crypto = crypto;
    }

    public async Task<List<TemplateView>> ListAsync(CancellationToken ct)
    {
        await using var db = await _mainDb.CreateDbContextAsync(ct);
        return (await db.ConfigTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct)).Select(t => View(t, Content(t))).ToList();
    }

    private static TemplateView View(ConfigTemplate t, JsonObject content) => new()
    {
        Id = t.Id, Name = t.Name, Description = t.Description, SourceSiteSlug = t.SourceSiteSlug, UpdatedAt = t.UpdatedAt,
        Collections = JsonSerializer.Deserialize<List<string>>(t.CollectionsJson) ?? [], ReplaceLists = t.ReplaceLists,
        HasLists = HasLists(content),
    };

    private JsonObject Content(ConfigTemplate t) => JsonNode.Parse(_crypto.Decrypt(t.ContentEncrypted)) as JsonObject ?? [];

    /// <summary>Whether any carried object has a non-empty list that merges on a template (a WLAN's PPSKs).</summary>
    internal static bool HasLists(JsonObject content) =>
        content.Any(c => c.Key != ReferencesKey && ConfigCollections.Get(c.Key) is { MergeOnTemplate.Count: > 0 } def
            && (c.Value as JsonArray)?.OfType<JsonObject>().Any(o => def.MergeOnTemplate.Keys.Any(f => o[f] is JsonArray { Count: > 0 })) == true);

    /// <summary>Content key holding <see cref="References"/>: never a collection key.</summary>
    private const string ReferencesKey = "_references";

    /// <summary>A template's content as a state (only the template's collections).</summary>
    public async Task<(TemplateView View, ConfigState State)?> LoadAsync(int id, CancellationToken ct) =>
        await LoadCoreAsync(id, withReferences: false, ct);

    /// <summary>
    /// A template's content plus the objects it references in collections it does not carry, so a plan
    /// can match them on the target and Edit can name them. Plans still write <see cref="TemplateView.Collections"/> only.
    /// </summary>
    public async Task<(TemplateView View, ConfigState State)?> LoadWithReferencesAsync(int id, CancellationToken ct) =>
        await LoadCoreAsync(id, withReferences: true, ct);

    private async Task<(TemplateView View, ConfigState State)?> LoadCoreAsync(int id, bool withReferences, CancellationToken ct)
    {
        await using var db = await _mainDb.CreateDbContextAsync(ct);
        var t = await db.ConfigTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t == null) return null;
        var state = new ConfigState { ReadAt = t.UpdatedAt };
        var content = Content(t);
        foreach (var (collection, list) in content)
            if (collection != ReferencesKey) state.Collections[collection] = Objects(list);
        if (withReferences && content[ReferencesKey] is JsonObject refs)
            foreach (var (collection, list) in refs)
                if (!state.Collections.ContainsKey(collection)) state.Collections[collection] = Objects(list);
        return (View(t, content), state);
    }

    private static List<JsonObject> Objects(JsonNode? list) =>
        (list as JsonArray)?.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList() ?? [];

    /// <summary>
    /// The objects the template's collections point at in collections it does not carry (a PPSK's
    /// network, a policy's zone), secrets removed: enough to match them on another site, never written.
    /// </summary>
    internal static Dictionary<string, List<JsonObject>> References(ConfigState state, IReadOnlyCollection<string> collections)
    {
        var carried = collections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byId = state.ObjectsById();
        var found = new Dictionary<string, List<JsonObject>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o) if (k is not "_id" and not "id") Walk(v);
                    break;
                case JsonArray a:
                    foreach (var e in a) Walk(e);
                    break;
                case JsonValue v when v.TryGetValue<string>(out var s) && byId.TryGetValue(s, out var target)
                                       && !carried.Contains(target.Collection) && seen.Add(s):
                    if (!found.TryGetValue(target.Collection, out var list)) found[target.Collection] = list = [];
                    list.Add(WithoutSecrets(ConfigCollections.Get(target.Collection), target.Object));
                    break;
            }
        }
        foreach (var c in carried)
            foreach (var o in state.Get(c)) Walk(o);
        return found;
    }

    private static JsonObject WithoutSecrets(ConfigCollectionDef? def, JsonObject o)
    {
        JsonNode? Strip(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Where(p => !ConfigCollections.IsSecretField(def, p.Key)).Select(p => KeyValuePair.Create(p.Key, Strip(p.Value)))),
            JsonArray arr => new JsonArray(arr.Select(Strip).ToArray()),
            _ => node?.DeepClone(),
        };
        return (JsonObject)Strip(o)!;
    }

    /// <summary>
    /// Creates or updates a template from a state's collections, with the objects they reference.
    /// <paramref name="replaceLists"/> sets <see cref="TemplateView.ReplaceLists"/>; null leaves it as stored.
    /// </summary>
    public async Task<int> SaveAsync(int? id, string name, string? description, string? sourceSite, ConfigState state,
        IReadOnlyCollection<string> collections, string by, CancellationToken ct, bool? replaceLists = null)
    {
        var content = new JsonObject();
        foreach (var c in collections)
            if (state.Collections.TryGetValue(c, out var list))
                content[c] = new JsonArray(list.Select(o => (JsonNode?)o.DeepClone()).ToArray());
        var references = References(state, collections);
        if (references.Count > 0)
            content[ReferencesKey] = new JsonObject(references.Select(r =>
                KeyValuePair.Create(r.Key, (JsonNode?)new JsonArray(r.Value.Select(o => (JsonNode?)o).ToArray()))));
        await using var db = await _mainDb.CreateDbContextAsync(ct);
        var t = id == null ? null : await db.ConfigTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t == null)
        {
            t = new ConfigTemplate { CreatedAt = DateTime.UtcNow, CreatedBy = by };
            db.ConfigTemplates.Add(t);
        }
        t.Name = name;
        t.Description = description;
        t.SourceSiteSlug = sourceSite;
        t.CollectionsJson = JsonSerializer.Serialize(collections);
        if (replaceLists is { } replace) t.ReplaceLists = replace;
        t.ContentEncrypted = _crypto.EncryptForSite(content.ToJsonString(), sourceSite ?? SiteManagementService.DefaultSiteSlug);
        t.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return t.Id;
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        await using var db = await _mainDb.CreateDbContextAsync(ct);
        await db.ConfigTemplates.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Changes a template's name, description, and list handling, leaving its content alone.</summary>
    public async Task RenameAsync(int id, string name, string? description, bool replaceLists, CancellationToken ct)
    {
        await using var db = await _mainDb.CreateDbContextAsync(ct);
        var t = await db.ConfigTemplates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new InvalidOperationException("Template not found.");
        t.Name = name;
        t.Description = description;
        t.ReplaceLists = replaceLists;
        t.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Templates (spec RBAC: create and edit are instance Admin; applying one is Site Admin on the
/// target, through <see cref="IChangeControlService"/>'s site). Instance-wide.
/// </summary>
[MutatingService]
public interface IConfigTemplateService
{
    /// <summary>Every template.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<TemplateView>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a template from a site's snapshot (null: its live config).</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlTemplateSaved, TargetType = "config_template", InstanceScoped = true)]
    Task<int> CreateFromSiteAsync(string siteSlug, int? snapshotId, string name, string? description, IReadOnlyCollection<string> collections, bool replaceLists = false, CancellationToken cancellationToken = default);

    /// <summary>Replaces a template's content from YAML.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlTemplateSaved, TargetType = "config_template", InstanceScoped = true)]
    Task<List<YamlProblem>> UpdateFromYamlAsync(int templateId, string name, string? description, string yaml, CancellationToken cancellationToken = default);

    /// <summary>A template as YAML (secrets masked as tokens that resolve against the template).</summary>
    [RequireRole(Roles.Admin)]
    Task<string> GetYamlAsync(int templateId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a template.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlTemplateDeleted, TargetType = "config_template", InstanceScoped = true)]
    Task DeleteAsync(int templateId, CancellationToken cancellationToken = default);

    /// <summary>Renames a template and replaces its description.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlTemplateSaved, TargetType = "config_template", InstanceScoped = true)]
    Task RenameAsync(int templateId, string name, string? description, bool replaceLists, CancellationToken cancellationToken = default);

    /// <summary>Edit tab: one collection of a template as YAML (secrets masked), its object counts, and the editor schema.</summary>
    [RequireRole(Roles.Admin)]
    Task<(string Yaml, Dictionary<string, int> Counts, string Schema)> GetEditBaseAsync(int templateId, string collection, CancellationToken cancellationToken = default);

    /// <summary>Edit tab's Add: a new object as YAML, from the template's source site's create defaults when it is reachable.</summary>
    [RequireRole(Roles.Admin)]
    Task<string> GetNewObjectYamlAsync(int templateId, string collection, CancellationToken cancellationToken = default);

    /// <summary>Edit tab: checks a template's edited files as they are typed, and counts the objects they change.</summary>
    [RequireRole(Roles.Admin)]
    Task<YamlValidation> ValidateYamlAsync(int templateId, IReadOnlyDictionary<string, string> filesByCollection, CancellationToken cancellationToken = default);

    /// <summary>Edit tab: saves a template's edited files. Returns the problems; nothing is saved when one is an error.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlTemplateSaved, TargetType = "config_template", InstanceScoped = true)]
    Task<List<YamlProblem>> SaveFilesAsync(int templateId, IReadOnlyDictionary<string, string> filesByCollection, CancellationToken cancellationToken = default);

    /// <summary>Edit tab: a masked secret of a template as YAML, for the Show button.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlSecretRevealed, TargetType = "config_secret", InstanceScoped = true)]
    Task<string?> RevealSecretAsync(int templateId, string token, CancellationToken cancellationToken = default);
}

/// <summary>The template service.</summary>
public sealed class ConfigTemplateService : IConfigTemplateService
{
    private readonly ConfigTemplateStore _store;
    private readonly ChangeControlRegistry _registry;
    private readonly Identity.ICallerContext _caller;

    public ConfigTemplateService(ConfigTemplateStore store, ChangeControlRegistry registry, Identity.ICallerContext caller)
    {
        _store = store;
        _registry = registry;
        _caller = caller;
    }

    public Task<List<TemplateView>> ListAsync(CancellationToken cancellationToken = default) => _store.ListAsync(cancellationToken);

    public async Task<int> CreateFromSiteAsync(string siteSlug, int? snapshotId, string name, string? description,
        IReadOnlyCollection<string> collections, bool replaceLists = false, CancellationToken cancellationToken = default)
    {
        var engine = _registry.GetFor(siteSlug);
        var state = snapshotId is { } id
            ? await engine.LoadSnapshotStateAsync(id, cancellationToken) ?? throw new InvalidOperationException("Snapshot not found.")
            : await engine.ReadLiveAsync(cancellationToken);
        // Devices and clients are per site; a template carries site-wide objects only.
        var allowed = collections.Where(c => c is not "device" and not "user" && ConfigCollections.Get(c)?.Write != null).ToList();
        return await _store.SaveAsync(null, name, description, siteSlug, state, allowed, _caller.Current?.ActorName ?? "unknown", cancellationToken, replaceLists);
    }

    public async Task<List<YamlProblem>> UpdateFromYamlAsync(int templateId, string name, string? description, string yaml, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(templateId, cancellationToken);
        var parsed = YamlConverter.Parse(yaml, loaded.State, "template.yaml");
        if (parsed.HasErrors) return parsed.Problems;
        var state = loaded.State.Clone();
        foreach (var (c, list) in parsed.Collections) state.Collections[c] = list;
        YamlConverter.AssignPermanentIds(state);
        // The template's collections plus any the file adds; its references stay references.
        var collections = loaded.View.Collections.Union(parsed.Collections.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        await _store.SaveAsync(templateId, name, description, loaded.View.SourceSiteSlug, state, collections,
            _caller.Current?.ActorName ?? "unknown", cancellationToken);
        return parsed.Problems;
    }

    public async Task<string> GetYamlAsync(int templateId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(templateId, cancellationToken);
        // Masked, not redacted: UpdateFromYamlAsync parses against this same state, so each token
        // resolves to the template's own secret. <keep current> would be stored as the value.
        // The references name what the template's objects point at; they are not written themselves.
        return YamlConverter.Export(loaded.State, loaded.View.Collections, SecretMode.Mask);
    }

    public Task DeleteAsync(int templateId, CancellationToken cancellationToken = default) => _store.DeleteAsync(templateId, cancellationToken);

    public Task RenameAsync(int templateId, string name, string? description, bool replaceLists, CancellationToken cancellationToken = default) =>
        _store.RenameAsync(templateId, name, description, replaceLists, cancellationToken);

    // With its references, so the Edit tab names a PPSK's network and Save keeps the references.
    private async Task<(TemplateView View, ConfigState State)> LoadAsync(int templateId, CancellationToken ct) =>
        await _store.LoadWithReferencesAsync(templateId, ct) ?? throw new InvalidOperationException("Template not found.");

    public async Task<(string Yaml, Dictionary<string, int> Counts, string Schema)> GetEditBaseAsync(int templateId, string collection, CancellationToken cancellationToken = default)
    {
        var state = (await LoadAsync(templateId, cancellationToken)).State;
        var counts = state.Collections.ToDictionary(k => k.Key, k => k.Value.Count);
        var schema = ConfigCollections.Get(collection) is { } def ? YamlConverter.EditSchema(state, [def]).ToJsonString() : "{}";
        return (YamlConverter.Export(state, [collection], SecretMode.Mask, ids: true), counts, schema);
    }

    public async Task<string> GetNewObjectYamlAsync(int templateId, string collection, CancellationToken cancellationToken = default)
    {
        var def = ConfigCollections.Get(collection) is { } d && NewObjects.CanAdd(d) ? d : throw new InvalidOperationException("Objects of this collection cannot be added here.");
        var (view, state) = await LoadAsync(templateId, cancellationToken);
        // The source site may be gone; its defaults are a nicety, the skeleton stands without them.
        IUniFiConfigApi? api = null;
        try { if (view.SourceSiteSlug is { } slug) api = _registry.GetFor(slug).Api; }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return NewObjects.Yaml(def, state, await NewObjects.ReadDefaultsAsync(api, def, cancellationToken));
    }

    public async Task<YamlValidation> ValidateYamlAsync(int templateId, IReadOnlyDictionary<string, string> filesByCollection, CancellationToken cancellationToken = default)
    {
        var baseState = (await LoadAsync(templateId, cancellationToken)).State;
        var (desired, problems, touched) = ChangeControlService.ParseEdits(baseState, filesByCollection);
        var ops = touched.Count == 0 ? [] : PlanBuilder.Build(desired, baseState, new PlanOptions { Collections = touched }).Operations;
        return new YamlValidation
        {
            Problems = problems, EditedObjects = ops.Count, EditedCollections = ops.Select(o => o.Collection).Distinct().Count(),
            EditedByCollection = ops.GroupBy(o => o.Collection).ToDictionary(g => g.Key, g => g.Count()),
            DeletedByCollection = ops.Where(o => o.Action == PlanAction.Delete).GroupBy(o => o.Collection).ToDictionary(g => g.Key, g => g.Count()),
        };
    }

    public async Task<List<YamlProblem>> SaveFilesAsync(int templateId, IReadOnlyDictionary<string, string> filesByCollection, CancellationToken cancellationToken = default)
    {
        var (view, state) = await LoadAsync(templateId, cancellationToken);
        var (desired, problems, _) = ChangeControlService.ParseEdits(state, filesByCollection);
        if (problems.Any(p => p.Level == "Error")) return problems;
        YamlConverter.AssignPermanentIds(desired);
        // The template keeps its own collections: an edit never adds one it was not created with.
        await _store.SaveAsync(templateId, view.Name, view.Description, view.SourceSiteSlug, desired, view.Collections,
            _caller.Current?.ActorName ?? "unknown", cancellationToken);
        return problems;
    }

    public async Task<string?> RevealSecretAsync(int templateId, string token, CancellationToken cancellationToken = default)
    {
        if (YamlConverter.SecretTokenPath(token) is not { } path) return null;
        var state = (await LoadAsync(templateId, cancellationToken)).State;
        return YamlConverter.FindSecret(state, path) is { } found ? YamlConverter.ScalarText(found.Value) : null;
    }
}
