using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Renames a client in UniFi Network. Site Admin: the alias is stored on the UniFi Console and
/// every UniFi app shows it, not only Network Optimizer.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IClientRenameService
{
    /// <summary>
    /// Sets the client's alias, or clears it when <paramref name="name"/> is blank. Only the name
    /// is written; the client's fixed IP, DNS record, and other settings are untouched.
    /// </summary>
    /// <returns>The name UniFi Network now shows: the alias, else the hostname, else the MAC.</returns>
    /// <exception cref="InvalidOperationException">Not connected, or the Console has no record of the client.</exception>
    /// <exception cref="UniFi.UniFiPermissionException">The UniFi account cannot change client settings.</exception>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ClientRenamed, TargetType = "client")]
    Task<string> RenameAsync(string mac, string? name);
}

/// <inheritdoc cref="IClientRenameService" />
public class ClientRenameService : IClientRenameService
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
    private readonly ILogger<ClientRenameService> _logger;

    /// <param name="connections">Per-site UniFi Console connections.</param>
    /// <param name="auditContext">Carries the old and new name into the audit entry.</param>
    /// <param name="licenseState">Writes are refused on a site whose license is not operational.</param>
    /// <param name="siteContext">The site this scope serves.</param>
    /// <param name="logger">Logger.</param>
    public ClientRenameService(
        SiteConnectionRegistry connections,
        IAuditContext auditContext,
        Licensing.LicenseStateService licenseState,
        SiteContextService siteContext,
        ILogger<ClientRenameService> logger)
    {
        _connections = connections;
        _auditContext = auditContext;
        _licenseState = licenseState;
        _siteSlug = siteContext.Slug;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> RenameAsync(string mac, string? name)
    {
        Licensing.LicenseGuard.EnsureOperational(_licenseState, _siteSlug);

        var newName = name?.Trim() ?? "";
        if (newName.Length > MaxNameLength)
            throw new InvalidOperationException(NameTooLongMessage);
        var connection = _connections.GetFor(_siteSlug);
        var client = connection.IsConnected ? connection.Client : null;
        if (client == null) throw new InvalidOperationException("Not connected to UniFi Network.");

        var record = await client.GetKnownClientAsync(mac)
            ?? throw new InvalidOperationException("UniFi Network has no record of this client.");
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
        UniFi.ClientDisplayNameCache.Invalidate(client);
        try
        {
            var names = await UniFi.ClientDisplayNameCache.GetAsync(client);
            if (names.TryGetValue(written.Mac, out var shown) && !string.IsNullOrWhiteSpace(shown)) return shown;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Client {Mac} renamed; display name read-back failed", written.Mac);
        }
        return DisplayName(written.Name, written.Hostname, written.Mac);
    }

    private static string DisplayName(string? name, string? hostname, string mac) =>
        !string.IsNullOrWhiteSpace(name) ? name
        : !string.IsNullOrWhiteSpace(hostname) ? hostname
        : mac;
}
