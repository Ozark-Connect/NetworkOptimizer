using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.CrowdSec;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>The CrowdSec CTI card's state. The key itself never leaves the server.</summary>
public sealed record CtiSettingsView(bool Enabled, bool HasKey, int DailyQuota, int RequestsToday);

/// <summary>
/// The CrowdSec CTI card in Settings - Security &amp; Alerts. The public build keeps one key and quota
/// for the instance, in the main database. A multi-tenant host gives each tenant its own by wrapping
/// this interface and <see cref="ICtiCredentialScope"/>; the reputation cache stays shared either way.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ICtiSettingsAdmin
{
    /// <summary>Whether the site in context edits the CTI settings, so Settings shows the card there.</summary>
    [RequireRole(Roles.Admin)]
    Task<bool> CanEditAsync(CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    Task<CtiSettingsView> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the settings; a null or empty <paramref name="newApiKey"/> keeps the saved key.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "crowdsec")]
    Task SaveAsync(bool enabled, string? newApiKey, int dailyQuota, CancellationToken cancellationToken = default);

    /// <summary>Checks the saved key against CrowdSec. Null when no key is saved.</summary>
    [RequireRole(Roles.Admin)]
    Task<(bool Success, string Message)?> TestAsync(CancellationToken cancellationToken = default);
}

public sealed class CtiSettingsAdminService(ISystemSettingsService settings, ICtiCredentialScope credentials,
    CrowdSecClient client, ICredentialProtectionService protection, SiteContextService site) : ICtiSettingsAdmin
{
    private string? Site => site.IsDefault ? null : site.Slug;

    // One key for the instance, set where the other instance-wide threat settings are: the main site.
    public Task<bool> CanEditAsync(CancellationToken ct = default) => Task.FromResult(site.IsDefault);

    public async Task<CtiSettingsView> GetAsync(CancellationToken ct = default)
    {
        var credential = await credentials.GetAsync(Site, ct);
        var quota = credential?.DailyQuota
            ?? (int.TryParse(await settings.GetGlobalAsync(CrowdSecSettingKeys.DailyQuota), out var q) && q >= 1
                ? q : CrowdSecSettingKeys.DefaultDailyQuota);
        return new CtiSettingsView(
            await settings.GetGlobalAsync(CrowdSecSettingKeys.Enabled) == "true",
            credential != null,
            quota,
            credential == null ? 0 : client.RequestsToday(credential.ApiKey));
    }

    public async Task SaveAsync(bool enabled, string? newApiKey, int dailyQuota, CancellationToken ct = default)
    {
        // A Site Admin of a managed site administers that site only, never the key every site uses.
        if (!await CanEditAsync(ct))
            throw new InvalidOperationException("CrowdSec CTI is set on the main site.");
        await settings.SetGlobalAsync(CrowdSecSettingKeys.Enabled, enabled ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(newApiKey))
            await settings.SetGlobalAsync(CrowdSecSettingKeys.ApiKey, protection.Encrypt(newApiKey.Trim()));
        await settings.SetGlobalAsync(CrowdSecSettingKeys.DailyQuota,
            Math.Max(1, dailyQuota).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<(bool Success, string Message)?> TestAsync(CancellationToken ct = default) =>
        await credentials.GetAsync(Site, ct) is { } credential ? await client.TestApiKeyAsync(credential.ApiKey, ct) : null;
}
