using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// An access point whose agent cannot steer (no hostapd on ubus) is remembered per site from its
/// health, so Client Performance can withhold Roam for a client on it.
/// </summary>
public class ApAgentSteeringAvailabilityTests
{
    private static ApAgentTargetDirectory Directory() =>
        new(null!, null!, NullLogger<ApAgentTargetDirectory>.Instance);

    [Fact]
    public void AnApThatCannotSteer_is_listed_by_lower_case_mac()
    {
        var directory = Directory();

        directory.RecordSteering("main", "AA:BB:CC:DD:EE:01", canSteer: false);

        directory.ApsWithoutSteering("main").Should().Equal("aa:bb:cc:dd:ee:01");
    }

    [Fact]
    public void AnApThatRegainsSteering_drops_off_the_list()
    {
        var directory = Directory();
        directory.RecordSteering("main", "aa:bb:cc:dd:ee:01", canSteer: false);

        directory.RecordSteering("main", "aa:bb:cc:dd:ee:01", canSteer: true);

        directory.ApsWithoutSteering("main").Should().BeEmpty();
    }

    [Fact]
    public void TheListIsPerSite()
    {
        var directory = Directory();

        directory.RecordSteering("main", "aa:bb:cc:dd:ee:01", canSteer: false);

        directory.ApsWithoutSteering("other-site").Should().BeEmpty();
    }

    [Fact]
    public void AnApThatCanSteer_is_never_listed()
    {
        var directory = Directory();

        directory.RecordSteering("main", "aa:bb:cc:dd:ee:02", canSteer: true);

        directory.ApsWithoutSteering("main").Should().BeEmpty();
    }
}
