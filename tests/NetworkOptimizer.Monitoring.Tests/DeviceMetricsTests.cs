using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class DeviceMetricsTests
{
    #region Default Values Tests

    [Fact]
    public void DeviceMetrics_DefaultValues_AreCorrect()
    {
        // Act
        var metrics = new DeviceMetrics();

        // Assert
        metrics.IpAddress.Should().BeEmpty();
        metrics.Hostname.Should().BeEmpty();
        metrics.Description.Should().BeEmpty();
        metrics.Location.Should().BeEmpty();
        metrics.Contact.Should().BeEmpty();
        metrics.Uptime.Should().Be(0);
        metrics.ObjectId.Should().BeEmpty();
        metrics.Model.Should().BeEmpty();
        metrics.FirmwareVersion.Should().BeEmpty();
        metrics.MacAddress.Should().BeEmpty();
        metrics.CpuUsage.Should().Be(0);
        metrics.MemoryUsage.Should().Be(0);
        metrics.TotalMemory.Should().Be(0);
        metrics.UsedMemory.Should().Be(0);
        metrics.FreeMemory.Should().Be(0);
        metrics.Temperature.Should().BeNull();
        metrics.InterfaceCount.Should().Be(0);
        metrics.Interfaces.Should().NotBeNull();
        metrics.Interfaces.Should().BeEmpty();
        metrics.DeviceType.Should().Be(DeviceType.Unknown);
        metrics.IsReachable.Should().BeTrue();
        metrics.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void DeviceMetrics_Timestamp_DefaultsToUtcNow()
    {
        // Act
        var metrics = new DeviceMetrics();

        // Assert
        metrics.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region UptimeSpan Tests

    [Fact]
    public void UptimeSpan_ConvertsFromHundredthsOfSeconds()
    {
        // Arrange - Uptime is in hundredths of a second (10ms each)
        var metrics = new DeviceMetrics { Uptime = 100 }; // 1 second

        // Act
        var span = metrics.UptimeSpan;

        // Assert
        span.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void UptimeSpan_ZeroUptime_ReturnsZeroSpan()
    {
        // Arrange
        var metrics = new DeviceMetrics { Uptime = 0 };

        // Act & Assert
        metrics.UptimeSpan.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(8640000, 1)]     // 1 day
    [InlineData(86400000, 10)]   // 10 days
    [InlineData(604800000, 70)]  // 70 days (approx)
    public void UptimeSpan_LargeValues_CalculatesCorrectly(long uptime, double expectedDays)
    {
        // Arrange
        var metrics = new DeviceMetrics { Uptime = uptime };

        // Act
        var days = metrics.UptimeSpan.TotalDays;

        // Assert
        days.Should().BeApproximately(expectedDays, 0.1);
    }

    #endregion

    #region UptimeDays Tests

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8640000, 1)]      // 1 day
    [InlineData(17280000, 2)]     // 2 days
    [InlineData(259200000, 30)]   // 30 days
    public void UptimeDays_CalculatesCorrectly(long uptime, double expectedDays)
    {
        // Arrange
        var metrics = new DeviceMetrics { Uptime = uptime };

        // Act & Assert
        metrics.UptimeDays.Should().BeApproximately(expectedDays, 0.01);
    }

    #endregion

    #region Memory MB Tests

    [Fact]
    public void UsedMemoryMB_ConvertsFromBytes()
    {
        // Arrange
        var metrics = new DeviceMetrics { UsedMemory = 1_048_576 }; // 1 MB in bytes

        // Act & Assert
        metrics.UsedMemoryMB.Should().Be(1);
    }

    [Fact]
    public void TotalMemoryMB_ConvertsFromBytes()
    {
        // Arrange
        var metrics = new DeviceMetrics { TotalMemory = 4_294_967_296 }; // 4 GB in bytes

        // Act & Assert
        metrics.TotalMemoryMB.Should().Be(4096);
    }

    [Fact]
    public void FreeMemoryMB_ConvertsFromBytes()
    {
        // Arrange
        var metrics = new DeviceMetrics { FreeMemory = 2_147_483_648 }; // 2 GB in bytes

        // Act & Assert
        metrics.FreeMemoryMB.Should().Be(2048);
    }

    [Fact]
    public void MemoryMB_ZeroValues_ReturnZero()
    {
        // Arrange
        var metrics = new DeviceMetrics
        {
            TotalMemory = 0,
            UsedMemory = 0,
            FreeMemory = 0
        };

        // Act & Assert
        metrics.TotalMemoryMB.Should().Be(0);
        metrics.UsedMemoryMB.Should().Be(0);
        metrics.FreeMemoryMB.Should().Be(0);
    }

    [Theory]
    [InlineData(1_073_741_824, 1024)]   // 1 GB
    [InlineData(2_147_483_648, 2048)]   // 2 GB
    [InlineData(8_589_934_592, 8192)]   // 8 GB
    [InlineData(17_179_869_184, 16384)] // 16 GB
    public void TotalMemoryMB_VariousValues_CalculatesCorrectly(long bytes, double expectedMB)
    {
        // Arrange
        var metrics = new DeviceMetrics { TotalMemory = bytes };

        // Act & Assert
        metrics.TotalMemoryMB.Should().Be(expectedMB);
    }

    #endregion

    #region Interfaces Collection Tests

    [Fact]
    public void Interfaces_CanAddInterfaces()
    {
        // Arrange
        var metrics = new DeviceMetrics();
        var iface = new InterfaceMetrics { Index = 1, Description = "eth0" };

        // Act
        metrics.Interfaces.Add(iface);

        // Assert
        metrics.Interfaces.Should().HaveCount(1);
        metrics.Interfaces[0].Description.Should().Be("eth0");
    }

    [Fact]
    public void Interfaces_InterfaceCount_CanBeSetIndependently()
    {
        // Arrange - InterfaceCount from SNMP might differ from Interfaces list
        var metrics = new DeviceMetrics
        {
            InterfaceCount = 10
        };
        metrics.Interfaces.Add(new InterfaceMetrics { Index = 1 });

        // Assert - The count property vs actual collection can differ
        metrics.InterfaceCount.Should().Be(10);
        metrics.Interfaces.Should().HaveCount(1);
    }

    #endregion

    #region DeviceType Enum Tests

    [Fact]
    public void DeviceType_AllValuesAreDefined()
    {
        // Assert
        var values = Enum.GetValues<DeviceType>();
        values.Should().Contain(DeviceType.Unknown);
        values.Should().Contain(DeviceType.Gateway);
        values.Should().Contain(DeviceType.Switch);
        values.Should().Contain(DeviceType.AccessPoint);
        values.Should().Contain(DeviceType.Router);
        values.Should().Contain(DeviceType.Firewall);
        values.Should().Contain(DeviceType.Server);
        values.Should().Contain(DeviceType.Other);
    }

    [Fact]
    public void DeviceType_Unknown_IsDefault()
    {
        // Assert
        default(DeviceType).Should().Be(DeviceType.Unknown);
    }

    #endregion

    #region Property Setting Tests

    [Fact]
    public void DeviceMetrics_CanSetAllProperties()
    {
        // Arrange & Act
        var timestamp = DateTime.UtcNow;
        var metrics = new DeviceMetrics
        {
            IpAddress = "192.0.2.1",
            Hostname = "test-device",
            Description = "Test Switch",
            Location = "Server Room",
            Contact = "admin@test.com",
            Uptime = 8640000,
            ObjectId = "1.3.6.1.4.1.41112.1.6",
            Model = "USW-Pro-48",
            FirmwareVersion = "6.0.0",
            MacAddress = "aa:bb:cc:dd:ee:ff",
            CpuUsage = 25.5,
            MemoryUsage = 60.2,
            TotalMemory = 4_294_967_296,
            UsedMemory = 2_576_980_378,
            FreeMemory = 1_717_986_918,
            Temperature = 42.5,
            InterfaceCount = 52,
            DeviceType = DeviceType.Switch,
            IsReachable = true,
            ErrorMessage = null,
            Timestamp = timestamp
        };

        // Assert
        metrics.IpAddress.Should().Be("192.0.2.1");
        metrics.Hostname.Should().Be("test-device");
        metrics.Description.Should().Be("Test Switch");
        metrics.Location.Should().Be("Server Room");
        metrics.Contact.Should().Be("admin@test.com");
        metrics.Uptime.Should().Be(8640000);
        metrics.ObjectId.Should().Be("1.3.6.1.4.1.41112.1.6");
        metrics.Model.Should().Be("USW-Pro-48");
        metrics.FirmwareVersion.Should().Be("6.0.0");
        metrics.MacAddress.Should().Be("aa:bb:cc:dd:ee:ff");
        metrics.CpuUsage.Should().Be(25.5);
        metrics.MemoryUsage.Should().Be(60.2);
        metrics.TotalMemory.Should().Be(4_294_967_296);
        metrics.UsedMemory.Should().Be(2_576_980_378);
        metrics.FreeMemory.Should().Be(1_717_986_918);
        metrics.Temperature.Should().Be(42.5);
        metrics.InterfaceCount.Should().Be(52);
        metrics.DeviceType.Should().Be(DeviceType.Switch);
        metrics.IsReachable.Should().BeTrue();
        metrics.ErrorMessage.Should().BeNull();
        metrics.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void DeviceMetrics_ErrorMessage_CanBeSet()
    {
        // Arrange
        var metrics = new DeviceMetrics
        {
            IsReachable = false,
            ErrorMessage = "SNMP timeout"
        };

        // Assert
        metrics.IsReachable.Should().BeFalse();
        metrics.ErrorMessage.Should().Be("SNMP timeout");
    }

    [Fact]
    public void DeviceMetrics_Temperature_NullByDefault()
    {
        // Arrange
        var metrics = new DeviceMetrics();

        // Assert
        metrics.Temperature.Should().BeNull();
    }

    [Fact]
    public void DeviceMetrics_Temperature_CanBeSet()
    {
        // Arrange
        var metrics = new DeviceMetrics { Temperature = 55.0 };

        // Assert
        metrics.Temperature.Should().Be(55.0);
    }

    #endregion
}
