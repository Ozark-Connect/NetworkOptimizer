using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Waf;
using NetworkOptimizer.Web.Services.Gates;

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
}

public sealed class WafConnectionAdminService(
    IThreatSettingsAccessor settings,
    ICredentialProtectionService credentials,
    ISiteHttpClientProvider http,
    ThreatCollectionService collector,
    ThreatBlockingService blocking,
    SiteContextService siteContext) : IWafConnectionAdmin
{
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
}
