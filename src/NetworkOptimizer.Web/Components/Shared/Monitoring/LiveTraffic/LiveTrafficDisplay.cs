using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

namespace NetworkOptimizer.Web.Components.Shared.Monitoring.LiveTraffic;

/// <summary>Labels and classes every Live Traffic list draws the same way.</summary>
public static class LiveTrafficDisplay
{
    public static string RouteClass(LiveRouteKind kind) => kind switch
    {
        LiveRouteKind.Vpn => "lt-route-vpn",
        LiveRouteKind.Lan => "lt-route-lan",
        _ => "lt-route-wan",
    };

    public static string? ActionClass(string? action) => action == "Blocked" ? "lt-action-blocked" : "lt-action-allowed";

    public static string FieldLabel(LiveTrafficField field) => field switch
    {
        LiveTrafficField.Client => "Client",
        LiveTrafficField.Destination => "Destination",
        LiveTrafficField.App => "App",
        LiveTrafficField.Route => "Route",
        LiveTrafficField.Protocol => "Protocol",
        LiveTrafficField.Port => "Port",
        LiveTrafficField.Action => "Action",
        _ => "Resolver",
    };

    /// <summary>A wired client's "Office Switch · # 10" spelled out ("Office Switch, Port 10"); null for anything else.</summary>
    public static string? ViaTooltip(string? via)
    {
        if (via == null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(via, @"^(?:(.+) · )?# (\d+)$");
        if (!m.Success) return null;
        return m.Groups[1].Success ? $"{m.Groups[1].Value}, Port {m.Groups[2].Value}" : $"Port {m.Groups[2].Value}";
    }

    /// <summary>
    /// The client card's lines under the name, one per line: how it connects, then its address and
    /// source port ("Office Switch, Port 10" / "192.0.2.10 · TCP 51512").
    /// </summary>
    public static string ClientCardDetail(LiveFlowRow f)
    {
        var via = ViaTooltip(f.ClientVia) ?? (f.ClientVia != f.ClientIp ? f.ClientVia : null);
        var address = f.ClientPort > 0 ? $"{f.ClientIp} · {f.Proto} {f.ClientPort}" : f.ClientIp;
        return Lines(via, address, RoundTripLine(f.ClientRoundTrip));
    }

    /// <summary>The client card's lines for a site client or device at the far end: how it connects, then its address and port.</summary>
    public static string PeerCardDetail(LiveFlowRow f)
    {
        var via = ViaTooltip(f.RemoteClientVia) ?? f.RemoteClientVia;
        var address = f.RemotePort > 0 ? $"{f.DestinationIp} · {f.Proto} {f.RemotePort}" : f.DestinationIp;
        return Lines(via, address, RoundTripLine(f.DestinationRoundTrip));
    }

    private static string Lines(params string?[] lines) => string.Join("\n", lines.Where(l => !string.IsNullOrEmpty(l)));

    /// <summary>
    /// A round trip: "16 ms", "4.2 ms". Under 1 ms reads "&lt;1 ms": the Gateway times events as it reads
    /// them, about a quarter of a millisecond late, so finer digits would be noise.
    /// </summary>
    public static string RoundTrip(double ms) => ms < 1 ? "<1 ms" : DisplayFormatters.FormatResponseMs(ms);

    /// <summary>A hover card's round trip line: "Round trip 16 ms (median of 8 connections)"; null for none.</summary>
    public static string? RoundTripLine(RoundTripSummary? summary) => summary switch
    {
        null => null,
        { Connections: 1 } => $"Round trip {RoundTrip(summary.MedianMs)}",
        _ => $"Round trip {RoundTrip(summary.MedianMs)} (median of {summary.Connections} connections)",
    };

    /// <summary>The flow detail's round trip value: to the destination, then to the client when there is one.</summary>
    public static string? FlowRoundTrip(LiveFlowRow f)
    {
        if (f.RemoteRttMs is null && f.ClientRttMs is null) return null;
        var parts = new List<string>(2);
        if (f.RemoteRttMs is { } remote) parts.Add($"{RoundTrip(remote)} to the destination");
        if (f.ClientRttMs is { } client) parts.Add($"{RoundTrip(client)} to the client");
        return string.Join(", ", parts);
    }

    public static string Pct(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"12s ago", "Over 3 min ago" for a flow older than the agent, or "Ended just now".</summary>
    public static string StartedLabel(LiveFlowRow f)
    {
        // An ended flow says when it ended; its start is in the detail panel.
        if (f.EndedAt is { } ended) return $"Ended {Relative(ended)}";
        if (f.Started is not { } s) return "";
        var label = TimeFormatHelper.FormatRelativeTimeShort(s);
        return f.StartedIsUpperBound && DateTime.UtcNow - s >= TimeSpan.FromMinutes(1) ? $"Over {label}" : label;

        static string Relative(DateTime utc) =>
            DateTime.UtcNow - utc < TimeSpan.FromMinutes(1) ? "just now" : TimeFormatHelper.FormatRelativeTimeShort(utc);
    }

    public static string DurationLabel(LiveFlowRow f) =>
        f.Duration is { } d ? d < TimeSpan.FromSeconds(1) ? "<1s" : TimeFormatHelper.FormatDuration(d) : "";
}
