using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class MetricsAggregatorTests
{
    private readonly Mock<ILogger<MetricsAggregator>> _loggerMock;
    private readonly MetricsAggregator _aggregator;

    public MetricsAggregatorTests()
    {
        _loggerMock = new Mock<ILogger<MetricsAggregator>>();
        _aggregator = new MetricsAggregator(_loggerMock.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new MetricsAggregator(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    public void Constructor_DefaultMaxBatchSize_IsThousand()
    {
        // Arrange
        var aggregator = new MetricsAggregator(_loggerMock.Object);

        // Act - add 999 metrics, should not be ready
        for (int i = 0; i < 999; i++)
        {
            aggregator.AddCustomMetric($"metric.{i}", i, "192.0.2.1");
        }
        var batch999 = aggregator.GetBatch();

        // Add one more to hit 1000
        aggregator.AddCustomMetric("metric.999", 999, "192.0.2.1");
        var batch1000 = aggregator.GetBatch();

        // Assert
        batch999.IsReady.Should().BeFalse();
        batch1000.IsReady.Should().BeTrue();
    }

    [Fact]
    public void Constructor_CustomMaxBatchSize_IsRespected()
    {
        // Arrange
        var aggregator = new MetricsAggregator(_loggerMock.Object, maxBatchSize: 5);

        // Act - add 4 metrics
        for (int i = 0; i < 4; i++)
        {
            aggregator.AddCustomMetric($"metric.{i}", i, "192.0.2.1");
        }
        var batch4 = aggregator.GetBatch();

        // Add one more to hit 5
        aggregator.AddCustomMetric("metric.4", 4, "192.0.2.1");
        var batch5 = aggregator.GetBatch();

        // Assert
        batch4.IsReady.Should().BeFalse();
        batch5.IsReady.Should().BeTrue();
    }

    #endregion

    #region AddDeviceMetrics Tests

    [Fact]
    public void AddDeviceMetrics_NullDeviceMetrics_ThrowsArgumentNullException()
    {
        // Act
        var act = () => _aggregator.AddDeviceMetrics(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("deviceMetrics");
    }

    [Fact]
    public void AddDeviceMetrics_BasicDevice_AddsInterfaceCountAndReachability()
    {
        // Arrange
        var device = CreateDeviceMetrics();

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "device.interfaces.count");
        batch.Metrics.Should().Contain(m => m.Name == "device.reachable");
    }

    [Fact]
    public void AddDeviceMetrics_WithUptime_AddsUptimeMetric()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.Uptime = 8640000; // 1 day in hundredths of a second

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        var uptimeMetric = batch.Metrics.FirstOrDefault(m => m.Name == "device.uptime");
        uptimeMetric.Should().NotBeNull();
        uptimeMetric!.Value.Should().Be(8640000);
    }

    [Fact]
    public void AddDeviceMetrics_ZeroUptime_DoesNotAddUptimeMetric()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.Uptime = 0;

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().NotContain(m => m.Name == "device.uptime");
    }

    [Fact]
    public void AddDeviceMetrics_WithCpuUsage_AddsCpuMetric()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.CpuUsage = 65.5;

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        var cpuMetric = batch.Metrics.FirstOrDefault(m => m.Name == "device.cpu.usage");
        cpuMetric.Should().NotBeNull();
        cpuMetric!.Value.Should().Be(65.5);
    }

    [Fact]
    public void AddDeviceMetrics_ZeroCpuUsage_DoesNotAddCpuMetric()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.CpuUsage = 0;

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().NotContain(m => m.Name == "device.cpu.usage");
    }

    [Fact]
    public void AddDeviceMetrics_WithMemoryUsage_AddsMemoryUsageMetric()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.MemoryUsage = 45.2;

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        var memUsageMetric = batch.Metrics.FirstOrDefault(m => m.Name == "device.memory.usage.percent");
        memUsageMetric.Should().NotBeNull();
        memUsageMetric!.Value.Should().Be(45.2);
    }

    [Fact]
    public void AddDeviceMetrics_WithTotalMemory_AddsAllMemoryMetrics()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.TotalMemory = 4_000_000_000; // 4 GB
        device.UsedMemory = 2_500_000_000;  // 2.5 GB
        device.FreeMemory = 1_500_000_000;  // 1.5 GB

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "device.memory.total.bytes" && m.Value == 4_000_000_000);
        batch.Metrics.Should().Contain(m => m.Name == "device.memory.used.bytes" && m.Value == 2_500_000_000);
        batch.Metrics.Should().Contain(m => m.Name == "device.memory.free.bytes" && m.Value == 1_500_000_000);
    }

    [Fact]
    public void AddDeviceMetrics_ZeroTotalMemory_DoesNotAddMemoryBytesMetrics()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.TotalMemory = 0;

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().NotContain(m => m.Name == "device.memory.total.bytes");
        batch.Metrics.Should().NotContain(m => m.Name == "device.memory.used.bytes");
        batch.Metrics.Should().NotContain(m => m.Name == "device.memory.free.bytes");
    }

    [Fact]
    public void AddDeviceMetrics_WithTemperature_AddsTemperatureMetric()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.Temperature = 42.5;

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        var tempMetric = batch.Metrics.FirstOrDefault(m => m.Name == "device.temperature.celsius");
        tempMetric.Should().NotBeNull();
        tempMetric!.Value.Should().Be(42.5);
    }

    [Fact]
    public void AddDeviceMetrics_NullTemperature_DoesNotAddTemperatureMetric()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.Temperature = null;

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().NotContain(m => m.Name == "device.temperature.celsius");
    }

    [Fact]
    public void AddDeviceMetrics_Reachable_AddsReachableMetricAsOne()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.IsReachable = true;

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        var reachableMetric = batch.Metrics.FirstOrDefault(m => m.Name == "device.reachable");
        reachableMetric.Should().NotBeNull();
        reachableMetric!.Value.Should().Be(1);
    }

    [Fact]
    public void AddDeviceMetrics_NotReachable_AddsReachableMetricAsZero()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.IsReachable = false;

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        var reachableMetric = batch.Metrics.FirstOrDefault(m => m.Name == "device.reachable");
        reachableMetric.Should().NotBeNull();
        reachableMetric!.Value.Should().Be(0);
    }

    [Fact]
    public void AddDeviceMetrics_SetsCorrectDeviceInfo()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.IpAddress = "192.0.2.100";
        device.Hostname = "test-switch";

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().OnlyContain(m =>
            m.DeviceIp == "192.0.2.100" &&
            m.DeviceHostname == "test-switch");
    }

    [Fact]
    public void AddDeviceMetrics_SetsCorrectSource()
    {
        // Arrange
        var device = CreateDeviceMetrics();

        // Act
        _aggregator.AddDeviceMetrics(device, MetricSource.Agent);
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().OnlyContain(m => m.Source == MetricSource.Agent);
    }

    [Fact]
    public void AddDeviceMetrics_SetsCorrectTags()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.IpAddress = "192.0.2.50";
        device.DeviceType = DeviceType.Switch;
        device.Hostname = "switch-01";
        device.Model = "US-48-500W";
        device.Location = "Server Room";

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.First();
        metric.Tags.Should().ContainKey("device_ip").WhoseValue.Should().Be("192.0.2.50");
        metric.Tags.Should().ContainKey("device_type").WhoseValue.Should().Be("Switch");
        metric.Tags.Should().ContainKey("hostname").WhoseValue.Should().Be("switch-01");
        metric.Tags.Should().ContainKey("model").WhoseValue.Should().Be("US-48-500W");
        metric.Tags.Should().ContainKey("location").WhoseValue.Should().Be("Server Room");
    }

    [Fact]
    public void AddDeviceMetrics_EmptyHostname_DoesNotAddHostnameTag()
    {
        // Arrange
        var device = CreateDeviceMetrics();
        device.Hostname = "";

        // Act
        _aggregator.AddDeviceMetrics(device);
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.First();
        metric.Tags.Should().NotContainKey("hostname");
    }

    [Fact]
    public void AddDeviceMetrics_AllMetricsSources_AreSupported()
    {
        // Arrange & Act & Assert
        foreach (var source in Enum.GetValues<MetricSource>())
        {
            var device = CreateDeviceMetrics();
            _aggregator.AddDeviceMetrics(device, source);
            var batch = _aggregator.GetBatch();
            batch.Metrics.Should().OnlyContain(m => m.Source == source);
            _aggregator.ClearBatch();
        }
    }

    #endregion

    #region AddInterfaceMetrics Tests

    [Fact]
    public void AddInterfaceMetrics_NullList_ThrowsArgumentNullException()
    {
        // Act
        var act = () => _aggregator.AddInterfaceMetrics(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("interfaceMetrics");
    }

    [Fact]
    public void AddInterfaceMetrics_EmptyList_AddsNoMetrics()
    {
        // Arrange
        var interfaces = new List<InterfaceMetrics>();

        // Act
        _aggregator.AddInterfaceMetrics(interfaces);
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().BeEmpty();
    }

    [Fact]
    public void AddInterfaceMetrics_AddsStatusMetrics()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.AdminStatus = 1;
        iface.OperStatus = 1;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "interface.admin.status" && m.Value == 1);
        batch.Metrics.Should().Contain(m => m.Name == "interface.oper.status" && m.Value == 1);
        batch.Metrics.Should().Contain(m => m.Name == "interface.is.up" && m.Value == 1);
    }

    [Fact]
    public void AddInterfaceMetrics_InterfaceDown_IsUpMetricIsZero()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.OperStatus = 2; // Down

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "interface.is.up" && m.Value == 0);
    }

    [Fact]
    public void AddInterfaceMetrics_WithSpeed_AddsSpeedMetric()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.Speed = 1_000_000_000; // 1 Gbps

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "interface.speed.mbps" && m.Value == 1000);
    }

    [Fact]
    public void AddInterfaceMetrics_WithHighSpeed_AddsSpeedMetric()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.HighSpeed = 10000; // 10 Gbps

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "interface.speed.mbps" && m.Value == 10000);
    }

    [Fact]
    public void AddInterfaceMetrics_ZeroSpeed_DoesNotAddSpeedMetric()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.Speed = 0;
        iface.HighSpeed = 0;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().NotContain(m => m.Name == "interface.speed.mbps");
    }

    [Fact]
    public void AddInterfaceMetrics_AddsTrafficCounters()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InOctets = 1_000_000;
        iface.OutOctets = 2_000_000;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "interface.in.octets" && m.Value == 1_000_000);
        batch.Metrics.Should().Contain(m => m.Name == "interface.out.octets" && m.Value == 2_000_000);
    }

    [Fact]
    public void AddInterfaceMetrics_AddsPacketCounters()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InUcastPkts = 100;
        iface.OutUcastPkts = 200;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        // TotalInPackets/TotalOutPackets are computed properties
        batch.Metrics.Should().Contain(m => m.Name == "interface.in.packets");
        batch.Metrics.Should().Contain(m => m.Name == "interface.out.packets");
    }

    [Fact]
    public void AddInterfaceMetrics_WithUnicastPackets_AddsUnicastMetrics()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InUcastPkts = 1000;
        iface.OutUcastPkts = 2000;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "interface.in.ucast.packets" && m.Value == 1000);
        batch.Metrics.Should().Contain(m => m.Name == "interface.out.ucast.packets" && m.Value == 2000);
    }

    [Fact]
    public void AddInterfaceMetrics_ZeroUnicastPackets_DoesNotAddUnicastMetrics()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InUcastPkts = 0;
        iface.OutUcastPkts = 0;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().NotContain(m => m.Name == "interface.in.ucast.packets");
        batch.Metrics.Should().NotContain(m => m.Name == "interface.out.ucast.packets");
    }

    [Fact]
    public void AddInterfaceMetrics_WithMulticastPackets_AddsMulticastMetrics()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InMulticastPkts = 500;
        iface.OutMulticastPkts = 600;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "interface.in.multicast.packets" && m.Value == 500);
        batch.Metrics.Should().Contain(m => m.Name == "interface.out.multicast.packets" && m.Value == 600);
    }

    [Fact]
    public void AddInterfaceMetrics_ZeroMulticastPackets_DoesNotAddMulticastMetrics()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InMulticastPkts = 0;
        iface.OutMulticastPkts = 0;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().NotContain(m => m.Name == "interface.in.multicast.packets");
        batch.Metrics.Should().NotContain(m => m.Name == "interface.out.multicast.packets");
    }

    [Fact]
    public void AddInterfaceMetrics_WithBroadcastPackets_AddsBroadcastMetrics()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InBroadcastPkts = 100;
        iface.OutBroadcastPkts = 200;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "interface.in.broadcast.packets" && m.Value == 100);
        batch.Metrics.Should().Contain(m => m.Name == "interface.out.broadcast.packets" && m.Value == 200);
    }

    [Fact]
    public void AddInterfaceMetrics_ZeroBroadcastPackets_DoesNotAddBroadcastMetrics()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InBroadcastPkts = 0;
        iface.OutBroadcastPkts = 0;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().NotContain(m => m.Name == "interface.in.broadcast.packets");
        batch.Metrics.Should().NotContain(m => m.Name == "interface.out.broadcast.packets");
    }

    [Fact]
    public void AddInterfaceMetrics_AddsErrorAndDiscardMetrics()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InErrors = 10;
        iface.OutErrors = 20;
        iface.InDiscards = 5;
        iface.OutDiscards = 15;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "interface.in.errors" && m.Value == 10);
        batch.Metrics.Should().Contain(m => m.Name == "interface.out.errors" && m.Value == 20);
        batch.Metrics.Should().Contain(m => m.Name == "interface.in.discards" && m.Value == 5);
        batch.Metrics.Should().Contain(m => m.Name == "interface.out.discards" && m.Value == 15);
    }

    [Fact]
    public void AddInterfaceMetrics_WithUnknownProtos_AddsUnknownProtosMetric()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InUnknownProtos = 42;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().Contain(m => m.Name == "interface.in.unknown.protos" && m.Value == 42);
    }

    [Fact]
    public void AddInterfaceMetrics_ZeroUnknownProtos_DoesNotAddUnknownProtosMetric()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.InUnknownProtos = 0;

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().NotContain(m => m.Name == "interface.in.unknown.protos");
    }

    [Fact]
    public void AddInterfaceMetrics_SetsCorrectInterfaceTags()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.DeviceIp = "192.0.2.1";
        iface.DeviceHostname = "switch-01";
        iface.Index = 5;
        iface.Description = "Port 5";
        iface.Name = "eth5";
        iface.PhysicalAddress = "aa:bb:cc:dd:ee:ff";

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.First();
        metric.Tags.Should().ContainKey("device_ip").WhoseValue.Should().Be("192.0.2.1");
        metric.Tags.Should().ContainKey("hostname").WhoseValue.Should().Be("switch-01");
        metric.Tags.Should().ContainKey("interface_index").WhoseValue.Should().Be("5");
        metric.Tags.Should().ContainKey("interface_description").WhoseValue.Should().Be("Port 5");
        metric.Tags.Should().ContainKey("interface_name").WhoseValue.Should().Be("eth5");
        metric.Tags.Should().ContainKey("mac_address").WhoseValue.Should().Be("aa:bb:cc:dd:ee:ff");
    }

    [Fact]
    public void AddInterfaceMetrics_EmptyInterfaceName_DoesNotAddNameTag()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.Name = "";

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.First();
        metric.Tags.Should().NotContainKey("interface_name");
    }

    [Fact]
    public void AddInterfaceMetrics_SetsInterfaceDescription()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();
        iface.Description = "Uplink to Core Switch";

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface });
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().OnlyContain(m => m.InterfaceDescription == "Uplink to Core Switch");
    }

    [Fact]
    public void AddInterfaceMetrics_MultipleInterfaces_AddsMetricsForAll()
    {
        // Arrange
        var interfaces = new List<InterfaceMetrics>
        {
            CreateInterfaceMetrics("eth0"),
            CreateInterfaceMetrics("eth1"),
            CreateInterfaceMetrics("eth2")
        };

        // Act
        _aggregator.AddInterfaceMetrics(interfaces);
        var batch = _aggregator.GetBatch();

        // Assert
        // Each interface should have at least status metrics (admin, oper, is_up) + traffic counters
        batch.Metrics.Should().Contain(m => m.Tags["interface_description"] == "eth0");
        batch.Metrics.Should().Contain(m => m.Tags["interface_description"] == "eth1");
        batch.Metrics.Should().Contain(m => m.Tags["interface_description"] == "eth2");
    }

    [Fact]
    public void AddInterfaceMetrics_SetsCorrectSource()
    {
        // Arrange
        var iface = CreateInterfaceMetrics();

        // Act
        _aggregator.AddInterfaceMetrics(new List<InterfaceMetrics> { iface }, MetricSource.UniFiApi);
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().OnlyContain(m => m.Source == MetricSource.UniFiApi);
    }

    #endregion

    #region AddCustomMetric Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddCustomMetric_EmptyName_ThrowsArgumentException(string? name)
    {
        // Act
        var act = () => _aggregator.AddCustomMetric(name!, 100, "192.0.2.1");

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddCustomMetric_EmptyDeviceIp_ThrowsArgumentException(string? deviceIp)
    {
        // Act
        var act = () => _aggregator.AddCustomMetric("custom.metric", 100, deviceIp!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("deviceIp");
    }

    [Fact]
    public void AddCustomMetric_ValidInput_AddsMetric()
    {
        // Act
        _aggregator.AddCustomMetric("custom.metric", 42.5, "192.0.2.1");
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.FirstOrDefault(m => m.Name == "custom.metric");
        metric.Should().NotBeNull();
        metric!.Value.Should().Be(42.5);
        metric.DeviceIp.Should().Be("192.0.2.1");
        metric.Source.Should().Be(MetricSource.Custom);
    }

    [Fact]
    public void AddCustomMetric_WithTags_IncludesTags()
    {
        // Arrange
        var tags = new Dictionary<string, string>
        {
            { "env", "production" },
            { "region", "us-east" }
        };

        // Act
        _aggregator.AddCustomMetric("custom.metric", 100, "192.0.2.1", tags);
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.First();
        metric.Tags.Should().ContainKey("env").WhoseValue.Should().Be("production");
        metric.Tags.Should().ContainKey("region").WhoseValue.Should().Be("us-east");
    }

    [Fact]
    public void AddCustomMetric_NullTags_CreatesEmptyTagsDictionary()
    {
        // Act
        _aggregator.AddCustomMetric("custom.metric", 100, "192.0.2.1", null);
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.First();
        metric.Tags.Should().NotBeNull();
        metric.Tags.Should().BeEmpty();
    }

    [Fact]
    public void AddCustomMetric_NormalizesMetricName()
    {
        // Act
        _aggregator.AddCustomMetric("Custom_Metric__Name", 100, "192.0.2.1");
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.First();
        metric.Name.Should().Be("custom.metric.name");
    }

    [Fact]
    public void AddCustomMetric_NegativeValue_IsAccepted()
    {
        // Act
        _aggregator.AddCustomMetric("temperature.diff", -5.5, "192.0.2.1");
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.First();
        metric.Value.Should().Be(-5.5);
    }

    [Fact]
    public void AddCustomMetric_ZeroValue_IsAccepted()
    {
        // Act
        _aggregator.AddCustomMetric("counter.zero", 0, "192.0.2.1");
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.First();
        metric.Value.Should().Be(0);
    }

    [Fact]
    public void AddCustomMetric_LargeValue_IsAccepted()
    {
        // Act
        _aggregator.AddCustomMetric("bytes.total", double.MaxValue, "192.0.2.1");
        var batch = _aggregator.GetBatch();

        // Assert
        var metric = batch.Metrics.First();
        metric.Value.Should().Be(double.MaxValue);
    }

    #endregion

    #region GetBatch Tests

    [Fact]
    public void GetBatch_EmptyBatch_ReturnsEmptyBatch()
    {
        // Act
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Should().NotBeNull();
        batch.Metrics.Should().BeEmpty();
        batch.Count.Should().Be(0);
        batch.IsReady.Should().BeFalse();
    }

    [Fact]
    public void GetBatch_WithMetrics_ReturnsBatchWithMetrics()
    {
        // Arrange
        _aggregator.AddCustomMetric("metric.1", 1, "192.0.2.1");
        _aggregator.AddCustomMetric("metric.2", 2, "192.0.2.1");

        // Act
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().HaveCount(2);
        batch.Count.Should().Be(2);
    }

    [Fact]
    public void GetBatch_ReturnsNewBatchInstance()
    {
        // Arrange
        _aggregator.AddCustomMetric("metric", 1, "192.0.2.1");

        // Act
        var batch1 = _aggregator.GetBatch();
        var batch2 = _aggregator.GetBatch();

        // Assert
        batch1.Should().NotBeSameAs(batch2);
        batch1.BatchId.Should().NotBe(batch2.BatchId);
    }

    [Fact]
    public void GetBatch_ReturnsCopyOfMetrics()
    {
        // Arrange
        _aggregator.AddCustomMetric("metric", 1, "192.0.2.1");

        // Act
        var batch = _aggregator.GetBatch();
        batch.Metrics.Clear();
        var batch2 = _aggregator.GetBatch();

        // Assert - original batch in aggregator should be unaffected
        batch2.Metrics.Should().HaveCount(1);
    }

    [Fact]
    public void GetBatch_UnderMaxSize_IsReadyIsFalse()
    {
        // Arrange
        var aggregator = new MetricsAggregator(_loggerMock.Object, maxBatchSize: 10);
        for (int i = 0; i < 9; i++)
        {
            aggregator.AddCustomMetric($"metric.{i}", i, "192.0.2.1");
        }

        // Act
        var batch = aggregator.GetBatch();

        // Assert
        batch.IsReady.Should().BeFalse();
    }

    [Fact]
    public void GetBatch_AtMaxSize_IsReadyIsTrue()
    {
        // Arrange
        var aggregator = new MetricsAggregator(_loggerMock.Object, maxBatchSize: 10);
        for (int i = 0; i < 10; i++)
        {
            aggregator.AddCustomMetric($"metric.{i}", i, "192.0.2.1");
        }

        // Act
        var batch = aggregator.GetBatch();

        // Assert
        batch.IsReady.Should().BeTrue();
    }

    [Fact]
    public void GetBatch_OverMaxSize_IsReadyIsTrue()
    {
        // Arrange
        var aggregator = new MetricsAggregator(_loggerMock.Object, maxBatchSize: 10);
        for (int i = 0; i < 15; i++)
        {
            aggregator.AddCustomMetric($"metric.{i}", i, "192.0.2.1");
        }

        // Act
        var batch = aggregator.GetBatch();

        // Assert
        batch.IsReady.Should().BeTrue();
        batch.Count.Should().Be(15);
    }

    [Fact]
    public void GetBatch_HasValidBatchId()
    {
        // Act
        var batch = _aggregator.GetBatch();

        // Assert
        batch.BatchId.Should().NotBeEmpty();
    }

    [Fact]
    public void GetBatch_HasRecentCreatedAt()
    {
        // Act
        var batch = _aggregator.GetBatch();

        // Assert
        batch.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region ClearBatch Tests

    [Fact]
    public void ClearBatch_EmptyBatch_DoesNotThrow()
    {
        // Act
        var act = () => _aggregator.ClearBatch();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void ClearBatch_WithMetrics_RemovesAllMetrics()
    {
        // Arrange
        _aggregator.AddCustomMetric("metric.1", 1, "192.0.2.1");
        _aggregator.AddCustomMetric("metric.2", 2, "192.0.2.1");

        // Act
        _aggregator.ClearBatch();
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().BeEmpty();
        batch.Count.Should().Be(0);
    }

    [Fact]
    public void ClearBatch_AllowsAddingNewMetrics()
    {
        // Arrange
        _aggregator.AddCustomMetric("old.metric", 1, "192.0.2.1");
        _aggregator.ClearBatch();

        // Act
        _aggregator.AddCustomMetric("new.metric", 2, "192.0.2.1");
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().HaveCount(1);
        batch.Metrics.First().Name.Should().Be("new.metric");
    }

    [Fact]
    public void ClearBatch_ResetsIsReadyFlag()
    {
        // Arrange
        var aggregator = new MetricsAggregator(_loggerMock.Object, maxBatchSize: 3);
        aggregator.AddCustomMetric("m1", 1, "192.0.2.1");
        aggregator.AddCustomMetric("m2", 2, "192.0.2.1");
        aggregator.AddCustomMetric("m3", 3, "192.0.2.1");

        var batchBefore = aggregator.GetBatch();
        batchBefore.IsReady.Should().BeTrue();

        // Act
        aggregator.ClearBatch();
        var batchAfter = aggregator.GetBatch();

        // Assert
        batchAfter.IsReady.Should().BeFalse();
    }

    #endregion

    #region GetBatchCount Tests

    [Fact]
    public void GetBatchCount_EmptyBatch_ReturnsZero()
    {
        // Act
        var count = _aggregator.GetBatchCount();

        // Assert
        count.Should().Be(0);
    }

    [Fact]
    public void GetBatchCount_WithMetrics_ReturnsCorrectCount()
    {
        // Arrange
        _aggregator.AddCustomMetric("m1", 1, "192.0.2.1");
        _aggregator.AddCustomMetric("m2", 2, "192.0.2.1");
        _aggregator.AddCustomMetric("m3", 3, "192.0.2.1");

        // Act
        var count = _aggregator.GetBatchCount();

        // Assert
        count.Should().Be(3);
    }

    [Fact]
    public void GetBatchCount_AfterClear_ReturnsZero()
    {
        // Arrange
        _aggregator.AddCustomMetric("m1", 1, "192.0.2.1");
        _aggregator.ClearBatch();

        // Act
        var count = _aggregator.GetBatchCount();

        // Assert
        count.Should().Be(0);
    }

    [Fact]
    public void GetBatchCount_MatchesBatchCount()
    {
        // Arrange
        _aggregator.AddCustomMetric("m1", 1, "192.0.2.1");
        _aggregator.AddCustomMetric("m2", 2, "192.0.2.1");

        // Act
        var count = _aggregator.GetBatchCount();
        var batch = _aggregator.GetBatch();

        // Assert
        count.Should().Be(batch.Count);
    }

    #endregion

    #region NormalizeMetricName Tests

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("MixedCase", "mixedcase")]
    [InlineData("with_underscore", "with.underscore")]
    [InlineData("with__double__underscore", "with.double.underscore")]
    [InlineData("with-dash", "with.dash")]
    [InlineData("with space", "with.space")]
    [InlineData("device.cpu.usage", "device.cpu.usage")]
    [InlineData("Device_CPU__Usage", "device.cpu.usage")]
    public void NormalizeMetricName_VariousInputs_NormalizesCorrectly(string input, string expected)
    {
        // Act
        _aggregator.AddCustomMetric(input, 1, "192.0.2.1");
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.First().Name.Should().Be(expected);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public void AddMetrics_ConcurrentAccess_DoesNotCorrupt()
    {
        // Arrange
        var tasks = new List<Task>();

        // Act - add metrics from multiple threads
        for (int i = 0; i < 10; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    _aggregator.AddCustomMetric($"metric.{index}.{j}", index * 100 + j, "192.0.2.1");
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());
        var batch = _aggregator.GetBatch();

        // Assert
        batch.Metrics.Should().HaveCount(1000); // 10 threads * 100 metrics each
    }

    [Fact]
    public void GetBatchCount_ConcurrentAccess_ReturnsConsistentCount()
    {
        // Arrange
        var addTask = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                _aggregator.AddCustomMetric($"metric.{i}", i, "192.0.2.1");
                Thread.Sleep(1); // Small delay to interleave operations
            }
        });

        var countResults = new List<int>();
        var countTask = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                countResults.Add(_aggregator.GetBatchCount());
                Thread.Sleep(2);
            }
        });

        // Act
        Task.WaitAll(addTask, countTask);

        // Assert - counts should be non-decreasing (unless ClearBatch is called)
        for (int i = 1; i < countResults.Count; i++)
        {
            countResults[i].Should().BeGreaterThanOrEqualTo(countResults[i - 1]);
        }
    }

    #endregion

    #region Model Tests

    [Fact]
    public void AggregatedMetric_DefaultValues_AreCorrect()
    {
        // Act
        var metric = new AggregatedMetric();

        // Assert
        metric.Id.Should().NotBeEmpty();
        metric.Name.Should().BeEmpty();
        metric.Value.Should().Be(0);
        metric.Source.Should().Be(MetricSource.Snmp);
        metric.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        metric.DeviceIp.Should().BeEmpty();
        metric.DeviceHostname.Should().BeEmpty();
        metric.InterfaceDescription.Should().BeNull();
        metric.Tags.Should().NotBeNull();
        metric.Tags.Should().BeEmpty();
        metric.Fields.Should().NotBeNull();
        metric.Fields.Should().BeEmpty();
    }

    [Fact]
    public void MetricsBatch_DefaultValues_AreCorrect()
    {
        // Act
        var batch = new MetricsBatch();

        // Assert
        batch.BatchId.Should().NotBeEmpty();
        batch.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        batch.Metrics.Should().NotBeNull();
        batch.Metrics.Should().BeEmpty();
        batch.Count.Should().Be(0);
        batch.IsReady.Should().BeFalse();
    }

    [Fact]
    public void MetricsBatch_Count_ReflectsMetricsList()
    {
        // Arrange
        var batch = new MetricsBatch();
        batch.Metrics.Add(new AggregatedMetric());
        batch.Metrics.Add(new AggregatedMetric());

        // Act & Assert
        batch.Count.Should().Be(2);
    }

    [Fact]
    public void MetricSource_AllValuesAreDefined()
    {
        // Assert
        var values = Enum.GetValues<MetricSource>();
        values.Should().Contain(MetricSource.Snmp);
        values.Should().Contain(MetricSource.Agent);
        values.Should().Contain(MetricSource.UniFiApi);
        values.Should().Contain(MetricSource.Custom);
    }

    #endregion

    #region Helper Methods

    private static DeviceMetrics CreateDeviceMetrics()
    {
        return new DeviceMetrics
        {
            IpAddress = "192.0.2.1",
            Hostname = "test-device",
            DeviceType = DeviceType.Switch,
            InterfaceCount = 24,
            IsReachable = true,
            Timestamp = DateTime.UtcNow
        };
    }

    private static InterfaceMetrics CreateInterfaceMetrics(string description = "eth0")
    {
        return new InterfaceMetrics
        {
            Index = 1,
            Description = description,
            Name = description,
            DeviceIp = "192.0.2.1",
            DeviceHostname = "test-device",
            AdminStatus = 1,
            OperStatus = 1,
            Timestamp = DateTime.UtcNow
        };
    }

    #endregion
}
