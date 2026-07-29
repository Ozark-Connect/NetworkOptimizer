using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// The SSH settings as the forms in Settings edit them, gated and audited.
///
/// Deliberately a separate surface from <see cref="IGatewaySshService"/> and
/// <see cref="IUniFiSshService"/> rather than gates bolted onto those. Those two are on the connection
/// path: monitoring probes call them with no caller established, and a gated call with an unset caller
/// is a hard failure (<c>ICallerContext.Require</c>), so marking them would take monitoring down. They
/// also genuinely need the key-file path in order to connect, which rules out redacting there.
///
/// Only the edit forms resolve this interface, so the boundary lands at the service tier without the
/// connection path noticing.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ISshSettingsAdminService
{
    /// <summary>Gateway SSH settings for the form, with the key-file path redacted below global Admin.</summary>
    [RequireRole(Roles.Admin)]
    Task<GatewaySshSettings> GetGatewayForEditAsync();

    /// <summary>Saves gateway SSH settings. The key-file path is only writable by a global Admin.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, TargetType = "gateway_ssh")]
    Task<GatewaySshSettings> SaveGatewayAsync(GatewaySshSettings settings);

    /// <summary>Device SSH settings for the form, with the key-file path redacted below global Admin.</summary>
    [RequireRole(Roles.Admin)]
    Task<UniFiSshSettings> GetDeviceForEditAsync();

    /// <summary>Saves device SSH settings. The key-file path is only writable by a global Admin.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, TargetType = "device_ssh")]
    Task<UniFiSshSettings> SaveDeviceAsync(UniFiSshSettings settings);

    /// <summary>
    /// Whether a key-file path is configured, for a caller who may not see the path itself.
    ///
    /// Redacting the path to null told the form "no key file", which is a different statement from
    /// "a key file you may not read". The form believed the first: it refused to save or test for want
    /// of a credential that was in fact configured, and the note telling the caller so never rendered,
    /// because it was written to appear only when the path it was hiding was non-empty.
    ///
    /// A boolean is the whole of what a caller below global Admin needs - it cannot name another
    /// tenant's key, which is the reason the path is withheld in the first place.
    /// </summary>
    [RequireRole(Roles.Admin)]
    Task<bool> IsGatewayKeyFilePathConfiguredAsync();

    /// <inheritdoc cref="IsGatewayKeyFilePathConfiguredAsync"/>
    [RequireRole(Roles.Admin)]
    Task<bool> IsDeviceKeyFilePathConfiguredAsync();
}

/// <inheritdoc />
public sealed class SshSettingsAdminService : ISshSettingsAdminService
{
    private readonly GatewaySshRegistry _gatewayRegistry;
    private readonly UniFiSshRegistry _deviceRegistry;
    private readonly SiteContextService _siteContext;
    private readonly ICallerContext _caller;

    public SshSettingsAdminService(
        GatewaySshRegistry gatewayRegistry,
        UniFiSshRegistry deviceRegistry,
        SiteContextService siteContext,
        ICallerContext caller)
    {
        _gatewayRegistry = gatewayRegistry;
        _deviceRegistry = deviceRegistry;
        _siteContext = siteContext;
        _caller = caller;
    }

    /// <inheritdoc />
    public async Task<GatewaySshSettings> GetGatewayForEditAsync()
    {
        var settings = await Gateway.GetSettingsAsync(forceRefresh: true);
        if (MayUseKeyFilePath)
            return settings;

        // Redact a COPY. GetSettingsAsync hands back the instance it caches, so nulling the path on it
        // took the path out of the cache - and the connection path reads that same cache. One caller
        // below global Admin opening this form left the gateway with no key file for every consumer,
        // monitoring probes and SQM deployment included, until the cache expired. A failed SSH test
        // forces a refresh and so re-poisoned it every time, which is why it looked like the test broke
        // SSH: the test was the thing reloading the settings.
        var redacted = settings.ShallowCopy();
        redacted.PrivateKeyPath = null;
        return redacted;
    }

    /// <inheritdoc />
    public async Task<GatewaySshSettings> SaveGatewayAsync(GatewaySshSettings settings)
    {
        if (!MayUseKeyFilePath)
        {
            // The form never showed the field, so whatever arrived in it is not the caller's to set -
            // and blanking it would silently drop a path a global Admin configured.
            var stored = await Gateway.GetSettingsAsync(forceRefresh: true);
            settings.PrivateKeyPath = stored.PrivateKeyPath;
        }

        return await Gateway.SaveSettingsAsync(settings);
    }

    /// <inheritdoc />
    public async Task<UniFiSshSettings> GetDeviceForEditAsync()
    {
        var settings = await Device.GetSettingsAsync();
        if (MayUseKeyFilePath)
            return settings;

        // A copy, for the same reason as the gateway above.
        var redacted = settings.ShallowCopy();
        redacted.PrivateKeyPath = null;
        return redacted;
    }

    /// <inheritdoc />
    public async Task<UniFiSshSettings> SaveDeviceAsync(UniFiSshSettings settings)
    {
        if (!MayUseKeyFilePath)
        {
            var stored = await Device.GetSettingsAsync();
            settings.PrivateKeyPath = stored.PrivateKeyPath;
        }

        return await Device.SaveSettingsAsync(settings);
    }

    /// <inheritdoc />
    public async Task<bool> IsGatewayKeyFilePathConfiguredAsync()
        => !string.IsNullOrWhiteSpace((await Gateway.GetSettingsAsync(forceRefresh: true)).PrivateKeyPath);

    /// <inheritdoc />
    public async Task<bool> IsDeviceKeyFilePathConfiguredAsync()
        => !string.IsNullOrWhiteSpace((await Device.GetSettingsAsync()).PrivateKeyPath);

    /// <summary>
    /// Whether the caller may see and set a path to a key file on the server. Global Admin only: the
    /// connection opens whatever path the record names, so a Site Admin who could set it could point
    /// the server at another tenant's key and authenticate as them. They can still use a path an
    /// administrator configured - they just never see or change it.
    ///
    /// A system caller and an auth-disabled install both pass, matching the interceptor: on a
    /// single-site self-hosted box with no password there is no principal, and the local operator has
    /// always been able to do everything.
    /// </summary>
    private bool MayUseKeyFilePath
    {
        get
        {
            var caller = _caller.Current;
            if (caller is null || caller.IsSystem || caller.AuthenticationDisabled)
                return true;
            return caller.Principal?.IsInRole(Roles.Admin) == true;
        }
    }

    private GatewaySshService Gateway => _gatewayRegistry.GetFor(_siteContext.Slug);

    private UniFiSshService Device => _deviceRegistry.GetFor(_siteContext.Slug);
}
