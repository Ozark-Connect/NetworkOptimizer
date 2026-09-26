using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Monitoring.HealthChecks;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class HealthCheckTargetTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    private static DiscoveredDevice Gateway(string ip = "192.0.2.1") => new()
    {
        Mac = "aa:bb:cc:dd:ee:ff",
        Name = "Gateway1",
        IpAddress = ip,
        Type = DeviceType.Gateway,
        HardwareType = DeviceType.Gateway,
    };

    private static HealthCheckDefinition Check() => new() { DeviceMac = "aa:bb:cc:dd:ee:ff", Name = "Check1" };

    [Fact]
    public void FromCheck_is_null_before_anything_was_recorded()
    {
        HealthCheckTarget.FromCheck(Check()).Should().BeNull();
    }

    [Fact]
    public void Remember_then_FromCheck_round_trips_the_device()
    {
        var check = Check();
        HealthCheckTarget.Remember(check, Gateway(), Now).Should().BeTrue();

        var target = HealthCheckTarget.FromCheck(check)!;
        target.Name.Should().Be("Gateway1");
        target.Host.Should().Be("192.0.2.1");
        target.EffectiveType.Should().Be(DeviceType.Gateway);
        target.FromLastKnown.Should().BeTrue();
        check.LastKnownAt.Should().Be(Now);
    }

    [Fact]
    public void Remember_reports_no_change_for_the_same_listing()
    {
        var check = Check();
        HealthCheckTarget.Remember(check, Gateway(), Now);
        HealthCheckTarget.Remember(check, Gateway(), Now.AddMinutes(5)).Should().BeFalse();
        check.LastKnownAt.Should().Be(Now);
    }

    [Fact]
    public void Remember_takes_a_new_address()
    {
        var check = Check();
        HealthCheckTarget.Remember(check, Gateway("192.0.2.1"), Now);
        HealthCheckTarget.Remember(check, Gateway("192.0.2.9"), Now.AddMinutes(5)).Should().BeTrue();
        check.LastKnownHost.Should().Be("192.0.2.9");
    }

    [Fact]
    public void An_empty_address_never_overwrites_a_known_one()
    {
        var check = Check();
        HealthCheckTarget.Remember(check, Gateway("192.0.2.1"), Now);
        HealthCheckTarget.Remember(check, Gateway(""), Now.AddMinutes(5));
        check.LastKnownHost.Should().Be("192.0.2.1");

        HealthCheckTarget.FromDevice(Gateway(""), check).Host.Should().Be("192.0.2.1");
    }

    [Fact]
    public void Gateway_hardware_in_an_access_point_role_keeps_the_gateway_path()
    {
        var check = Check();
        var meshed = Gateway();
        meshed.Type = DeviceType.AccessPoint;
        HealthCheckTarget.Remember(check, meshed, Now);

        var target = HealthCheckTarget.FromCheck(check)!;
        target.Type.Should().Be(DeviceType.AccessPoint);
        target.EffectiveType.Should().Be(DeviceType.Gateway);
    }
}
