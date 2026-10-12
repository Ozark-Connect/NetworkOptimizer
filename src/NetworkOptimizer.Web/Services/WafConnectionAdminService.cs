using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Waf;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services;

/// <summary>A site's WAF connection as the form shows it; the token itself is never read back.</summary>
public sealed record WafConnection(bool Enabled, string Url, bool HasToken);

/// <summary>
/// The site in context's netopt-waf connection. Per site: each site may run its own proxy and WAF,
/// reached through the site's agent when the site is routed that way. Site Admin, like the rest of
/// the site's configuration.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IWafConnectionAdmin
{
    [RequireRole(Roles.Admin)]
    Task<WafConnection> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the connection; a null or empty token keeps the stored one.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "waf")]
    Task SaveAsync(bool enabled, string url, string? token, CancellationToken cancellationToken = default);

    /// <summary>Reads one page from the WAF; a null or empty token uses the stored one.</summary>
    [RequireRole(Roles.Admin)]
    Task<WafEventPage> TestAsync(string url, string? token, CancellationToken cancellationToken = default);

    /// <summary>Switches the site's WAF between detect and block, through its saved connection.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "waf_mode")]
    Task<WafModeState> SetModeAsync(string mode, CancellationToken cancellationToken = default);

    /// <summary>The site's managed exclusions, plus the built-in app sets its WAF loads when it answers.</summary>
    [RequireRole(Roles.Viewer)]
    Task<WafExclusionsView> GetExclusionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns one CRS rule off for requests matching every field given, pushing the new set to the
    /// WAF before saving it. An exclusion with the same rule and scope is returned as it is.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "waf_exclusion")]
    Task<WafRuleExclusion> AddExclusionAsync(long ruleId, string? host, string? method, string? path, string? note,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an exclusion from the WAF and the site; false when there is none with that ID.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "waf_exclusion")]
    Task<bool> RemoveExclusionAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>A site's managed exclusions, and what its WAF reports when it answers.</summary>
/// <param name="Apps">The WAF's built-in app sets; null when it did not answer.</param>
/// <param name="WafError">Why the WAF did not answer, or null.</param>
public sealed record WafExclusionsView(IReadOnlyList<WafRuleExclusion> Exclusions, IReadOnlyList<string>? Apps, string? WafError);

public sealed class WafConnectionAdminService(
    IThreatSettingsAccessor settings,
    ICredentialProtectionService credentials,
    ISiteHttpClientProvider http,
    ThreatCollectionService collector,
    ThreatBlockingService blocking,
    SiteContextService siteContext,
    ICallerContext caller) : IWafConnectionAdmin
{
    private static readonly HashSet<string> Methods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];

    private string? Site => siteContext.IsDefault ? null : siteContext.Slug;
    private string Key(string key) => WafSettingKeys.ForSite(key, Site);

    public async Task<WafConnection> GetAsync(CancellationToken ct = default) => new(
        await settings.GetSettingAsync(Key(WafSettingKeys.Enabled), ct) == "true",
        await settings.GetSettingAsync(Key(WafSettingKeys.Url), ct) ?? "",
        !string.IsNullOrEmpty(await settings.GetSettingAsync(Key(WafSettingKeys.Token), ct)));

    public async Task SaveAsync(bool enabled, string url, string? token, CancellationToken ct = default)
    {
        url = url.Trim();
        if (enabled && !(Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"))
            throw new ArgumentException("Enter the WAF URL as an http:// or https:// address.");
        await settings.SaveSettingAsync(Key(WafSettingKeys.Enabled), enabled ? "true" : "false", ct);
        await settings.SaveSettingAsync(Key(WafSettingKeys.Url), url, ct);
        if (!string.IsNullOrWhiteSpace(token))
            await settings.SaveSettingAsync(Key(WafSettingKeys.Token), credentials.Encrypt(token.Trim()), ct);
        collector.TriggerCollection();
        blocking.Trigger();
    }

    public async Task<WafEventPage> TestAsync(string url, string? token, CancellationToken ct = default)
    {
        var useToken = string.IsNullOrWhiteSpace(token)
            ? await settings.GetDecryptedSettingAsync(Key(WafSettingKeys.Token), ct) ?? ""
            : token.Trim();
        var client = new WafClient(await http.GetClientAsync(Site, ct));
        return await client.GetEventsAsync(url.Trim(), useToken, 0, ct, limit: 1);
    }

    public async Task<WafModeState> SetModeAsync(string mode, CancellationToken ct = default)
    {
        if (mode is not ("detect" or "block"))
            throw new ArgumentException("The WAF mode is detect or block.");
        var url = await settings.GetSettingAsync(Key(WafSettingKeys.Url), ct);
        var token = await settings.GetDecryptedSettingAsync(Key(WafSettingKeys.Token), ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Connect the WAF first.");
        var client = new WafClient(await http.GetClientAsync(Site, ct));
        try
        {
            var state = await client.PutModeAsync(url.Trim(), token, mode, ct);
            collector.TriggerCollection();
            return state;
        }
        catch (WafClientException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public async Task<WafExclusionsView> GetExclusionsAsync(CancellationToken ct = default)
    {
        var list = await WafRuleExclusions.LoadAsync(settings, Site, ct);
        var (client, url, token) = await ConnectionAsync(ct);
        if (client == null) return new WafExclusionsView(list, null, null);
        try
        {
            var set = await client.GetExclusionsAsync(url, token, ct);
            return new WafExclusionsView(list, set.Apps, null);
        }
        catch (WafClientException ex)
        {
            return new WafExclusionsView(list, null, ex.Message);
        }
    }

    public async Task<WafRuleExclusion> AddExclusionAsync(long ruleId, string? host, string? method, string? path, string? note,
        CancellationToken ct = default)
    {
        var candidate = new WafRuleExclusion
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            RuleId = ruleId,
            Host = Blank(host)?.ToLowerInvariant(),
            Method = Blank(method)?.ToUpperInvariant(),
            Path = Blank(path?.Split('?', '#')[0]),
            Note = Blank(note) is { } n && n.Length > 200 ? n[..200] : Blank(note),
            CreatedBy = caller.Current?.ActorName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (ruleId is < 900000 or > 999999)
            throw new ArgumentException($"{ruleId} is not an OWASP CRS rule ID.");
        if (candidate.Host == null && candidate.Path == null)
            throw new ArgumentException("Set a host or a path. An exclusion for every request goes in after-crs.conf by hand.");
        if (candidate.Method != null && !Methods.Contains(candidate.Method))
            throw new ArgumentException($"{candidate.Method} is not an HTTP method the WAF matches.");
        if (candidate.Path != null && (!candidate.Path.StartsWith('/') || candidate.Path.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\')))
            throw new ArgumentException("The path starts with / and holds no spaces, quotes, or backslashes.");

        var list = await WafRuleExclusions.LoadAsync(settings, Site, ct);
        if (list.FirstOrDefault(x => x.SameScope(candidate)) is { } existing) return existing;
        list.Add(candidate);
        await PushAndSaveAsync(list, ct);
        return candidate;
    }

    public async Task<bool> RemoveExclusionAsync(string id, CancellationToken ct = default)
    {
        var list = await WafRuleExclusions.LoadAsync(settings, Site, ct);
        if (list.RemoveAll(x => x.Id == id) == 0) return false;
        await PushAndSaveAsync(list, ct);
        return true;
    }

    // The WAF builds the set first and refuses one it cannot build, so nothing is saved that it rejected.
    private async Task PushAndSaveAsync(List<WafRuleExclusion> list, CancellationToken ct)
    {
        var (client, url, token) = await ConnectionAsync(ct);
        if (client == null) throw new ArgumentException("Connect the WAF first.");
        try
        {
            await client.PutExclusionsAsync(url, token, list, ct);
        }
        catch (WafClientException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        await WafRuleExclusions.SaveAsync(settings, Site, list, ct);
        blocking.RecordExclusionsSent(Site, list);
    }

    private async Task<(WafClient? Client, string Url, string Token)> ConnectionAsync(CancellationToken ct)
    {
        var url = await settings.GetSettingAsync(Key(WafSettingKeys.Url), ct);
        var token = await settings.GetDecryptedSettingAsync(Key(WafSettingKeys.Token), ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return (null, "", "");
        return (new WafClient(await http.GetClientAsync(Site, ct)), url.Trim(), token);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
