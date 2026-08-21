using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// What a stored WAN speed test schedule means when it runs. Every schedule on an existing install
/// predates the WAN choice, so the point of these is that adding the field changed none of them.
/// </summary>
public class WanScheduleConfigTests
{
    [Fact]
    public void AServerScheduleMadeBeforeWanChoiceTakesTheDefaultPath()
    {
        var (testType, maxMode, _, _, wanContextId, _) =
            ScheduleExecutorRegistration.ParseWanTestConfig("""{"testType":"server"}""");

        testType.Should().Be("server");
        maxMode.Should().BeFalse();
        wanContextId.Should().BeNull();
    }

    [Fact]
    public void AMaxLoadServerScheduleKeepsMaxLoad()
    {
        var (testType, maxMode, _, _, wanContextId, _) =
            ScheduleExecutorRegistration.ParseWanTestConfig("""{"testType":"server","maxMode":true}""");

        testType.Should().Be("server");
        maxMode.Should().BeTrue();
        wanContextId.Should().BeNull();
    }

    [Fact]
    public void ASingleWanGatewayScheduleIsUnchanged()
    {
        var (testType, maxMode, wanGroup, wanName, wanContextId, multiInterfaces) =
            ScheduleExecutorRegistration.ParseWanTestConfig(
                """{"testType":"gateway","wanGroup":"WAN","wanName":"Fiber"}""");

        testType.Should().Be("gateway");
        maxMode.Should().BeFalse();
        wanGroup.Should().Be("WAN");
        wanName.Should().Be("Fiber");
        multiInterfaces.Should().BeNull();
        // Never read on the gateway branch, but it must not arrive as anything but absent.
        wanContextId.Should().BeNull();
    }

    [Fact]
    public void AMultiWanGatewayScheduleKeepsItsInterfaceList()
    {
        var (testType, _, wanGroup, wanName, _, multiInterfaces) =
            ScheduleExecutorRegistration.ParseWanTestConfig(
                """{"testType":"gateway","wanGroup":"WAN+WAN2","wanName":"Fiber + LTE","interfaces":["eth4","eth8"]}""");

        testType.Should().Be("gateway");
        wanGroup.Should().Be("WAN+WAN2");
        wanName.Should().Be("Fiber + LTE");
        multiInterfaces.Should().Equal("eth4", "eth8");
    }

    [Fact]
    public void AnEmptyConfigStillMeansAGatewayTest()
    {
        // The default every caller relied on before any of these fields were written.
        foreach (var config in new string?[] { null, "", "{}" })
        {
            var (testType, maxMode, wanGroup, wanName, wanContextId, multiInterfaces) =
                ScheduleExecutorRegistration.ParseWanTestConfig(config);

            testType.Should().Be("gateway");
            maxMode.Should().BeFalse();
            wanGroup.Should().BeNull();
            wanName.Should().BeNull();
            wanContextId.Should().BeNull();
            multiInterfaces.Should().BeNull();
        }
    }

    [Fact]
    public void AServerScheduleCanNameTheWanItMeasures()
    {
        var (testType, _, _, wanName, wanContextId, _) =
            ScheduleExecutorRegistration.ParseWanTestConfig(
                """{"testType":"server","wanContextId":4,"wanName":"Starlink"}""");

        testType.Should().Be("server");
        wanContextId.Should().Be(4);
        wanName.Should().Be("Starlink");
    }
}
