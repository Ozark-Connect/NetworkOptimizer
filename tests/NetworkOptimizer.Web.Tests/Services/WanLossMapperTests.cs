using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class WanLossMapperTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void Apply_MapsBinaryJsonPerPhase()
    {
        const string json = """
            {
              "success": true,
              "idle_loss": { "method": "tcp_connect", "sent": 500, "retried": 1, "failed": 0 },
              "download": { "bps": 1e9, "loss": { "method": "tcp_connect", "sent": 400, "retried": 2, "failed": 1 } },
              "upload": { "bps": 9e8, "loss": { "method": "tcp_connect", "sent": 400, "retried": 0, "failed": 0 } }
            }
            """;
        var run = JsonSerializer.Deserialize<WanSpeedTestResult>(json, JsonOptions)!;
        var result = new Iperf3Result();

        WanLossMapper.Apply(result, [run]);

        result.IdleLossPercent.Should().Be(0.2);
        result.IdleLossProbes.Should().Be(500);
        result.DownloadLossPercent.Should().Be(0.75);
        result.DownloadLossProbes.Should().Be(400);
        result.UploadLossPercent.Should().Be(0);
        result.UploadLossProbes.Should().Be(400);
    }

    [Fact]
    public void Apply_BinaryWithoutLoss_LeavesNull()
    {
        var run = JsonSerializer.Deserialize<WanSpeedTestResult>(
            """{ "success": true, "download": { "bps": 1e9 }, "upload": { "bps": 9e8 } }""", JsonOptions)!;
        var result = new Iperf3Result();

        WanLossMapper.Apply(result, [run]);

        result.IdleLossPercent.Should().BeNull();
        result.IdleLossProbes.Should().BeNull();
        result.DownloadLossPercent.Should().BeNull();
        result.UploadLossPercent.Should().BeNull();
    }

    [Fact]
    public void Apply_ParallelWans_PoolsProbesRatherThanAveragingPercents()
    {
        var wan1 = new WanSpeedTestResult { Download = new WanThroughput { Loss = new WanLoss { Sent = 400, Retried = 4 } } };
        var wan2 = new WanSpeedTestResult { Download = new WanThroughput { Loss = new WanLoss { Sent = 100, Failed = 1 } } };
        var noLoss = new WanSpeedTestResult { Download = new WanThroughput() };
        var result = new Iperf3Result();

        WanLossMapper.Apply(result, [wan1, wan2, noLoss]);

        result.DownloadLossPercent.Should().Be(1.0);
        result.DownloadLossProbes.Should().Be(500);
    }
}
