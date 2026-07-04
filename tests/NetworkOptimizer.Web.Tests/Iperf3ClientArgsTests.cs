using FluentAssertions;
using NetworkOptimizer.AgentProtocol;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The iperf3 client argument builder shared by the central server and the
/// on-site agent. Guards the directional <c>-R</c> mapping and parameter
/// passthrough so an agent-run LAN test invokes iperf3 identically to a local
/// run - the direction (reverse) must survive the tunnel round trip intact.
/// </summary>
public class Iperf3ClientArgsTests
{
    [Fact]
    public void Build_ForwardTest_OmitsReverseFlag()
    {
        var request = new Iperf3ClientRequest
        {
            Host = "192.0.2.10",
            Port = 5201,
            DurationSeconds = 10,
            ParallelStreams = 4,
            Reverse = false,
        };

        var args = Iperf3ClientArgs.Build(request);

        args.Should().Be("-c 192.0.2.10 -p 5201 -t 10 -P 4 -J --connect-timeout 5000");
        args.Should().NotContain("-R");
    }

    [Fact]
    public void Build_ReverseTest_AppendsReverseFlag()
    {
        var request = new Iperf3ClientRequest
        {
            Host = "192.0.2.10",
            Port = 5201,
            DurationSeconds = 10,
            ParallelStreams = 4,
            Reverse = true,
        };

        var args = Iperf3ClientArgs.Build(request);

        args.Should().Be("-c 192.0.2.10 -p 5201 -t 10 -P 4 -J --connect-timeout 5000 -R");
    }

    [Fact]
    public void Build_CarriesEveryParameter()
    {
        var request = new Iperf3ClientRequest
        {
            Host = "198.51.100.5",
            Port = 5202,
            DurationSeconds = 30,
            ParallelStreams = 8,
            Reverse = false,
        };

        var args = Iperf3ClientArgs.Build(request);

        args.Should().Contain("-c 198.51.100.5");
        args.Should().Contain("-p 5202");
        args.Should().Contain("-t 30");
        args.Should().Contain("-P 8");
    }
}
