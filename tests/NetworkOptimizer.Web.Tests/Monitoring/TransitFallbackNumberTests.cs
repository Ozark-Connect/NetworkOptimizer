using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class TransitFallbackNumberTests
{
    private static TransitAsnCandidate Hop(string address, string? savedName = null) => new()
    {
        AsnNumber = 64500,
        AsnName = "ExampleNet",
        Method = DiscoveryMethod.DirectRouter,
        HopAddress = address,
        Label = savedName,
        PreservedFromExisting = savedName != null,
    };

    private static MonitoringTarget Row(int id, string address, string name) => new()
    {
        Id = id,
        TargetId = $"transit-{address}",
        Address = address,
        Name = name,
    };

    [Fact]
    public void New_hops_number_from_one_with_nothing_saved()
    {
        var a = Hop("192.0.2.1");
        var b = Hop("192.0.2.2");

        UpstreamTracerService.AssignFallbackNumbers([a, b], []);

        a.Label.Should().Be("ExampleNet 1");
        b.Label.Should().Be("ExampleNet 2");
    }

    [Fact]
    public void New_hop_skips_a_name_a_saved_hop_off_the_path_holds()
    {
        var saved = new[] { Row(13, "198.51.100.9", "ExampleNet 1") };
        var newHop = Hop("192.0.2.1");

        UpstreamTracerService.AssignFallbackNumbers([newHop], saved);

        newHop.Label.Should().Be("ExampleNet 2");
    }

    [Fact]
    public void Saved_hop_keeps_its_name_and_a_new_hop_numbers_past_it()
    {
        var saved = new[] { Row(13, "192.0.2.1", "ExampleNet 1") };
        var kept = Hop("192.0.2.1", "ExampleNet 1");
        var newHop = Hop("192.0.2.2");

        UpstreamTracerService.AssignFallbackNumbers([newHop, kept], saved);

        kept.Label.Should().Be("ExampleNet 1");
        newHop.Label.Should().Be("ExampleNet 2");
    }

    [Fact]
    public void Newer_duplicate_is_renumbered_and_the_older_keeps_the_name()
    {
        var saved = new[]
        {
            Row(13, "198.51.100.9", "ExampleNet 1"),
            Row(49, "192.0.2.1", "ExampleNet 1"),
        };
        var newer = Hop("192.0.2.1", "ExampleNet 1");

        UpstreamTracerService.AssignFallbackNumbers([newer], saved);

        newer.Label.Should().Be("ExampleNet 2");
    }

    [Fact]
    public void Older_duplicate_on_the_path_keeps_its_name()
    {
        var saved = new[]
        {
            Row(13, "198.51.100.9", "ExampleNet 1"),
            Row(49, "192.0.2.1", "ExampleNet 1"),
        };
        var older = Hop("198.51.100.9", "ExampleNet 1");

        UpstreamTracerService.AssignFallbackNumbers([older], saved);

        older.Label.Should().Be("ExampleNet 1");
    }

    [Fact]
    public void A_name_the_user_chose_is_never_renumbered()
    {
        var saved = new[]
        {
            Row(13, "198.51.100.9", "Core router"),
            Row(49, "192.0.2.1", "Core router"),
        };
        var hop = Hop("192.0.2.1", "Core router");

        UpstreamTracerService.AssignFallbackNumbers([hop], saved);

        hop.Label.Should().Be("Core router");
    }
}
