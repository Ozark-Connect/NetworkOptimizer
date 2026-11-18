using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>A hop listed under the map point it is placed at.</summary>
/// <param name="Number">The hop number; 0 for the trace's starting point.</param>
/// <param name="Label">The hop's hostname, or its address when it has none.</param>
/// <param name="RttMs">The hop's minimum round trip, in ms.</param>
public sealed record TraceMapHop(int Number, string Label, double? RttMs);

/// <summary>One place on the trace map: every hop placed at the same coordinates.</summary>
/// <param name="Source">"hostname", "neighbor", "geolite2", or "start": the strongest evidence among its hops.</param>
public sealed record TraceMapNode(
    double Latitude, double Longitude, string City, string? CountryCode, string Source, List<TraceMapHop> Hops);

/// <summary>A drawn stretch between two places the trace visits one after the other.</summary>
/// <param name="From">Index into <see cref="TraceMapPath.Nodes"/>.</param>
/// <param name="To">Index into <see cref="TraceMapPath.Nodes"/>.</param>
/// <param name="Kilometers">Great-circle distance between the two places.</param>
/// <param name="SilentHops">Hop numbers between the two places that answered no probe: the way between them is not known.</param>
public sealed record TraceMapLeg(int From, int To, double Kilometers, int SilentHops = 0);

/// <summary>
/// A traced path as places and the legs between them, for the Network Tools trace map. Hops placed
/// in the same city share one point; hops placed in no city are counted, not drawn.
/// </summary>
public sealed record TraceMapPath(List<TraceMapNode> Nodes, List<TraceMapLeg> Legs, int UnplacedHops)
{
    /// <summary>True when the path visits at least two places, so there is something to draw.</summary>
    public bool HasRoute => Nodes.Count >= 2;

    /// <summary>
    /// Builds the path from the responding hops, in hop order, and their locations by address.
    /// </summary>
    /// <param name="hops">The responding hops, in hop order.</param>
    /// <param name="locations">Each hop's location, by address (<see cref="HopLocations.Resolve"/>).</param>
    /// <param name="start">Where the trace started, when known: drawn as the first point.</param>
    public static TraceMapPath Build(
        IEnumerable<(int Number, string? Address, string? Hostname, double? MinRttMs)> hops,
        IReadOnlyDictionary<string, HopLocationView> locations,
        (string Label, string City, string? CountryCode, double Latitude, double Longitude)? start = null)
    {
        var nodes = new List<TraceMapNode>();
        var legs = new List<TraceMapLeg>();
        var unplaced = 0;
        var previous = -1;
        // Hop numbers missing from the responding hops answered nothing; they count toward the next leg.
        var silent = 0;
        var lastNumber = 0;

        void Visit(double lat, double lon, string city, string? country, HopLocationSource? source, TraceMapHop hop)
        {
            var index = nodes.FindIndex(n => Math.Abs(n.Latitude - lat) < 0.01 && Math.Abs(n.Longitude - lon) < 0.01);
            var name = source is { } s ? SourceName(s) : "start";
            if (index < 0)
            {
                nodes.Add(new TraceMapNode(lat, lon, city, country, name, [hop]));
                index = nodes.Count - 1;
            }
            else
            {
                nodes[index].Hops.Add(hop);
                if (source is { } stronger && Rank(SourceName(stronger)) < Rank(nodes[index].Source))
                    nodes[index] = nodes[index] with { Source = SourceName(stronger) };
            }

            if (previous >= 0 && previous != index && !legs.Any(l => l.From == previous && l.To == index))
            {
                legs.Add(new TraceMapLeg(previous, index, GeoDistance.Kilometers(
                    nodes[previous].Latitude, nodes[previous].Longitude, nodes[index].Latitude, nodes[index].Longitude), silent));
            }
            previous = index;
            silent = 0;
        }

        if (start is { } from)
            Visit(from.Latitude, from.Longitude, from.City, from.CountryCode, null, new TraceMapHop(0, from.Label, null));

        foreach (var h in hops)
        {
            if (string.IsNullOrEmpty(h.Address)) continue;
            if (h.Number > lastNumber + 1) silent += h.Number - lastNumber - 1;
            lastNumber = Math.Max(lastNumber, h.Number);
            if (!locations.TryGetValue(h.Address, out var view)
                || view is not { City.Length: > 0, Latitude: double lat, Longitude: double lon })
            {
                // A private hop has no location to miss; only public hops count as unplaced.
                if (NetworkUtilities.IsPublicIpAddress(h.Address)) unplaced++;
                continue;
            }
            var label = !string.IsNullOrEmpty(h.Hostname) && h.Hostname != h.Address ? h.Hostname : h.Address;
            Visit(lat, lon, view.City, view.CountryCode, view.Source, new TraceMapHop(h.Number, label, h.MinRttMs));
        }
        return new TraceMapPath(nodes, legs, unplaced);
    }

    /// <summary>The marker's source name for what placed a hop, as both route maps' JS reads it.</summary>
    internal static string SourceName(HopLocationSource source) => source switch
    {
        HopLocationSource.Set => "set",
        HopLocationSource.Hostname => "hostname",
        HopLocationSource.Neighbor => "neighbor",
        HopLocationSource.Learned => "learned",
        _ => "geolite2",
    };

    // The start point keeps its own marker whatever else lands on it.
    internal static int Rank(string source) => source switch
    {
        "start" => 0,
        "set" => 1,
        "hostname" => 2,
        "neighbor" => 3,
        "learned" => 4,
        _ => 5,
    };
}
