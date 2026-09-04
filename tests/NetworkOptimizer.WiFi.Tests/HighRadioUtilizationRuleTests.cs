using FluentAssertions;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class HighRadioUtilizationRuleTests
{
    private readonly HighRadioUtilizationRule _rule = new();

    private static AccessPointSnapshot Ap(string mac, string name, int consoleUtil, int? measured = null, int? self = null, int? other = null) => new()
    {
        Mac = mac,
        Name = name,
        Radios = new()
        {
            new RadioSnapshot
            {
                Band = RadioBand.Band5GHz, Channel = 36, ChannelWidth = 80,
                ChannelUtilization = consoleUtil,
                MeasuredUtilization = measured, MeasuredSelfAirtime = self, MeasuredInterference = other
            }
        }
    };

    private static WiFiOptimizerContext Context(params AccessPointSnapshot[] aps) => new()
    {
        AccessPoints = aps.ToList(), Clients = [], Wlans = [], Networks = [], LegacyClients = [], SteerableClients = []
    };

    [Fact]
    public void Without_measurements_the_original_issue_names_every_busy_radio()
    {
        var issues = _rule.EvaluateAll(Context(
            Ap("aa:bb:cc:dd:ee:01", "AP-1", 80),
            Ap("aa:bb:cc:dd:ee:02", "AP-2", 75),
            Ap("aa:bb:cc:dd:ee:03", "AP-3", 20))).ToList();

        var issue = issues.Should().ContainSingle().Subject;
        issue.Title.Should().Be("High Radio Utilization Detected");
        issue.Description.Should().Be("2 radio(s) have utilization above 70%. Clients may experience slow speeds and higher latency during busy periods.");
        issue.AffectedEntity.Should().Be("AP-1 (5 GHz 80%), AP-2 (5 GHz 75%)");
        issue.ScoreImpact.Should().Be(-8);
        issue.Class.Should().Be(HealthIssueClass.Measured);
    }

    [Fact]
    public void Measured_radios_split_by_cause_and_the_rest_keep_the_original_issue()
    {
        var issues = _rule.EvaluateAll(Context(
            Ap("aa:bb:cc:dd:ee:01", "AP-Own", 80, measured: 78, self: 71, other: 5),
            Ap("aa:bb:cc:dd:ee:02", "AP-Other", 82, measured: 82, self: 10, other: 65),
            Ap("aa:bb:cc:dd:ee:03", "AP-Mixed", 75, measured: 75, self: 40, other: 35))).ToList();

        issues.Should().HaveCount(3);
        var own = issues[0];
        own.Title.Should().Be("High Radio Utilization From Own Clients");
        own.Description.Should().Be("1 radio(s) are busier than 70%, and most of that airtime is their own clients' traffic: AP-Own (5 GHz 78% busy, 71% own).");
        own.ScoreImpact.Should().Be(-8);

        var other = issues[1];
        other.Title.Should().Be("High Radio Utilization From Interference");
        other.Description.Should().Be("1 radio(s) are busier than 70%, and most of that airtime is not theirs: AP-Other (5 GHz 82% busy, 65% from other transmitters). Counted once against the health score with the issue above.");
        other.ScoreImpact.Should().Be(0, "one impact for the rule");

        var generic = issues[2];
        generic.Title.Should().Be("High Radio Utilization Detected");
        generic.AffectedEntity.Should().Be("AP-Mixed (5 GHz 75%)");

        issues.Select(i => i.Key).Should().OnlyHaveUniqueItems();
    }
}
