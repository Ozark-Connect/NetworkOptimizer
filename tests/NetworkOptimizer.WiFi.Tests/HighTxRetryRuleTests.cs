using FluentAssertions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class HighTxRetryRuleTests
{
    private readonly HighTxRetryRule _rule = new();

    private static AccessPointSnapshot Ap(RadioBand band, double retries, int clients, int? busy = null, int? interference = null) => new()
    {
        Mac = "aa:bb:cc:dd:ee:01",
        Name = "AP-1",
        Status = new(DeviceStatusKind.Online, "Online"),
        Radios = new()
        {
            new RadioSnapshot
            {
                Band = band, Channel = 6, TxRetriesPct = retries, ClientCount = clients,
                MeasuredUtilization = busy, MeasuredInterference = interference
            }
        }
    };

    private static WiFiOptimizerContext Context(params AccessPointSnapshot[] aps) => new()
    {
        AccessPoints = aps.ToList(), Clients = [], Wlans = [], Networks = [], LegacyClients = [], SteerableClients = []
    };

    [Theory]
    [InlineData(RadioBand.Band2_4GHz, 18.3, false)]
    [InlineData(RadioBand.Band2_4GHz, 26.0, true)]
    [InlineData(RadioBand.Band5GHz, 14.0, false)]
    [InlineData(RadioBand.Band5GHz, 16.0, true)]
    [InlineData(RadioBand.Band6GHz, 9.0, false)]
    [InlineData(RadioBand.Band6GHz, 11.0, true)]
    public void The_bar_is_per_band(RadioBand band, double retries, bool raised)
    {
        var issue = _rule.Evaluate(Context(Ap(band, retries, 4)));

        (issue != null).Should().Be(raised);
    }

    [Fact]
    public void Two_clients_are_not_a_radio_problem()
    {
        _rule.Evaluate(Context(Ap(RadioBand.Band5GHz, 30, 2))).Should().BeNull();
    }

    [Theory]
    [InlineData(RadioBand.Band2_4GHz, 41.0)]
    [InlineData(RadioBand.Band5GHz, 30.0)]
    [InlineData(RadioBand.Band6GHz, 20.0)]
    public void The_bands_critical_rate_makes_it_critical(RadioBand band, double retries)
    {
        var issue = _rule.Evaluate(Context(Ap(band, retries, 3)))!;

        issue.Severity.Should().Be(HealthIssueSeverity.Critical);
        issue.ScoreImpact.Should().Be(-12);
    }

    [Fact]
    public void A_console_radio_says_nothing_about_the_cause()
    {
        var issue = _rule.Evaluate(Context(Ap(RadioBand.Band5GHz, 20, 4)))!;

        issue.Severity.Should().Be(HealthIssueSeverity.Warning);
        issue.Description.Should().EndWith("hidden node problems.");
        issue.AffectedEntity.Should().Be("AP-1 (5 GHz 20.0%, 4 clients, threshold 15%)");
    }

    [Fact]
    public void Measured_contention_names_the_cause()
    {
        var issue = _rule.Evaluate(Context(Ap(RadioBand.Band5GHz, 20, 4, busy: 60, interference: 35)))!;

        issue.Description.Should().EndWith(" AP-1 5 GHz: other transmitters hold 35% of the airtime, so these retries are contention; a channel move is the fix.");
    }

    [Fact]
    public void Measured_quiet_air_names_the_other_cause()
    {
        var issue = _rule.Evaluate(Context(Ap(RadioBand.Band5GHz, 20, 4, busy: 12, interference: 3)))!;

        issue.Description.Should().EndWith(" AP-1 5 GHz: the air is quiet (12% busy), so these retries are weak signal or a hidden node, not contention.");
    }
}
