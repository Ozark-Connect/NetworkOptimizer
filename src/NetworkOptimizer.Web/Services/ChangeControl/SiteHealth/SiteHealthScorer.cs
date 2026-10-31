using System.Text.Json;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.ChangeControl.SiteHealth;

/// <summary>
/// One 5-minute window of the loss pool: the median loss across its targets, how close the WAN came to
/// saturation (0 idle, 1 at or past <c>LossSaturationLoadFraction</c> of plan), and whether the LAN lost
/// packets alongside it, which makes the loss local rather than the ISP's.
/// </summary>
public sealed record LossWindow(double LossPct, double Saturation, bool LanLoss);

/// <summary>One hour's measurements, gathered by <see cref="SiteHealthJob"/>.</summary>
public sealed class HealthHourInputs
{
    public DateTime Hour { get; init; }

    // Internet (primary WAN)
    /// <summary>The hour's loss, for display; scored from <see cref="LossWindows"/> when there are any.</summary>
    public double? LossPct { get; init; }
    /// <summary>The hour's loss pool per 5-minute window, scored by time share.</summary>
    public IReadOnlyList<LossWindow> LossWindows { get; init; } = [];
    public double? IdleRttMs { get; init; }
    public double? LoadedRttMs { get; init; }
    public double LoadedShare { get; init; }
    public double OutageMinutes { get; init; }
    /// <summary>The outage label when the default "WAN outage, N min" would misdescribe it (a partial-only hour).</summary>
    public string? OutageText { get; init; }
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
    /// <summary>The hour's Fabric loss, for display; scored from <see cref="FabricWindows"/> when there are any.</summary>
    public double? FabricLossPct { get; init; }
    /// <summary>Median Fabric loss per 5-minute window, scored by time share.</summary>
    public IReadOnlyList<double> FabricWindows { get; init; } = [];
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
    public const int Version = 7;

    /// <summary>Loss below this is not felt, so it costs nothing.</summary>
    internal const double NoticeableLossPct = 0.25;

    /// <summary>Loss this far above its ceiling is a full impairment for the window.</summary>
    internal const double LossRampPct = 2.5;

    /// <summary>The window loss and Fabric loss are scored in.</summary>
    public const int WindowMinutes = 5;

    /// <summary>Satisfaction at or above this is good service (UniFi's own coloring).</summary>
    public const int GoodSatisfaction = 80;

    public static SiteHealthHour Score(HealthHourInputs x, AccessProfile profile, DateTime nowUtc)
    {
        var contributors = new List<HealthContributor>();

        // Internet: outage, loss, latency are the ISP's unless something local explains them.
        var outage = Math.Clamp(x.OutageMinutes / 60.0, 0, 1);
        var (loss, lossText) = LossImpairment(x, profile);
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
        var hasInternet = x.LossPct != null || x.LossWindows.Count > 0 || x.IdleRttMs != null || x.OutageMinutes > 0;
        double? internet = hasInternet ? Combine(isp, local) : null;

        var where = x.LocalCause is { } cause ? $" ({cause})" : " (ISP)";
        if (outage > 0) contributors.Add(new("Internet", outage, $"{x.OutageText ?? $"WAN outage, {x.OutageMinutes:0} min"}{where}"));
        if (loss > 0) contributors.Add(new("Internet", loss, $"{lossText}{where}"));
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

        var (fabric, fabricText) = FabricImpairment(x);
        var link = Math.Clamp(x.LinkImpairment, 0, 1);
        double? lan = x.FabricLossPct != null || x.FabricWindows.Count > 0 || x.HasLinkData ? Combine(fabric, link) : null;
        if (fabric > 0) contributors.Add(new("LAN", fabric, fabricText!));
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

    /// <summary>
    /// Loss as a share of the hour. Each 5-minute window ramps over <see cref="LossRampPct"/> from a
    /// ceiling that rises with load, and the windows average by time, so a short burst costs its share
    /// of the hour. A window where the LAN lost packets too is left to LAN, which scores it.
    /// </summary>
    internal static (double Impairment, string? Text) LossImpairment(HealthHourInputs x, AccessProfile profile)
    {
        if (x.LossWindows.Count == 0)
        {
            if (x.LossPct is not { } l) return (0, null);
            var ceiling = LossCeiling(profile, 0);
            var whole = Ramp(l, ceiling, ceiling + LossRampPct);
            return whole > 0 ? (whole, $"loss {l:0.##}%") : (0, null);
        }

        double sum = 0;
        var hit = new List<LossWindow>();
        foreach (var w in x.LossWindows)
        {
            if (w.LanLoss) continue;
            var ceiling = LossCeiling(profile, w.Saturation);
            var impairment = Ramp(w.LossPct, ceiling, ceiling + LossRampPct);
            if (impairment <= 0) continue;
            sum += impairment;
            hit.Add(w);
        }
        if (hit.Count == 0) return (0, null);
        var underLoad = hit.Average(w => w.Saturation) >= 0.5 ? " under load" : "";
        return (sum / x.LossWindows.Count, $"loss {hit.Average(w => w.LossPct):0.##}% for {hit.Count * WindowMinutes} min{underLoad}");
    }

    /// <summary>
    /// Where loss starts to cost, as ISP Health calibrates Packet Loss: the profile's idle ceiling, rising
    /// quadratically with <paramref name="saturation"/> to its loaded-loss band. Never under
    /// <see cref="NoticeableLossPct"/>, since no one notices less.
    /// </summary>
    internal static double LossCeiling(AccessProfile profile, double saturation)
    {
        var t = Math.Clamp(saturation, 0, 1);
        var ceiling = profile.IdleLossAcceptablePct + t * t * (profile.LoadedLossDownLowPct - profile.IdleLossAcceptablePct);
        return Math.Max(NoticeableLossPct, ceiling);
    }

    /// <summary>LAN (Fabric) loss as a share of the hour, by 5-minute window like Internet loss.</summary>
    private static (double Impairment, string? Text) FabricImpairment(HealthHourInputs x)
    {
        if (x.FabricWindows.Count == 0)
            return x.FabricLossPct is { } fl && Ramp(fl, 0.5, 5) is var whole && whole > 0 ? (whole, $"LAN loss {fl:0.#}%") : (0, null);
        var hit = x.FabricWindows.Where(f => Ramp(f, 0.5, 5) > 0).ToList();
        if (hit.Count == 0) return (0, null);
        return (x.FabricWindows.Sum(f => Ramp(f, 0.5, 5)) / x.FabricWindows.Count,
            $"LAN loss {hit.Average():0.#}% for {hit.Count * WindowMinutes} min");
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
