using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class PerfTweaksDeploymentServiceTests
{
    // CheckAllStatusAsync only depends on IGatewaySshService.
    private static PerfTweaksDeploymentService BuildService(Mock<IGatewaySshService> ssh)
        => new(Mock.Of<ILogger<PerfTweaksDeploymentService>>(), ssh.Object, null!, null!, null!, null!);

    private static readonly Regex GatedDevmem = new(
        @"if \[ -f /data/on_boot\.d/[\w.-]*sgmiiplus[\w.-]*\.sh \] \|\| lsmod \| grep -q force_uniphy\d_sgmiiplus; then busybox devmem ",
        RegexOptions.Compiled);

    [Fact]
    public async Task CheckAllStatusAsync_EveryDevmemReadIsGatedOnTheSgmiiPlusTweak()
    {
        // Raw MMIO reads reset hardware without this SerDes (a CloudKey set as the gateway SSH target).
        string? command = null;
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan?, CancellationToken>((c, _, _) => command = c)
            .ReturnsAsync((false, "unreachable"));

        await BuildService(ssh).CheckAllStatusAsync();

        command.Should().NotBeNull();
        var devmemCount = Regex.Matches(command!, @"\bdevmem\b").Count;
        devmemCount.Should().BeGreaterThan(0);
        GatedDevmem.Matches(command!).Count.Should().Be(devmemCount);
    }
}
