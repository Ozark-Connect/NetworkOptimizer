using System.Text.Json;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.LanFlowMap;

/// <summary>
/// The caller's own view of the 3D LAN Flow Map on the site in context. Viewer on that site: the map
/// is open to every role, and a view choice changes nothing anyone else sees. No audit entry, for
/// the same reason: nothing here is configuration.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ILanMapView
{
    /// <summary>The client MACs the caller has hidden from the map on this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<string>> GetHiddenDevicesAsync();

    /// <summary>Replaces the caller's hidden list for this site, and returns it as stored.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<string>> SetHiddenDevicesAsync(IEnumerable<string?> macs);
}

/// <inheritdoc />
public sealed class LanMapViewService(IUserPreferences preferences, SiteContextService site) : ILanMapView
{
    public async Task<IReadOnlyList<string>> GetHiddenDevicesAsync() =>
        HiddenMapDevices.Parse(await preferences.GetAsync(HiddenMapDevices.Key(site.Slug)));

    public async Task<IReadOnlyList<string>> SetHiddenDevicesAsync(IEnumerable<string?> macs)
    {
        var hidden = HiddenMapDevices.Normalize(macs);
        await preferences.SetAsync(HiddenMapDevices.Key(site.Slug), JsonSerializer.Serialize(hidden));
        return hidden;
    }
}
