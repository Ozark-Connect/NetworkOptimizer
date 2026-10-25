using System.Text.Json;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.ChangeControl.SiteHealth;

/// <summary>One hour's measurements, gathered by <see cref="SiteHealthJob"/>.</summary>
public sealed class HealthHourInputs
{
    public DateTime Hour { get; init; }

    // Internet (primary WAN)
    public double? LossPct { get; init; }
    public double? IdleRttMs { get; init; }
    public double? LoadedRttMs { get; init; }
    public double LoadedShare { get; init; }
    public double OutageMinutes { get; init; }
    public string? LocalCause { get; init; }
    public double? BaselineRttMs { get; init; }

    // Wi-Fi
    public int WifiSamples { get; init; }
    public int WifiBelow80 { get; init; }
    public int WifiBelow60 { get; init; }
    public string? WorstRadio { get; init; }
    public double WorstRadioShare { get; init; }

    // Clients
    public int? WirelessClients { get; init; }
    public int? WiredClients { get; init; }

    // Availability
    public double Availability { get; init; }
    public string? AvailabilityText { get; init; }

    // LAN
    public double? FabricLossPct { get; init; }
    public bool HasLinkData { get; init; }
    public double LinkImpairment { get; init; }
    public string? LinkText { get; init; }
}

/// <summary>One contributor to an hour's impairment, for the hover and the worst label.</summary>
public sealed record HealthContributor(string Area, double Impairment, string Text);

/// <summary>
/// Site Health for one hour: the share of user-time with good service. Each area's impairment is the
/// share of the people it serves that had a bad time; areas combine by population, so there are no
/// hand-tuned weights between them. Thresholds come from ISP Health's access-technology profile.
/// </summary>
public static class SiteHealthScorer
{
    /// <summary>Bump when the scoring changes; stored hours at an older version are recomputed.</summary>
    public const int Version = 2;

    /// <summary>Loss below this is not felt, so it costs nothing.</summary>
    internal const double NoticeableLossPct = 0.25;

    /// <summary>Satisfaction at or above this is good service (UniFi's own coloring).</summary>
    public const int GoodSatisfaction = 80;

    public static SiteHealthHour Score(HealthHourInputs x, AccessProfile profile, DateTime nowUtc)
    {
        var contributors = new List<HealthContributor>();

        // Internet: outage, loss, latency are the ISP's unless something local explains them.
        var outage = Math.Clamp(x.OutageMinutes / 60.0, 0, 1);
        // Loss hurts from about a quarter percent (calls and games show it); below that no one notices,
        // whatever the profile's ideal. Full impairment 2.5 points above where it starts.
        var lossFloor = Math.Max(profile.IdleLossAcceptablePct, NoticeableLossPct);
        var loss = x.LossPct is { } l ? Ramp(l, lossFloor, lossFloor + 2.5) : 0;
        double latency = 0;
        if (x.IdleRttMs is { } rtt)
        {
            var lo = Math.Max(profile.IdleRttNormalHighMs, (x.BaselineRttMs ?? 0) * 1.5);
            var hi = Math.Max(profile.IdleRttPoorMs, (x.BaselineRttMs ?? 0) * 3);
            latency = 0.5 * Ramp(rtt, lo, hi) + 0.5 * Ramp(rtt, hi, 2 * hi);
        }
        double bloat = 0;
        double? delta = x.LoadedRttMs is { } loaded && x.IdleRttMs is { } idle ? Math.Max(0, loaded - idle) : null;
        if (delta is { } d && x.LoadedShare > 0)
            bloat = Math.Clamp(x.LoadedShare, 0, 1) * Ramp(d, profile.LoadedDeltaAcceptableMs, 4 * profile.LoadedDeltaAcceptableMs);

        var upstream = Combine(outage, loss, latency);
        var isp = x.LocalCause == null ? upstream : 0;
        var local = Combine(x.LocalCause == null ? 0 : upstream, bloat);
        var hasInternet = x.LossPct != null || x.IdleRttMs != null || x.OutageMinutes > 0;
        double? internet = hasInternet ? Combine(isp, local) : null;

        var where = x.LocalCause is { } cause ? $" ({cause})" : " (ISP)";
        if (outage > 0) contributors.Add(new("Internet", outage, $"WAN outage, {x.OutageMinutes:0} min{where}"));
        if (loss > 0) contributors.Add(new("Internet", loss, $"loss {x.LossPct:0.##}%{where}"));
        if (latency > 0)
            contributors.Add(new("Internet", latency, $"latency {x.IdleRttMs:0.#} ms{(x.BaselineRttMs is { } b ? $" vs {b:0.#} normal" : "")}{where}"));
        if (bloat > 0) contributors.Add(new("Internet", bloat, $"latency +{delta:0} ms under load (bufferbloat)"));

        // Wi-Fi: a sample under 60 counts fully, one between 60 and 80 counts half.
        double? wifi = x.WifiSamples > 0
            ? Math.Clamp(((x.WifiBelow80 - x.WifiBelow60) * 0.5 + x.WifiBelow60) / x.WifiSamples, 0, 1)
            : null;
        var clients = (x.WirelessClients ?? 0) + (x.WiredClients ?? 0);
        double? wirelessShare = clients > 0 ? (double)(x.WirelessClients ?? 0) / clients : x.WifiSamples > 0 ? 1 : null;
        var wifiWeighted = (wifi ?? 0) * (wirelessShare ?? 1);
        if (wifiWeighted > 0)
            contributors.Add(new("Wi-Fi", wifiWeighted, x.WorstRadio is { } radio
                ? $"{radio}, {x.WorstRadioShare * 100:0}% of samples under {GoodSatisfaction}"
                : $"{(double)x.WifiBelow80 / Math.Max(1, x.WifiSamples) * 100:0}% of samples under {GoodSatisfaction}"));

        var availability = Math.Clamp(x.Availability, 0, 1);
        if (availability > 0) contributors.Add(new("Availability", availability, x.AvailabilityText ?? "devices offline"));

        var fabric = x.FabricLossPct is { } fl ? Ramp(fl, 0.5, 5) : 0;
        var link = Math.Clamp(x.LinkImpairment, 0, 1);
        double? lan = x.FabricLossPct != null || x.HasLinkData ? Combine(fabric, link) : null;
        if (fabric > 0) contributors.Add(new("LAN", fabric, $"LAN loss {x.FabricLossPct:0.#}%"));
        if (link > 0) contributors.Add(new("LAN", link, x.LinkText ?? "port errors"));

        double? health = internet == null && wifi == null && lan == null
            ? null
            : Math.Round(100 * (1 - (internet ?? 0)) * (1 - availability) * (1 - wifiWeighted) * (1 - (lan ?? 0)), 1);

        var top = contributors.Where(c => c.Impairment >= 0.001).OrderByDescending(c => c.Impairment).Take(3).ToList();
        return new SiteHealthHour
        {
            HourStart = x.Hour,
            Health = health,
            Internet = Round(internet),
            InternetIsp = hasInternet ? Round(isp) : null,
            Wifi = Round(wifi),
            Availability = Round(availability),
            Lan = Round(lan),
            WirelessShare = Round(wirelessShare),
            Clients = clients > 0 ? clients : null,
            LossPct = x.LossPct,
            AccessRttMs = x.IdleRttMs,
            LoadedDeltaMs = delta,
            LoadedShare = x.LoadedShare > 0 ? Round(x.LoadedShare) : null,
            Worst = top.FirstOrDefault() is { } w ? Truncate($"{w.Area}: {w.Text}", 200) : null,
            DetailsJson = top.Count > 0 ? Truncate(JsonSerializer.Serialize(top), 2000) : null,
            Version = Version,
            ComputedAt = nowUtc,
        };
    }

    /// <summary>Health with the ISP's part taken out: what the site itself delivered. Null when unknown.</summary>
    public static double? LocalHealth(SiteHealthHour h) =>
        h.Health is not { } health ? null
        : h.InternetIsp is { } isp && isp < 1 ? Math.Min(100, health / (1 - isp))
        : h.InternetIsp is null ? health : null;

    /// <summary>The stored contributors for an hour.</summary>
    public static List<HealthContributor> Contributors(SiteHealthHour h)
    {
        if (string.IsNullOrEmpty(h.DetailsJson)) return [];
        try { return JsonSerializer.Deserialize<List<HealthContributor>>(h.DetailsJson) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>0 below <paramref name="lo"/>, 1 at or above <paramref name="hi"/>, linear between.</summary>
    internal static double Ramp(double v, double lo, double hi) =>
        hi <= lo ? (v >= hi ? 1 : 0) : Math.Clamp((v - lo) / (hi - lo), 0, 1);

    /// <summary>Independent impairments: the share affected by at least one.</summary>
    internal static double Combine(params double[] parts) => 1 - parts.Aggregate(1.0, (acc, p) => acc * (1 - Math.Clamp(p, 0, 1)));

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 4) : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
