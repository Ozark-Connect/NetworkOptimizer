using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Tailscale;

/// <summary>
/// The site in context's tailnet: names and routes for every Tailscale device, and how the local
/// tailscaled reaches each one. Reads are open to any role, since Client Performance, speed test
/// results, and Live Traffic show them; the settings are Site Admin, like the rest of Settings.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ITailscaleDirectory
{
    /// <summary>The site's tailnet as last read (empty when Tailscale is off or not read yet).</summary>
    [RequireRole(Roles.Viewer)]
    Task<TailnetSnapshot> GetPeersAsync(CancellationToken cancellationToken = default);

    /// <summary>The device at a tailnet address, or null.</summary>
    [RequireRole(Roles.Viewer)]
    Task<TailnetPeer?> GetPeerAsync(string ip, CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    Task<TailscaleSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>What the last refresh found, or null before the first one.</summary>
    [RequireRole(Roles.Admin)]
    Task<TailnetStatus?> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the settings and refreshes the tailnet at once; empty secrets keep the stored ones.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "tailscale")]
    Task SaveSettingsAsync(TailscaleSettingsUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Reads everything the given settings point at, storing nothing; empty secrets use the stored ones.</summary>
    [RequireRole(Roles.Admin)]
    Task<TailnetTestResult> TestAsync(TailscaleSettingsUpdate update, CancellationToken cancellationToken = default);
}

public sealed class TailscaleDirectoryService(TailscaleDirectoryRegistry registry, SiteContextService site) : ITailscaleDirectory
{
    public Task<TailnetSnapshot> GetPeersAsync(CancellationToken ct = default) => registry.GetReadyAsync(site.Slug, ct);

    public async Task<TailnetPeer?> GetPeerAsync(string ip, CancellationToken ct = default) => (await registry.GetReadyAsync(site.Slug, ct)).Peer(ip);

    public async Task<TailscaleSettings> GetSettingsAsync(CancellationToken ct = default) =>
        (await registry.ReadSettingsAsync(site.Slug)).Settings;

    public Task<TailnetStatus?> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(registry.StatusFor(site.Slug));

    public async Task SaveSettingsAsync(TailscaleSettingsUpdate update, CancellationToken ct = default)
    {
        Validate(update.Settings);
        await registry.SaveSettingsAsync(site.Slug, update);
        await registry.RefreshNowAsync(site.Slug, ct);
    }

    public async Task<TailnetTestResult> TestAsync(TailscaleSettingsUpdate update, CancellationToken ct = default)
    {
        Validate(update.Settings);
        var stored = await registry.ReadSettingsAsync(site.Slug);
        var test = new TailscaleDirectoryRegistry.StoredSettings(
            update.Settings,
            string.IsNullOrWhiteSpace(update.ClientSecret) ? stored.ClientSecret : update.ClientSecret.Trim(),
            string.IsNullOrWhiteSpace(update.HeadscaleKey) ? stored.HeadscaleKey : update.HeadscaleKey.Trim());
        return await registry.TestAsync(site.Slug, test, ct);
    }

    private static void Validate(TailscaleSettings s)
    {
        if (!s.Enabled) return;
        if (s.DeviceSource == TailnetDeviceSource.LocalStatus && s.LocalSource == TailnetLocalSource.Off)
            throw new ArgumentException("Choose where to read local status from, or read the device list from an API.");
        if (s.DeviceSource == TailnetDeviceSource.HeadscaleApi && HeadscaleApiClient.UrlProblem(s.HeadscaleUrl) is { } problem)
            throw new ArgumentException(problem);
        if (s.DeviceSource == TailnetDeviceSource.TailscaleApi && string.IsNullOrWhiteSpace(s.ClientId))
            throw new ArgumentException("Enter the OAuth client ID.");
    }
}
