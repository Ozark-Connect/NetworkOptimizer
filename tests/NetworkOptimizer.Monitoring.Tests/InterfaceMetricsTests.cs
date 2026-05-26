using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class InterfaceMetricsTests
{
    #region Default Values Tests

    [Fact]
    public void InterfaceMetrics_DefaultValues_AreCorrect()
    {
        // Act
        var metrics = new InterfaceMetrics();

        // Assert
        metrics.Index.Should().Be(0);
        metrics.Description.Should().BeEmpty();
        metrics.Name.Should().BeEmpty();
        metrics.Type.Should().Be(0);
        metrics.Speed.Should().Be(0);
        metrics.HighSpeed.Should().Be(0);
        metrics.PhysicalAddress.Should().BeEmpty();
        metrics.AdminStatus.Should().Be(0);
        metrics.OperStatus.Should().Be(0);
        metrics.LastChange.Should().Be(0);
        metrics.InOctets.Should().Be(0);
        metrics.InUcastPkts.Should().Be(0);
        metrics.InMulticastPkts.Should().Be(0);
        metrics.InBroadcastPkts.Should().Be(0);
        metrics.InDiscards.Should().Be(0);
        metrics.InErrors.Should().Be(0);
        metrics.InUnknownProtos.Should().Be(0);
        metrics.OutOctets.Should().Be(0);
        metrics.OutUcastPkts.Should().Be(0);
        metrics.OutMulticastPkts.Should().Be(0);
        metrics.OutBroadcastPkts.Should().Be(0);
        metrics.OutDiscards.Should().Be(0);
        metrics.OutErrors.Should().Be(0);
        metrics.Mtu.Should().Be(0);
        metrics.DeviceIp.Should().BeEmpty();
        metrics.DeviceHostname.Should().BeEmpty();
    }

    [Fact]
    public void InterfaceMetrics_Timestamp_DefaultsToUtcNow()
    {
        // Act
        var metrics = new InterfaceMetrics();

        // Assert
        metrics.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region IsUp Tests

    [Theory]
    [InlineData(1, true)]   // Up
    [InlineData(2, false)]  // Down
    [InlineData(3, false)]  // Testing
    [InlineData(4, false)]  // Unknown
    [InlineData(5, false)]  // Dormant
    [InlineData(6, false)]  // NotPresent
    [InlineData(7, false)]  // LowerLayerDown
    [InlineData(0, false)]  // Default
    public void IsUp_ReturnsCorrectValue(int operStatus, bool expected)
    {
        // Arrange
        var metrics = new InterfaceMetrics { OperStatus = operStatus };

        // Act & Assert
        metrics.IsUp.Should().Be(expected);
    }

    #endregion

    #region IsEnabled Tests

    [Theory]
    [InlineData(1, true)]   // Up
    [InlineData(2, false)]  // Down
    [InlineData(3, false)]  // Testing
    [InlineData(0, false)]  // Default
    public void IsEnabled_ReturnsCorrectValue(int adminStatus, bool expected)
    {
        // Arrange
        var metrics = new InterfaceMetrics { AdminStatus = adminStatus };

        // Act & Assert
        metrics.IsEnabled.Should().Be(expected);
    }

    #endregion

    #region SpeedMbps Tests

    [Fact]
    public void SpeedMbps_WithHighSpeed_ReturnsHighSpeed()
    {
        // Arrange - HighSpeed is in Mbps already
        var metrics = new InterfaceMetrics
        {
            Speed = 100_000_000,  // 100 Mbps in bits/sec
            HighSpeed = 10000    // 10 Gbps in Mbps
        };

        // Act & Assert
        metrics.SpeedMbps.Should().Be(10000);
    }

    [Fact]
    public void SpeedMbps_WithoutHighSpeed_ConvertsSpeedToBitsThenMbps()
    {
        // Arrange - Speed is in bits/sec
        var metrics = new InterfaceMetrics
        {
            Speed = 1_000_000_000,  // 1 Gbps in bits/sec
            HighSpeed = 0
        };

        // Act & Assert
        metrics.SpeedMbps.Should().Be(1000);
    }

    [Fact]
    public void SpeedMbps_WithZeroSpeed_ReturnsZero()
    {
        // Arrange
        var metrics = new InterfaceMetrics
        {
            Speed = 0,
            HighSpeed = 0
        };

        // Act & Assert
        metrics.SpeedMbps.Should().Be(0);
    }

    [Theory]
    [InlineData(10_000_000, 0, 10)]       // 10 Mbps
    [InlineData(100_000_000, 0, 100)]     // 100 Mbps
    [InlineData(1_000_000_000, 0, 1000)]  // 1 Gbps
    [InlineData(0, 10000, 10000)]         // 10 Gbps via HighSpeed
    [InlineData(0, 100000, 100000)]       // 100 Gbps via HighSpeed
    public void SpeedMbps_VariousSpeeds_CalculatesCorrectly(long speed, long highSpeed, double expected)
    {
        // Arrange
        var metrics = new InterfaceMetrics
        {
            Speed = speed,
            HighSpeed = highSpeed
        };

        // Act & Assert
        metrics.SpeedMbps.Should().Be(expected);
    }

    #endregion

    #region SpeedGbps Tests

    [Theory]
    [InlineData(0, 1000, 1)]      // 1 Gbps
    [InlineData(0, 10000, 10)]    // 10 Gbps
    [InlineData(0, 100000, 100)]  // 100 Gbps
    public void SpeedGbps_CalculatesCorrectly(long speed, long highSpeed, double expected)
    {
        // Arrange
        var metrics = new InterfaceMetrics
        {
            Speed = speed,
            HighSpeed = highSpeed
        };

        // Act & Assert
        metrics.SpeedGbps.Should().Be(expected);
    }

    #endregion

    #region TotalInPackets Tests

    [Fact]
    public void TotalInPackets_SumsAllInPacketTypes()
    {
        // Arrange
        var metrics = new InterfaceMetrics
        {
            InUcastPkts = 1000,
            InMulticastPkts = 200,
            InBroadcastPkts = 50
        };

        // Act & Assert
        metrics.TotalInPackets.Should().Be(1250);
    }

    [Fact]
    public void TotalInPackets_WithZeros_ReturnsZero()
    {
        // Arrange
        var metrics = new InterfaceMetrics();

        // Act & Assert
        metrics.TotalInPackets.Should().Be(0);
    }

    #endregion

    #region TotalOutPackets Tests

    [Fact]
    public void TotalOutPackets_SumsAllOutPacketTypes()
    {
        // Arrange
        var metrics = new InterfaceMetrics
        {
            OutUcastPkts = 2000,
            OutMulticastPkts = 100,
            OutBroadcastPkts = 30
        };

        // Act & Assert
        metrics.TotalOutPackets.Should().Be(2130);
    }

    #endregion

    #region TotalInProblems Tests

    [Fact]
    public void TotalInProblems_SumsErrorsAndDiscards()
    {
        // Arrange
        var metrics = new InterfaceMetrics
        {
            InErrors = 5,
            InDiscards = 10
        };

        // Act & Assert
        metrics.TotalInProblems.Should().Be(15);
    }

    #endregion

    #region TotalOutProblems Tests

    [Fact]
    public void TotalOutProblems_SumsErrorsAndDiscards()
    {
        // Arrange
        var metrics = new InterfaceMetrics
        {
            OutErrors = 3,
            OutDiscards = 7
        };

        // Act & Assert
        metrics.TotalOutProblems.Should().Be(10);
    }

    #endregion

    #region ShouldMonitor Tests

    [Theory]
    [InlineData("eth0", "eth0", true)]
    [InlineData("Ethernet0", "Port 1", true)]
    [InlineData("wan0", "WAN Interface", true)]
    [InlineData("lan1", "LAN Port 1", true)]
    [InlineData("sfp0", "SFP+ Port", true)]
    public void ShouldMonitor_PhysicalInterfaces_ReturnsTrue(string description, string name, bool expected)
    {
        // Arrange
        var metrics = new InterfaceMetrics
        {
            Description = description,
            Name = name
        };

        // Act & Assert
        metrics.ShouldMonitor().Should().Be(expected);
    }

    [Theory]
    [InlineData("lo", "Loopback")]
    public void ShouldMonitor_Loopback_ReturnsFalse(string description, string name)
    {
        var metrics = new InterfaceMetrics { Description = description, Name = name };
        metrics.ShouldMonitor().Should().BeFalse();
    }

    [Theory]
    [InlineData("br-lan", "Bridge LAN")]
    [InlineData("br-guest", "Bridge Guest")]
    [InlineData("br0", "br0")]
    [InlineData("br0.42", "br0.42")]
    [InlineData("br150", "br150")]
    public void ShouldMonitor_BridgeInterfaces_ReturnsFalse(string description, string name)
    {
        var metrics = new InterfaceMetrics { Description = description, Name = name };
        metrics.ShouldMonitor().Should().BeFalse();
    }

    [Theory]
    [InlineData("switch0", "switch0")]
    [InlineData("switch0.1", "switch0.1")]
    public void ShouldMonitor_SwitchChip_ReturnsFalse(string description, string name)
    {
        var metrics = new InterfaceMetrics { Description = description, Name = name };
        metrics.ShouldMonitor().Should().BeFalse();
    }

    [Theory]
    [InlineData("bond0", "bond0")]
    [InlineData("dummy0", "dummy0")]
    [InlineData("sit0", "sit0")]
    [InlineData("erspan0", "erspan0")]
    [InlineData("gretap0", "gretap0")]
    [InlineData("ip_vti0", "ip_vti0")]
    [InlineData("ip6_vti0", "ip6_vti0")]
    [InlineData("ip6tnl0", "ip6tnl0")]
    [InlineData("soc0", "soc0")]
    [InlineData("pd99", "pd99")]
    [InlineData("mld0", "mld0")]
    [InlineData("scan0", "scan0")]
    public void ShouldMonitor_KernelDefaults_ReturnsFalse(string description, string name)
    {
        var metrics = new InterfaceMetrics { Description = description, Name = name };
        metrics.ShouldMonitor().Should().BeFalse();
    }

    [Theory]
    [InlineData("tun0", "OpenVPN Tunnel")]
    [InlineData("tap0", "TAP Device")]
    [InlineData("docker0", "Docker Bridge")]
    [InlineData("veth1234", "veth")]
    [InlineData("ifb0", "ifb0")]
    [InlineData("virbr0", "Virtual Bridge")]
    [InlineData("null0", "Null Interface")]
    [InlineData("gre0", "GRE Tunnel")]
    [InlineData("wgclt1", "WireGuard")]
    [InlineData("honeypot0", "Honeypot")]
    [InlineData("wwan0", "Cellular Modem")]
    public void ShouldMonitor_MonitorableInterfaces_ReturnsTrue(string description, string name)
    {
        var metrics = new InterfaceMetrics { Description = description, Name = name };
        metrics.ShouldMonitor().Should().BeTrue();
    }

    [Theory]
    [InlineData("Port 1", "Loft Lower")]
    [InlineData("Port 2", "Lobby Switch")]
    [InlineData("Port 3", "Loading Dock")]
    public void ShouldMonitor_UserAliasesStartingWithLo_ReturnsTrue(string description, string name)
    {
        var metrics = new InterfaceMetrics { Description = description, Name = name };
        metrics.ShouldMonitor().Should().BeTrue();
    }

    [Theory]
    [InlineData("Device 17cb:1109", "Device 17cb:1109")]
    [InlineData("Device 168c:0046", "Device 168c:0046")]
    [InlineData("device 17cb:1109", "device 17cb:1109")]
    public void ShouldMonitor_DeviceDescriptors_ReturnsFalse(string description, string name)
    {
        var metrics = new InterfaceMetrics
        {
            Description = description,
            Name = name
        };

        metrics.ShouldMonitor().Should().BeFalse();
    }

    [Theory]
    [InlineData("miireg", "miireg")]
    public void ShouldMonitor_MiiRegister_ReturnsFalse(string description, string name)
    {
        var metrics = new InterfaceMetrics
        {
            Description = description,
            Name = name
        };

        metrics.ShouldMonitor().Should().BeFalse();
    }

    [Theory]
    [InlineData("teql0", "teql0")]
    public void ShouldMonitor_TrafficEqualizer_ReturnsFalse(string description, string name)
    {
        var metrics = new InterfaceMetrics
        {
            Description = description,
            Name = name
        };

        metrics.ShouldMonitor().Should().BeFalse();
    }

    [Fact]
    public void ShouldMonitor_CaseInsensitive()
    {
        var metrics1 = new InterfaceMetrics { Description = "LO", Name = "Loopback" };
        var metrics2 = new InterfaceMetrics { Description = "BR0", Name = "Bridge" };

        metrics1.ShouldMonitor().Should().BeFalse();
        metrics2.ShouldMonitor().Should().BeFalse();
    }

    [Fact]
    public void ShouldMonitor_ChecksNameIfDescriptionMatches()
    {
        var metrics = new InterfaceMetrics
        {
            Description = "Something Else",
            Name = "switch0.1"
        };

        metrics.ShouldMonitor().Should().BeFalse();
    }

    #endregion
}
