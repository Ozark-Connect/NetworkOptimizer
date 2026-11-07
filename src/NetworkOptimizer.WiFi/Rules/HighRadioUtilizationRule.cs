using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when a radio's air is busy enough to slow every client on it. One issue per
/// radio, gated on the console's utilization averaged over the last hour (a single reading of a
/// spiky counter is never the gate). On radios the AP Agent measures, the airtime is attributed:
/// mostly other networks (move channel), mostly the radio's own clients (add capacity), or mixed.
/// The cause picks the title and the action; the key stays the radio's, so an acknowledgment
/// survives the cause changing.
/// </summary>
public class HighRadioUtilizationRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-HIGH-UTILIZATION-001";

    /// <summary>Utilization above which a radio is busy (percentage).</summary>
    private const int UtilizationThreshold = 70;

    /// <summary>Utilization above which a busy radio with enough clients is Critical.</summary>
    private const int CriticalUtilization = 90;

    /// <summary>Share of the measured airtime one side must hold to be called the cause.</summary>
    private const double DominantSharePct = 60;

    /// <summary>Clients on the radio from which busy air is a Warning; below it, Info with no impact.</summary>
    private const int WarningClientCount = 3;

    private const int WarningImpact = -8;
    private const int CriticalImpact = -10;

    private enum Cause { OtherNetworks, OwnClients, Mixed, Unknown }

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx) => null;

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext ctx)
    {
        var steeringOnEverywhere = BandSteeringOnEverywhere(ctx.Wlans);

        foreach (var ap in ctx.AccessPoints)
        {
            foreach (var radio in ap.Radios.Where(r => r.Channel.HasValue))
            {
                // The hour's average when the console reported one, else its latest reading.
                double? busy = radio.HourUtilizationAvg ?? radio.ChannelUtilization;
                if (busy is not { } utilization || utilization <= UtilizationThreshold) continue;

                var clients = radio.ClientCount ?? 0;
                if (clients == 0) continue;

                var cause = CauseOf(radio);
                var severity = clients < WarningClientCount ? HealthIssueSeverity.Info
                    : utilization > CriticalUtilization ? HealthIssueSeverity.Critical
                    : HealthIssueSeverity.Warning;

                var description = Describe(ap, radio, utilization, clients, cause);
                if (severity == HealthIssueSeverity.Info)
                    description += clients == 1
                        ? " Only 1 client uses this radio, so the impact is small."
                        : $" Only {clients} clients use this radio, so the impact is small.";

                yield return new HealthIssue
                {
                    Severity = severity,
                    Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.CapacityHeadroom, HealthDimension.ChannelHealth },
                    Class = HealthIssueClass.Measured,
                    Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Radio(ap.Mac, radio.Band)),
                    Title = cause switch
                    {
                        Cause.OtherNetworks => "High Radio Utilization From Interference",
                        Cause.OwnClients => "High Radio Utilization From Own Clients",
                        _ => "High Radio Utilization"
                    },
                    Description = description,
                    AffectedEntity = ap.Name,
                    LinkUrl = $"./wifi-optimizer?tab=metrics&ap={Uri.EscapeDataString(ap.Mac)}&band={radio.Band.ToPropagationBand()}&range=1h",
                    Recommendation = Action(radio.Band, cause, steeringOnEverywhere),
                    ScoreImpact = severity switch
                    {
                        HealthIssueSeverity.Critical => CriticalImpact,
                        HealthIssueSeverity.Warning => WarningImpact,
                        _ => 0
                    }
                };
            }
        }
    }

    /// <summary>The agent's split, when it measured both sides and there is a total to take the share of.</summary>
    private static bool Attributable(RadioSnapshot r) =>
        r.MeasuredUtilization is > 0 && r.MeasuredSelfAirtime.HasValue && r.MeasuredInterference.HasValue
        && r.MeasuredSelfAirtime + r.MeasuredInterference > 0;

    private static Cause CauseOf(RadioSnapshot r)
    {
        if (!Attributable(r)) return Cause.Unknown;
        double Share(int? part) => 100.0 * (part ?? 0) / r.MeasuredUtilization!.Value;
        if (Share(r.MeasuredInterference) >= DominantSharePct) return Cause.OtherNetworks;
        if (Share(r.MeasuredSelfAirtime) >= DominantSharePct) return Cause.OwnClients;
        return Cause.Mixed;
    }

    /// <summary>
    /// One line, every number from the same window. The split applies the agent's own/other ratio
    /// to the hour's average, so the two parts add up to the total shown.
    /// </summary>
    private static string Describe(AccessPointSnapshot ap, RadioSnapshot radio, double utilization, int clients, Cause cause)
    {
        var subject = $"{ap.Name} {radio.Band.ToDisplayString()}";
        var onIt = StringUtilities.Pluralize(clients, "client");
        var total = (int)Math.Round(utilization);
        if (radio.HourUtilizationAvg is null)
            return $"{subject} is {total}% busy right now, with {onIt} on it.";

        var peak = (int)Math.Round(radio.HourUtilizationPeak ?? utilization);
        var averaged = $"{subject} averaged {total}% busy over the last hour (peak {peak}%)";
        if (cause == Cause.Unknown)
            return $"{averaged}, with {onIt} on it.";

        var interference = radio.MeasuredInterference!.Value;
        var other = (int)Math.Round((double)total * interference / (interference + radio.MeasuredSelfAirtime!.Value));
        return $"{averaged}. {other} points are other networks and {total - other} are its {onIt}.";
    }

    private static string Action(RadioBand band, Cause cause, bool steeringOnEverywhere)
    {
        switch (cause)
        {
            case Cause.OtherNetworks:
                var move = "Run the Channel Recommendation on Channels to find a quieter channel, and check RF Environment for the networks on this one.";
                return band == RadioBand.Band2_4GHz
                    ? move + " 2.4 GHz has only three non-overlapping channels, so with neighbors on all three, some of this may be unavoidable."
                    : move;
            case Cause.OwnClients:
                if (band != RadioBand.Band2_4GHz)
                    return "Spread clients across more APs, and check Client Performance for one client doing most of the talking.";
                return steeringOnEverywhere
                    ? "Band steering is already on, so the clients still on 2.4 GHz are likely 2.4 GHz-only. Check Client Performance for which ones, and for one client doing most of the talking."
                    : "Spread clients across more APs, or turn on band steering so capable clients move to 5 GHz or 6 GHz.";
            case Cause.Mixed:
                return "Other networks and this radio's own clients are both using the air. Run the Channel Recommendation, and spread clients across more APs.";
            default:
                return "Check RF Environment for networks on this channel, and Client Performance for heavy clients on this radio.";
        }
    }

    /// <summary>
    /// Every enabled non-guest SSID that could steer off 2.4 GHz (it is on 2.4 GHz and a higher
    /// band) already has band steering on. A 2.4 GHz-only SSID has nothing to steer to, so it is not counted.
    /// </summary>
    private static bool BandSteeringOnEverywhere(List<WlanConfiguration> wlans)
    {
        var steerable = wlans
            .Where(w => w.Enabled && !w.IsGuest && w.EnabledBands.Contains(RadioBand.Band2_4GHz)
                && w.EnabledBands.Any(b => b is RadioBand.Band5GHz or RadioBand.Band6GHz))
            .ToList();
        return steerable.Count > 0 && steerable.All(w => w.BandSteeringEnabled);
    }
}
