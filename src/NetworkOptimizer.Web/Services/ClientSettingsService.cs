using System.Net;
using System.Net.Sockets;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Changes a client's settings in UniFi Network: its name and its fixed IP. Site Admin: both are
/// stored on the UniFi Console and apply everywhere, not only in Network Optimizer.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IClientSettingsService
{
    /// <summary>
    /// Sets the client's alias, or clears it when <paramref name="name"/> is blank. Only the name
    /// is written; the client's fixed IP, DNS record, and other settings are untouched.
    /// </summary>
    /// <returns>The name UniFi Network now shows: the alias, else the Console's own choice.</returns>
    /// <exception cref="InvalidOperationException">Not connected, or the Console has no record of the client.</exception>
    /// <exception cref="UniFiPermissionException">The UniFi account cannot change client settings.</exception>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ClientRenamed, TargetType = "client")]
    Task<string> RenameAsync(string mac, string? name);

    /// <summary>The client's reserved address, or null when it has no fixed IP. Read when the editor opens.</summary>
    /// <exception cref="InvalidOperationException">Not connected, or the Console has no record of the client.</exception>
    [RequireRole(Roles.Admin)]
    Task<string?> GetFixedIpAsync(string mac);

    /// <summary>
    /// Reserves <paramref name="fixedIp"/> for the client, or removes the reservation when it is
    /// blank. Removing also turns off the client's Local DNS Record, as UniFi Network does.
    /// </summary>
    /// <returns>The reserved address, or null when the reservation was removed.</returns>
    /// <exception cref="InvalidOperationException">Not an IPv4 address, not connected, no client record, or refused.</exception>
    /// <exception cref="UniFiPermissionException">The UniFi account cannot change client settings.</exception>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ClientFixedIpChanged, TargetType = "client")]
    Task<string?> SetFixedIpAsync(string mac, string? fixedIp);
}

/// <inheritdoc cref="IClientSettingsService" />
public class ClientSettingsService : IClientSettingsService
{
    /// <summary>
    /// UniFi Network's form limit on a client name. The API itself stores any length, so this
    /// matches the UI rather than a Console constraint.
    /// </summary>
    public const int MaxNameLength = 128;

    /// <summary>Shown when a name is over <see cref="MaxNameLength"/>.</summary>
    public const string NameTooLongMessage = "Client names can be up to 128 characters.";

    private readonly SiteConnectionRegistry _connections;
    private readonly IAuditContext _auditContext;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly string _siteSlug;
    private readonly ILogger<ClientSettingsService> _logger;

    /// <param name="connections">Per-site UniFi Console connections.</param>
    /// <param name="auditContext">Carries the old and new values into the audit entry.</param>
    /// <param name="licenseState">Writes are refused on a site whose license is not operational.</param>
    /// <param name="siteContext">The site this scope serves.</param>
    /// <param name="logger">Logger.</param>
    public ClientSettingsService(
        SiteConnectionRegistry connections,
        IAuditContext auditContext,
        Licensing.LicenseStateService licenseState,
        SiteContextService siteContext,
        ILogger<ClientSettingsService> logger)
    {
        _connections = connections;
        _auditContext = auditContext;
        _licenseState = licenseState;
        _siteSlug = siteContext.Slug;
        _logger = logger;
    }

    /// <summary>The message to show for a failed client settings call, whichever layer threw it.</summary>
    public static string Describe(Exception ex, string fallback) => ex switch
    {
        AuthorizationDeniedException => "You do not have permission to change client settings.",
        UniFiApiErrorException { Code: "api.err.DuplicateFixedIP" } => "Another client already has this fixed IP.",
        UniFiApiErrorException { Code: "api.err.InvalidFixedIP" } => "This address is outside the client's network.",
        UniFiPermissionException or UniFiApiErrorException or InvalidOperationException => ex.Message,
        _ => fallback
    };

    /// <inheritdoc />
    public async Task<string> RenameAsync(string mac, string? name)
    {
        Licensing.LicenseGuard.EnsureOperational(_licenseState, _siteSlug);

        var newName = name?.Trim() ?? "";
        if (newName.Length > MaxNameLength)
            throw new InvalidOperationException(NameTooLongMessage);

        var client = ConnectedClient();
        var record = await KnownClientAsync(client, mac);
        _auditContext.SetTarget(record.Mac, DisplayName(record.Name, record.Hostname, record.Mac));

        if (record.Name == newName)
        {
            _auditContext.SuppressNoChange();
            return DisplayName(record.Name, record.Hostname, record.Mac);
        }

        var written = await client.UpdateClientAsync(record.Id, new UniFi.Models.UniFiClientUpdate { Name = newName })
            ?? throw new InvalidOperationException("UniFi Network refused the new name.");

        _auditContext.SetDetails(new { record.Mac, From = record.Name, To = written.Name });

        // A cleared alias falls back to the Console's own choice (often a fingerprint name, not the
        // hostname), so read it back rather than guess.
        ClientDisplayNameCache.Invalidate(client);
        try
        {
            var names = await ClientDisplayNameCache.GetAsync(client);
            if (names.TryGetValue(written.Mac, out var shown) && !string.IsNullOrWhiteSpace(shown)) return shown;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Client {Mac} renamed; display name read-back failed", written.Mac);
        }
        return DisplayName(written.Name, written.Hostname, written.Mac);
    }

    /// <inheritdoc />
    public async Task<string?> GetFixedIpAsync(string mac)
    {
        var record = await KnownClientAsync(ConnectedClient(), mac);
        return record.UseFixedIp && !string.IsNullOrEmpty(record.FixedIp) ? record.FixedIp : null;
    }

    /// <inheritdoc />
    public async Task<string?> SetFixedIpAsync(string mac, string? fixedIp)
    {
        Licensing.LicenseGuard.EnsureOperational(_licenseState, _siteSlug);

        var newIp = string.IsNullOrWhiteSpace(fixedIp) ? null : fixedIp.Trim();
        if (newIp != null && !(IPAddress.TryParse(newIp, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork
                               && parsed.ToString() == newIp))
            throw new InvalidOperationException("Enter an IPv4 address, such as 192.168.1.50.");

        var client = ConnectedClient();
        var record = await KnownClientAsync(client, mac);
        _auditContext.SetTarget(record.Mac, DisplayName(record.Name, record.Hostname, record.Mac));

        var current = record.UseFixedIp && !string.IsNullOrEmpty(record.FixedIp) ? record.FixedIp : null;
        if (current == newIp)
        {
            _auditContext.SuppressNoChange();
            return current;
        }

        var written = await client.SetClientFixedIpAsync(record.Mac, newIp)
            ?? throw new InvalidOperationException(newIp == null
                ? "UniFi Network refused to remove the fixed IP."
                : "UniFi Network refused the address.");

        _auditContext.SetDetails(new { record.Mac, From = current, To = written.UseFixedIp ? written.FixedIp : null });
        return written.UseFixedIp ? written.FixedIp : null;
    }

    private UniFiApiClient ConnectedClient()
    {
        var connection = _connections.GetFor(_siteSlug);
        return (connection.IsConnected ? connection.Client : null)
            ?? throw new InvalidOperationException("Not connected to UniFi Network.");
    }

    private static async Task<UniFi.Models.UniFiClientResponse> KnownClientAsync(UniFiApiClient client, string mac) =>
        await client.GetKnownClientAsync(mac)
            ?? throw new InvalidOperationException("UniFi Network has no record of this client.");

    private static string DisplayName(string? name, string? hostname, string mac) =>
        !string.IsNullOrWhiteSpace(name) ? name
        : !string.IsNullOrWhiteSpace(hostname) ? hostname
        : mac;
}
