using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class High2GHzConcentrationRuleTests
{
    private readonly High2GHzConcentrationRule _rule = new();

    private static WirelessClientSnapshot Client(string mac, string name, RadioBand band) =>
        new() { Mac = mac, Name = name, Band = band };

    private static WiFiOptimizerContext Context(List<WirelessClientSnapshot> clients, List<WirelessClientSnapshot> steerable,
        List<WlanConfiguration>? wlans = null, List<NetworkInfo>? networks = null) => new()
    {
        AccessPoints = [], Clients = clients, Wlans = wlans ?? [], Networks = networks ?? [], LegacyClients = [], SteerableClients = steerable
    };

    private static List<WirelessClientSnapshot> OneSteerableOnHome(out WirelessClientSnapshot reader)
    {
        reader = Client("aa:bb:cc:dd:ee:01", "Reader", RadioBand.Band2_4GHz);
        reader.Essid = "Home";
        return [reader, Client("aa:bb:cc:dd:ee:02", "Plug", RadioBand.Band2_4GHz), Client("aa:bb:cc:dd:ee:03", "Laptop", RadioBand.Band5GHz)];
    }

    private static List<NetworkInfo> IoTNetwork() =>
        [new NetworkInfo { Id = "net-iot", Name = "IoT", VlanId = 30, Purpose = NetworkPurpose.IoT }];

    [Fact]
    public void Nothing_set_up_offers_all_three()
    {
        var clients = OneSteerableOnHome(out var reader);
        var wlans = new List<WlanConfiguration> { new() { Id = "w1", Name = "Home", Enabled = true } };

        var issue = _rule.Evaluate(Context(clients, [reader], wlans))!;

        issue.Recommendation.Should().Be("Consider: (1) enabling band steering, (2) reducing 2.4 GHz TX power to " +
            "encourage clients to use 5 GHz, or (3) creating a separate IoT SSID for 2.4 GHz-only devices.");
    }

    [Fact]
    public void Band_steering_on_and_an_IoT_SSID_leave_only_TX_power()
    {
        var clients = OneSteerableOnHome(out var reader);
        var wlans = new List<WlanConfiguration>
        {
            new() { Id = "w1", Name = "Home", Enabled = true, BandSteeringEnabled = true },
            new() { Id = "w2", Name = "Things", Enabled = true, NetworkId = "net-iot" }
        };

        var issue = _rule.Evaluate(Context(clients, [reader], wlans, IoTNetwork()))!;

        issue.Recommendation.Should().Be("Band steering is already on for Home, so this client stays on 2.4 GHz on its own. " +
            "Consider reducing 2.4 GHz TX power to encourage clients to use 5 GHz.");
    }

    [Fact]
    public void Steering_off_on_the_clients_SSID_is_still_offered()
    {
        var clients = OneSteerableOnHome(out var reader);
        var wlans = new List<WlanConfiguration>
        {
            new() { Id = "w1", Name = "Home", Enabled = true },
            new() { Id = "w3", Name = "Office", Enabled = true, BandSteeringEnabled = true },
            new() { Id = "w2", Name = "Things", Enabled = true, NetworkId = "net-iot" }
        };

        var issue = _rule.Evaluate(Context(clients, [reader], wlans, IoTNetwork()))!;

        issue.Recommendation.Should().Be("Consider: (1) enabling band steering, or (2) reducing 2.4 GHz TX power to encourage clients to use 5 GHz.");
    }

    [Fact]
    public void One_steerable_client_links_to_that_client()
    {
        var phone = Client("aa:bb:cc:dd:ee:01", "Phone", RadioBand.Band2_4GHz);
        var clients = new List<WirelessClientSnapshot> { phone, Client("aa:bb:cc:dd:ee:02", "Plug", RadioBand.Band2_4GHz), Client("aa:bb:cc:dd:ee:03", "Laptop", RadioBand.Band5GHz) };

        var issue = _rule.Evaluate(Context(clients, [phone]))!;

        issue.AffectedLinks.Should().ContainSingle().Which.Should().Be(
            new IssueLink("Phone", "./wifi-optimizer?tab=client&client=aa%3Abb%3Acc%3Add%3Aee%3A01"));
    }

    [Fact]
    public void Several_steerable_clients_link_to_the_band()
    {
        var a = Client("aa:bb:cc:dd:ee:01", "Phone", RadioBand.Band2_4GHz);
        var b = Client("aa:bb:cc:dd:ee:02", "Tablet", RadioBand.Band2_4GHz);
        var clients = new List<WirelessClientSnapshot> { a, b, Client("aa:bb:cc:dd:ee:03", "Laptop", RadioBand.Band5GHz) };

        var issue = _rule.Evaluate(Context(clients, [a, b]))!;

        issue.AffectedLinks.Should().ContainSingle().Which.Url.Should().Be("./wifi-optimizer?tab=client&band=2.4");
        issue.Description.Should().Contain("but 2 of them");
    }

    [Fact]
    public void A_5_GHz_client_that_could_use_6_GHz_is_not_counted()
    {
        var phone = Client("aa:bb:cc:dd:ee:01", "Phone", RadioBand.Band2_4GHz);
        var laptop = Client("aa:bb:cc:dd:ee:03", "Laptop", RadioBand.Band5GHz);
        var clients = new List<WirelessClientSnapshot> { phone, Client("aa:bb:cc:dd:ee:02", "Plug", RadioBand.Band2_4GHz), laptop };

        var issue = _rule.Evaluate(Context(clients, [phone, laptop]))!;

        issue.Description.Should().Contain("but 1 of them");
    }
}
