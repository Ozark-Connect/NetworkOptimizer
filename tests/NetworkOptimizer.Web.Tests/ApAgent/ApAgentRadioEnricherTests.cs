using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The block center reaches the console-sourced snapshot only for the radio it was measured on,
/// and only while both sides agree on the primary.
/// </summary>
public class ApAgentRadioEnricherTests
{
    private const string ApMac = "aa:bb:cc:dd:ee:01";

    private static ApAgentRadioAirtime AgentRadio(string name, string band, int channel, int width, int? centerMhz) =>
        new(name, band, channel, width, centerMhz, -96,
            new Dictionary<string, long>(), new Dictionary<string, long>(), 30, DateTime.UtcNow);

    private static AccessPointSnapshot Ap(params RadioSnapshot[] radios) =>
        new() { Mac = ApMac, Name = "AP-1", Radios = radios.ToList() };

    private static RadioSnapshot Radio(string name, RadioBand band, int channel, int width) =>
        new() { Name = name, Band = band, Channel = channel, ChannelWidth = width };

    [Fact]
    public void Center_lands_on_the_radio_with_the_same_name_as_a_channel_number()
    {
        var ap = Ap(Radio("wifi2", RadioBand.Band6GHz, 165, 320), Radio("wifi1", RadioBand.Band5GHz, 100, 160));
        var agent = new[]
        {
            AgentRadio("wifi2", "6", 165, 320, 6745),
            AgentRadio("wifi1", "5", 100, 160, 5570),
        };

        ApAgentRadioEnricher.Apply(new[] { ap }, _ => agent);

        ap.Radios[0].CenterChannel.Should().Be(159, "6745 MHz is 6 GHz channel 159");
        ap.Radios[1].CenterChannel.Should().Be(114, "5570 MHz is 5 GHz channel 114");
    }

    [Fact]
    public void Falls_back_to_the_band_when_the_names_differ_and_the_band_holds_one_radio()
    {
        var ap = Ap(Radio("ra0", RadioBand.Band6GHz, 69, 320));

        ApAgentRadioEnricher.Apply(new[] { ap }, _ => new[] { AgentRadio("wifi2", "6", 69, 320, 6265) });

        ap.Radios[0].CenterChannel.Should().Be(63);
    }

    [Fact]
    public void A_primary_the_agent_disagrees_on_carries_no_center()
    {
        // The console has already moved the radio; the agent's last pass is from the old channel.
        var ap = Ap(Radio("wifi2", RadioBand.Band6GHz, 101, 320));

        ApAgentRadioEnricher.Apply(new[] { ap }, _ => new[] { AgentRadio("wifi2", "6", 69, 320, 6265) });

        ap.Radios[0].CenterChannel.Should().BeNull();
    }

    [Fact]
    public void Absent_agent_data_leaves_the_snapshot_untouched()
    {
        var ap = Ap(Radio("wifi2", RadioBand.Band6GHz, 69, 320));

        ApAgentRadioEnricher.Apply(new[] { ap }, _ => Array.Empty<ApAgentRadioAirtime>());
        ap.Radios[0].CenterChannel.Should().BeNull("no agent covers this AP");

        ApAgentRadioEnricher.Apply(new[] { ap }, _ => new[] { AgentRadio("wifi2", "6", 69, 320, null) });
        ap.Radios[0].CenterChannel.Should().BeNull("the agent could not read iw");
    }

    [Fact]
    public void Traces_say_what_happened_to_each_wide_radio_on_a_covered_ap()
    {
        var covered = Ap(Radio("wifi2", RadioBand.Band6GHz, 69, 320), Radio("wifi1", RadioBand.Band5GHz, 100, 160));
        var uncovered = new AccessPointSnapshot
        {
            Mac = "aa:bb:cc:dd:ee:02", Name = "AP-2",
            Radios = new() { Radio("wifi2", RadioBand.Band6GHz, 5, 320) }
        };
        var agent = new[]
        {
            AgentRadio("wifi2", "6", 69, 320, 6265),
            AgentRadio("wifi1", "5", 36, 160, 5250),
        };

        var traces = ApAgentRadioEnricher.Apply(new[] { covered, uncovered },
            mac => mac == ApMac ? agent : Array.Empty<ApAgentRadioAirtime>());

        traces.Should().HaveCount(2, "the uncovered AP contributes nothing");
        traces[0].ToString().Should().Be("wifi2 6 GHz ch 69/320 center 63 -> block 33-93 (measured, 6265 MHz)");
        traces[1].ToString().Should().Be("wifi1 5 GHz ch 100/160 no center -> block 100-128 (agent still on ch 36, waiting for it to agree)");
    }

    [Fact]
    public void Fresh_counters_land_as_measured_airtime_and_floor_and_stale_ones_do_not()
    {
        var ap = Ap(Radio("wifi2", RadioBand.Band6GHz, 69, 160), Radio("wifi0", RadioBand.Band2_4GHz, 11, 20));
        var counters = new Dictionary<string, long> { ["cu_total"] = 44, ["cu_self_tx"] = 20, ["cu_self_rx"] = 11, ["cu_interf"] = 9 };
        var fresh = new ApAgentRadioAirtime("wifi2", "6", 69, 160, 6345, -91, counters, new Dictionary<string, long>(), 30, DateTime.UtcNow);
        var stale = new ApAgentRadioAirtime("wifi0", "2.4", 11, 20, null, -88, counters, new Dictionary<string, long>(), 30, DateTime.UtcNow.AddMinutes(-5));

        var traces = ApAgentRadioEnricher.Apply(new[] { ap }, _ => new[] { fresh, stale }, (_, radio) => radio == "wifi2" ? -90 : null);

        var wide = ap.Radios[0];
        wide.MeasuredUtilization.Should().Be(44);
        wide.MeasuredSelfAirtime.Should().Be(31);
        wide.MeasuredInterference.Should().Be(9);
        wide.MeasuredNoiseFloor.Should().Be(-91);
        wide.MeasuredNoiseFloorHour.Should().Be(-90);
        wide.CenterChannel.Should().Be(79);

        var narrow = ap.Radios[1];
        narrow.MeasuredUtilization.Should().BeNull("a five-minute-old reading is not copied");
        narrow.CenterChannel.Should().BeNull();

        traces.Should().HaveCount(2);
        traces[0].ToString().Should().Be("wifi2 6 GHz ch 69/160 center 79 -> block 65-93 (measured, 6345 MHz); airtime 44% (self 31%, other 9%), floor -91 dBm");
        traces[1].ToString().Should().Be("wifi0 2.4 GHz ch 11/20 no center -> block 9-13 (narrow radio, no block to resolve)");
    }

    [Fact]
    public void Twenty_megahertz_and_2_4_GHz_radios_are_skipped()
    {
        var ap = Ap(Radio("wifi0", RadioBand.Band2_4GHz, 11, 40), Radio("wifi1", RadioBand.Band5GHz, 36, 20));
        var agent = new[]
        {
            AgentRadio("wifi0", "2.4", 11, 40, 2472),
            AgentRadio("wifi1", "5", 36, 20, 5180),
        };

        ApAgentRadioEnricher.Apply(new[] { ap }, _ => agent);

        ap.Radios.Should().OnlyContain(r => r.CenterChannel == null);
    }
}
