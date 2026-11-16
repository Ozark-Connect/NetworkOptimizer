using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Monitoring.Probes;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>One TTL of a stored route.</summary>
/// <param name="Ttl">The hop number.</param>
/// <param name="Address">The address that answered; null for a hop that said nothing.</param>
/// <param name="Hostname">Its PTR name, from the vantage or this server.</param>
/// <param name="MinRttMs">Its lowest round trip in the trace.</param>
/// <param name="Probes">Probes sent at this TTL.</param>
/// <param name="Responses">Probes answered.</param>
/// <param name="SeenAt">Set only on a hop filled in from another trace (<see cref="HopHistory"/>): when it last answered.
/// Null for a hop this trace measured. The confirmed route keeps such hops, marked, for up to <see cref="HopHistory.MaxAge"/>.</param>
public sealed record StoredRouteHop(int Ttl, string? Address, string? Hostname, double? MinRttMs, int Probes, int Responses,
    DateTime? SeenAt = null);

/// <summary>One trace of a target, as the Path Map keeps it.</summary>
/// <param name="CheckedAt">When the trace ran, UTC.</param>
/// <param name="Vantage">"server" or "agent-N".</param>
/// <param name="Bind">The source address or interface it was bound to; null for the default route.</param>
/// <param name="Destination">The address the trace went to (a hostname target's resolved address).</param>
/// <param name="Hops">Every TTL, nearest first, the destination's included.</param>
/// <param name="Signature">The route's places and networks in order (<see cref="RouteSignature"/>); null when nothing on it is placed.</param>
public sealed record StoredRoute(DateTime CheckedAt, string Vantage, string? Bind, string? Destination,
    IReadOnlyList<StoredRouteHop> Hops, string? Signature);

/// <summary>A confirmed path shift: the route before it and the route after it.</summary>
/// <param name="At">When the new route was first seen, UTC.</param>
public sealed record RouteShift(DateTime At, StoredRoute From, StoredRoute To);

/// <summary>
/// What <see cref="NetworkOptimizer.Storage.Models.UpstreamDiscovery.RouteJson"/> holds: the latest
/// trace, the one before it, and the route last confirmed by two traces in a row.
/// </summary>
public sealed record StoredRouteState(StoredRoute Latest, StoredRoute? Previous, StoredRoute Stable);

/// <summary>
/// Reading, writing, and advancing a target's stored routes. A route change is reported only when two
/// consecutive traces agree on a route that differs from the confirmed one, so one odd trace (a hop
/// that stopped answering, a flap) never draws a shift.
/// </summary>
public static class StoredRoutes
{
    /// <summary>Most shifts kept per target.</summary>
    public const int MaxShifts = 10;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The route a trace result records: every TTL, in order.</summary>
    /// <param name="hostnames">PTR names this server found for hops the vantage named nothing, by address.</param>
    public static StoredRoute FromTrace(TracerouteResult trace, string vantage, string? bind, string? destination,
        IReadOnlyDictionary<string, string>? hostnames, string? signature) =>
        new(trace.Timestamp == default ? DateTime.UtcNow : DateTime.SpecifyKind(trace.Timestamp, DateTimeKind.Utc),
            vantage, bind, destination,
            trace.Hops.OrderBy(h => h.HopNumber).Select(h => new StoredRouteHop(
                h.HopNumber,
                h.Responded ? h.Address : null,
                !string.IsNullOrEmpty(h.Hostname) && h.Hostname != h.Address ? h.Hostname
                    : h.Address != null && hostnames?.TryGetValue(h.Address, out var name) == true ? name : null,
                h.RttMinMs,
                h.Probes,
                h.Responses)).ToList(),
            signature);

    public static StoredRouteState? ReadState(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<StoredRouteState>(json, Json); }
        catch (JsonException) { return null; }
    }

    public static IReadOnlyList<RouteShift> ReadShifts(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<RouteShift>();
        try { return JsonSerializer.Deserialize<List<RouteShift>>(json, Json) ?? new List<RouteShift>(); }
        catch (JsonException) { return Array.Empty<RouteShift>(); }
    }

    public static string Write(StoredRouteState state) => JsonSerializer.Serialize(state, Json);

    public static string Write(IReadOnlyList<RouteShift> shifts) => JsonSerializer.Serialize(shifts, Json);

    /// <summary>
    /// Folds a new trace into the stored state. Returns the new state and, when the trace confirms a
    /// route change, the shift.
    /// </summary>
    public static (StoredRouteState State, RouteShift? Shift) Advance(StoredRouteState? current, StoredRoute trace)
    {
        if (current == null) return (new StoredRouteState(trace, null, trace), null);

        var previous = current.Latest;
        // Nothing placed on it says nothing about where the route goes: keep the confirmed one.
        if (trace.Signature == null || current.Stable.Signature == null)
            return (new StoredRouteState(trace, previous, trace.Signature != null ? trace : current.Stable), null);

        // Same route: it becomes the confirmed one, keeping a hop the confirmed route heard that this trace did not
        // (a router answering only some of the time), marked with when it last answered. Every destination of one
        // target counts as the same one: a hostname target rotates its address.
        if (!RouteSignature.Differs(current.Stable.Signature, trace.Signature))
            return (new StoredRouteState(trace, previous, HopHistory.Fill(trace, [current.Stable], _ => 0)), null);

        // Changed, and the trace before agrees with it: confirmed from when it was first seen.
        if (previous.Signature != null && !RouteSignature.Differs(previous.Signature, trace.Signature)
            && RouteSignature.Differs(current.Stable.Signature, previous.Signature))
            return (new StoredRouteState(trace, previous, trace), new RouteShift(previous.CheckedAt, current.Stable, trace));

        return (new StoredRouteState(trace, previous, current.Stable), null);
    }

    /// <summary>The shifts with one more added, oldest dropped past <see cref="MaxShifts"/>.</summary>
    public static IReadOnlyList<RouteShift> Append(IReadOnlyList<RouteShift> shifts, RouteShift shift) =>
        shifts.Append(shift).OrderBy(s => s.At).TakeLast(MaxShifts).ToList();

    /// <summary>
    /// The route a target took at <paramref name="at"/>: the "before" of the first shift after it, else
    /// the confirmed route.
    /// </summary>
    public static StoredRoute RouteAt(StoredRouteState state, IReadOnlyList<RouteShift> shifts, DateTime at) =>
        shifts.Where(s => s.At > at).OrderBy(s => s.At).FirstOrDefault()?.From ?? state.Stable;
}

/// <summary>
/// A route's places and networks in order, one "city|ASN" token per hop, consecutive repeats
/// collapsed. Never addresses: ECMP changes them between traces without the route going anywhere.
/// </summary>
public static class RouteSignature
{
    /// <summary>
    /// Builds the signature. Hops with neither a city nor a network are left out; null when none is left.
    /// </summary>
    /// <param name="hops">(city or null, ASN or null) per responding hop, nearest first.</param>
    public static string? Build(IEnumerable<(string? City, int? Asn)> hops)
    {
        var tokens = new List<string>();
        foreach (var (city, asn) in hops)
        {
            if (string.IsNullOrEmpty(city) && asn is not > 0) continue;
            var token = $"{city}|{asn}";
            if (tokens.Count > 0 && tokens[^1] == token) continue;
            tokens.Add(token);
        }
        return tokens.Count == 0 ? null : string.Join(" > ", tokens);
    }

    /// <summary>
    /// Whether two signatures describe different routes: each crosses a place or network the other
    /// does not. A token missing from one side only is a hop that stopped (or started) answering,
    /// which drops it from one trace without the route going anywhere.
    /// </summary>
    public static bool Differs(string a, string b)
    {
        var left = Tokens(a);
        var right = Tokens(b);
        return left.Except(right).Any() && right.Except(left).Any();
    }

    /// <summary>The tokens of a signature, in order.</summary>
    public static IReadOnlyList<string> Tokens(string signature) =>
        signature.Split(" > ", StringSplitOptions.RemoveEmptyEntries);
}
