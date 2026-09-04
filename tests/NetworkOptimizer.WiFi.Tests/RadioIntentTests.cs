using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class RadioIntentTests
{
    private static object Json(string literal) => JsonSerializer.Deserialize<JsonElement>(literal);

    [Fact]
    public void A_numeric_radio_table_channel_is_fixed_and_auto_is_not()
    {
        RadioIntent.IsFixedChannel(Json("36")).Should().BeTrue();
        RadioIntent.IsFixedChannel(Json("\"36\"")).Should().BeTrue();
        RadioIntent.IsFixedChannel(Json("\"auto\"")).Should().BeFalse();
        RadioIntent.IsFixedChannel("AUTO").Should().BeFalse();
        RadioIntent.IsFixedChannel(null).Should().BeFalse();
        RadioIntent.IsFixedChannel(101).Should().BeTrue();
    }

    private static AccessPointSnapshot Ap(string mac, RadioBand band, int width) => new()
    {
        Mac = mac,
        Name = mac,
        Status = new(DeviceStatusKind.Online, "Online"),
        Radios = new() { new RadioSnapshot { Band = band, Channel = 36, ChannelWidth = width } }
    };

    [Fact]
    public void The_radio_that_differs_from_the_bands_usual_width_is_the_override()
    {
        var aps = new List<AccessPointSnapshot>
        {
            Ap("aa:bb:cc:dd:ee:01", RadioBand.Band5GHz, 80),
            Ap("aa:bb:cc:dd:ee:02", RadioBand.Band5GHz, 80),
            Ap("aa:bb:cc:dd:ee:03", RadioBand.Band5GHz, 160),
        };

        RadioIntent.ComputeWidthOverrides(aps);

        aps[0].Radios[0].WidthIsOverride.Should().BeFalse();
        aps[1].Radios[0].WidthIsOverride.Should().BeFalse();
        aps[2].Radios[0].WidthIsOverride.Should().BeTrue();
    }

    [Fact]
    public void A_tie_or_a_lone_radio_marks_nothing()
    {
        var tie = new List<AccessPointSnapshot>
        {
            Ap("aa:bb:cc:dd:ee:01", RadioBand.Band5GHz, 80),
            Ap("aa:bb:cc:dd:ee:02", RadioBand.Band5GHz, 160),
        };
        RadioIntent.ComputeWidthOverrides(tie);
        tie.Should().OnlyContain(ap => !ap.Radios[0].WidthIsOverride);

        var lone = new List<AccessPointSnapshot> { Ap("aa:bb:cc:dd:ee:01", RadioBand.Band6GHz, 320) };
        RadioIntent.ComputeWidthOverrides(lone);
        lone[0].Radios[0].WidthIsOverride.Should().BeFalse();
    }

    [Fact]
    public void Marking_deliberate_drops_to_info_and_appends_the_hint()
    {
        var issue = new HealthIssue { Severity = HealthIssueSeverity.Warning, Description = "Wide." };

        RadioIntent.MarkDeliberate(issue, RadioIntent.PowerHint);

        issue.Severity.Should().Be(HealthIssueSeverity.Info);
        issue.Description.Should().Be("Wide. " + RadioIntent.PowerHint);
    }
}
