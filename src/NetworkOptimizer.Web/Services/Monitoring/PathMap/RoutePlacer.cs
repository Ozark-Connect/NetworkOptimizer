using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Threats.Enrichment;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>A stored route's hops placed and attributed: where each address is, and its network.</summary>
public sealed record PlacedRoute(StoredRoute Route, IReadOnlyDictionary<string, HopLocationView> Locations,
    IReadOnlyDictionary<string, AsnLookup?> Asns);

/// <summary>
/// Places stored routes with the Route Map's engine (<see cref="HopLocations.Resolve"/>, cities set by
/// hand included) and attributes each hop's network. Used for the path-shift signature when a trace is
/// stored and for the Path Map when it is drawn.
/// </summary>
public sealed class RoutePlacer
{
    private readonly GeoEnrichmentService _geo;
    private readonly AsnResolutionService _asn;
    private readonly IHopLocationOverrideSource _overrides;
    private readonly LearnedHopLocations _learned;

    public RoutePlacer(GeoEnrichmentService geo, AsnResolutionService asn, IHopLocationOverrideSource overrides,
        LearnedHopLocations learned)
    {
        _geo = geo;
        _asn = asn;
        _overrides = overrides;
        _learned = learned;
    }

    /// <summary>An address's network (ASN) from GeoLite2, for matching two destinations; null when unknown.</summary>
    public int? NetworkOf(string address) => _geo.Enrich(address).Asn;

    /// <summary>The learned places a site's traces use (<see cref="LearnedHopLocations"/>).</summary>
    public Task<IReadOnlyDictionary<string, LearnedHop>> LearnedAsync(string siteSlug, CancellationToken ct = default) =>
        _learned.GetAsync(siteSlug, ct);

    /// <summary>Learns the transit routers a fresh trace placed by hostname or a nearby hop. Never throws.</summary>
    public async Task LearnAsync(string siteSlug, StoredRoute route, CancellationToken ct = default)
    {
        var hops = route.Hops.Where(h => h.Address != null).Select(h => (h.Address, h.Hostname, h.MinRttMs)).ToList();
        var found = HopLocations.Resolve(hops, _geo.Enrich, await OverridesAsync(siteSlug, ct));
        await _learned.LearnAsync(siteSlug, hops, found, _geo.Enrich, route.CheckedAt, ct);
    }

    /// <summary>
    /// The cities set by hand that a site's traces use. Empty when they cannot be read: placement still runs
    /// without them.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, HopLocationSetting>> OverridesAsync(string siteSlug, CancellationToken ct = default)
    {
        try { return await _overrides.ForSiteAsync(siteSlug, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return new Dictionary<string, HopLocationSetting>(); }
    }

    /// <summary>Places one route's responding hops.</summary>
    /// <param name="overrides">The cities set by hand (<see cref="OverridesAsync"/>).</param>
    /// <param name="learned">Learned places, for drawing (<see cref="LearnedAsync"/>). Never for the signature: a hop
    /// gaining a learned city is not the route going anywhere.</param>
    public async Task<PlacedRoute> PlaceAsync(StoredRoute route, IReadOnlyDictionary<string, HopLocationSetting> overrides,
        CancellationToken ct = default, IReadOnlyDictionary<string, LearnedHop>? learned = null)
    {
        var responding = route.Hops.Where(h => h.Address != null).ToList();
        var locations = HopLocations.Resolve(responding.Select(h => (h.Address, h.Hostname, h.MinRttMs)), _geo.Enrich, overrides, learned);
        var asns = new Dictionary<string, AsnLookup?>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in responding.Select(h => h.Address!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!NetworkUtilities.IsPublicIpAddress(address)) continue;
            try { asns[address] = await _asn.ResolveAsync(address, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { asns[address] = null; }
        }
        return new PlacedRoute(route, locations, asns);
    }

    /// <summary>
    /// The route's signature: the place and network of each responding hop before the destination.
    /// Only a city counts as a place; a country alone is too coarse to tell two routes apart.
    /// </summary>
    public static string? Signature(PlacedRoute placed) => RouteSignature.Build(placed.Route.Hops
        .Where(h => h.Address != null && !string.Equals(h.Address, placed.Route.Destination, StringComparison.OrdinalIgnoreCase))
        .Select(h => (
            placed.Locations.TryGetValue(h.Address!, out var at) ? at.City : null,
            placed.Asns.TryGetValue(h.Address!, out var asn) ? asn?.Asn : null)));
}
