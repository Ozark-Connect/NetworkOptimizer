using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class SpeedTestRegressionTests
{
    private static Iperf3Result R(double downMbps, double upMbps, SpeedTestDirection kind = SpeedTestDirection.ServerToDevice) => new()
    {
        Direction = kind, Success = true, DownloadBitsPerSecond = downMbps * 1_000_000, UploadBitsPerSecond = upMbps * 1_000_000,
    };

    private static List<Iperf3Result> History(int count, double downMbps, double upMbps, SpeedTestDirection kind = SpeedTestDirection.ServerToDevice) =>
        Enumerable.Range(0, count).Select(_ => R(downMbps, upMbps, kind)).ToList();

    [Fact]
    public void LanUploadDrop_IsReportedAsToDevice()
    {
        var drop = SpeedTestRegression.Worst(R(940, 300), History(5, 940, 940))!;

        drop.Direction.Should().Be("To Device");
        drop.CurrentMbps.Should().Be(300);
        drop.AverageMbps.Should().Be(940);
        drop.DropPercent.Should().BeApproximately(68.1, 0.1);
        drop.Samples.Should().Be(5);
        SpeedTestRegression.Severity(drop).Should().Be(AlertSeverity.Error);
    }

    [Fact]
    public void LanDownloadDrop_IsReportedAsFromDevice()
    {
        SpeedTestRegression.Worst(R(600, 940), History(5, 940, 940))!.Direction.Should().Be("From Device");
    }

    [Fact]
    public void BothDirectionsDrop_TheLargerDropIsReported()
    {
        var drop = SpeedTestRegression.Worst(R(800, 350, SpeedTestDirection.CloudflareWan), History(5, 1000, 500, SpeedTestDirection.CloudflareWan))!;

        drop.Direction.Should().Be("Upload");
        drop.DropPercent.Should().BeApproximately(30, 0.001);
        SpeedTestRegression.Severity(drop).Should().Be(AlertSeverity.Warning);
    }

    [Fact]
    public void WanDownloadDrop_IsReportedAsDownload()
    {
        SpeedTestRegression.Worst(R(500, 50, SpeedTestDirection.UwnWanGateway), History(4, 1000, 50, SpeedTestDirection.UwnWanGateway))!
            .Direction.Should().Be("Download");
    }

    [Fact]
    public void NoDrop_IsNull()
    {
        SpeedTestRegression.Worst(R(950, 950), History(5, 940, 940)).Should().BeNull();
    }

    [Fact]
    public void FewerThanThreeSamples_IsNull()
    {
        SpeedTestRegression.Worst(R(100, 100), History(2, 940, 940)).Should().BeNull();
    }

    [Fact]
    public void ADirectionTheTestSkipped_IsNoDrop()
    {
        // A one-way client test leaves the other direction at 0: not a 100% drop.
        SpeedTestRegression.Worst(R(0, 950, SpeedTestDirection.ClientToServer), History(5, 940, 940, SpeedTestDirection.ClientToServer))
            .Should().BeNull();
    }

    [Fact]
    public void EarlierTestsThatSkippedADirection_AreNoBaselineForIt()
    {
        // Upload-only results in the history would otherwise pull the download average to near 0.
        var history = new List<Iperf3Result> { R(0, 500), R(0, 500), R(1000, 500), R(1000, 500), R(1000, 500) };

        var drop = SpeedTestRegression.Worst(R(500, 500), history)!;

        drop.Direction.Should().Be("From Device");
        drop.AverageMbps.Should().Be(1000);
        drop.Samples.Should().Be(3);
    }

    [Fact]
    public void OnlyTheNewestFivePerDirectionAreAveraged()
    {
        var history = History(5, 1000, 1000).Concat(History(10, 100, 100)).ToList();

        SpeedTestRegression.Worst(R(900, 900), history)!.AverageMbps.Should().Be(1000);
    }

    [Theory]
    [InlineData(SpeedTestDirection.ServerToDevice, false)]
    [InlineData(SpeedTestDirection.ClientToServer, false)]
    [InlineData(SpeedTestDirection.BrowserToServer, false)]
    [InlineData(SpeedTestDirection.CloudflareWan, true)]
    [InlineData(SpeedTestDirection.CloudflareWanGateway, true)]
    [InlineData(SpeedTestDirection.UwnWan, true)]
    [InlineData(SpeedTestDirection.UwnWanGateway, true)]
    [InlineData(SpeedTestDirection.OpenSpeedTestWan, true)]
    public void EveryTestKind_IsLanOrWan(SpeedTestDirection kind, bool wan)
    {
        SpeedTestRegression.IsWan(kind).Should().Be(wan);
        SpeedTestRegression.Labels(kind).Should().Be(wan ? ("Download", "Upload") : ("From Device", "To Device"));
    }
}
