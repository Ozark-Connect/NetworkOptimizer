using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

public class ApAgentWidthDemandTests
{
    private const string Ap1 = "aa:bb:cc:dd:ee:01";
    private const string Ap2 = "aa:bb:cc:dd:ee:02";

    private static AccessPointSnapshot Ap(string mac) => new()
    {
        Mac = mac,
        Radios = new() { new RadioSnapshot { Band = RadioBand.Band5GHz, Channel = 36, ChannelWidth = 160 } }
    };

    private static WirelessClientSnapshot Client(string mac, string apMac, int? negotiated = null, string? lockedTo = null, bool online = true) => new()
    {
        Mac = mac, ApMac = apMac, Band = RadioBand.Band5GHz, Signal = -60, IsOnline = online,
        NegotiatedWidth = negotiated, FixedApEnabled = lockedTo != null, FixedApMac = lockedTo
    };

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> History(params (string Mac, string Band, int Width)[] rows)
        => rows.GroupBy(r => r.Mac, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key,
                g => (IReadOnlyDictionary<string, int>)g.ToDictionary(r => r.Band, r => r.Width, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_client_that_negotiated_wide_elsewhere_this_week_counts_because_it_can_roam_here()
    {
        var ap1 = Ap(Ap1);
        var clients = new List<WirelessClientSnapshot> { Client("cc:00:00:00:00:01", Ap1, 80), Client("cc:00:00:00:00:02", Ap2, 160) };
        var history = History(("cc:00:00:00:00:02", "5ghz", 160), ("cc:00:00:00:00:03", "5ghz", 40));

        ApAgentWidthDemand.Apply([ap1, Ap(Ap2)], clients, history).Should().Be(2);

        ap1.Radios[0].MeasuredMaxNegotiatedWidth.Should().Be(160);
    }

    [Fact]
    public void A_client_locked_to_another_ap_is_not_demand_here_but_one_locked_to_this_ap_is()
    {
        var ap1 = Ap(Ap1);
        var ap2 = Ap(Ap2);
        // Both locked devices are offline, so only the console's roster knows their locks.
        var clients = new List<WirelessClientSnapshot> { Client("cc:00:00:00:00:01", Ap1, 40) };
        var history = History(("cc:00:00:00:00:02", "5ghz", 160), ("cc:00:00:00:00:03", "5ghz", 80));
        var locks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CC:00:00:00:00:02"] = Ap2,
            ["cc:00:00:00:00:03"] = Ap1,
        };

        ApAgentWidthDemand.Apply([ap1, ap2], clients, history, locks);

        ap1.Radios[0].MeasuredMaxNegotiatedWidth.Should().Be(80, "the 160 MHz client is locked to the other AP");
        ap2.Radios[0].MeasuredMaxNegotiatedWidth.Should().Be(160);
    }

    [Fact]
    public void Another_bands_history_does_not_count_and_no_evidence_leaves_the_radio_null()
    {
        var ap1 = Ap(Ap1);
        var history = History(("cc:00:00:00:00:02", "6ghz", 320));

        ApAgentWidthDemand.Apply([ap1], [], history).Should().Be(0);

        ap1.Radios[0].MeasuredMaxNegotiatedWidth.Should().BeNull();
    }
}
