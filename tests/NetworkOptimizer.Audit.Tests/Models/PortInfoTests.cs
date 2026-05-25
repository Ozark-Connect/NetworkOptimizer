using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Models;

public class PortInfoTests
{
    private static PortInfo CreatePortWithOpMode(string? opMode)
    {
        return new PortInfo
        {
            PortIndex = 1,
            OpMode = opMode,
            Switch = new SwitchInfo { Name = "Test", Capabilities = new SwitchCapabilities() }
        };
    }

    [Fact]
    public void IsMirrorDestination_OpModeMirror_ReturnsTrue()
    {
        var port = CreatePortWithOpMode("mirror");
        port.IsMirrorDestination.Should().BeTrue();
    }

    [Fact]
    public void IsMirrorDestination_OpModeSwitch_ReturnsFalse()
    {
        var port = CreatePortWithOpMode("switch");
        port.IsMirrorDestination.Should().BeFalse();
    }

    [Fact]
    public void IsMirrorDestination_OpModeNull_ReturnsFalse()
    {
        var port = CreatePortWithOpMode(null);
        port.IsMirrorDestination.Should().BeFalse();
    }

    [Fact]
    public void IsMirrorDestination_OpModeMixedCase_ReturnsTrue()
    {
        // String comparison is OrdinalIgnoreCase; UniFi has historically used different
        // casing across firmware versions for similar enum-like fields.
        var port = CreatePortWithOpMode("Mirror");
        port.IsMirrorDestination.Should().BeTrue();
    }
}
