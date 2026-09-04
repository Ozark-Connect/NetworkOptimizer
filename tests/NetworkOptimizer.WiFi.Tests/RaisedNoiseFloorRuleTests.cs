using FluentAssertions;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class RaisedNoiseFloorRuleTests
{
    private readonly RaisedNoiseFloorRule _rule = new();

    private static AccessPointSnapshot Ap(string mac, string name, int? hourFloor, int? latest = null) => new()
    {
        Mac = mac,
        Name = name,
        Radios = new()
        {
            new RadioSnapshot
            {
                Band = RadioBand.Band5GHz, Channel = 36, ChannelWidth = 80,
                MeasuredNoiseFloorHour = hourFloor, MeasuredNoiseFloor = latest ?? hourFloor
            }
        }
    };

    private static WiFiOptimizerContext Context(params AccessPointSnapshot[] aps) => new()
    {
        AccessPoints = aps.ToList(), Clients = [], Wlans = [], Networks = [], LegacyClients = [], SteerableClients = []
    };

    [Fact]
    public void One_radio_well_above_its_siblings_is_reported_with_the_reference()
    {
        var issue = _rule.EvaluateAll(Context(
            Ap("aa:bb:cc:dd:ee:01", "AP-1", -92),
            Ap("aa:bb:cc:dd:ee:02", "AP-2", -92),
            Ap("aa:bb:cc:dd:ee:03", "AP-3", -92),
            Ap("aa:bb:cc:dd:ee:04", "AP-Loud", -81))).Should().ContainSingle().Subject;

        issue.Title.Should().Be("Raised Noise Floor on 5 GHz: AP-Loud");
        issue.Description.Should().Be("AP-Loud's 5 GHz radio has measured a noise floor of -81 dBm over the last hour, 11 dB above the other 5 GHz radios on this site (-92 dBm). Something near it is transmitting on or next to its channel.");
        issue.Key.Should().Be("WIFI-NOISE-FLOOR-001|aa:bb:cc:dd:ee:04/na");
        issue.Class.Should().Be(HealthIssueClass.Measured);
    }

    [Fact]
    public void A_whole_band_that_is_high_is_not_reported_against_itself()
    {
        _rule.EvaluateAll(Context(
            Ap("aa:bb:cc:dd:ee:01", "AP-1", -80),
            Ap("aa:bb:cc:dd:ee:02", "AP-2", -80),
            Ap("aa:bb:cc:dd:ee:03", "AP-3", -80))).Should().BeEmpty();
    }

    [Fact]
    public void A_lone_covered_radio_has_nothing_to_compare_against()
    {
        _rule.EvaluateAll(Context(
            Ap("aa:bb:cc:dd:ee:01", "AP-1", -70),
            Ap("aa:bb:cc:dd:ee:02", "AP-2", null))).Should().BeEmpty();
    }

    [Fact]
    public void A_raised_latest_sample_without_a_raised_hour_is_not_reported()
    {
        _rule.EvaluateAll(Context(
            Ap("aa:bb:cc:dd:ee:01", "AP-1", -92, latest: -70),
            Ap("aa:bb:cc:dd:ee:02", "AP-2", -92))).Should().BeEmpty();
    }

    [Fact]
    public void Eight_dB_above_a_very_quiet_reference_but_still_quiet_is_not_reported()
    {
        _rule.EvaluateAll(Context(
            Ap("aa:bb:cc:dd:ee:01", "AP-1", -100),
            Ap("aa:bb:cc:dd:ee:02", "AP-2", -100),
            Ap("aa:bb:cc:dd:ee:03", "AP-3", -91))).Should().BeEmpty("-91 is still a quiet floor");
    }
}
