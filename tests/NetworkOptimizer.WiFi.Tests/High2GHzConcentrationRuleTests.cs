using FluentAssertions;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class High2GHzConcentrationRuleTests
{
    private readonly High2GHzConcentrationRule _rule = new();

    private static WirelessClientSnapshot Client(string mac, string name, RadioBand band) =>
        new() { Mac = mac, Name = name, Band = band };

    private static WiFiOptimizerContext Context(List<WirelessClientSnapshot> clients, List<WirelessClientSnapshot> steerable) => new()
    {
        AccessPoints = [], Clients = clients, Wlans = [], Networks = [], LegacyClients = [], SteerableClients = steerable
    };

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
