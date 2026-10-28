using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class AgentProbeServiceTests
{
    [Fact]
    public async Task OnAgentDisconnected_SupersededConnection_FailsOnlyItsOwnProbes()
    {
        // The old connection's teardown runs after the agent reconnected. It must fail the probe
        // that was sent on it, and leave the one riding the new tunnel alone.
        var registry = new AgentTunnelRegistry(new AgentTunnelOptions(Enabled: true, Port: 0));
        var service = new AgentProbeService(registry, NullLogger<AgentProbeService>.Instance);
        var old = registry.Register(1, "branch-office", "Agent");

        var oldRequest = new ProbeRequest();
        var oldProbe = service.RunAsync("branch-office", oldRequest, TimeSpan.FromSeconds(30), CancellationToken.None);

        registry.Register(1, "branch-office", "Agent");
        var newRequest = new ProbeRequest();
        var newProbe = service.RunAsync("branch-office", newRequest, TimeSpan.FromSeconds(30), CancellationToken.None);

        service.OnAgentDisconnected(old);

        var oldResult = await oldProbe;
        oldResult!.Success.Should().BeFalse();
        newProbe.IsCompleted.Should().BeFalse();

        service.OnResult(new ProbeResponse { RequestId = newRequest.RequestId, Success = true });
        (await newProbe)!.Success.Should().BeTrue();
    }
}
