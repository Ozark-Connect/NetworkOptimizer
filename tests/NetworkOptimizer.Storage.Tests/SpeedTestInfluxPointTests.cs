using System.Globalization;
using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class SpeedTestInfluxPointTests
{
    private static readonly DateTime TestTime = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(SpeedTestDirection.CloudflareWan, "server", "cloudflare")]
    [InlineData(SpeedTestDirection.CloudflareWanGateway, "gateway", "cloudflare")]
    [InlineData(SpeedTestDirection.UwnWan, "server", "uwn")]
    [InlineData(SpeedTestDirection.UwnWanGateway, "gateway", "uwn")]
    public void WanPoint_ContainsSummaryAndBoundedTags(SpeedTestDirection direction, string runner, string provider)
    {
        var result = new Iperf3Result
        {
            Id = 42,
            Direction = direction,
            TestTime = TestTime,
            DeviceHost = "speed.example.net",
            DeviceName = "Test edge",
            DeviceType = "WAN",
            WanNetworkGroup = "WAN+WAN2",
            WanName = "Fiber + Backup",
            Success = true,
            DownloadBitsPerSecond = 900_000_000,
            UploadBitsPerSecond = 100_000_000,
            PingMs = 12.5,
            JitterMs = 0.5,
            DownloadLatencyMs = 20.5,
            DownloadJitterMs = 1.5,
            UploadLatencyMs = 30.5,
            UploadJitterMs = 2.5,
            DurationSeconds = 8,
            ParallelStreams = 20
        };

        var line = MonitoringInfluxClient.BuildSpeedTestPoint(result).ToLineProtocol();
        var tags = line[..line.IndexOf(' ')];

        tags.Should().Contain("speed_test,").And.Contain("test_type=wan")
            .And.Contain($"runner={runner}").And.Contain($"provider={provider}").And.Contain("wan_network_group=WAN+WAN2")
            .And.NotContain("direction=");
        tags.Should().NotContain("result_id").And.NotContain("speed.example.net")
            .And.NotContain("Test").And.NotContain("Fiber");
        line.Should().Contain("result_id=42i").And.Contain("success=true")
            .And.Contain("download_bps=900000000").And.Contain("upload_bps=100000000")
            .And.Contain("duration_s=8i").And.Contain("parallel_streams=20i")
            .And.Contain("server_host=\"speed.example.net\"").And.Contain("server_name=\"Test edge\"")
            .And.Contain("wan_name=\"Fiber + Backup\"")
            .And.Contain("ping_ms=12.5").And.Contain("jitter_ms=0.5")
            .And.Contain("download_latency_ms=20.5").And.Contain("download_jitter_ms=1.5")
            .And.Contain("upload_latency_ms=30.5").And.Contain("upload_jitter_ms=2.5");
        line.Should().EndWith($" {TimestampNs(TestTime)}");
        line.Should().NotContain("target_host").And.NotContain("target_name").And.NotContain("target_type");
    }

    [Theory]
    [InlineData(SpeedTestDirection.ServerToDevice, "server", "iperf3")]
    [InlineData(SpeedTestDirection.ClientToServer, "client", "iperf3")]
    [InlineData(SpeedTestDirection.BrowserToServer, "client", "openspeedtest")]
    public void LanPoint_PreservesFromDeviceAndToDeviceRatesAndOmitsMissingLatency(SpeedTestDirection direction, string runner, string provider)
    {
        var result = new Iperf3Result
        {
            Id = 7,
            DeviceHost = "192.0.2.10",
            Direction = direction,
            DownloadBitsPerSecond = 800_000_000,
            UploadBitsPerSecond = 700_000_000,
            Success = true,
            TestTime = TestTime
        };

        var line = MonitoringInfluxClient.BuildSpeedTestPoint(result).ToLineProtocol();
        var tags = line[..line.IndexOf(' ')];

        tags.Should().Contain("test_type=lan").And.Contain($"runner={runner}").And.Contain($"provider={provider}")
            .And.NotContain("direction=")
            .And.Contain("target_host=192.0.2.10").And.NotContain("wan_network_group");
        line.Should().Contain("download_bps=800000000").And.Contain("upload_bps=700000000")
            .And.NotContain("ping_ms").And.NotContain("jitter_ms").And.NotContain("latency_ms")
            .And.NotContain("server_host");
    }

    [Fact]
    public void FailedPoint_ExportsStatusWithoutRawPayloadsOrPersonalMetadata()
    {
        var result = new Iperf3Result
        {
            Direction = SpeedTestDirection.UwnWanGateway,
            TestTime = TestTime,
            Success = false,
            RawDownloadJson = "private-download-payload",
            RawUploadJson = "private-upload-payload",
            PathAnalysisJson = "private-topology",
            Notes = "private-notes",
            ErrorMessage = "private-error",
            UserAgent = "private-user-agent",
            ClientMac = "aa:bb:cc:dd:ee:ff",
            LocalIp = "192.0.2.20",
            Latitude = 42.123,
            Longitude = -71.123
        };

        var line = MonitoringInfluxClient.BuildSpeedTestPoint(result).ToLineProtocol();

        line.Should().Contain("success=false").And.Contain("wan_network_group=unknown")
            .And.NotContain("private").And.NotContain("aa:bb").And.NotContain("192.0.2.20")
            .And.NotContain("42.123").And.NotContain("-71.123");
    }

    [Fact]
    public void SqliteUnspecifiedTimestamp_IsTreatedAsUtc()
    {
        var utc = new Iperf3Result { DeviceHost = "192.0.2.10", TestTime = TestTime };
        var fromSqlite = new Iperf3Result
        {
            DeviceHost = "192.0.2.10",
            TestTime = DateTime.SpecifyKind(TestTime, DateTimeKind.Unspecified)
        };

        MonitoringInfluxClient.BuildSpeedTestPoint(fromSqlite).ToLineProtocol()
            .Should().Be(MonitoringInfluxClient.BuildSpeedTestPoint(utc).ToLineProtocol());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WanPoint_WithMissingHost_OmitsOptionalServerField(string? host)
    {
        var result = new Iperf3Result
        {
            Direction = SpeedTestDirection.UwnWanGateway,
            DeviceHost = host!,
            TestTime = TestTime,
            Success = true
        };

        var line = MonitoringInfluxClient.BuildSpeedTestPoint(result).ToLineProtocol();

        line.Should().Contain("success=true").And.NotContain("server_host").And.NotContain("target_host");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LanPoint_WithMissingTarget_RejectsExport(string? host)
    {
        var result = new Iperf3Result { DeviceHost = host!, TestTime = TestTime };

        var build = () => MonitoringInfluxClient.BuildSpeedTestPoint(result);

        build.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData((SpeedTestDirection)999)]
    public void UnsupportedDirection_RejectsExport(SpeedTestDirection direction)
    {
        var build = () => MonitoringInfluxClient.BuildSpeedTestPoint(new Iperf3Result { Direction = direction });

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BrowserWan_UsesExternalServerIdentityAndDoesNotExportClientAddress()
    {
        var result = new Iperf3Result
        {
            Direction = SpeedTestDirection.OpenSpeedTestWan,
            DeviceHost = "192.0.2.50",
            ExternalServerName = "vps-test",
            DownloadBitsPerSecond = 500_000_000,
            UploadBitsPerSecond = 50_000_000,
            TestTime = TestTime,
            Success = true
        };

        var line = MonitoringInfluxClient.BuildSpeedTestPoint(result).ToLineProtocol();

        line.Should().Contain("test_type=wan").And.Contain("runner=client").And.Contain("provider=openspeedtest")
            .And.NotContain("wan_network_group").And.Contain("server_name=\"vps-test\"")
            .And.Contain("download_bps=500000000").And.Contain("upload_bps=50000000")
            .And.NotContain("server_host").And.NotContain("target_host").And.NotContain("192.0.2.50");
    }

    private static string TimestampNs(DateTime time) =>
        ((time.Ticks - DateTime.UnixEpoch.Ticks) * 100).ToString(CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(SpeedTestDirection.CloudflareWan)]
    [InlineData(SpeedTestDirection.CloudflareWanGateway)]
    [InlineData(SpeedTestDirection.UwnWan)]
    [InlineData(SpeedTestDirection.UwnWanGateway)]
    [InlineData(SpeedTestDirection.OpenSpeedTestWan)]
    [InlineData(SpeedTestDirection.ServerToDevice)]
    [InlineData(SpeedTestDirection.ClientToServer)]
    [InlineData(SpeedTestDirection.BrowserToServer)]
    public void FailedPoint_OmitsDescriptionsAndMetadataUpdates(SpeedTestDirection direction)
    {
        var result = new Iperf3Result
        {
            Direction = direction, DeviceHost = "UWN Test", DeviceName = "Gateway", DeviceType = "WAN",
            WanName = "Placeholder WAN", ExternalServerName = "Cloudflare", Success = false, TestTime = TestTime
        };

        var line = MonitoringInfluxClient.BuildSpeedTestPoint(result).ToLineProtocol();
        var fields = line[(line.IndexOf(' ') + 1)..];

        fields.Should().Contain("success=false").And.NotContain("server_host").And.NotContain("server_name")
            .And.NotContain("target_name").And.NotContain("target_type").And.NotContain("wan_name")
            .And.NotContain("external_server_name");
        MonitoringInfluxClient.BuildSpeedTestMetadataPoint(result).Should().BeNull();
    }

    [Fact]
    public void SuccessfulGatewayWan_OmitsPlaceholderHostButKeepsRealServerName()
    {
        var result = new Iperf3Result
        {
            Direction = SpeedTestDirection.UwnWanGateway, DeviceHost = "UWN Test",
            DeviceName = "Real edge", DeviceType = "WAN", Success = true, TestTime = TestTime
        };

        var line = MonitoringInfluxClient.BuildSpeedTestPoint(result).ToLineProtocol();

        line.Should().Contain("server_name=\"Real edge\"").And.NotContain("server_host")
            .And.NotContain("UWN Test").And.NotContain("target_type").And.NotContain("target_name");
    }

    [Fact]
    public void BrowserWan_ClientEnrichmentDoesNotReplaceExternalServerName()
    {
        var result = new Iperf3Result
        {
            Direction = SpeedTestDirection.OpenSpeedTestWan, DeviceHost = "192.0.2.50",
            DeviceName = "Private laptop", DeviceType = "Client", ExternalServerName = "vps-test",
            WanNetworkGroup = "WAN2", Success = true, TestTime = TestTime
        };

        var line = MonitoringInfluxClient.BuildSpeedTestPoint(result).ToLineProtocol();

        line.Should().Contain("server_name=\"vps-test\"").And.Contain("wan_network_group=WAN2")
            .And.NotContain("Private").And.NotContain("target_name").And.NotContain("target_type");
        MonitoringInfluxClient.BuildSpeedTestMetadataPoint(result).Should().BeNull();
    }

    [Theory]
    [InlineData(SpeedTestDirection.BrowserToServer)]
    [InlineData(SpeedTestDirection.ClientToServer)]
    [InlineData(SpeedTestDirection.UwnWan)]
    public void MetadataPoint_SharesSummaryIdentityWithoutMeasurementFields(SpeedTestDirection direction)
    {
        var result = new Iperf3Result
        {
            Direction = direction, DeviceHost = "192.0.2.50", TestTime = TestTime,
            DeviceName = "Laptop", DeviceType = "Client", Success = true,
            DownloadBitsPerSecond = 500_000_000, UploadBitsPerSecond = 50_000_000, PingMs = 12.5
        };
        var summary = MonitoringInfluxClient.BuildSpeedTestPoint(result).ToLineProtocol();
        result.TestTime = DateTime.SpecifyKind(TestTime, DateTimeKind.Unspecified);
        var metadata = MonitoringInfluxClient.BuildSpeedTestMetadataPoint(result)!.ToLineProtocol();

        metadata[..metadata.IndexOf(' ')].Should().Be(summary[..summary.IndexOf(' ')]);
        metadata.Should().EndWith($" {TimestampNs(TestTime)}");
        if (direction == SpeedTestDirection.UwnWan)
            metadata.Should().Contain("server_name=\"Laptop\"").And.NotContain("target_name").And.NotContain("target_type");
        else
            metadata.Should().Contain("target_name=\"Laptop\"").And.Contain("target_type=\"Client\"");
        metadata.Should().NotContain("download_bps").And.NotContain("upload_bps").And.NotContain("ping_ms")
            .And.NotContain("success=").And.NotContain("result_id").And.NotContain("duration_s")
            .And.NotContain("parallel_streams");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MetadataPoint_WithoutMetadata_DoesNotBuildFieldlessPoint(string? metadata)
    {
        MonitoringInfluxClient.BuildSpeedTestMetadataPoint(new Iperf3Result
        {
            DeviceName = metadata, DeviceType = metadata
        }).Should().BeNull();
    }
}
