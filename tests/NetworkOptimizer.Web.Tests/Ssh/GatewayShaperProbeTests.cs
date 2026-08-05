using FluentAssertions;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Ssh;

/// <summary>
/// Parser coverage for the Smart Queues shaper probe. The samples are what a UniFi gateway
/// actually emits: an htb root class when the shaper is running, the kernel's own multiqueue
/// classes when it is not (the exact output from the report in #1083), and iproute2's message
/// when UniFi never created the ingress device at all.
/// </summary>
public class GatewayShaperProbeTests
{
    private static readonly ShaperProbeTarget PppoeWan =
        new("Fiber", "ppp0", "ifbppp0", DownRateMbps: 894, UpRateMbps: 550);

    [Fact]
    public void BuildCommand_AsksEveryInterfaceInOneTrip()
    {
        var command = GatewayShaperProbe.BuildCommand(new[] { "ppp0", "ifbppp0", "eth7" });

        command.Should().Contain("###TC ppp0");
        command.Should().Contain("tc class show dev ppp0 2>&1");
        command.Should().Contain("###TC ifbppp0");
        command.Should().Contain("tc class show dev eth7 2>&1");
        command.Should().EndWith("true");
    }

    [Fact]
    public void Parse_ShapedWan_ReadsBothDirections()
    {
        const string output = """
            ###TC ppp0
            class htb 1:1 root rate 550Mbit ceil 550Mbit burst 2750b cburst 2750b
            ###TC ifbppp0
            class htb 1:1 root rate 894Mbit ceil 894Mbit burst 111750b cburst 111750b
            """;

        var state = GatewayShaperProbe.Parse(output, new[] { PppoeWan }).Should().ContainSingle().Subject;

        state.WanName.Should().Be("Fiber");
        state.Egress.DeviceFound.Should().BeTrue();
        state.Egress.HasRootHtb.Should().BeTrue();
        state.Ingress.DeviceFound.Should().BeTrue();
        state.Ingress.HasRootHtb.Should().BeTrue();
        state.DownRateMbps.Should().Be(894);
        state.UpRateMbps.Should().Be(550);
    }

    [Fact]
    public void Parse_MultiqueueOnly_IsNotShaped()
    {
        // A physical WAN port left unshaped: the kernel's own mq classes and nothing else.
        const string output = """
            ###TC eth6
            class mq :1 root
            class mq :2 root
            class mq :3 root
            class mq :4 root
            ###TC ifbeth6
            class mq :1 root
            """;

        var target = new ShaperProbeTarget("Fiber", "eth6", "ifbeth6", 900, 500);

        var state = GatewayShaperProbe.Parse(output, new[] { target }).Should().ContainSingle().Subject;

        state.Egress.DeviceFound.Should().BeTrue();
        state.Egress.HasRootHtb.Should().BeFalse();
        state.Ingress.HasRootHtb.Should().BeFalse();
    }

    [Fact]
    public void Parse_EmptySection_IsAFoundButUnshapedDevice()
    {
        // An interface with no classful qdisc lists nothing at all - that is an answer, not a gap.
        const string output = """
            ###TC ppp0
            ###TC ifbppp0
            """;

        var state = GatewayShaperProbe.Parse(output, new[] { PppoeWan }).Should().ContainSingle().Subject;

        state.Egress.DeviceFound.Should().BeTrue();
        state.Egress.HasRootHtb.Should().BeFalse();
        state.Ingress.DeviceFound.Should().BeTrue();
        state.Ingress.HasRootHtb.Should().BeFalse();
    }

    [Fact]
    public void Parse_MissingIfbDevice_IsReportedAsNotFound()
    {
        const string output = """
            ###TC ppp0
            class htb 1:1 root rate 550Mbit ceil 550Mbit
            ###TC ifbppp0
            Cannot find device "ifbppp0"
            """;

        var state = GatewayShaperProbe.Parse(output, new[] { PppoeWan }).Should().ContainSingle().Subject;

        state.Egress.HasRootHtb.Should().BeTrue();
        state.Ingress.DeviceFound.Should().BeFalse();
        state.Ingress.HasRootHtb.Should().BeFalse();
    }

    [Fact]
    public void Parse_SeveralWansInOneOutput_KeepsThemApart()
    {
        const string output = """
            ###TC ppp0
            class htb 1:1 root rate 550Mbit ceil 550Mbit
            ###TC ifbppp0
            class htb 1:1 root rate 894Mbit ceil 894Mbit
            ###TC eth7
            class mq :1 root
            ###TC ifbeth7
            Cannot find device "ifbeth7"
            """;

        var second = new ShaperProbeTarget("Cable", "eth7", "ifbeth7", 500, 20);

        var states = GatewayShaperProbe.Parse(output, new[] { PppoeWan, second });

        states.Should().HaveCount(2);
        states[0].Egress.HasRootHtb.Should().BeTrue();
        states[1].Egress.HasRootHtb.Should().BeFalse();
        states[1].Ingress.DeviceFound.Should().BeFalse();
    }

    [Fact]
    public void Parse_TruncatedOutput_DropsTheWanRatherThanGuessing()
    {
        // Only the egress section came back. Treating the absent ifb section as "no shaper"
        // would turn a truncated read into a finding.
        const string output = """
            ###TC ppp0
            class htb 1:1 root rate 550Mbit ceil 550Mbit
            """;

        GatewayShaperProbe.Parse(output, new[] { PppoeWan }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("eth6", true)]
    [InlineData("eth6.100", true)]
    [InlineData("ifbppp0", true)]
    [InlineData("", false)]
    [InlineData("eth6; rm -rf /", false)]
    [InlineData("$(reboot)", false)]
    [InlineData("eth6 && reboot", false)]
    public void IsValidInterfaceName_RejectsAnythingWithShellMeaning(string name, bool expected)
    {
        GatewayShaperProbe.IsValidInterfaceName(name).Should().Be(expected);
    }
}
