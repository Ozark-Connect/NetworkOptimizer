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
        return (await db.ConfigTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct)).Select(View).ToList();
    }

    private static TemplateView View(ConfigTemplate t) => new()
    {
        Id = t.Id, Name = t.Name, Description = t.Description, SourceSiteSlug = t.SourceSiteSlug, UpdatedAt = t.UpdatedAt,
        Collections = JsonSerializer.Deserialize<List<string>>(t.CollectionsJson) ?? [],
    };

    /// <summary>A template's content as a state (only the template's collections).</summary>
    public async Task<(TemplateView View, ConfigState State)?> LoadAsync(int id, CancellationToken ct)
    {
        await using var db = await _mainDb.CreateDbContextAsync(ct);
        var t = await db.ConfigTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t == null) return null;
        var state = new ConfigState { ReadAt = t.UpdatedAt };
        var content = JsonNode.Parse(_crypto.Decrypt(t.ContentEncrypted)) as JsonObject ?? [];
        foreach (var (collection, list) in content)
            state.Collections[collection] = (list as JsonArray)?.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList() ?? [];
        return (View(t), state);
    }

    /// <summary>Creates or updates a template from a state's collections.</summary>
    public async Task<int> SaveAsync(int? id, string name, string? description, string? sourceSite, ConfigState state,
        IReadOnlyCollection<string> collections, string by, CancellationToken ct)
    {
        var content = new JsonObject();
        foreach (var c in collections)
            if (state.Collections.TryGetValue(c, out var list))
                content[c] = new JsonArray(list.Select(o => (JsonNode?)o.DeepClone()).ToArray());
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

    /// <summary>Changes a template's name and description, leaving its content alone.</summary>
    public async Task RenameAsync(int id, string name, string? description, CancellationToken ct)
    {
        await using var db = await _mainDb.CreateDbContextAsync(ct);
        var t = await db.ConfigTemplates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new InvalidOperationException("Template not found.");
        t.Name = name;
        t.Description = description;
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
    Task<int> CreateFromSiteAsync(string siteSlug, int? snapshotId, string name, string? description, IReadOnlyCollection<string> collections, CancellationToken cancellationToken = default);

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
    Task RenameAsync(int templateId, string name, string? description, CancellationToken cancellationToken = default);

    /// <summary>Edit tab: one collection of a template as YAML (secrets masked), its object counts, and the editor schema.</summary>
    [RequireRole(Roles.Admin)]
    Task<(string Yaml, Dictionary<string, int> Counts, string Schema)> GetEditBaseAsync(int templateId, string collection, CancellationToken cancellationToken = default);

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
        IReadOnlyCollection<string> collections, CancellationToken cancellationToken = default)
    {
        var engine = _registry.GetFor(siteSlug);
        var state = snapshotId is { } id
            ? await engine.LoadSnapshotStateAsync(id, cancellationToken) ?? throw new InvalidOperationException("Snapshot not found.")
            : await engine.ReadLiveAsync(cancellationToken);
        // Devices and clients are per site; a template carries site-wide objects only.
        var allowed = collections.Where(c => c is not "device" and not "user" && ConfigCollections.Get(c)?.Write != null).ToList();
        return await _store.SaveAsync(null, name, description, siteSlug, state, allowed, _caller.Current?.ActorName ?? "unknown", cancellationToken);
    }

    public async Task<List<YamlProblem>> UpdateFromYamlAsync(int templateId, string name, string? description, string yaml, CancellationToken cancellationToken = default)
    {
        var loaded = await _store.LoadAsync(templateId, cancellationToken) ?? throw new InvalidOperationException("Template not found.");
        var parsed = YamlConverter.Parse(yaml, loaded.State, "template.yaml");
        if (parsed.HasErrors) return parsed.Problems;
        var state = loaded.State.Clone();
        foreach (var (c, list) in parsed.Collections) state.Collections[c] = list;
        YamlConverter.AssignPermanentIds(state);
        await _store.SaveAsync(templateId, name, description, loaded.View.SourceSiteSlug, state, state.Collections.Keys.ToList(),
            _caller.Current?.ActorName ?? "unknown", cancellationToken);
        return parsed.Problems;
    }

    public async Task<string> GetYamlAsync(int templateId, CancellationToken cancellationToken = default)
    {
        var loaded = await _store.LoadAsync(templateId, cancellationToken) ?? throw new InvalidOperationException("Template not found.");
        // Masked, not redacted: UpdateFromYamlAsync parses against this same state, so each token
        // resolves to the template's own secret. <keep current> would be stored as the value.
        return YamlConverter.Export(loaded.State, null, SecretMode.Mask);
    }

    public Task DeleteAsync(int templateId, CancellationToken cancellationToken = default) => _store.DeleteAsync(templateId, cancellationToken);

    public Task RenameAsync(int templateId, string name, string? description, CancellationToken cancellationToken = default) =>
        _store.RenameAsync(templateId, name, description, cancellationToken);

    private async Task<(TemplateView View, ConfigState State)> LoadAsync(int templateId, CancellationToken ct) =>
        await _store.LoadAsync(templateId, ct) ?? throw new InvalidOperationException("Template not found.");

    public async Task<(string Yaml, Dictionary<string, int> Counts, string Schema)> GetEditBaseAsync(int templateId, string collection, CancellationToken cancellationToken = default)
    {
        var state = (await LoadAsync(templateId, cancellationToken)).State;
        var counts = state.Collections.ToDictionary(k => k.Key, k => k.Value.Count);
        var schema = ConfigCollections.Get(collection) is { } def ? YamlConverter.EditSchema(state, [def]).ToJsonString() : "{}";
        return (YamlConverter.Export(state, [collection], SecretMode.Mask, ids: true), counts, schema);
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
