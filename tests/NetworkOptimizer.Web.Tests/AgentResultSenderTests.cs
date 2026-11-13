using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Agent results complete the request they answer. Request ids are sequential per service, so the
/// first request a fresh service sends is id 1.
/// </summary>
public class AgentResultSenderTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

    private static (AgentTunnelRegistry Registry, AgentTunnelConnection Asked, AgentTunnelConnection Other) TwoSites()
    {
        var registry = new AgentTunnelRegistry(new AgentTunnelOptions(Enabled: true, Port: 0));
        var asked = registry.Register(1, "site-a", "Agent A");
        asked.OnGateway = false;
        var other = registry.Register(2, "site-b", "Agent B");
        return (registry, asked, other);
    }

    [Fact]
    public async Task Iperf3_ResultFromTheAskedAgent_CompletesTheRun()
    {
        var (registry, asked, _) = TwoSites();
        var service = new AgentIperf3Service(registry,
            new AgentOnGatewayDetector(null!, null!, null!, null!, registry, NullLogger<AgentOnGatewayDetector>.Instance),
            NullLogger<AgentIperf3Service>.Instance);

        var run = service.RunClientAsync("site-a", "192.0.2.10", 5201, 10, 4, false, CancellationToken.None);
        await Task.Delay(Settle);
        service.OnResult(asked, new Iperf3ClientResult { RequestId = 1, Success = true, Output = "{}" });

        (await run).success.Should().BeTrue();
    }

    [Fact]
    public async Task Uwn_ResultFromTheAskedAgent_CompletesTheRun()
    {
        var (registry, asked, _) = TwoSites();
        var service = new AgentUwnService(registry, NullLogger<AgentUwnService>.Instance);

        var run = service.RunAsync("site-a", asked.AgentId, 4, 2, 10, 60, CancellationToken.None);
        await Task.Delay(Settle);
        service.OnResult(asked, new UwnResult { RequestId = 1, Success = true, Output = "{}" });

        (await run).success.Should().BeTrue();
    }

    [Fact]
    public async Task Probe_ResultFromTheAskedAgent_CompletesTheProbe()
    {
        var (registry, asked, _) = TwoSites();
        var service = new AgentProbeService(registry, NullLogger<AgentProbeService>.Instance);

        var request = new ProbeRequest();
        var probe = service.RunAsync("site-a", request, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(Settle);
        service.OnResult(asked, new ProbeResponse { RequestId = request.RequestId, Success = true });

        (await probe)!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SnmpOid_ResultFromTheAskedAgent_CompletesTheQuery()
    {
        var (registry, asked, _) = TwoSites();
        var service = new AgentSnmpQueryService(registry, NullLogger<AgentSnmpQueryService>.Instance);

        var query = service.QueryAsync("site-a", "192.0.2.20", "1.3.6.1.2.1.1.3.0", TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(Settle);
        service.OnResult(asked, new SnmpOidResult { RequestId = 1, Success = true });

        (await query)!.Success.Should().BeTrue();
    }

    // --- Another site's agent answering the same request id ---

    [Fact]
    public async Task Iperf3_ResultFromAnotherAgent_IsIgnored()
    {
        var (registry, asked, other) = TwoSites();
        var service = new AgentIperf3Service(registry,
            new AgentOnGatewayDetector(null!, null!, null!, null!, registry, NullLogger<AgentOnGatewayDetector>.Instance),
            NullLogger<AgentIperf3Service>.Instance);

        var run = service.RunClientAsync("site-a", "192.0.2.10", 5201, 10, 4, false, CancellationToken.None);
        await Task.Delay(Settle);
        service.OnResult(other, new Iperf3ClientResult { RequestId = 1, Success = true, Output = "forged" });
        await Task.Delay(Settle);
        run.IsCompleted.Should().BeFalse();

        service.OnResult(asked, new Iperf3ClientResult { RequestId = 1, Success = true, Output = "{}" });
        (await run).output.Should().Be("{}");
    }

    [Fact]
    public async Task Uwn_ResultFromAnotherAgent_IsIgnored()
    {
        var (registry, asked, other) = TwoSites();
        var service = new AgentUwnService(registry, NullLogger<AgentUwnService>.Instance);

        var run = service.RunAsync("site-a", asked.AgentId, 4, 2, 10, 60, CancellationToken.None);
        await Task.Delay(Settle);
        service.OnResult(other, new UwnResult { RequestId = 1, Success = true, Output = "forged" });
        await Task.Delay(Settle);
        run.IsCompleted.Should().BeFalse();

        service.OnResult(asked, new UwnResult { RequestId = 1, Success = true, Output = "{}" });
        (await run).output.Should().Be("{}");
    }

    [Fact]
    public async Task Probe_ResponseFromAnotherAgent_IsIgnored()
    {
        var (registry, asked, other) = TwoSites();
        var service = new AgentProbeService(registry, NullLogger<AgentProbeService>.Instance);

        var request = new ProbeRequest();
        var probe = service.RunAsync("site-a", request, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(Settle);
        service.OnResult(other, new ProbeResponse { RequestId = request.RequestId, Success = false, Error = "forged" });
        await Task.Delay(Settle);
        probe.IsCompleted.Should().BeFalse();

        service.OnResult(asked, new ProbeResponse { RequestId = request.RequestId, Success = true });
        (await probe)!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SnmpOid_ResultFromAnotherAgent_IsIgnored()
    {
        var (registry, asked, other) = TwoSites();
        var service = new AgentSnmpQueryService(registry, NullLogger<AgentSnmpQueryService>.Instance);

        var query = service.QueryAsync("site-a", "192.0.2.20", "1.3.6.1.2.1.1.3.0", TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(Settle);
        service.OnResult(other, new SnmpOidResult { RequestId = 1, Success = false, Error = "forged" });
        await Task.Delay(Settle);
        query.IsCompleted.Should().BeFalse();

        service.OnResult(asked, new SnmpOidResult { RequestId = 1, Success = true });
        (await query)!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Probe_ResponseFromAReconnectedAgentsOldConnection_IsIgnored()
    {
        var registry = new AgentTunnelRegistry(new AgentTunnelOptions(Enabled: true, Port: 0));
        var service = new AgentProbeService(registry, NullLogger<AgentProbeService>.Instance);
        var old = registry.Register(1, "site-a", "Agent A");
        var current = registry.Register(1, "site-a", "Agent A");

        var request = new ProbeRequest();
        var probe = service.RunAsync("site-a", request, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(Settle);
        service.OnResult(old, new ProbeResponse { RequestId = request.RequestId, Success = false, Error = "stale" });
        await Task.Delay(Settle);
        probe.IsCompleted.Should().BeFalse();

        service.OnResult(current, new ProbeResponse { RequestId = request.RequestId, Success = true });
        (await probe)!.Success.Should().BeTrue();
    }
}
