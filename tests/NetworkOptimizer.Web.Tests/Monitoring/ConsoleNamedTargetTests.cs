using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// A device's target is named as the UniFi Console names the device and follows a rename there.
/// Targets a user adds keep the name the user gave them.
/// </summary>
public class ConsoleNamedTargetTests
{
    private static MonitoringTarget Fabric(string name, bool autoDiscovered = true, string? mac = "aa:bb:cc:dd:ee:ff") => new()
    {
        TargetId = "fabric-aa:bb:cc:dd:ee:ff",
        Name = name,
        Address = "192.0.2.10",
        TargetType = MonitoringTargetType.Fabric,
        DeviceMac = mac,
        AutoDiscovered = autoDiscovered,
        Enabled = true,
    };

    [Fact]
    public void A_console_rename_reaches_the_device_target()
    {
        var target = Fabric("Old Switch");

        MonitoringCollectionAgent.ApplyConsoleName(target, "Office Switch").Should().BeTrue();

        target.Name.Should().Be("Office Switch");
    }

    [Fact]
    public void An_unchanged_name_is_no_change()
    {
        var target = Fabric("Office Switch");

        MonitoringCollectionAgent.ApplyConsoleName(target, "Office Switch").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_console_with_no_name_for_the_device_leaves_the_name(string? consoleName)
    {
        var target = Fabric("Office Switch");

        MonitoringCollectionAgent.ApplyConsoleName(target, consoleName).Should().BeFalse();

        target.Name.Should().Be("Office Switch");
    }

    [Fact]
    public void A_target_the_user_added_keeps_its_name()
    {
        var target = Fabric("My Switch", autoDiscovered: false);

        MonitoringCollectionAgent.ApplyConsoleName(target, "Office Switch").Should().BeFalse();

        target.Name.Should().Be("My Switch");
        MonitoringTargetNaming.IsConsoleNamed(target).Should().BeFalse("its pencil stays");
    }

    [Fact]
    public void Only_device_targets_are_console_named()
    {
        MonitoringTargetNaming.IsConsoleNamed(Fabric("Switch")).Should().BeTrue();
        MonitoringTargetNaming.IsConsoleNamed(Fabric("Switch", mac: null)).Should().BeFalse();

        var custom = Fabric("Cloudflare");
        custom.TargetType = MonitoringTargetType.Custom;
        MonitoringTargetNaming.IsConsoleNamed(custom).Should().BeFalse();
    }
}
