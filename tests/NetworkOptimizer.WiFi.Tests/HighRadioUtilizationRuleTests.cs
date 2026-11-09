using FluentAssertions;
using NetworkOptimizer.WiFi.Analyzers;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class HighRadioUtilizationRuleTests
{
    private readonly HighRadioUtilizationRule _rule = new();

    private static AccessPointSnapshot Ap(
        string name, RadioBand band = RadioBand.Band5GHz, int? consoleUtil = 50, double? hourAvg = 75, double? hourPeak = 88,
        int clients = 6, int? measured = null, int? self = null, int? other = null) => new()
    {
        Mac = "aa:bb:cc:dd:ee:01",
        Name = name,
        Radios = new()
        {
            new RadioSnapshot
            {
                Band = band, Channel = band == RadioBand.Band2_4GHz ? 6 : 36, ChannelWidth = band == RadioBand.Band2_4GHz ? 20 : 80,
                ChannelUtilization = consoleUtil, HourUtilizationAvg = hourAvg, HourUtilizationPeak = hourPeak,
                ClientCount = clients,
                MeasuredUtilization = measured, MeasuredSelfAirtime = self, MeasuredInterference = other
            }
        }
    };

    private static WlanConfiguration Wlan(bool steering) => new()
    {
        Name = "Home", Enabled = true, BandSteeringEnabled = steering,
        EnabledBands = [RadioBand.Band2_4GHz, RadioBand.Band5GHz]
    };

    private static WiFiOptimizerContext Context(AccessPointSnapshot ap, params WlanConfiguration[] wlans) => new()
    {
        AccessPoints = [ap], Clients = [], Wlans = wlans.ToList(), Networks = [], LegacyClients = [], SteerableClients = []
    };

    private List<HealthIssue> Evaluate(AccessPointSnapshot ap, params WlanConfiguration[] wlans) =>
        _rule.EvaluateAll(Context(ap, wlans)).ToList();

    [Fact]
    public void A_busy_radio_raises_one_issue_where_the_scorer_used_to_raise_a_second()
    {
        var ap = Ap("Back Yard", RadioBand.Band2_4GHz, consoleUtil: 79, hourAvg: 74, measured: 36, self: 6, other: 30);

        var scored = new SiteHealthScorer().Calculate([ap], []);
        var issues = scored.Issues.Concat(Evaluate(ap)).Where(i => i.Dimensions.Contains(HealthDimension.ChannelHealth)).ToList();

        var issue = issues.Should().ContainSingle().Subject;
        issue.Key.Should().Be("WIFI-HIGH-UTILIZATION-001|aa:bb:cc:dd:ee:01/ng");
    }

    [Fact]
    public void The_gate_is_the_hours_average_not_the_latest_reading()
    {
        Evaluate(Ap("AP-1", consoleUtil: 95, hourAvg: 40)).Should().BeEmpty("one busy reading in a quiet hour is not a busy radio");
        Evaluate(Ap("AP-1", consoleUtil: 30, hourAvg: 75)).Should().ContainSingle();
    }

    [Fact]
    public void Without_an_hourly_average_the_latest_reading_gates_and_is_quoted()
    {
        var issue = Evaluate(Ap("AP-1", consoleUtil: 80, hourAvg: null, hourPeak: null)).Should().ContainSingle().Subject;
        issue.Description.Should().Be("AP-1 5 GHz is 80% busy right now, with 6 clients on it.");
    }

    [Fact]
    public void Client_tiers_none_few_and_many()
    {
        Evaluate(Ap("AP-1", clients: 0)).Should().BeEmpty("a radio nobody uses pays nothing for busy air");

        var few = Evaluate(Ap("AP-1", clients: 1)).Should().ContainSingle().Subject;
        few.Severity.Should().Be(HealthIssueSeverity.Info);
        few.ScoreImpact.Should().Be(0);
        few.Description.Should().EndWith("with 1 client on it. Only 1 client uses this radio, so the impact is small.");

        Evaluate(Ap("AP-1", clients: 2)).Single().Description.Should().EndWith("Only 2 clients use this radio, so the impact is small.");

        var many = Evaluate(Ap("AP-1", clients: 3)).Should().ContainSingle().Subject;
        many.Severity.Should().Be(HealthIssueSeverity.Warning);
        many.ScoreImpact.Should().BeNegative();

        Evaluate(Ap("AP-1", hourAvg: 92, hourPeak: 97)).Single().Severity.Should().Be(HealthIssueSeverity.Critical);
    }

    [Fact]
    public void Other_networks_split_names_both_parts_of_the_same_average()
    {
        var issue = Evaluate(Ap("Back Yard", hourAvg: 74, hourPeak: 88, measured: 36, self: 6, other: 30)).Single();

        issue.Title.Should().Be("High Radio Utilization From Interference");
        // 74 points split 30:6 between other networks and own clients.
        issue.Description.Should().Be("Back Yard 5 GHz averaged 74% busy over the last hour (peak 88%). 62 points are other networks and 12 are its 6 clients.");
        issue.Recommendation.Should().Be("Run the Channel Recommendation on Channels to find a quieter channel, and check RF Environment for the networks on this one.");
        issue.LinkUrl.Should().Be("./wifi-optimizer?tab=metrics&ap=aa%3Abb%3Acc%3Add%3Aee%3A01&band=5&range=1h");
    }

    [Fact]
    public void Other_networks_on_24_GHz_adds_the_three_channel_qualifier()
    {
        var issue = Evaluate(Ap("AP-1", RadioBand.Band2_4GHz, measured: 50, self: 5, other: 40)).Single();
        issue.Recommendation.Should().EndWith(" 2.4 GHz has only three non-overlapping channels, so with neighbors on all three, some of this may be unavoidable.");
    }

    [Fact]
    public void Other_networks_on_24_GHz_at_its_quietest_channel_is_Info_with_no_impact()
    {
        var ap = Ap("AP-1", RadioBand.Band2_4GHz, hourAvg: 80, measured: 50, self: 5, other: 40);
        ap.Radios[0].QuietestChannelUtilization = 77;

        var issue = Evaluate(ap).Single();
        issue.Severity.Should().Be(HealthIssueSeverity.Info);
        issue.ScoreImpact.Should().Be(0);
        issue.Recommendation.Should().EndWith("some of this may be unavoidable.");

        ap.Radios[0].QuietestChannelUtilization = 40;
        Evaluate(ap).Single().Severity.Should().Be(HealthIssueSeverity.Warning, "a quieter channel exists");
    }

    [Fact]
    public void Own_clients_on_24_GHz_depends_on_band_steering()
    {
        var off = Evaluate(Ap("AP-1", RadioBand.Band2_4GHz, measured: 60, self: 50, other: 5), Wlan(steering: false)).Single();
        off.Title.Should().Be("High Radio Utilization From Own Clients");
        off.Recommendation.Should().Be("Move fixed devices to a less busy AP with Lock to AP in UniFi Network, or turn on band steering so capable clients move to 5 GHz or 6 GHz.");

        Evaluate(Ap("AP-1", RadioBand.Band5GHz, measured: 60, self: 50, other: 5), Wlan(steering: false)).Single().Recommendation
            .Should().Be("Move fixed devices to a less busy AP with Lock to AP in UniFi Network, and check Client Performance for one client doing most of the talking.");

        var on = Evaluate(Ap("AP-1", RadioBand.Band2_4GHz, measured: 60, self: 50, other: 5), Wlan(steering: true)).Single();
        on.Recommendation.Should().StartWith("Band steering is already on, so the clients still on 2.4 GHz are likely 2.4 GHz-only.");
    }

    [Fact]
    public void Mixed_split_names_both_actions()
    {
        var issue = Evaluate(Ap("AP-1", measured: 70, self: 35, other: 30)).Single();
        issue.Title.Should().Be("High Radio Utilization");
        issue.Recommendation.Should().Be("Try a quieter channel first: run the Channel Recommendation. If it stays busy, use Lock to AP in UniFi Network to move fixed devices to a less busy AP.");
    }

    [Fact]
    public void Without_an_agent_split_the_average_and_peak_stand_alone()
    {
        var issue = Evaluate(Ap("AP-1", hourAvg: 75.4, hourPeak: 90.2)).Single();
        issue.Title.Should().Be("High Radio Utilization");
        issue.Description.Should().Be("AP-1 5 GHz averaged 75% busy over the last hour (peak 90%), with 6 clients on it.");
        issue.Recommendation.Should().Be("Check RF Environment for networks on this channel, and Client Performance for heavy clients on this radio.");
    }

    [Fact]
    public void The_key_is_the_radio_whatever_the_cause()
    {
        var own = Evaluate(Ap("AP-1", measured: 70, self: 60, other: 5)).Single();
        var other = Evaluate(Ap("AP-1", measured: 70, self: 5, other: 60)).Single();
        var none = Evaluate(Ap("AP-1")).Single();

        own.Key.Should().Be(other.Key).And.Be(none.Key).And.Be("WIFI-HIGH-UTILIZATION-001|aa:bb:cc:dd:ee:01/na");
    }
}
