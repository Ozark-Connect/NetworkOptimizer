using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.ClientIcons;

/// <summary>
/// The icon UniFi Network shows for each client, for any page that shows a client. Reads only: any
/// role can see a client's icon wherever it can see the client.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IClientIcons
{
    /// <summary>The site's client icons; pass a client's MAC to <see cref="ClientIconMap.UrlFor"/>.</summary>
    [RequireRole(Roles.Viewer)]
    Task<ClientIconMap> GetMapAsync(CancellationToken cancellationToken = default);

    /// <summary>The site's client icons once the console read in progress lands (the map as is when none is running).</summary>
    [RequireRole(Roles.Viewer)]
    Task<ClientIconMap> GetRefreshedMapAsync(CancellationToken cancellationToken = default);

    /// <summary>An icon's cached file, or null when the key is unknown to the site or the icon cannot be fetched.</summary>
    [RequireRole(Roles.Viewer)]
    Task<ClientIconImage?> GetImageAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Page helpers for <see cref="IClientIcons"/>.</summary>
public static class ClientIconsExtensions
{
    /// <summary>
    /// Loads the map without holding up the page: start it with <c>_ = </c> from initialization and
    /// set the field and re-render in <paramref name="apply"/> (through the component's InvokeAsync).
    /// When a slow console read was still running, <paramref name="apply"/> runs again once it lands,
    /// so the page fills in without a reload. Icons are decoration: a failure leaves the page as it is.
    /// </summary>
    public static async Task LoadIntoAsync(this IClientIcons icons, Func<ClientIconMap, Task> apply, ILogger? logger = null)
    {
        try
        {
            var first = await icons.GetMapAsync();
            await apply(first);
            var refreshed = await icons.GetRefreshedMapAsync();
            if (!ReferenceEquals(refreshed, first))
                await apply(refreshed);
        }
        catch (Exception ex)
        {
            // Also a page closed before the map arrived (its InvokeAsync throws once disposed).
            logger?.LogDebug(ex, "Client icons unavailable");
        }
    }
}

/// <summary>Serves <see cref="IClientIcons"/> from the site in context.</summary>
public sealed class ClientIconService(ClientIconRegistry registry, SiteContextService site) : IClientIcons
{
    public Task<ClientIconMap> GetMapAsync(CancellationToken ct = default) => registry.GetReadyAsync(site.Slug, ct);

    public Task<ClientIconMap> GetRefreshedMapAsync(CancellationToken ct = default) => registry.GetRefreshedAsync(site.Slug, ct);

    public Task<ClientIconImage?> GetImageAsync(string key, CancellationToken ct = default) => registry.GetImageAsync(site.Slug, key, ct);
}
