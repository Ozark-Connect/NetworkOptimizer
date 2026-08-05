using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests.Probes;

/// <summary>
/// Traceroute is what discovers a WAN's upstream path, so on a multi-WAN install it has to leave
/// by the WAN being discovered. These cover the source binding it grew: an interface name becomes
/// -i, an IP becomes -s, and anything that can't be bound - a hostile value, a binary without the
/// options, the Windows managed path - fails instead of tracing out the default route and filing
/// another WAN's upstream under this one.
/// </summary>
public class TracerouteCommandTests
{
    private static readonly LocalProbeExecutor.TracerouteBinaryTraits Gnu =
        LocalProbeExecutor.TracerouteBinaryTraits.FullyBindable;

    [Fact]
    public void NoSource_BuildsTheSameCommandItAlwaysHas()
    {
        // The single-WAN case: nothing about the command changes.
        var (exe, args, error) = LocalProbeExecutor.BuildTracerouteCommand(
            new ProbeTarget("192.0.2.1", ProbeMode.Icmp), maxHops: 30, perHopTimeout: TimeSpan.FromSeconds(2), Gnu, isWindows: false);

        error.Should().BeNull();
        exe.Should().Be("traceroute");
        args.Should().Be("-m 30 -q 2 -w 2 -I 192.0.2.1");
    }

    [Fact]
    public void InterfaceName_BindsWithDashI()
    {
        var (_, args, error) = LocalProbeExecutor.BuildTracerouteCommand(
            new ProbeTarget("192.0.2.1", ProbeMode.Icmp, null, "eth8"), 30, TimeSpan.FromSeconds(2), Gnu, isWindows: false);

        error.Should().BeNull();
        args.Should().Be("-m 30 -q 2 -w 2 -I -i eth8 192.0.2.1");
    }

    [Fact]
    public void IpLiteral_BindsWithDashS()
    {
        var (_, args, error) = LocalProbeExecutor.BuildTracerouteCommand(
            new ProbeTarget("192.0.2.1", ProbeMode.Udp, null, "198.51.100.7"), 30, TimeSpan.FromSeconds(2), Gnu, isWindows: false);

        error.Should().BeNull();
        args.Should().Contain("-s 198.51.100.7").And.NotContain("-i ");
    }

    [Fact]
    public void UnsafeSourceValue_FailsInsteadOfReachingTheCommandLine()
    {
        var (_, args, error) = LocalProbeExecutor.BuildTracerouteCommand(
            new ProbeTarget("192.0.2.1", ProbeMode.Icmp, null, "eth0; rm -rf /"), 30, TimeSpan.FromSeconds(2), Gnu, isWindows: false);

        error.Should().Contain("Invalid probe source");
        args.Should().BeEmpty();
    }

    [Fact]
    public void BusyBoxWithoutTheOptions_FailsRatherThanTracingUnbound()
    {
        var stripped = new LocalProbeExecutor.TracerouteBinaryTraits(
            IsBusyBox: true, CanBindAddress: false, CanBindInterface: false);

        var iface = LocalProbeExecutor.BuildTracerouteCommand(
            new ProbeTarget("192.0.2.1", ProbeMode.Icmp, null, "eth8"), 30, TimeSpan.FromSeconds(2), stripped, isWindows: false);
        var address = LocalProbeExecutor.BuildTracerouteCommand(
            new ProbeTarget("192.0.2.1", ProbeMode.Icmp, null, "198.51.100.7"), 30, TimeSpan.FromSeconds(2), stripped, isWindows: false);

        iface.Error.Should().Contain("source interface").And.Contain("eth8");
        address.Error.Should().Contain("source address");
    }

    [Fact]
    public void BusyBoxWithoutTheOptions_StillTracesWhenNothingAskedForABind()
    {
        var stripped = new LocalProbeExecutor.TracerouteBinaryTraits(
            IsBusyBox: true, CanBindAddress: false, CanBindInterface: false);

        var (_, args, error) = LocalProbeExecutor.BuildTracerouteCommand(
            new ProbeTarget("192.0.2.1", ProbeMode.Icmp), 30, TimeSpan.FromSeconds(2), stripped, isWindows: false);

        error.Should().BeNull();
        args.Should().Be("-m 30 -q 2 -w 2 -I 192.0.2.1");
    }

    [Fact]
    public void Windows_CannotBindAtAllAndSaysSo()
    {
        // tracert.exe has no source option, and the Windows managed path can't bind either -
        // the same loud failure the managed ping path gives rather than a wrong-WAN reading.
        var (_, _, error) = LocalProbeExecutor.BuildTracerouteCommand(
            new ProbeTarget("192.0.2.1", ProbeMode.Icmp, null, "198.51.100.7"), 30, TimeSpan.FromSeconds(2),
            Gnu, isWindows: true);

        error.Should().Contain("native traceroute binary");
    }

    [Fact]
    public void Windows_WithoutASourceBuildsTheTracertCommandItAlwaysHas()
    {
        var (exe, args, error) = LocalProbeExecutor.BuildTracerouteCommand(
            new ProbeTarget("192.0.2.1", ProbeMode.Icmp), 30, TimeSpan.FromSeconds(2), Gnu, isWindows: true);

        error.Should().BeNull();
        exe.Should().Be("tracert.exe");
        args.Should().Be("-h 30 -w 2000 192.0.2.1");
    }

    [Fact]
    public void BusyBoxUsageListingBothOptions_ReadsAsBindable()
    {
        const string usage =
            "BusyBox v1.36.1 (2024-01-01) multi-call binary.\n" +
            "Usage: traceroute [-46FIlnrv] [-f 1ST_TTL] [-m MAXTTL] [-q PROBES] [-s SRC_IP]\n" +
            "        [-t TOS] [-w WAIT_SEC] [-G GATEWAY] [-i IFACE] HOST [BYTES]";

        var traits = LocalProbeExecutor.InterpretTracerouteBanner(usage);

        traits.IsBusyBox.Should().BeTrue();
        traits.CanBindAddress.Should().BeTrue();
        traits.CanBindInterface.Should().BeTrue();
    }

    [Fact]
    public void BusyBoxUsageWithoutSourceOptions_ReadsAsUnbindable()
    {
        const string usage =
            "BusyBox v1.36.1 multi-call binary.\n" +
            "Usage: traceroute [-46Fln] [-m MAXTTL] [-q PROBES] [-w WAIT_SEC] HOST [BYTES]";

        var traits = LocalProbeExecutor.InterpretTracerouteBanner(usage);

        traits.IsBusyBox.Should().BeTrue();
        traits.CanBindAddress.Should().BeFalse();
        traits.CanBindInterface.Should().BeFalse();
    }

    [Theory]
    [InlineData("Modern traceroute for Linux, version 2.1.0")]
    [InlineData("Version 1.4a12")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingButBusyBox_ReadsAsFullyBindable(string? banner)
    {
        // GNU traceroute and BSD traceroute both document -s and -i, and a binary that answered
        // nothing gets the same benefit of the doubt: an option it doesn't have makes the command
        // fail loudly, which is still not a silently unbound probe.
        var traits = LocalProbeExecutor.InterpretTracerouteBanner(banner);

        traits.IsBusyBox.Should().BeFalse();
        traits.CanBindAddress.Should().BeTrue();
        traits.CanBindInterface.Should().BeTrue();
    }
}
