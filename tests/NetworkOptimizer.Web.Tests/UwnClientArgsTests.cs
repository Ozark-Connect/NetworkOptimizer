using FluentAssertions;
using NetworkOptimizer.AgentProtocol;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The uwnspeedtest argument builder shared by the central server and the on-site
/// agent. Guards parameter passthrough so an agent-run WAN test invokes the binary
/// identically to a local run - streams, servers, and timing must survive the
/// tunnel round trip intact.
/// </summary>
public class UwnClientArgsTests
{
    [Fact]
    public void Build_FromParameters_EmitsEveryFlagInOrder()
    {
        var args = UwnClientArgs.Build(streams: 20, servers: 4, durationSeconds: 8, timeoutSeconds: 90);

        args.Should().Be("-streams 20 -servers 4 -duration 8 -timeout 90");
    }

    [Fact]
    public void Build_MaxModeParameters_CarriesHigherStreamAndServerCounts()
    {
        var args = UwnClientArgs.Build(streams: 48, servers: 12, durationSeconds: 8, timeoutSeconds: 90);

        args.Should().Be("-streams 48 -servers 12 -duration 8 -timeout 90");
    }

    [Fact]
    public void Build_FromRequest_MatchesParameterOverload()
    {
        var request = new UwnRequest
        {
            RequestId = 7,
            Streams = 20,
            Servers = 4,
            DurationSeconds = 8,
            TimeoutSeconds = 90,
        };

        var fromRequest = UwnClientArgs.Build(request);
        var fromParams = UwnClientArgs.Build(20, 4, 8, 90);

        fromRequest.Should().Be(fromParams);
        fromRequest.Should().Contain("-streams 20");
        fromRequest.Should().Contain("-servers 4");
        fromRequest.Should().Contain("-duration 8");
        fromRequest.Should().Contain("-timeout 90");
    }
}
